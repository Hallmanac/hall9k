using System.Collections.Concurrent;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.Challenge;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Plan;

/// <summary>One sweep's tally: steps taken for live plans, sessions dispatched, sessions a restart left behind that were recorded as failed, and foreign dispatches ended on the ceiling.</summary>
public sealed record PlanSweepResult(int Steps, int Dispatched, int Recovered, int Expired);

/// <summary>
/// Drives plans (<c>h9k plan start</c>): the daemon's half of a plan, on the node where it was started. A sweep takes
/// each live plan oldest first and takes one step at a time until it has none, re-reading the plan between steps, so
/// every step is decided over what is on the stream and nothing is remembered in memory but which sessions are running.
/// <para>
/// The steps, in the order a sweep looks for them: close a draft a person published or abandoned by hand (which also
/// answers a park on it); absorb the outcome of a challenge the plan requested; start a next round a person granted;
/// then, only when nothing is parked, end the plan, publish the drafts that are ready, request a challenge, or dispatch a
/// refinement session. Parked means nothing new starts for any draft of the plan, and a session already running finishes.
/// </para>
/// <para>
/// A refinement session runs as a background job the sweep starts and does not await, as
/// <see cref="Closeout.CloseoutTriageSessions"/> does, because a session runs for up to
/// <see cref="DaemonOptions.PlanRefinementTimeout"/> and awaiting one in the sweep would hold up the next plan's step and
/// the next challenge. A node runs one at a time, oldest plan first, and a plan's session takes no slot against the
/// node's run ceiling or the project's: a plan has no run or lease to count. While the node's launch hold stands, its
/// spend budget is spent, or the project's max parallel tasks is 0, a plan starts no session and requests no
/// challenge; <c>h9k plan show</c> says which.
/// </para>
/// <para>
/// The daemon never writes a task through a session. It runs the installed <c>h9k</c> for the revise that applies a
/// session's revision and for each publish (<see cref="IPlanTaskCommands"/>), so the validation, the challenge gate, the
/// tracker item, and the ledger record are exactly what a person gets. A session's dispatch and outcome go on the task's
/// stream, project-scoped, and the plan's steps go on the plan's own, node-scoped stream, in one save.
/// </para>
/// </summary>
public sealed class PlanEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    IProcessManager processManager,
    IWorktreeManager worktrees,
    ProcessRunner processRunner,
    ReadingCheckoutLeases checkoutLeases,
    IPlanTaskCommands commands,
    LaunchHoldEngine launchHold,
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<PlanEngine> logger) : IDisposable
{
    /// <summary>How long the shutdown path gets to record an outcome: it runs while the daemon is stopping.</summary>
    private static readonly TimeSpan ShutdownRecordTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most steps one plan takes in one sweep, so a plan that cannot settle cannot hold the sweep.</summary>
    private const int MaxStepsPerPlan = 64;

    /// <summary>How many times an outcome retries a lost race with another append to the task or plan stream.</summary>
    private const int RecordAttempts = 5;

    private readonly DaemonOptions _options = options.Value;
    private readonly SemaphoreSlim _oneSweep = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, RunningSession> _sessions = [];
    private readonly Dictionary<Guid, string> _reportedGate = [];

    /// <summary>Set by the loop to its own doorbell, so a session that ends wakes the next step at once.</summary>
    public Action? OnWork { get; set; }

    private sealed record RunningSession(Guid PlanId, Guid DraftId, CancellationTokenSource Cancel, TaskCompletionSource Finished);

    /// <summary>Everything a session's job needs once it is dispatched.</summary>
    private sealed record RefinementJob(
        Guid PlanId,
        Guid TaskId,
        Guid SessionId,
        RefinementPurpose Purpose,
        string Checkout,
        string SessionDirectory,
        string ReportPath,
        string RevisionPath,
        string Prompt,
        AgentModel Model,
        AgentEffort Effort);

    /// <summary>What recording a session's end is told: how it ended and what it cost.</summary>
    private sealed record SessionEnd(
        Guid TaskId,
        Guid? SessionId,
        RefinementPurpose Purpose,
        AgentModel Model,
        AgentEffort Effort,
        RefinementOutcome Outcome,
        RefinementFailureReason Failure,
        AgentResult? Result,
        Guid? RanOnNodeId = null);

    /// <summary>Completes once every session started so far has recorded its outcome; the daemon never waits on it.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_sessions.Values.Select(running => running.Finished.Task));

    /// <summary>
    /// Called by the loop when the daemon is stopping: every running session is a detached process that would outlive
    /// the daemon, so each job is told to stop its session and record the outcome, and this waits a short while for them.
    /// What does not land in time is recorded as failed by the next start. It takes no <see cref="CancellationToken"/>: it
    /// runs after the host's stopping token has fired, which is what called it, and <paramref name="wait"/> is its bound.
    /// </summary>
    public async Task StopAsync(TimeSpan wait)
    {
        await _stopping.CancelAsync();
        await Task.WhenAny(WhenIdleAsync(), Task.Delay(wait));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>One sweep: settle what a restart left behind, stop sessions of plans that ended, then step each live plan.</summary>
    public async Task<PlanSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        await _oneSweep.WaitAsync(cancellationToken);
        try
        {
            (int recovered, int expired) = await RecoverAsync(cancellationToken);
            await StopSessionsOfEndedPlansAsync(cancellationToken);

            IReadOnlyList<Guid> plans;
            await using (IQuerySession query = store.QuerySession())
            {
                plans = [.. (await query.Query<PlanDetails>().ToListAsync(cancellationToken))
                    .Where(plan => plan.IsLive)
                    .OrderBy(plan => plan.StartedAt)
                    .Select(plan => plan.Id)];
            }

            int steps = 0;
            int dispatched = 0;
            foreach (Guid planId in plans)
            {
                for (int step = 0; step < MaxStepsPerPlan; step++)
                {
                    StepOutcome outcome;
                    try
                    {
                        outcome = await StepAsync(planId, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Plan {PlanId}: a step failed; leaving it for the next sweep", planId);
                        break;
                    }

                    if (outcome == StepOutcome.Nothing)
                    {
                        break;
                    }

                    steps++;
                    if (outcome == StepOutcome.Dispatched)
                    {
                        dispatched++;
                    }
                }
            }

            return new PlanSweepResult(steps, dispatched, recovered, expired);
        }
        finally
        {
            _oneSweep.Release();
        }
    }

    private enum StepOutcome
    {
        /// <summary>There was nothing to do for the plan.</summary>
        Nothing,

        /// <summary>A step was taken and recorded.</summary>
        Stepped,

        /// <summary>A refinement session was dispatched.</summary>
        Dispatched,
    }

    /// <summary>
    /// Takes one step for one plan, or reports there is none. Every call reads the plan and its drafts fresh, so the
    /// decision is over the record and a step that lost a race with a person's command is simply decided again.
    /// </summary>
    private async Task<StepOutcome> StepAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(planId, cancellationToken);
        PlanAggregate? plan = fence is null
            ? null
            : await session.Events.AggregateStreamAsync<PlanAggregate>(planId, version: fence.Version, token: cancellationToken);
        if (fence is null || plan is null || !plan.IsLive)
        {
            return StepOutcome.Nothing;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<TaskDetails> loaded = await session.LoadManyAsync<TaskDetails>(
            cancellationToken, [.. plan.Drafts.Select(draft => draft.DraftId)]);
        Dictionary<Guid, TaskDetails> tasks = loaded.ToDictionary(task => task.Id);

        // A draft a person published or abandoned by hand is closed here, which also answers any park on it.
        foreach (PlanDraftState draft in plan.Drafts.Where(draft => !draft.IsClosed))
        {
            if (!tasks.TryGetValue(draft.DraftId, out TaskDetails? task))
            {
                continue;
            }

            if (task.State == TaskState.Abandoned)
            {
                return await SavePlanAsync(
                    session, fence, [new PlanDraftAbandoned(plan.Id, plan.ProjectId, draft.DraftId, now)], cancellationToken);
            }

            if (task.State != TaskState.Draft)
            {
                return await SavePlanAsync(
                    session, fence, [new PlanDraftPublished(plan.Id, plan.ProjectId, draft.DraftId, ByHand: true, now)],
                    cancellationToken);
            }
        }

        // A challenge the plan requested has an outcome: the verdict decides where the draft goes next. Absorbed even
        // while the plan is parked, because a challenge already running finishes and its outcome is the plan's to record.
        foreach (PlanDraftState draft in plan.Drafts.Where(draft => draft.ChallengeRequestedAt is not null && !draft.IsClosed))
        {
            if (tasks.TryGetValue(draft.DraftId, out TaskDetails? task)
                && draft.ChallengeRequestedAt is { } requestedAt
                && !task.ChallengeRequested
                && task.RunningChallengeSessionId is null
                && task.LatestChallengeOutcome is { } outcome
                && outcome.RecordedAt >= requestedAt)
            {
                return await SavePlanAsync(
                    session, fence, PlanDecider.AfterChallenge(
                        plan, draft, outcome, _options.PlanRoundCap, now,
                        draftChangedSinceJudged: task.LatestChallengeVerdictJudgedOlderDraft || task.RevisedSinceLastDraftSession),
                    cancellationToken);
            }
        }

        foreach (PlanDraftState draft in plan.Drafts)
        {
            if (PlanDecider.NextStep(draft) == PlanStepKind.StartNextRound)
            {
                return await SavePlanAsync(session, fence, [PlanDecider.StartNextRound(plan, draft, now)], cancellationToken);
            }
        }

        // Parked: nothing new starts for any draft until a person answers it.
        if (plan.IsParked)
        {
            return StepOutcome.Nothing;
        }

        // A draft declared ready that a person revised since was never read by a challenge or a session: it waits for a
        // person rather than publishing text nobody judged, however long a sibling's refinement kept it ready.
        foreach (PlanDraftState draft in plan.Drafts.Where(draft => draft.Ready && !draft.IsClosed))
        {
            if (tasks.TryGetValue(draft.DraftId, out TaskDetails? ready) && ready.RevisedSinceLastDraftSession)
            {
                return await SavePlanAsync(session, fence, PlanDecider.ParkReadyDraftChanged(plan, draft, now), cancellationToken);
            }
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken);
        IReadOnlyList<Guid> withoutTrackerItem = project is null
            ? []
            : [.. plan.Drafts
                .Where(draft => draft.Published && tasks.TryGetValue(draft.DraftId, out TaskDetails? published)
                    && TrackerGap.Exists(project.BacklogPolicy, published))
                .Select(draft => draft.DraftId)];
        if (PlanDecider.End(plan, now, withoutTrackerItem) is { } end)
        {
            return await SavePlanAsync(session, fence, [end], cancellationToken);
        }

        if (PlanDecider.DraftsToPublish(plan) is [Guid next, ..])
        {
            return await PublishAsync(plan, next, fence, cancellationToken);
        }

        if (project is null)
        {
            return StepOutcome.Nothing;
        }

        if (await LaunchGateAsync(session, plan, project, cancellationToken) is not null)
        {
            return StepOutcome.Nothing;
        }

        foreach (PlanDraftState draft in plan.Drafts)
        {
            switch (PlanDecider.NextStep(draft))
            {
                // One at a time: a challenge the plan asked for and the challenge engine has not started would otherwise
                // start after the plan parked on a sibling's outcome, and the plan decides again once the first lands.
                case PlanStepKind.RequestChallenge when plan.Drafts.All(other => other.ChallengeRequestedAt is null):
                    return await RequestChallengeAsync(session, fence, plan, draft, project, now, cancellationToken);
                case PlanStepKind.DispatchRefinement when _sessions.IsEmpty:
                    return await DispatchAsync(session, fence, plan, draft, project, cancellationToken);
                default:
                    break;
            }
        }

        return StepOutcome.Nothing;
    }

    private async Task<StepOutcome> SavePlanAsync(
        IDocumentSession session, StreamState fence, IReadOnlyList<object> events, CancellationToken cancellationToken)
    {
        session.Events.Append(fence.Id, expectedVersion: fence.Version + events.Count, events.ToArray());
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("A plan changed while a step was being recorded; deciding it again");
            return StepOutcome.Nothing;
        }

        if (events.OfType<PlanParked>().FirstOrDefault() is { } parked)
        {
            logger.LogWarning(
                "Plan {PlanId} parked on draft {DraftId}: {Reason}", parked.Id, parked.DraftId, parked.Reason.Description);
        }

        return StepOutcome.Stepped;
    }

    /// <summary>
    /// Why this plan may not start a session or request a challenge right now, or null when it may: the node's launch
    /// hold, its spent spend budget, or the project's max parallel tasks of 0. <c>h9k plan show</c> reads the same three
    /// facts and says which, so the two cannot disagree about why a plan is not moving.
    /// </summary>
    private async Task<string?> LaunchGateAsync(
        IDocumentSession session, PlanAggregate plan, ProjectDetails project, CancellationToken cancellationToken)
    {
        string? reason = null;
        if (project.MaxParallelTasks == 0)
        {
            reason = $"project '{project.Name}' has max parallel tasks 0";
        }
        else if (await launchHold.CurrentHoldAsync(node.NodeId, cancellationToken) is { LaunchHoldActive: true })
        {
            reason = "this node's launch hold stands";
        }
        else if (_options.SpendBudgetTokens is { } budget)
        {
            DateTimeOffset periodStart = SpendPeriod.FromInput(_options.SpendPeriod).StartOf(DateTimeOffset.UtcNow);
            PeriodSpend spend = await PeriodSpend.ReadAsync(session, periodStart, cancellationToken);
            if (spend.TotalInputTokens >= budget)
            {
                reason = "this node's spend budget is spent";
            }
        }

        if (reason is null)
        {
            _reportedGate.Remove(plan.Id);
        }
        else if (!_reportedGate.TryGetValue(plan.Id, out string? reported) || reported != reason)
        {
            _reportedGate[plan.Id] = reason;
            logger.LogInformation("Plan {PlanId} starts no session and requests no challenge: {Reason}", plan.Id, reason);
        }

        return reason;
    }

    private async Task<StepOutcome> RequestChallengeAsync(
        IDocumentSession session, StreamState planFence, PlanAggregate plan, PlanDraftState draft, ProjectDetails project,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        StreamState? taskFence = await session.Events.FetchStreamStateAsync(draft.DraftId, cancellationToken);
        TaskAggregate? task = taskFence is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskAggregate>(
                draft.DraftId, version: taskFence.Version, token: cancellationToken);
        if (taskFence is null || task is null)
        {
            return StepOutcome.Nothing;
        }

        string checkout = ProjectCheckout.ForReading(project);
        string? missingCheckout = !Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout)
            ? checkout
            : null;
        try
        {
            DraftChallengeRequested requested = TaskDecider.RequestChallenge(
                task, now, plan.StartedByOwnerId, project.IsArchived, project.Name, missingCheckout);
            session.Events.Append(draft.DraftId, expectedVersion: taskFence.Version + 1, requested);
            session.Events.Append(
                plan.Id, expectedVersion: planFence.Version + 1,
                new PlanChallengeRequested(plan.Id, plan.ProjectId, draft.DraftId, draft.Round, now));
        }
        catch (DomainException refusal)
        {
            // The same rules the verb enforces, enforced again here: a draft can lose its criteria or its project's
            // checkout between refinement and challenge. The plan parks on the draft rather than asking again.
            logger.LogWarning("Plan {PlanId}: the challenge of draft {DraftId} was refused: {Reason}", plan.Id, draft.DraftId, refusal.Message);
            session.Events.Append(
                plan.Id, expectedVersion: planFence.Version + 1,
                PlanDecider.ParkOn(plan, draft.DraftId, PlanParkReason.ChallengeFailed, null, now));
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Plan {PlanId} or draft {DraftId} advanced while its challenge was being requested", plan.Id, draft.DraftId);
            return StepOutcome.Nothing;
        }

        await Doorbell.RingAsync(connection.ConnectionString, $"challenge-requested:{draft.DraftId}", cancellationToken);
        return StepOutcome.Stepped;
    }

    private async Task<StepOutcome> PublishAsync(
        PlanAggregate plan, Guid draftId, StreamState fence, CancellationToken cancellationToken)
    {
        TaskCommandResult result = await commands.PublishAsync(
            plan.Id, draftId, plan.NoExistingItemAttested, plan.UntrackedAttested, cancellationToken);

        // A draft that reads published counts as published however the step ended: a publish cut short exits non-zero
        // having committed, and a tracker failure exits zero. The task is the authority, not the exit code. A draft
        // abandoned while the publish waited on the tracker is abandoned, not published, and is recorded as that.
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(draftId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        object recorded = task switch
        {
            { State: var state } when state == TaskState.Abandoned => new PlanDraftAbandoned(plan.Id, plan.ProjectId, draftId, now),
            { State: var state } when state != TaskState.Draft => new PlanDraftPublished(plan.Id, plan.ProjectId, draftId, ByHand: false, now),
            _ => PlanDecider.ParkOn(plan, draftId, PlanParkReason.PublishRefused, null, now),
        };
        if (recorded is PlanParked)
        {
            logger.LogWarning(
                "Plan {PlanId}: publishing draft {DraftId} was refused (exit {ExitCode}, timed out {TimedOut}); its output is in {Output}",
                plan.Id, draftId, result.ExitCode, result.TimedOut, result.OutputFile);
        }

        return await SavePlanAsync(session, fence, [recorded], cancellationToken);
    }

    private async Task<StepOutcome> DispatchAsync(
        IDocumentSession session, StreamState planFence, PlanAggregate plan, PlanDraftState draft, ProjectDetails project,
        CancellationToken cancellationToken)
    {
        Guid taskId = draft.DraftId;
        RefinementPurpose purpose = draft.Purpose;
        AgentModel model = _options.ResolveModel(AgentRole.Refinement, null, project.Model);
        AgentEffort effort = _options.ResolveEffort(AgentRole.Refinement, null, project.Effort);

        StreamState? taskFence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        TaskAggregate? aggregate = taskFence is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, version: taskFence.Version, token: cancellationToken);
        if (taskFence is null || aggregate is null)
        {
            return StepOutcome.Nothing;
        }

        // A session the task already records as running, another node's still inside the foreign-session ceiling or a
        // person's earlier plan, is never joined by a second: the draft waits until that one has an outcome.
        if (aggregate.RunningRefinementSessionId is not null)
        {
            return StepOutcome.Nothing;
        }

        // The rules the verb enforced, enforced again where the session would start: the plan outlives the moment it
        // was started, and a project can be archived, lose its checkout, or stop skipping permissions in between.
        string checkout = ProjectCheckout.ForReading(project);
        string? refusal = aggregate.State != TaskState.Draft
            ? $"task is {aggregate.State.Value}, not Draft"
            : project.IsArchived
                ? "its project is archived"
                : !project.SkipPermissions
                    ? "its project no longer runs agents with skip-permissions"
                    : !Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout)
                        ? $"there is no working checkout of the project at {checkout}"
                        : null;
        if (refusal is not null)
        {
            logger.LogWarning("Plan {PlanId}: no refinement session was started for draft {DraftId}: {Why}", plan.Id, taskId, refusal);
            RefinementFailureReason failure = aggregate.State != TaskState.Draft
                ? RefinementFailureReason.NoLongerDraft
                : RefinementFailureReason.StartFailed;
            await RecordEndAsync(
                new SessionEnd(taskId, null, purpose, model, effort, RefinementOutcome.Unknown, failure, null),
                cancellationToken);
            return StepOutcome.Stepped;
        }

        // A verdict waiting to be applied judged the draft as it stood: a person's revision since then is text no challenge
        // read, so applying the verdict to it would publish it unjudged. The plan parks as draft changed, and a resume
        // challenges the draft as it now stands.
        if (purpose == RefinementPurpose.ApplyVerdict && aggregate.RevisedSinceLastDraftSession)
        {
            logger.LogWarning(
                "Plan {PlanId}: draft {DraftId} was revised after the verdict waiting to be applied; parking it", plan.Id, taskId);
            return await SavePlanAsync(
                session, planFence, PlanDecider.ParkReadyDraftChanged(plan, draft, DateTimeOffset.UtcNow), cancellationToken);
        }

        // Only the home's own dev/ is moved, the challenge engine's rule, and the outcome is carried into the prompt. The
        // lease is held until the session has ended: a challenge reads the same checkout on a loop of its own, and neither
        // moves the tree under the other's session (a checkout someone else is reading is left where it stands, and the
        // prompt says so).
        ReadingCheckoutJoin join = await checkoutLeases.JoinAsync(
            checkout,
            ProjectCheckout.IsHomeDevWorktree(project, checkout)
                ? () => worktrees.RefreshReadingCheckoutAsync(checkout, project.BaseBranch, cancellationToken)
                : null,
            cancellationToken);
        bool handedToJob = false;
        try
        {
            CheckoutRefresh? refresh = join.Refresh;
            if (refresh is not null)
            {
                logger.Log(
                    refresh.UpToDate
                        ? LogLevel.Information
                        : LogLevel.Warning,
                    "Refinement session for draft {TaskId} reads {Checkout}, which {Detail}", taskId, checkout, refresh.Detail);
            }

            string? commit = await HeadCommitAsync(checkout, cancellationToken);
            TaskDetails current = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
                ?? throw new InvalidOperationException($"Task {taskId} has a stream and no document.");

            Guid sessionId = DomainId.New();
            string sessionDirectory = RunPaths.GlobalDirectory(sessionId);
            (string? ideaWorkspace, string? taskDirectory) = await DraftChallengeEngine.LocateDraftFilesAsync(
                session, project, current, cancellationToken);
            string? journal = taskDirectory is null
                ? null
                : DraftChallengeEngine.FindJournal(taskDirectory);
            // Told to every session for a draft that has one: a plan started on a draft a person already challenged, or begun
            // again after one that exhausted its rounds, dispatches a plain refine whose draft was judged before. The report
            // is only on the node that ran the challenge, so a verdict that replicated from another names no path here.
            PriorVerdictFacts? prior = current.LatestChallengeVerdict is { } verdict
                ? new PriorVerdictFacts(
                    verdict.Verdict, verdict.MustCount, verdict.ShouldCount, verdict.ParkCount,
                    verdict is { SessionId: { } challengeSession } && verdict.NodeId == node.NodeId
                        ? RunPaths.ChallengeReportFile(challengeSession)
                        : null)
                : null;
            string reportPath = RunPaths.RefinementReportFile(sessionId);
            string revisionPath = RunPaths.RefinementRevisionFile(sessionId);
            string? projectHome = project.HomeDirectory.HasValue
                ? project.HomeDirectory.Value
                : null;
            string? journalToWrite = journal
                ?? (taskDirectory is null
                    ? null
                    : Path.Combine(taskDirectory, "journal.md"));
            string prompt = AgentPromptBuilder.BuildPlanRefinement(
                current, project,
                new PlanRefinementPromptFacts(
                    purpose, projectHome, checkout, commit, refresh, reportPath, revisionPath, sessionDirectory, ideaWorkspace,
                    journal, journalToWrite, prior));

            // Recorded before anything is spawned, RunLauncher's order, and on both streams in one save: committing first
            // is what makes a crash lose too, and the plan never claims a session the task does not know about.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            session.Events.Append(
                taskId, expectedVersion: taskFence.Version + 1,
                new DraftRefinementDispatched(taskId, sessionId, node.NodeId, now, model, effort, purpose));
            session.Events.Append(
                plan.Id, expectedVersion: planFence.Version + 1,
                new PlanRefinementDispatched(plan.Id, plan.ProjectId, taskId, sessionId, purpose, now));
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                logger.LogDebug("Draft {TaskId} or its plan advanced while its refinement was being dispatched; leaving it", taskId);
                return StepOutcome.Nothing;
            }

            RefinementJob job = new(
                plan.Id, taskId, sessionId, purpose, checkout, sessionDirectory, reportPath, revisionPath, prompt, model, effort);
            CancellationTokenSource cancel = new();
            TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessions[sessionId] = new RunningSession(plan.Id, taskId, cancel, finished);
            handedToJob = true;

            // Registered before the task starts and removed only by its own last act, so a sweep that runs while the
            // session is live never mistakes it for one a restart left behind.
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunJobAsync(job, cancel, _stopping.Token);
                    }
                    finally
                    {
                        join.Lease.Dispose();
                        _sessions.TryRemove(sessionId, out _);
                        cancel.Dispose();
                        finished.SetResult();
                        OnWork?.Invoke();
                    }
                },
                CancellationToken.None);
            return StepOutcome.Dispatched;
        }
        finally
        {
            if (!handedToJob)
            {
                join.Lease.Dispose();
            }
        }
    }

    private async Task RunJobAsync(RefinementJob job, CancellationTokenSource cancel, CancellationToken stopping)
    {
        try
        {
            await ExecuteAsync(job, cancel.Token, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The daemon is stopping; the outcome was recorded on the way out, or the next start records it.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Draft {TaskId}: the refinement session failed before an outcome could be recorded", job.TaskId);
            // Recorded with no token of its own on purpose: this is the last act of a job that has already failed, and a
            // cancelled daemon is exactly when the outcome most needs to land.
            await RecordEndQuietlyAsync(job, RefinementOutcome.Unknown, RefinementFailureReason.SessionError, null, CancellationToken.None);
        }
    }

    private async Task ExecuteAsync(RefinementJob job, CancellationToken planEnded, CancellationToken stopping)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, planEnded);
        SpawnedAgent agent;
        try
        {
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    job.SessionId, job.SessionId, job.Checkout, job.SessionDirectory, job.Prompt, ExecutorMode.Subscription,
                    job.Model, job.Effort,
                    // Required by `plan start`: the session writes its report, a revision, and the journal outside its
                    // checkout, which the plain settings refuse.
                    SkipPermissions: true)
                {
                    TaskId = job.TaskId,
                    SessionName = SessionRoleName.For(DomainId.Short(job.TaskId), SessionRoleName.Refinement),
                },
                linked.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Draft {TaskId}: the refinement session could not be spawned", job.TaskId);
            await RecordEndAsync(EndOf(job, RefinementOutcome.Unknown, RefinementFailureReason.StartFailed, null), CancellationToken.None);
            return;
        }

        await RecordProcessIdentityAsync(job, agent);
        logger.LogInformation(
            "Draft {TaskId}: refinement session {SessionId} dispatched (pid {ProcessId}, model {Model}, artifacts in {Directory})",
            job.TaskId, job.SessionId, agent.ProcessId, job.Model.Value, job.SessionDirectory);

        AgentResult? result = null;
        bool timedOut = false;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        budget.CancelAfter(_options.PlanRefinementTimeout);
        try
        {
            result = (await SessionResultWaiter.WaitAsync(
                RunPaths.StreamFile(job.SessionDirectory), agent.ProcessId, agent.StartedAt, processManager,
                onOutput: null, budget.Token)).Result;
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        {
            Terminate(job, agent);
            timedOut = true;
            logger.LogWarning(
                "Draft {TaskId}: the refinement session exceeded {Timeout} and was terminated", job.TaskId, _options.PlanRefinementTimeout);
        }
        catch (OperationCanceledException) when (planEnded.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            // The plan was cancelled: the session is stopped and nothing it wrote is applied.
            Terminate(job, agent);
            await RecordEndAsync(EndOf(job, RefinementOutcome.Unknown, RefinementFailureReason.Cancelled, null), CancellationToken.None);
            return;
        }
        catch (OperationCanceledException)
        {
            // The daemon is stopping: the session is detached and would outlive it, so it is stopped and its outcome
            // recorded with a token of its own. If that does not land, the next start records it.
            Terminate(job, agent);
            try
            {
                using CancellationTokenSource shutdown = new(ShutdownRecordTimeout);
                await RecordEndAsync(EndOf(job, RefinementOutcome.Unknown, RefinementFailureReason.DaemonRestarted, null), shutdown.Token);
            }
            catch (Exception recording)
            {
                logger.LogWarning(recording, "Draft {TaskId}: could not record the refinement outcome during shutdown; the next start does", job.TaskId);
            }

            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Draft {TaskId}: the refinement session (pid {ProcessId}) could not be seen through to an outcome", job.TaskId, agent.ProcessId);
            Terminate(job, agent);
            await RecordEndAsync(EndOf(job, RefinementOutcome.Unknown, RefinementFailureReason.SessionError, null), CancellationToken.None);
            return;
        }

        RefinementOutcome? outcome = null;
        RefinementFailureReason? failure = null;
        if (timedOut)
        {
            failure = RefinementFailureReason.TimedOut;
        }
        else if (result is null)
        {
            failure = RefinementFailureReason.NoResult;
        }
        else if (result.IsError)
        {
            failure = RefinementFailureReason.SessionError;
        }
        else
        {
            try
            {
                (outcome, failure) = await RefinementReport.ReadAsync(job.ReportPath, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Draft {TaskId}: the refinement report could not be read", job.TaskId);
                failure = RefinementFailureReason.NoReport;
            }

            if (outcome == RefinementOutcome.Revised)
            {
                failure = await ApplyRevisionAsync(job, CancellationToken.None);
            }
        }

        await RecordEndAsync(
            EndOf(job, outcome ?? RefinementOutcome.Unknown, failure ?? RefinementFailureReason.Unknown, result), CancellationToken.None);
    }

    /// <summary>
    /// Applies the revision a session wrote with the same validation and effects as <c>h9k task revise --file</c>, by
    /// running that command. Applied only to the draft the session read: if anyone changed it since dispatch the revision
    /// is not applied. A revision that names a model is refused, because a refinement sets none and <c>--file</c> would
    /// otherwise set one. A plan that ended while the session ran applies nothing, however the session ended: the stop a
    /// cancel sends reaches a job only at the top of a sweep, so the job asks the record itself. Returns the reason nothing
    /// was applied, or null when it was.
    /// </summary>
    private async Task<RefinementFailureReason?> ApplyRevisionAsync(RefinementJob job, CancellationToken cancellationToken)
    {
        await using (IQuerySession ended = store.QuerySession())
        {
            if (await ended.LoadAsync<PlanDetails>(job.PlanId, cancellationToken) is not { IsLive: true })
            {
                logger.LogInformation("Plan {PlanId} ended while refinement session {SessionId} ran; its revision is not applied", job.PlanId, job.SessionId);
                return RefinementFailureReason.Cancelled;
            }
        }

        string text;
        try
        {
            if (!File.Exists(job.RevisionPath))
            {
                return RefinementFailureReason.UnreadableReport;
            }

            text = await File.ReadAllTextAsync(job.RevisionPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Draft {TaskId}: the revision file could not be read", job.TaskId);
            return RefinementFailureReason.UnreadableReport;
        }

        try
        {
            if (FrontmatterYaml.Parse(text).Scalar("model").IsNotBlank())
            {
                logger.LogWarning("Draft {TaskId}: the revision names a model, which a refinement does not set; refused", job.TaskId);
                await WriteRefusalAsync(job, "The revision names a model, which a refinement does not set, so it was not applied.", cancellationToken);
                return RefinementFailureReason.RevisionRefused;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Draft {TaskId}: the revision file is not a task file; refused", job.TaskId);
            await WriteRefusalAsync(job, $"The revision file is not a task file, so it was not applied: {exception.Message}", cancellationToken);
            return RefinementFailureReason.RevisionRefused;
        }

        await using (IQuerySession query = store.QuerySession())
        {
            TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(job.TaskId, token: cancellationToken);
            if (task is null || task.State != TaskState.Draft)
            {
                return RefinementFailureReason.NoLongerDraft;
            }

            if (task.RevisedWhileRefinementRan)
            {
                logger.LogWarning("Draft {TaskId}: the draft was revised after the session was dispatched; its revision is not applied", job.TaskId);
                return RefinementFailureReason.DraftChanged;
            }
        }

        // The check above only spares a child that would refuse. The child makes the real one: told which session it acts
        // for, it applies the revision only if nobody revised the draft since that session was dispatched, in the same
        // fenced append, and records it as the session's own. A person's revise cannot slip between the two.
        TaskCommandResult result = await commands.ReviseAsync(job.PlanId, job.SessionId, job.TaskId, job.RevisionPath, cancellationToken);
        if (!result.Succeeded)
        {
            logger.LogWarning(
                "Draft {TaskId}: applying the revision was refused (exit {ExitCode}, timed out {TimedOut}); its output is in {Output}",
                job.TaskId, result.ExitCode, result.TimedOut, result.OutputFile);

            // A child that refused because the draft changed under it left the record saying so; any other refusal did not.
            await using IQuerySession after = store.QuerySession();
            TaskDetails? latest = await after.LoadAsync<TaskDetails>(job.TaskId, cancellationToken);
            return latest is { RevisedWhileRefinementRan: true }
                ? RefinementFailureReason.DraftChanged
                : RefinementFailureReason.RevisionRefused;
        }

        return null;
    }

    /// <summary>
    /// A revision refused before the child <c>h9k</c> ran has no command output, so the reason is written where
    /// <c>h9k plan show</c> sends a person to read a refusal, and the pointer is true.
    /// </summary>
    private async Task WriteRefusalAsync(RefinementJob job, string reason, CancellationToken cancellationToken)
    {
        string file = RunPaths.PlanReviseOutputFile(job.PlanId, job.SessionId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"No directory for {file}."));
            await File.WriteAllTextAsync(file, reason + Environment.NewLine, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Draft {TaskId}: could not write the refusal beside the plan's other output", job.TaskId);
        }
    }

    private static SessionEnd EndOf(RefinementJob job, RefinementOutcome outcome, RefinementFailureReason failure, AgentResult? result) =>
        new(job.TaskId, job.SessionId, job.Purpose, job.Model, job.Effort, outcome, failure, result);

    private async Task RecordEndQuietlyAsync(
        RefinementJob job, RefinementOutcome outcome, RefinementFailureReason failure, AgentResult? result,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordEndAsync(EndOf(job, outcome, failure, result), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not record the refinement outcome for draft {TaskId}", job.TaskId);
        }
    }

    /// <summary>
    /// Records how a session ended on both streams in one save: the task's outcome (and the spend it reported, under the
    /// model it ran on), and the plan's step. A failure, a refused revision, or a changed draft parks the plan on the
    /// draft; a report that was applied moves the draft where <see cref="PlanDecider.AfterRefinement"/> says. A plan that
    /// ended while the session ran records the task's outcome and nothing on the plan. A lost race with a person's command
    /// reloads both streams and decides again.
    /// </summary>
    private async Task RecordEndAsync(SessionEnd end, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= RecordAttempts; attempt++)
        {
            await using IDocumentSession session = store.LightweightSession();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            StreamState? taskFence = await session.Events.FetchStreamStateAsync(end.TaskId, cancellationToken);
            if (taskFence is null)
            {
                return;
            }

            List<object> taskEvents = [];
            if (end.Result is not null)
            {
                taskEvents.Add(end.Result.ToPublicationTokensRecorded(end.TaskId, now, end.Model));
            }

            taskEvents.Add(new DraftRefinementRecorded(
                end.TaskId, end.SessionId, end.RanOnNodeId ?? node.NodeId, end.Outcome, end.Failure, end.Purpose, end.Model, end.Effort, now));
            session.Events.Append(end.TaskId, expectedVersion: taskFence.Version + taskEvents.Count, taskEvents.ToArray());

            if (await PlanOfAsync(session, end.TaskId, end.SessionId, cancellationToken) is { } owner)
            {
                (PlanAggregate plan, StreamState planFence) = owner;
                PlanDraftState draft = plan.Draft(end.TaskId)
                    ?? throw new InvalidOperationException($"Plan {plan.Id} does not hold draft {end.TaskId}.");
                IReadOnlyList<object> planEvents = end.Failure.IsFailure
                    ? [PlanDecider.ParkOn(plan, end.TaskId, ParkReasonOf(end.Failure), end.SessionId, now)]
                    : PlanDecider.AfterRefinement(plan, draft, end.Purpose, end.Outcome, end.SessionId ?? Guid.Empty, now);
                session.Events.Append(plan.Id, expectedVersion: planFence.Version + planEvents.Count, planEvents.ToArray());
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (EventStreamUnexpectedMaxEventIdException exception) when (attempt < RecordAttempts)
            {
                logger.LogDebug(exception, "Draft {TaskId}: a stream moved while a refinement outcome was recorded; retrying", end.TaskId);
            }
        }
    }

    private static PlanParkReason ParkReasonOf(RefinementFailureReason failure) =>
        failure == RefinementFailureReason.DraftChanged
            ? PlanParkReason.DraftChanged
            : failure == RefinementFailureReason.RevisionRefused
                ? PlanParkReason.RevisionRefused
                : PlanParkReason.SessionFailed;

    /// <summary>
    /// The live plan whose draft was running this session (or, for a dispatch refused before any session, the live plan
    /// holding the draft), with its stream fence; null when the plan has ended or no plan on this node claims the session.
    /// A session a person cancelled the plan under, or one a foreign node dispatched, is task-only.
    /// </summary>
    private static async Task<(PlanAggregate Plan, StreamState Fence)?> PlanOfAsync(
        IDocumentSession session, Guid draftId, Guid? sessionId, CancellationToken cancellationToken)
    {
        PlanDetails? owner = (await session.Query<PlanDetails>().ToListAsync(cancellationToken))
            .Where(plan => plan.IsLive && plan.Draft(draftId) is { } draft && draft.RunningSessionId == sessionId)
            .OrderBy(plan => plan.StartedAt)
            .FirstOrDefault();
        if (owner is null)
        {
            return null;
        }

        StreamState? fence = await session.Events.FetchStreamStateAsync(owner.Id, cancellationToken);
        PlanAggregate? plan = fence is null
            ? null
            : await session.Events.AggregateStreamAsync<PlanAggregate>(owner.Id, version: fence.Version, token: cancellationToken);
        return fence is null || plan is null || !plan.IsLive
            ? null
            : (plan, fence);
    }

    /// <summary>
    /// Sessions this node's own record says are running that nothing in this process is watching, recorded as failed and
    /// never dispatched again (a restart's mark, <see cref="Closeout.CloseoutTriageSessions"/>'s rule), and another node's
    /// dispatch that has stood past the foreign-session ceiling, ended on the clock alone. The sweep is serial, so a
    /// dispatch of this node's own seen at the top of a sweep is one no live session of this process accounts for.
    /// </summary>
    private async Task<(int Recovered, int Expired)> RecoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDetails> running;
        await using (IQuerySession query = store.QuerySession())
        {
            running = await query.Query<TaskDetails>()
                .Where(task => task.RunningRefinementSessionId != null)
                .ToListAsync(cancellationToken);
        }

        int recovered = 0;
        int expired = 0;
        foreach (TaskDetails task in running)
        {
            if (task.RunningRefinementSessionId is not { } sessionId)
            {
                continue;
            }

            AgentModel model = task.RunningRefinementModel ?? AgentModel.Unknown;
            AgentEffort effort = task.RunningRefinementEffort ?? AgentEffort.Unknown;
            if (task.RunningRefinementNodeId == node.NodeId)
            {
                if (_sessions.ContainsKey(sessionId))
                {
                    continue;
                }

                // A job commits its outcome before it leaves _sessions, so a session absent from it may have ended
                // after the snapshot above was read. Read the task again: only one still recorded running is left behind.
                await using (IQuerySession fresh = store.QuerySession())
                {
                    TaskDetails? latest = await fresh.LoadAsync<TaskDetails>(task.Id, cancellationToken);
                    if (latest?.RunningRefinementSessionId != sessionId)
                    {
                        continue;
                    }
                }

                logger.LogWarning(
                    "Draft {TaskId}: refinement session {SessionId} was dispatched by this node and nothing is watching it; stopping it and recording it as failed",
                    task.Id, sessionId);
                await TerminateLeftBehindAsync(task.Id, sessionId, cancellationToken);
                await RecordEndAsync(
                    new SessionEnd(
                        task.Id, sessionId, await PurposeOfAsync(task.Id, sessionId, cancellationToken), model, effort,
                        RefinementOutcome.Unknown, RefinementFailureReason.DaemonRestarted, null),
                    cancellationToken);
                recovered++;
            }
            else if (task.RunningRefinementDispatchedAt is { } dispatchedAt
                && DateTimeOffset.UtcNow - dispatchedAt > _options.ForeignPublicationCeiling)
            {
                logger.LogWarning(
                    "Draft {TaskId}: refinement session {SessionId} dispatched by node {Node} at {DispatchedAt:u} has stood longer than {Ceiling} with no outcome; ending it here",
                    task.Id, sessionId, task.RunningRefinementNodeId, dispatchedAt, _options.ForeignPublicationCeiling);
                await RecordEndAsync(
                    new SessionEnd(
                        task.Id, sessionId, RefinementPurpose.Unknown, model, effort, RefinementOutcome.Unknown,
                        RefinementFailureReason.DispatchExpired, null, task.RunningRefinementNodeId),
                    cancellationToken);
                expired++;
            }
        }

        return (recovered, expired);
    }

    private async Task<RefinementPurpose> PurposeOfAsync(Guid draftId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        PlanDetails? owner = (await query.Query<PlanDetails>().ToListAsync(cancellationToken))
            .FirstOrDefault(plan => plan.Draft(draftId)?.RunningSessionId == sessionId);
        return owner?.Draft(draftId)?.Purpose ?? RefinementPurpose.Unknown;
    }

    /// <summary>Stops the session of any plan that has ended while it ran; its job records the cancellation and applies nothing.</summary>
    private async Task StopSessionsOfEndedPlansAsync(CancellationToken cancellationToken)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        await using IQuerySession query = store.QuerySession();
        foreach ((Guid sessionId, RunningSession running) in _sessions)
        {
            PlanDetails? plan = await query.LoadAsync<PlanDetails>(running.PlanId, cancellationToken);
            if (plan is { IsLive: false })
            {
                logger.LogInformation("Plan {PlanId} ended while refinement session {SessionId} ran; stopping it", running.PlanId, sessionId);
                try
                {
                    running.Cancel.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The job finished between the check and the cancel; nothing is left to stop.
                }
            }
        }
    }

    /// <summary>The commit the checkout is at, read by the daemon rather than asked of the session; null when git cannot answer.</summary>
    private async Task<string?> HeadCommitAsync(string checkout, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await processRunner("git", ["rev-parse", "HEAD"], checkout, cancellationToken);
            return result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } sha
                ? sha
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read HEAD in {Checkout} for a refinement session", checkout);
            return null;
        }
    }

    /// <summary>
    /// Writes the session's pid and start time beside its report, so the next start can stop a session a crashed daemon
    /// left running. The write takes no cancellation token on purpose, for the reason the challenge engine's does: the
    /// session is already running and the file is a few bytes.
    /// </summary>
    private async Task RecordProcessIdentityAsync(RefinementJob job, SpawnedAgent agent)
    {
        try
        {
            string file = RunPaths.RefinementProcessFile(job.SessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"No directory for {file}."));
            await File.WriteAllTextAsync(file, $"{agent.ProcessId}\n{agent.StartedAt.UtcTicks}\n", CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception, "Draft {TaskId}: could not write the refinement session's process identity; a daemon crash would leave it running",
                job.TaskId);
        }
    }

    /// <summary>
    /// Stops the session a previous daemon process left running, found by the identity file written at spawn. The pid and
    /// start time together are the identity, so a pid the operating system has since reused is not touched.
    /// </summary>
    private async Task TerminateLeftBehindAsync(Guid taskId, Guid sessionId, CancellationToken cancellationToken)
    {
        string file = RunPaths.RefinementProcessFile(sessionId);
        try
        {
            if (!File.Exists(file))
            {
                return;
            }

            string[] lines = await File.ReadAllLinesAsync(file, cancellationToken);
            if (lines.Length >= 2 && int.TryParse(lines[0], out int processId) && long.TryParse(lines[1], out long ticks))
            {
                processManager.Terminate(processId, new DateTimeOffset(ticks, TimeSpan.Zero));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Draft {TaskId}: could not stop refinement session {SessionId}; recording it as failed anyway", taskId, sessionId);
        }
    }

    private void Terminate(RefinementJob job, SpawnedAgent agent)
    {
        try
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Draft {TaskId}: could not terminate the refinement session (pid {ProcessId})", job.TaskId, agent.ProcessId);
        }
    }
}
