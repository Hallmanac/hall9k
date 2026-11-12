using System.Collections.Concurrent;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Challenge;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Feedback;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
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
/// Drives plans (<c>h9k plan start</c>): the daemon's half of a plan, on the node where it was started. A plan started from
/// an idea (<c>--idea</c>) begins in discovery: it dispatches one discovery session, copies a readable report into the idea's
/// workspace, and parks at a gate for a person; the drafts exist only after <c>h9k plan pursue</c> cut them, and from then on
/// the plan is driven as any other. A sweep takes
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
    ILogger<PlanEngine> logger,
    LocalFleetProvider? fleets = null,
    TimeProvider? clock = null) : IDisposable
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
    private readonly ForeignDispatchSettle _foreignSettle = new(options.Value.ForeignSessionSettlePeriod);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The start of the spend period last confirmed spent, or null; see <see cref="LaunchGateAsync"/>. In memory, like <see cref="_reportedGate"/>.</summary>
    private DateTimeOffset? _spendExhaustedSincePeriodStart;

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
        AgentEffort Effort,
        IReadOnlyList<SolutionMemberTask>? MemberTasks,
        bool FeedbackStep = false);

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
            await StopSessionsOfEndedWorkAsync(cancellationToken);

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

        // A plan started from an idea has no draft until a person pursues it at the gate, so none of the rules over drafts
        // below applies to it yet: its one step is discovery.
        if (plan.InDiscovery)
        {
            return await StepDiscoveryAsync(session, fence, plan, now, cancellationToken);
        }

        IReadOnlyList<TaskDetails> loaded = await session.LoadManyAsync<TaskDetails>(
            cancellationToken, [.. plan.Drafts.Select(draft => draft.DraftId)]);
        Dictionary<Guid, TaskDetails> tasks = loaded.ToDictionary(task => task.Id);

        // A draft published or abandoned outside the plan's own publish step is closed here, which also answers any park on it.
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

            // The plan's own publish can commit and the daemon stop before the plan records it, so the task, not the plan,
            // says whose publish it was: one that reads by hand here would tell a feedback step a person took over.
            if (task.State != TaskState.Draft)
            {
                return await SavePlanAsync(
                    session, fence, [new PlanDraftPublished(plan.Id, plan.ProjectId, draft.DraftId, ByHand: !task.LatestPublishByPlan, now)],
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
                        plan, draft, outcome, plan.IsFeedbackStep ? PlanRoundCap.FeedbackStep : _options.PlanRoundCap, now,
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

        // Asked before the launch gate: a plan that is only waiting on a session or a challenge has nothing to start, and
        // the gate's spend read is not free on a budgeted node.
        foreach (PlanDraftState draft in plan.Drafts)
        {
            PlanStepKind step = PlanDecider.NextStep(draft);
            bool startable = step switch
            {
                // One at a time: a challenge the plan asked for and the challenge engine has not started would otherwise
                // start after the plan parked on a sibling's outcome, and the plan decides again once the first lands.
                PlanStepKind.RequestChallenge => plan.Drafts.All(other => other.ChallengeRequestedAt is null),
                PlanStepKind.DispatchRefinement => _sessions.IsEmpty,
                _ => false,
            };
            if (!startable)
            {
                continue;
            }

            // A feedback step's plan asks for one challenge, ever, however the draft came back to challenge (a person's resume
            // included). A second ask parks the plan instead, and the park never reaches a session or a challenge.
            if (step == PlanStepKind.RequestChallenge && !PlanDecider.MayRequestChallenge(plan, draft))
            {
                logger.LogWarning(
                    "Plan {PlanId}: draft {DraftId} would be challenged a second time, which a feedback step never does; parking it",
                    plan.Id, draft.DraftId);
                return await SavePlanAsync(
                    session, fence, [PlanDecider.ParkOneChallengeOnly(plan, draft, now)], cancellationToken);
            }

            if (await LaunchGateAsync(session, plan, project, cancellationToken) is not null)
            {
                return StepOutcome.Nothing;
            }

            return step == PlanStepKind.RequestChallenge
                ? await RequestChallengeAsync(session, fence, plan, draft, project, now, cancellationToken)
                : await DispatchAsync(session, fence, plan, draft, project, cancellationToken);
        }

        // Nothing to start, so the gate is not read and a gate reported earlier is no longer in the way of anything.
        _reportedGate.Remove(plan.Id);
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
            // Spend only grows within a period, so once it is confirmed spent the answer stands until the period rolls over
            // (the dispatch engine's rule, and for the same reason: the read sums every spend event since the period began).
            DateTimeOffset periodStart = SpendPeriod.FromInput(_options.SpendPeriod).StartOf(DateTimeOffset.UtcNow);
            bool spent = _spendExhaustedSincePeriodStart == periodStart;
            if (!spent)
            {
                PeriodSpend spend = await PeriodSpend.ReadAsync(session, periodStart, cancellationToken);
                spent = spend.TotalInputTokens >= budget;
                _spendExhaustedSincePeriodStart = spent
                    ? periodStart
                    : null;
            }

            if (spent)
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
        // read, so applying the verdict to it would publish it unjudged. The plan parks as draft changed; when a person has
        // already resumed a park on this draft (the revision is what they resumed for) it goes straight back to a challenge
        // of the draft as it now stands instead of parking a second time for the same thing.
        if ((purpose == RefinementPurpose.ApplyVerdict || purpose == RefinementPurpose.JudgeVerdict) && aggregate.RevisedSinceLastDraftSession)
        {
            if (draft.ResumedFromPark)
            {
                logger.LogInformation(
                    "Plan {PlanId}: draft {DraftId} was revised after the verdict waiting to be applied and a person resumed it; challenging it again",
                    plan.Id, taskId);
                return await SavePlanAsync(session, planFence, [PlanDecider.BackToChallenge(plan, draft, DateTimeOffset.UtcNow)], cancellationToken);
            }

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
            IReadOnlyList<SiblingDraftFacts> siblings = await SiblingsOfAsync(session, plan, taskId, cancellationToken);
            string? discoveryReport = await DiscoveryReportOfAsync(session, plan, cancellationToken);
            IReadOnlyList<SolutionMemberTask>? memberTasks = await OtherMemberTasksOfAsync(session, current, cancellationToken);

            // A feedback step's session is told why the task is back in Draft: the notes its blockers left, read the way the
            // check read them, so a note the trust rule holds back is in neither prompt.
            FeedbackStepPromptFacts? feedback = plan.IsFeedbackStep && current.FeedbackStep is { } started
                ? new FeedbackStepPromptFacts(await FeedbackNotes.LoadAsync(
                    session, started.BlockerIds, node.NodeId,
                    fleets is null
                        ? null
                        : async token => (await fleets.GetAsync(project.Id, token))?.NodeIds,
                    cancellationToken))
                : null;
            string prompt = AgentPromptBuilder.BuildPlanRefinement(
                current, project,
                new PlanRefinementPromptFacts(
                    purpose, projectHome, checkout, commit, refresh, reportPath, revisionPath, sessionDirectory, ideaWorkspace,
                    journal, journalToWrite, prior, siblings, discoveryReport, memberTasks, feedback));

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
                plan.Id, taskId, sessionId, purpose, checkout, sessionDirectory, reportPath, revisionPath, prompt, model, effort, memberTasks,
                plan.IsFeedbackStep);
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
            // The plan was cancelled, or the draft was closed: the session is stopped and nothing it wrote is applied.
            Terminate(job, agent);
            await RecordEndAsync(
                EndOf(job, RefinementOutcome.Unknown, await WhyStoppedAsync(job, CancellationToken.None), null), CancellationToken.None);
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
            else if (outcome == RefinementOutcome.Split)
            {
                failure = await VetSplitAsync(job, CancellationToken.None);
            }
        }

        await RecordEndAsync(
            EndOf(job, outcome ?? RefinementOutcome.Unknown, failure ?? RefinementFailureReason.Unknown, result), CancellationToken.None);
    }

    /// <summary>
    /// Applies the revision a session wrote with the same validation and effects as <c>h9k task revise --file</c>, by
    /// running that command. Applied only to the draft the session read: if anyone changed it since dispatch the revision
    /// is not applied. A revision that names a model is refused, because a refinement sets none and <c>--file</c> would
    /// otherwise set one, and so is one that copies another member project's task objective into its
    /// objective, criteria or context (<see cref="MemberTextCopyGuard"/>). A plan that ended, or a draft that was closed, while the session ran applies nothing, however the
    /// session ended: the stop that follows reaches a job only at the top of a sweep, so the job asks the record itself. The
    /// child asks it again inside its own fenced append, which is where a cancel that lands after this check is caught.
    /// Returns the reason nothing was applied, or null when it was.
    /// </summary>
    private async Task<RefinementFailureReason?> ApplyRevisionAsync(RefinementJob job, CancellationToken cancellationToken)
    {
        await using (IQuerySession ended = store.QuerySession())
        {
            RefinementFailureReason? stopped = await ended.LoadAsync<PlanDetails>(job.PlanId, cancellationToken) is { } plan
                ? StoppedFor(plan, job.TaskId)
                : RefinementFailureReason.Cancelled;
            if (stopped is not null)
            {
                logger.LogInformation(
                    "Draft {TaskId} of plan {PlanId} was closed or its plan ended while refinement session {SessionId} ran ({Reason}); its revision is not applied",
                    job.TaskId, job.PlanId, job.SessionId, stopped.Value);
                return stopped;
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
            // A feedback step changes the objective, criteria, and context and nothing else: a revision that names a dependency,
            // a stack link, a type, an epic, or a model is refused whole, and the step parks for a person.
            if (job.FeedbackStep && FeedbackRevisionRules.FirstKeyBeyondText(text) is { } beyond)
            {
                logger.LogWarning(
                    "Draft {TaskId}: the revision of a feedback step names {Key}, which a feedback step does not change; refused", job.TaskId, beyond);
                await WriteRefusalAsync(
                    job, $"The revision names {beyond}, and a feedback step changes only the objective, criteria, and context, so it was not applied.",
                    cancellationToken);
                return RefinementFailureReason.RevisionBeyondText;
            }

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

        if (MemberTextCopyGuard.Refusal(text, job.MemberTasks) is { } copied)
        {
            logger.LogWarning("Draft {TaskId}: the revision copies another member project's task text; refused", job.TaskId);
            await WriteRefusalAsync(job, copied, cancellationToken);
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
    /// A proposed split applies no revision, but each increment line becomes a draft's objective once a person accepts it, so the
    /// lines are held to the rule a revision is (<see cref="MemberTextCopyGuard"/>) before the split is offered. A split that copies
    /// a member task's text parks as a refused revision, with the refusal written where <c>h9k plan show</c> sends a person to read
    /// it. A person's later edit of the report is not checked. Returns the reason the split is not offered, or null.
    /// </summary>
    private async Task<RefinementFailureReason?> VetSplitAsync(RefinementJob job, CancellationToken cancellationToken)
    {
        RefinementSplitReading reading = await RefinementSplitReport.ReadFileAsync(job.ReportPath, cancellationToken);
        if (reading.Increments is not { } increments)
        {
            return RefinementFailureReason.UnreadableReport;
        }

        if (MemberTextCopyGuard.SplitRefusal(increments, job.MemberTasks) is { } copied)
        {
            logger.LogWarning("Draft {TaskId}: the proposed split copies another member project's task text; refused", job.TaskId);
            await WriteRefusalAsync(job, copied, cancellationToken);
            return RefinementFailureReason.RevisionRefused;
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
                // A proposed split is offered only for a draft whose type the increment rule covers, read as the session left it.
                TaskType? draftType = end.Outcome == RefinementOutcome.Split
                    ? (await session.LoadAsync<TaskDetails>(end.TaskId, cancellationToken))?.Type
                    : null;
                IReadOnlyList<object> planEvents = end.Failure.IsFailure
                    ? [PlanDecider.ParkOn(plan, end.TaskId, ParkReasonOf(end.Failure), end.SessionId, now)]
                    : PlanDecider.AfterRefinement(plan, draft, end.Purpose, end.Outcome, end.SessionId ?? Guid.Empty, now, draftType);
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
                : failure == RefinementFailureReason.RevisionBeyondText
                    ? PlanParkReason.RevisionBeyondText
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
    /// The plan's other drafts, by id and objective, for a refinement session of a plan that carries more than one; empty for
    /// a plan of one. An abandoned draft is left out, because nothing can usefully depend on one.
    /// </summary>
    private static async Task<IReadOnlyList<SiblingDraftFacts>> SiblingsOfAsync(
        IDocumentSession session, PlanAggregate plan, Guid draftId, CancellationToken cancellationToken)
    {
        Guid[] others = [.. plan.Drafts.Where(draft => draft.DraftId != draftId && !draft.Abandoned).Select(draft => draft.DraftId)];
        if (others.Length == 0)
        {
            return [];
        }

        IReadOnlyList<TaskDetails> tasks = await session.LoadManyAsync<TaskDetails>(cancellationToken, others);
        return [.. others
            .Select(id => tasks.FirstOrDefault(task => task.Id == id))
            .OfType<TaskDetails>()
            .Select(task => new SiblingDraftFacts(task.Id, task.Objective))];
    }

    /// <summary>
    /// The tasks under a draft's solution epic in the other member projects this node holds, for its refinement session; null when
    /// the draft is in a project epic or none, or its own project has left the solution. Every solution-epic copy is loaded and
    /// filtered in memory by key (case-insensitively) and membership in <see cref="SolutionEpicMemberTasks.Choose"/>; the tasks read
    /// are only the lean row under each other member's copy, so no task's context or criteria is ever loaded for the prompt.
    /// </summary>
    internal static async Task<IReadOnlyList<SolutionMemberTask>?> OtherMemberTasksOfAsync(
        IDocumentSession session, TaskDetails draft, CancellationToken cancellationToken)
    {
        if (draft.EpicId is not { } epicId
            || await session.LoadAsync<EpicDetails>(epicId, cancellationToken) is not { SolutionEpicKey: { } epicKey } ownCopy)
        {
            return null;
        }

        // The key is compared ignoring case, as EpicGroup and SolutionEpicMemberTasks.Choose do, so the filter runs here rather than in the query.
        IReadOnlyList<EpicDetails> solutionCopies = await session.Query<EpicDetails>()
            .Where(epic => epic.SolutionEpicKey != null)
            .ToListAsync(cancellationToken);
        EpicDetails[] copies = [.. solutionCopies.Where(epic => string.Equals(epic.SolutionEpicKey, epicKey, StringComparison.OrdinalIgnoreCase))];
        List<TaskListItem> tasks = [];
        foreach (EpicDetails copy in copies.Where(copy => copy.ProjectId != ownCopy.ProjectId))
        {
            Guid copyId = copy.Id;
            tasks.AddRange(await session.Query<TaskListItem>()
                .Where(task => task.EpicId == copyId)
                .ToListAsync(cancellationToken));
        }

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        return SolutionEpicMemberTasks.Choose(ownCopy, copies, tasks, context);
    }

    /// <summary>The report a plan's discovery wrote, as a path in the idea's workspace now, for the refinement of a draft cut from it; null for a plan started from drafts.</summary>
    private static async Task<string?> DiscoveryReportOfAsync(IDocumentSession session, PlanAggregate plan, CancellationToken cancellationToken) =>
        plan.IdeaId is { } ideaId
        && await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken) is { } idea
            ? DiscoveryWorkspace.ReportPathOf(idea, plan.Id)
            : null;

    private sealed record DiscoveryJob(
        Guid PlanId,
        Guid IdeaId,
        Guid SessionId,
        string Checkout,
        string SessionDirectory,
        string ReportPath,
        string Prompt,
        AgentModel Model,
        AgentEffort Effort);

    /// <summary>
    /// The one step of a plan still in discovery. An idea that is no longer Captured, or is gone, ends the plan cancelled
    /// whatever else is going on (its session is stopped by the next top of a sweep, and nothing is copied). Otherwise a plan
    /// that is not parked and has no session running dispatches one, on the same one-at-a-time loop as a refinement session and
    /// under the same launch hold, spend budget, and max-parallel rules; a discovery session takes no ceiling slot.
    /// </summary>
    private async Task<StepOutcome> StepDiscoveryAsync(
        IDocumentSession session, StreamState fence, PlanAggregate plan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IdeaAggregate? idea = plan.IdeaId is { } ideaId
            ? await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken)
            : null;
        if (idea is null || idea.State != IdeaState.Captured)
        {
            logger.LogInformation(
                "Plan {PlanId}: its idea is {State}, not Captured, so the plan ends cancelled and its discovery session is stopped",
                plan.Id, idea?.State.Value ?? "gone");
            return await SavePlanAsync(
                session, fence,
                [new PlanCancelled(plan.Id, plan.ProjectId, "the idea is no longer Captured, so discovery has nothing to run on", null, now)],
                cancellationToken);
        }

        if (PlanDecider.NextDiscoveryStep(plan) != PlanStepKind.DispatchDiscovery || !_sessions.IsEmpty)
        {
            return StepOutcome.Nothing;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(plan.ProjectId, cancellationToken);
        if (project is null || await LaunchGateAsync(session, plan, project, cancellationToken) is not null)
        {
            return StepOutcome.Nothing;
        }

        return await DispatchDiscoveryAsync(session, fence, plan, idea, project, cancellationToken);
    }

    private async Task<StepOutcome> DispatchDiscoveryAsync(
        IDocumentSession session, StreamState planFence, PlanAggregate plan, IdeaAggregate idea, ProjectDetails project,
        CancellationToken cancellationToken)
    {
        AgentModel model = _options.ResolveModel(AgentRole.Refinement, null, project.Model);
        AgentEffort effort = _options.ResolveEffort(AgentRole.Refinement, null, project.Effort);
        string checkout = ProjectCheckout.ForReading(project);
        string? workspace = DiscoveryWorkspace.DirectoryOf(idea);

        // The rules the verb enforced, enforced again where the session would start: the plan outlives the moment it was
        // started, and a project can be archived, lose its checkout, or stop skipping permissions, and a workspace can go.
        string? refusal = project.IsArchived
            ? "its project is archived"
            : !project.SkipPermissions
                ? "its project no longer runs agents with skip-permissions"
                : !Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout)
                    ? $"there is no working checkout of the project at {checkout}"
                    : workspace is null || !Directory.Exists(workspace)
                        ? "the idea has no workspace directory on this node"
                        : null;
        if (refusal is not null || workspace is null)
        {
            logger.LogWarning("Plan {PlanId}: no discovery session was started for idea {IdeaId}: {Why}", plan.Id, idea.Id, refusal);
            return await SavePlanAsync(
                session, planFence,
                [PlanDecider.ParkDiscovery(plan, PlanParkReason.SessionFailed, null, RefinementFailureReason.StartFailed, DateTimeOffset.UtcNow)],
                cancellationToken);
        }

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
                    "Discovery session for idea {IdeaId} reads {Checkout}, which {Detail}", idea.Id, checkout, refresh.Detail);
            }

            string? commit = await HeadCommitAsync(checkout, cancellationToken);
            IReadOnlyList<TaskDetails> cut = idea.CutTaskIds.Count == 0
                ? []
                : await session.LoadManyAsync<TaskDetails>(cancellationToken, [.. idea.CutTaskIds]);
            string journalPath = Path.Combine(workspace, "journal.md");
            string[] earlier =
            [
                .. Directory.EnumerateFiles(workspace, "discovery-*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal),
            ];

            Guid sessionId = DomainId.New();
            string sessionDirectory = RunPaths.GlobalDirectory(sessionId);
            string reportPath = RunPaths.DiscoveryReportFile(sessionId);
            string prompt = AgentPromptBuilder.BuildPlanDiscovery(
                project,
                new PlanDiscoveryPromptFacts(
                    idea.Id, idea.Text,
                    [.. cut.Select(task => new DiscoveryCutTaskFacts(task.Id, task.State.Value, task.Objective))],
                    project.HomeDirectory.HasValue
                        ? project.HomeDirectory.Value
                        : null,
                    checkout, commit, refresh, workspace,
                    File.Exists(journalPath)
                        ? journalPath
                        : null,
                    earlier, reportPath, sessionDirectory, _options.PlanRefinementTimeout));

            // Recorded before anything is spawned, RunLauncher's order: committing first is what makes a crash lose too.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            session.Events.Append(
                plan.Id, expectedVersion: planFence.Version + 1,
                new PlanDiscoveryDispatched(plan.Id, plan.ProjectId, idea.Id, sessionId, now));
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                logger.LogDebug("Plan {PlanId} advanced while its discovery was being dispatched; leaving it", plan.Id);
                return StepOutcome.Nothing;
            }

            DiscoveryJob job = new(plan.Id, idea.Id, sessionId, checkout, sessionDirectory, reportPath, prompt, model, effort);
            CancellationTokenSource cancel = new();
            TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessions[sessionId] = new RunningSession(plan.Id, Guid.Empty, cancel, finished);
            handedToJob = true;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunDiscoveryJobAsync(job, cancel, _stopping.Token);
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

    private async Task RunDiscoveryJobAsync(DiscoveryJob job, CancellationTokenSource cancel, CancellationToken stopping)
    {
        try
        {
            await ExecuteDiscoveryAsync(job, cancel.Token, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The daemon is stopping; the outcome was recorded on the way out, or the next start records it.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Plan {PlanId}: the discovery session failed before an outcome could be recorded", job.PlanId);
            await RecordDiscoveryEndQuietlyAsync(job.PlanId, job.SessionId, RefinementFailureReason.SessionError, null, null, CancellationToken.None);
        }
    }

    private async Task ExecuteDiscoveryAsync(DiscoveryJob job, CancellationToken planEnded, CancellationToken stopping)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, planEnded);
        SpawnedAgent agent;
        try
        {
            // No task is named, as the courier's spawn names none; the dispatched-run variable is still stamped, so the verbs a
            // dispatched session is refused apply here too. Skip-permissions is required by `plan start`: the session writes
            // its report outside its checkout.
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    job.SessionId, job.SessionId, job.Checkout, job.SessionDirectory, job.Prompt, ExecutorMode.Subscription,
                    job.Model, job.Effort, SkipPermissions: true)
                {
                    SessionName = SessionRoleName.For(DomainId.Short(job.PlanId), SessionRoleName.Discovery),
                },
                linked.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Plan {PlanId}: the discovery session could not be spawned", job.PlanId);
            await RecordDiscoveryEndAsync(job.PlanId, job.SessionId, RefinementFailureReason.StartFailed, null, null, CancellationToken.None);
            return;
        }

        await RecordDiscoveryProcessIdentityAsync(job, agent);
        logger.LogInformation(
            "Plan {PlanId}: discovery session {SessionId} dispatched (pid {ProcessId}, model {Model}, artifacts in {Directory})",
            job.PlanId, job.SessionId, agent.ProcessId, job.Model.Value, job.SessionDirectory);

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
            TerminateAgent(job.PlanId, agent);
            timedOut = true;
            logger.LogWarning(
                "Plan {PlanId}: the discovery session exceeded {Timeout} and was terminated", job.PlanId, _options.PlanRefinementTimeout);
        }
        catch (OperationCanceledException) when (planEnded.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            // The plan was cancelled, or its idea stopped being Captured: the session is stopped and nothing is copied.
            TerminateAgent(job.PlanId, agent);
            await RecordDiscoveryEndAsync(job.PlanId, job.SessionId, RefinementFailureReason.Cancelled, null, null, CancellationToken.None);
            ForgetDiscoveryProcessIdentity(job);
            return;
        }
        catch (OperationCanceledException)
        {
            // The daemon is stopping: the session is detached and would outlive it, so it is stopped and its outcome
            // recorded with a token of its own. If that does not land, the next start records it.
            TerminateAgent(job.PlanId, agent);
            try
            {
                using CancellationTokenSource shutdown = new(ShutdownRecordTimeout);
                await RecordDiscoveryEndAsync(job.PlanId, job.SessionId, RefinementFailureReason.DaemonRestarted, null, null, shutdown.Token);
            }
            catch (Exception recording)
            {
                logger.LogWarning(recording, "Plan {PlanId}: could not record the discovery outcome during shutdown; the next start does", job.PlanId);
            }

            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Plan {PlanId}: the discovery session (pid {ProcessId}) could not be seen through to an outcome", job.PlanId, agent.ProcessId);
            TerminateAgent(job.PlanId, agent);
            await RecordDiscoveryEndAsync(job.PlanId, job.SessionId, RefinementFailureReason.SessionError, null, null, CancellationToken.None);
            ForgetDiscoveryProcessIdentity(job);
            return;
        }

        RefinementFailureReason failure = RefinementFailureReason.Unknown;
        string? copied = null;
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
            DiscoveryReading reading = await DiscoveryReport.ReadFileAsync(job.ReportPath, CancellationToken.None);
            if (reading.Missing)
            {
                failure = RefinementFailureReason.NoReport;
            }
            else if (!reading.Readable)
            {
                failure = RefinementFailureReason.UnreadableReport;
            }
            else
            {
                (copied, failure) = await CopyReportAsync(job, CancellationToken.None);
            }
        }

        await RecordDiscoveryEndAsync(
            job.PlanId, job.SessionId, failure, result?.ToPlanTokensRecorded(job.PlanId, DateTimeOffset.UtcNow, job.Model), copied,
            CancellationToken.None);
        ForgetDiscoveryProcessIdentity(job);
    }

    /// <summary>
    /// Removes the process file once the job has stopped its own session and recorded the outcome. The file is what marks a
    /// session as one a crash left running, so a plan that ended while its session ran (the outcome then records only the
    /// spend and leaves the running-session id set) would otherwise look like a crash's leftover on the next sweep. It is
    /// kept when the outcome did not land, and on the daemon's shutdown path, where the next start needs it.
    /// </summary>
    private void ForgetDiscoveryProcessIdentity(DiscoveryJob job) => TryDelete(RunPaths.DiscoveryProcessFile(job.SessionId));

    /// <summary>
    /// Copies a readable report into the idea's workspace at the path the idea has now, which a revised note may have moved
    /// since dispatch. Done by the daemon, and only for a readable report, so an unattended session never writes into the
    /// workspace where a person's journal can hold rulings. Nothing is copied for a plan that ended while the session ran, and
    /// the workspace is never created: a copy that cannot land leaves the report in the session directory. Returns the file
    /// written, or the closed reason it was not.
    /// </summary>
    private async Task<(string? Copied, RefinementFailureReason Failure)> CopyReportAsync(DiscoveryJob job, CancellationToken cancellationToken)
    {
        try
        {
            await using IQuerySession query = store.QuerySession();
            PlanDetails? plan = await query.LoadAsync<PlanDetails>(job.PlanId, cancellationToken);
            if (plan is not { IsLive: true } || plan.Discovery?.RunningSessionId != job.SessionId)
            {
                return (null, RefinementFailureReason.Unknown);
            }

            IdeaAggregate? idea = await query.Events.AggregateStreamAsync<IdeaAggregate>(job.IdeaId, token: cancellationToken);
            string? workspace = idea is null
                ? null
                : DiscoveryWorkspace.DirectoryOf(idea);
            if (idea is null || workspace is null || !Directory.Exists(workspace))
            {
                logger.LogWarning("Plan {PlanId}: the idea's workspace is not there, so the report stays at {Report}", job.PlanId, job.ReportPath);
                return (null, RefinementFailureReason.ReportNotCopied);
            }

            string destination = Path.Combine(workspace, DiscoveryWorkspace.ReportFileName(job.PlanId));
            File.Copy(job.ReportPath, destination, overwrite: true);
            return (destination, RefinementFailureReason.Unknown);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Plan {PlanId}: the report could not be copied into the idea's workspace; it stays at {Report}", job.PlanId, job.ReportPath);
            return (null, RefinementFailureReason.ReportNotCopied);
        }
    }

    /// <summary>
    /// Records how a discovery session ended on the plan's stream: its tokens, under the model it ran on, and the park. A readable
    /// report that was copied parks the plan at the gate; any failure parks it as session failed with the closed reason. A plan that
    /// ended while the session ran records the spend and nothing else, and a copy made for it is removed again, so a cancelled
    /// plan leaves nothing in the workspace. A lost race with a person's command reloads the stream and decides again.
    /// </summary>
    private async Task RecordDiscoveryEndAsync(
        Guid planId, Guid sessionId, RefinementFailureReason failure, PlanTokensRecorded? tokens, string? copied,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= RecordAttempts; attempt++)
        {
            await using IDocumentSession session = store.LightweightSession();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            StreamState? fence = await session.Events.FetchStreamStateAsync(planId, cancellationToken);
            PlanAggregate? plan = fence is null
                ? null
                : await session.Events.AggregateStreamAsync<PlanAggregate>(planId, version: fence.Version, token: cancellationToken);
            if (fence is null || plan is null)
            {
                return;
            }

            List<object> events = [];
            if (tokens is not null)
            {
                events.Add(tokens);
            }

            bool stillTheSession = plan.IsLive && plan.Discovery?.RunningSessionId == sessionId;
            if (stillTheSession && failure != RefinementFailureReason.Cancelled)
            {
                if (failure.IsFailure)
                {
                    logger.LogWarning(
                        "Plan {PlanId}: discovery session {SessionId} failed ({Failure}): {Description}",
                        planId, sessionId, failure.Value, failure.Description);
                }

                events.Add(PlanDecider.ParkDiscovery(
                    plan,
                    failure.IsFailure
                        ? PlanParkReason.SessionFailed
                        : PlanParkReason.Gate,
                    sessionId, failure, now));
            }
            else if (copied is not null)
            {
                TryDelete(copied);
            }

            if (events.Count == 0)
            {
                return;
            }

            session.Events.Append(planId, expectedVersion: fence.Version + events.Count, events.ToArray());
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (EventStreamUnexpectedMaxEventIdException exception) when (attempt < RecordAttempts)
            {
                logger.LogDebug(exception, "Plan {PlanId}: its stream moved while a discovery outcome was recorded; retrying", planId);
            }
        }
    }

    private async Task RecordDiscoveryEndQuietlyAsync(
        Guid planId, Guid sessionId, RefinementFailureReason failure, PlanTokensRecorded? tokens, string? copied,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordDiscoveryEndAsync(planId, sessionId, failure, tokens, copied, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not record the discovery outcome for plan {PlanId}", planId);
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove {File}, a discovery file nothing else removes", file);
        }
    }

    /// <summary>
    /// Writes the session's pid and start time beside its report, so the next start can stop a session a crashed daemon left
    /// running. It takes no <see cref="CancellationToken"/> on purpose, for the reason the refinement session's does: the session
    /// is already running and the file is a few bytes.
    /// </summary>
    private async Task RecordDiscoveryProcessIdentityAsync(DiscoveryJob job, SpawnedAgent agent)
    {
        try
        {
            string file = RunPaths.DiscoveryProcessFile(job.SessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"No directory for {file}."));
            await File.WriteAllTextAsync(file, $"{agent.ProcessId}\n{agent.StartedAt.UtcTicks}\n", CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception, "Plan {PlanId}: could not write the discovery session's process identity; a daemon crash would leave it running",
                job.PlanId);
        }
    }

    private void TerminateAgent(Guid planId, SpawnedAgent agent)
    {
        try
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plan {PlanId}: could not terminate the discovery session (pid {ProcessId})", planId, agent.ProcessId);
        }
    }

    /// <summary>
    /// Discovery sessions this node's own record says are running that nothing in this process is watching, stopped and
    /// recorded as failed and never dispatched again: the plan parks as session failed, and a person resumes it. A plan that
    /// ended while its session ran (cancelled after a crash, when no job was left to stop it) has no outcome to record, but its
    /// session is detached and would run on unbounded, so it is stopped here too, whatever state the plan is in.
    /// </summary>
    private async Task<int> RecoverDiscoveryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PlanDetails> running;
        await using (IQuerySession query = store.QuerySession())
        {
            running = [.. (await query.Query<PlanDetails>().ToListAsync(cancellationToken))
                .Where(plan => plan.Discovery is { RunningSessionId: not null })];
        }

        int recovered = 0;
        foreach (PlanDetails plan in running)
        {
            if (plan.Discovery?.RunningSessionId is not { } sessionId || _sessions.ContainsKey(sessionId))
            {
                continue;
            }

            // An ended plan keeps its running-session id for good, so the process file alone says whether there is still a session
            // to stop: without it there is nothing to do, and the plan is not read again on every sweep.
            if (!plan.IsLive && !File.Exists(RunPaths.DiscoveryProcessFile(sessionId)))
            {
                continue;
            }

            // A job commits its outcome before it leaves _sessions, so a session absent from it may have ended after the
            // snapshot above was read. Read the plan again: only one still recorded running is left behind.
            await using (IQuerySession fresh = store.QuerySession())
            {
                PlanDetails? latest = await fresh.LoadAsync<PlanDetails>(plan.Id, cancellationToken);
                if (latest?.Discovery?.RunningSessionId != sessionId)
                {
                    continue;
                }
            }

            if (!plan.IsLive)
            {
                // Nothing to record for an ended plan, so the process file is what marks the session as still to stop: it is
                // removed once the session has been, which keeps this from stopping the same one on every sweep.
                string processFile = RunPaths.DiscoveryProcessFile(sessionId);
                if (File.Exists(processFile))
                {
                    logger.LogWarning(
                        "Plan {PlanId} ended while discovery session {SessionId} ran and nothing is watching it; stopping it",
                        plan.Id, sessionId);
                    await TerminateLeftBehindAsync(processFile, plan.Id, sessionId, cancellationToken);
                    TryDelete(processFile);
                    recovered++;
                }

                continue;
            }

            logger.LogWarning(
                "Plan {PlanId}: discovery session {SessionId} was dispatched by this node and nothing is watching it; stopping it and recording it as failed",
                plan.Id, sessionId);
            await TerminateLeftBehindAsync(RunPaths.DiscoveryProcessFile(sessionId), plan.Id, sessionId, cancellationToken);
            await RecordDiscoveryEndAsync(plan.Id, sessionId, RefinementFailureReason.DaemonRestarted, null, null, cancellationToken);
            recovered++;
        }

        return recovered;
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

        _foreignSettle.KeepOnly(running.Select(task => task.RunningRefinementSessionId).OfType<Guid>().ToHashSet());
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
                await TerminateLeftBehindAsync(RunPaths.RefinementProcessFile(sessionId), task.Id, sessionId, cancellationToken);
                await RecordEndAsync(
                    new SessionEnd(
                        task.Id, sessionId, await PurposeOfAsync(task.Id, sessionId, cancellationToken), model, effort,
                        RefinementOutcome.Unknown, RefinementFailureReason.DaemonRestarted, null),
                    cancellationToken);
                recovered++;
            }
            else if (task.RunningRefinementDispatchedAt is { } dispatchedAt
                && _clock.GetUtcNow() - dispatchedAt > _options.ForeignPublicationCeiling
                && _foreignSettle.HasSettled(sessionId, _clock.GetUtcNow()))
            {
                logger.LogWarning(
                    "Draft {TaskId}: refinement session {SessionId} dispatched by node {Node} at {DispatchedAt:u} has stood longer than {Ceiling} with no outcome; ending it here",
                    task.Id, sessionId, task.RunningRefinementNodeId, dispatchedAt, _options.ForeignPublicationCeiling);
                await RecordEndAsync(
                    new SessionEnd(
                        task.Id, sessionId, RefinementPurpose.Unknown, model, effort, RefinementOutcome.Unknown,
                        RefinementFailureReason.DispatchExpired, null, task.RunningRefinementNodeId),
                    cancellationToken);
                _foreignSettle.Forget(sessionId);
                expired++;
            }
        }

        recovered += await RecoverDiscoveryAsync(cancellationToken);
        return (recovered, expired);
    }

    private async Task<RefinementPurpose> PurposeOfAsync(Guid draftId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        PlanDetails? owner = (await query.Query<PlanDetails>().ToListAsync(cancellationToken))
            .FirstOrDefault(plan => plan.Draft(draftId)?.RunningSessionId == sessionId);
        return owner?.Draft(draftId)?.Purpose ?? RefinementPurpose.Unknown;
    }

    /// <summary>
    /// Stops the session of any plan that has ended while it ran, and of any draft a person published or abandoned while its
    /// session ran, however many other drafts the plan still holds: a session for work that was walked away from only spends,
    /// and an entry in <c>_sessions</c> holds every other dispatch on the node back. Its job records why and applies nothing.
    /// </summary>
    private async Task StopSessionsOfEndedWorkAsync(CancellationToken cancellationToken)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        await using IQuerySession query = store.QuerySession();
        foreach ((Guid sessionId, RunningSession running) in _sessions)
        {
            PlanDetails? plan = await query.LoadAsync<PlanDetails>(running.PlanId, cancellationToken);
            if (plan is not null && StoppedFor(plan, running.DraftId) is { } reason)
            {
                logger.LogInformation(
                    "Draft {TaskId} of plan {PlanId} was closed or its plan ended while refinement session {SessionId} ran ({Reason}); stopping it",
                    running.DraftId, running.PlanId, sessionId, reason.Value);
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

    /// <summary>
    /// Why a session for this draft should no longer run or be applied, or null when it still should: the plan ended
    /// (<see cref="RefinementFailureReason.Cancelled"/>), or a person published or abandoned the draft inside a plan that
    /// goes on (<see cref="RefinementFailureReason.DraftClosed"/>).
    /// </summary>
    private static RefinementFailureReason? StoppedFor(PlanDetails plan, Guid draftId) =>
        !plan.IsLive
            ? RefinementFailureReason.Cancelled
            : plan.Draft(draftId) is { IsClosed: true }
                ? RefinementFailureReason.DraftClosed
                : null;

    /// <summary>The reason to stop a session, read from the record when the stop reached its job, which does not carry it.</summary>
    private async Task<RefinementFailureReason> WhyStoppedAsync(RefinementJob job, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        PlanDetails? plan = await query.LoadAsync<PlanDetails>(job.PlanId, cancellationToken);
        return (plan is null
                ? null
                : StoppedFor(plan, job.TaskId))
            ?? RefinementFailureReason.Cancelled;
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
    private async Task TerminateLeftBehindAsync(string file, Guid ownerId, Guid sessionId, CancellationToken cancellationToken)
    {
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
                exception, "{OwnerId}: could not stop session {SessionId}; recording it as failed anyway", ownerId, sessionId);
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
