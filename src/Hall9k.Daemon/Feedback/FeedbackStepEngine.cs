using System.Collections.Concurrent;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Learning.Queries;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Feedback;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Feedback;

/// <summary>One sweep's tally of what the feedback step did.</summary>
public sealed record FeedbackStepSweepResult(int Steps, int Dispatched);

/// <summary>
/// Drives the feedback step: the check the daemon runs, once, over the notes a task's blockers left, at the moment the task
/// would otherwise be released. The daemon makes every lifecycle move and starts every session; a session supplies judgment and
/// nothing else.
/// <para>
/// A task that takes the step is held at its release point (<see cref="TaskAggregate.FeedbackStepHeld"/>): Blocked, with
/// nothing left to wait for. The sweep takes each held task, and each task whose step is running on this node, and takes one step
/// at a time, deciding every one over a fresh read of the task's stream. The steps, in the order a step goes: begin (the node the
/// task is placed on, or, with no placement, a node of the owner it is queued for, appends the begun event under the stream's
/// version, so two nodes that saw the same hold begin one step between them); release at once when no blocker left a note the
/// trust rule lets into a prompt; otherwise run the check, one headless session with no tools; release the task when the check
/// says the notes do not affect it or fails; and when it says they do, take the task out of the queue and back to Draft and start
/// a plan for it (cap of one challenge, a refinement at the refinement tier, one challenge, a session that judges the verdict, a
/// publish), then queue it again for the same owner and node, or park it for a person.
/// </para>
/// <para>
/// Whatever happens, the step ends in exactly one <see cref="FeedbackStepOutcomeRecorded"/>, and nothing starts a second step
/// for the task: the begun event stays on the stream. A parked step also ends its plan, so a plan a person resumed later cannot
/// bring the step back to life, and a person who settles the park publishes and queues the task by hand.
/// </para>
/// </summary>
public sealed class FeedbackStepEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    IProcessManager processManager,
    LaunchHoldEngine launchHold,
    DaemonConnection connection,
    IOptions<DaemonOptions> options,
    ILogger<FeedbackStepEngine> logger,
    LocalFleetProvider? fleets = null) : IDisposable
{
    /// <summary>The most steps one task takes in one sweep, so a task that cannot settle cannot hold the sweep.</summary>
    private const int MaxStepsPerTask = 16;

    private readonly DaemonOptions _options = options.Value;
    private readonly SemaphoreSlim _oneSweep = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, RunningCheck> _sessions = [];
    private readonly Dictionary<Guid, string> _reportedGate = [];

    /// <summary>Set by the loop to its own doorbell, so a check that ends wakes the next step at once.</summary>
    public Action? OnWork { get; set; }

    private sealed record RunningCheck(Guid TaskId, TaskCompletionSource Finished);

    private enum StepOutcome
    {
        Nothing,
        Stepped,
        Dispatched,
    }

    /// <summary>Completes once every check started so far has recorded its outcome; the daemon never waits on it.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_sessions.Values.Select(running => running.Finished.Task));

    /// <summary>Called when the daemon is stopping: each running check is a detached process that would outlive it, so each job stops its session and records the failure.</summary>
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

    /// <summary>One sweep: every held task and every task whose step is running on this node, one step at a time.</summary>
    public async Task<FeedbackStepSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        await _oneSweep.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<Guid> candidates;
            await using (IQuerySession query = store.QuerySession())
            {
                candidates = await query.Query<TaskDetails>()
                    .Where(task => task.FeedbackStepHeld || task.FeedbackStepRunning)
                    .Select(task => task.Id)
                    .ToListAsync(cancellationToken);
            }

            int steps = 0;
            int dispatched = 0;
            foreach (Guid taskId in candidates)
            {
                for (int step = 0; step < MaxStepsPerTask; step++)
                {
                    StepOutcome outcome;
                    try
                    {
                        outcome = await StepAsync(taskId, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Task {TaskId}: a feedback step failed; leaving it for the next sweep", taskId);
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

            return new FeedbackStepSweepResult(steps, dispatched);
        }
        finally
        {
            _oneSweep.Release();
        }
    }

    /// <summary>Takes one step for one task, or reports there is none. Every call reads the task fresh, so a step that lost a race with a person's command is simply decided again.</summary>
    private async Task<StepOutcome> StepAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        TaskAggregate? task = fence is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, version: fence.Version, token: cancellationToken);
        if (fence is null || task is null)
        {
            return StepOutcome.Nothing;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (task.FeedbackStepHeld && !task.FeedbackStepBegun)
        {
            return await BeginAsync(session, fence, task, now, cancellationToken);
        }

        // A step that another node began is that node's to carry: it holds the plan and the session.
        if (!task.FeedbackStepRunning || task.FeedbackStep is not { } started || started.NodeId != node.NodeId)
        {
            return StepOutcome.Nothing;
        }

        if (task.RunningFeedbackCheckSessionId is { } running)
        {
            return _sessions.ContainsKey(running)
                ? StepOutcome.Nothing
                : await RecordCheckAsync(
                    task.Id, running, FeedbackCheckAnswer.Failed, RefinementFailureReason.DaemonRestarted, null, task.LatestFeedbackCheckDispatch?.Model ?? AgentModel.Unknown,
                    cancellationToken);
        }

        if (task.FeedbackStepPlanId is { } planId)
        {
            return await FollowRefinementAsync(session, fence, task, planId, now, cancellationToken);
        }

        return task.LatestFeedbackCheckDispatch is null
            ? await DispatchOrReleaseAsync(session, fence, task, now, cancellationToken)
            : await DecideAfterCheckAsync(session, fence, task, now, cancellationToken);
    }

    /// <summary>
    /// The held task's step begins on this node, if this is the node that may run it. Appended alone under the stream's version:
    /// two nodes that read the same hold append against the same version, and only one append lands. The other reads the begun
    /// event on its next look and does nothing.
    /// </summary>
    private async Task<StepOutcome> BeginAsync(
        IDocumentSession session, StreamState fence, TaskAggregate task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? ownerFingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, node.OwnerId, cancellationToken);
        if (!FeedbackStepDecider.MayBeginOnThisNode(task, node.NodeId, node.OwnerId, ownerFingerprint))
        {
            return StepOutcome.Nothing;
        }

        FeedbackStepStarted started = FeedbackStepDecider.Start(task, node.NodeId, now);
        session.Events.Append(task.Id, expectedVersion: fence.Version + 1, started);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} changed while its feedback step was beginning; deciding it again", task.Id);
            return StepOutcome.Nothing;
        }

        logger.LogInformation("Task {TaskId}: its blockers have all finished and it takes the feedback step, which has begun", task.Id);
        return StepOutcome.Stepped;
    }

    /// <summary>
    /// The begun step has no check yet: with no note to read the task is released at once and no session runs, otherwise the check
    /// is dispatched (unless the node's launch gate stands, in which case the step waits and the task stays held).
    /// </summary>
    private async Task<StepOutcome> DispatchOrReleaseAsync(
        IDocumentSession session, StreamState fence, TaskAggregate task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!task.FeedbackStepHeld || task.State != TaskState.Blocked)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.EndedWithoutActing,
                $"a person took the task out of its hold (it is {task.State.Value}) before the check ran", now, cancellationToken);
        }

        InjectedTaskNotes notes = await FeedbackNotes.LoadAsync(
            session, task.FeedbackStep!.BlockerIds, node.NodeId, FleetReader(task.ProjectId), cancellationToken);
        if (!notes.Any)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.ReleasedWithoutCheck,
                notes.HeldForProvenance > 0
                    ? $"no blocker left a note the trust rule lets into a prompt ({notes.HeldForProvenance} held back)"
                    : "no blocker left a note", now, cancellationToken);
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        TaskDetails? details = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken);
        if (project is null || details is null)
        {
            return StepOutcome.Nothing;
        }

        if (await LaunchGateAsync(session, task.Id, project, cancellationToken) is not null)
        {
            return StepOutcome.Nothing;
        }

        AgentModel model = _options.ResolveFeedbackCheckModel();
        AgentEffort effort = _options.ResolveFeedbackCheckEffort();
        Guid sessionId = DomainId.New();
        FeedbackCheckDispatched dispatched = FeedbackStepDecider.DispatchCheck(task, sessionId, node.NodeId, model, effort, now);
        string prompt = FeedbackCheckPromptBuilder.Compose(details, notes);

        // Recorded before anything is spawned, RunLauncher's order: committing first is what makes a crash lose too, and the task
        // never claims a session that was not recorded. A dispatch with no outcome is what the next sweep records as a failure.
        session.Events.Append(task.Id, expectedVersion: fence.Version + 1, dispatched);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} changed while its feedback check was being dispatched; deciding it again", task.Id);
            return StepOutcome.Nothing;
        }

        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessions[sessionId] = new RunningCheck(task.Id, finished);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunCheckAsync(task.Id, sessionId, prompt, model, effort, _stopping.Token);
                }
                finally
                {
                    _sessions.TryRemove(sessionId, out _);
                    finished.SetResult();
                    OnWork?.Invoke();
                }
            },
            CancellationToken.None);
        return StepOutcome.Dispatched;
    }

    private async Task RunCheckAsync(
        Guid taskId, Guid sessionId, string prompt, AgentModel model, AgentEffort effort, CancellationToken stopping)
    {
        string sessionDirectory = RunPaths.GlobalDirectory(sessionId);
        string artifactName = $"feedback-check-{sessionId.ToString("N")[..8]}";
        SpawnedAgent? unfinished = null;
        AgentResult? result = null;
        FeedbackCheckAnswer answer = FeedbackCheckAnswer.Failed;
        RefinementFailureReason failure = RefinementFailureReason.SessionError;
        try
        {
            SpawnedAgent agent;
            try
            {
                agent = await executor.SpawnAsync(
                    new AgentSpawnRequest(
                        sessionId, sessionId, sessionDirectory, sessionDirectory, prompt, ExecutorMode.Subscription, model, effort,
                        SkipPermissions: false,
                        SessionArtifactName: artifactName,
                        UntrustedWorkingDirectory: true,
                        MaxTurns: 1,
                        NoTools: true)
                    {
                        TaskId = taskId,
                        SessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.FeedbackChecker),
                    },
                    stopping);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Task {TaskId}: the feedback check session could not be started", taskId);
                await RecordCheckAsync(taskId, sessionId, FeedbackCheckAnswer.Failed, RefinementFailureReason.StartFailed, null, model, CancellationToken.None);
                return;
            }

            unfinished = agent;
            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            budget.CancelAfter(_options.FeedbackCheckTimeout);
            bool timedOut = false;
            try
            {
                result = (await SessionResultWaiter.WaitAsync(
                    RunPaths.SessionStreamFile(sessionDirectory, artifactName), agent.ProcessId, agent.StartedAt, processManager,
                    onOutput: null, budget.Token)).Result;
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                timedOut = true;
                logger.LogWarning("Task {TaskId}: the feedback check exceeded {Timeout} and was terminated", taskId, _options.FeedbackCheckTimeout);
            }

            // The wait either read the session's own result or is about to be cut off, so what remains is ours to terminate.
            if (timedOut)
            {
                Terminate(taskId, agent);
                unfinished = null;
                failure = RefinementFailureReason.TimedOut;
            }
            else
            {
                unfinished = null;
                if (result is null || (!result.IsError && result.Summary.IsBlank()))
                {
                    failure = RefinementFailureReason.NoResult;
                }
                else if (result.IsError)
                {
                    failure = RefinementFailureReason.SessionError;
                }
                else
                {
                    answer = FeedbackCheckAnswerReader.Read(result.Summary);
                    failure = answer.IsAnswer
                        ? RefinementFailureReason.Unknown
                        : RefinementFailureReason.UnreadableReport;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The daemon is stopping: the session is detached and would outlive it, so it is stopped and the failure recorded with
            // a token of its own. If that does not land, the next start records it.
            Terminate(taskId, unfinished);
            using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(5));
            try
            {
                await RecordCheckAsync(taskId, sessionId, FeedbackCheckAnswer.Failed, RefinementFailureReason.DaemonRestarted, result, model, shutdown.Token);
            }
            catch (Exception recording)
            {
                logger.LogWarning(recording, "Task {TaskId}: could not record the feedback check's end during shutdown; the next start does", taskId);
            }

            return;
        }
        catch (Exception exception)
        {
            Terminate(taskId, unfinished);
            logger.LogError(exception, "Task {TaskId}: the feedback check could not be seen through to an outcome", taskId);
            failure = RefinementFailureReason.SessionError;
        }

        await RecordCheckAsync(taskId, sessionId, answer, failure, result, model, CancellationToken.None);
    }

    /// <summary>Records how the check ended on the task's stream, with the spend it reported. Appended without a version: it is a fact about a session that ran, and its apply is guarded on the session.</summary>
    private async Task<StepOutcome> RecordCheckAsync(
        Guid taskId, Guid sessionId, FeedbackCheckAnswer answer, RefinementFailureReason failure, AgentResult? result, AgentModel model,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (await session.Events.FetchStreamStateAsync(taskId, cancellationToken) is null)
        {
            return StepOutcome.Nothing;
        }

        List<object> events = [];
        if (result is not null)
        {
            events.Add(result.ToPublicationTokensRecorded(taskId, now, model));
        }

        events.Add(new FeedbackCheckRecorded(
            taskId, sessionId, node.NodeId, answer, answer.IsAnswer ? RefinementFailureReason.Unknown : failure, now));
        session.Events.Append(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Task {TaskId}: the feedback check ended: {Answer}{Failure}", taskId, answer.IsAnswer ? answer.Word : "no answer",
            answer.IsAnswer ? string.Empty : $" ({failure.Value})");
        return StepOutcome.Stepped;
    }

    /// <summary>The check has an outcome and the step has not gone on: unaffected or failed releases the task; affected takes it back to Draft and starts the plan.</summary>
    private async Task<StepOutcome> DecideAfterCheckAsync(
        IDocumentSession session, StreamState fence, TaskAggregate task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        FeedbackCheckRecorded? check = task.LatestFeedbackCheck;
        if (check is null)
        {
            return StepOutcome.Nothing;
        }

        if (!task.FeedbackStepHeld || task.State != TaskState.Blocked)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.EndedWithoutActing,
                $"a person took the task out of its hold (it is {task.State.Value}) while the check ran", now, cancellationToken);
        }

        if (check.Answer != FeedbackCheckAnswer.Affects)
        {
            return check.Answer == FeedbackCheckAnswer.Unaffected
                ? await ConcludeAsync(
                    session, fence, task, FeedbackStepOutcome.ReleasedAfterCheck,
                    "the check found the blockers' notes do not affect the task", now, cancellationToken)
                : await ConcludeAsync(
                    session, fence, task, FeedbackStepOutcome.ReleasedAfterFailedCheck,
                    $"the check gave no answer ({check.Failure.Description})", now, cancellationToken);
        }

        TaskDetails? details = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken);
        if (details is null)
        {
            return StepOutcome.Nothing;
        }

        // Read before the dequeue and the return to draft, which clear the queued-for fields, and the tracking choice the task's
        // own publish recorded: the republish passes it on and never passes --no-existing-item, since nothing here searched a tracker.
        bool untracked = details.UntrackedAttested;
        Guid planId = DomainId.New();
        (TaskUnassigned dequeued, TaskReturnedToDraft drafted, FeedbackStepRefinementBegun begun) =
            FeedbackStepDecider.TakeBackToDraft(task, planId, node.OwnerId, now);
        PlanStarted plan = new(
            planId, task.ProjectId, [task.Id], PlanPhase.Refinement, NoExistingItemAttested: false, UntrackedAttested: untracked,
            node.OwnerId, now, IdeaId: null, FeedbackStepTaskId: task.Id);

        session.Events.Append(task.Id, expectedVersion: fence.Version + 3, dequeued, drafted, begun);
        session.Events.StartStream<PlanAggregate>(planId, plan);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} changed while its refinement was being started; deciding it again", task.Id);
            return StepOutcome.Nothing;
        }

        logger.LogInformation("Task {TaskId}: the check found the blockers' notes affect it, so it is back in Draft and plan {PlanId} refines it", task.Id, planId);
        await Doorbell.RingAsync(connection.ConnectionString, $"plan-started:{planId}", cancellationToken);
        return StepOutcome.Stepped;
    }

    /// <summary>
    /// The plan carries the draft; this reads where it came to and decides what the step does: requeue the task once the plan
    /// published it, park the step when the plan parked, end it when a person got there first, wait otherwise.
    /// </summary>
    private async Task<StepOutcome> FollowRefinementAsync(
        IDocumentSession session, StreamState fence, TaskAggregate task, Guid planId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StreamState? planFence = await session.Events.FetchStreamStateAsync(planId, cancellationToken);
        PlanAggregate? plan = planFence is null
            ? null
            : await session.Events.AggregateStreamAsync<PlanAggregate>(planId, version: planFence.Version, token: cancellationToken);
        PlanDraftState? draft = plan?.Draft(task.Id);
        if (planFence is null || plan is null || draft is null)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.Parked, "the plan that was refining the task is gone", now, cancellationToken);
        }

        if (task.State == TaskState.Abandoned || draft.Abandoned)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.EndedWithoutActing, "a person abandoned the task while the step ran", now,
                cancellationToken, plan, planFence);
        }

        if (draft.Published)
        {
            return draft.PublishedByHand
                ? await ConcludeAsync(
                    session, fence, task, FeedbackStepOutcome.EndedWithoutActing, "a person published the task by hand while the step ran", now,
                    cancellationToken, plan, planFence)
                : await RequeueAsync(session, fence, task, now, cancellationToken);
        }

        if (!plan.IsLive)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.EndedWithoutActing, "a person cancelled the plan that was refining the task", now,
                cancellationToken);
        }

        if (draft.IsParked)
        {
            PlanParkReason reason = draft.ParkReason;
            return reason == PlanParkReason.DraftChanged
                ? await ConcludeAsync(
                    session, fence, task, FeedbackStepOutcome.EndedWithoutActing, "a person revised the task while the step ran", now,
                    cancellationToken, plan, planFence)
                : await ConcludeAsync(
                    session, fence, task, FeedbackStepOutcome.Parked, ParkText(reason), now, cancellationToken, plan, planFence);
        }

        return StepOutcome.Nothing;
    }

    /// <summary>The plan published the refined task: queue it again for the same owner, fingerprint, assignee and node, or park the step when that is refused.</summary>
    private async Task<StepOutcome> RequeueAsync(
        IDocumentSession session, StreamState fence, TaskAggregate task, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (task.State != TaskState.Published)
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.EndedWithoutActing,
                $"a person changed the task while the step ran (it is {task.State.Value}, not Published)", now, cancellationToken);
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is { IsArchived: true })
        {
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.Parked, "queueing it again was refused: its project is archived", now, cancellationToken);
        }

        TaskAssigned assigned;
        try
        {
            IReadOnlyList<TaskDependency> dependencies = await TaskDependencyQuery.LoadAsync(session, task.BlockedBy, cancellationToken);
            assigned = FeedbackStepDecider.Requeue(task, dependencies, now);
        }
        catch (DomainException refusal)
        {
            logger.LogWarning("Task {TaskId}: queueing it again was refused: {Reason}", task.Id, refusal.Message);
            return await ConcludeAsync(
                session, fence, task, FeedbackStepOutcome.Parked, $"queueing it again was refused: {refusal.Message}", now, cancellationToken);
        }

        task.Apply(assigned);
        FeedbackStepOutcomeRecorded outcome = FeedbackStepDecider.Conclude(
            task, FeedbackStepOutcome.RevisedAndRequeued, "the task was refined against its blockers' notes, challenged once, published, and queued again", now);
        session.Events.Append(task.Id, expectedVersion: fence.Version + 2, assigned, outcome);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} changed while it was being queued again; deciding it again", task.Id);
            return StepOutcome.Nothing;
        }

        logger.LogInformation("Task {TaskId}: refined and queued again for its owner on its node", task.Id);
        return StepOutcome.Stepped;
    }

    /// <summary>
    /// Ends the step with <paramref name="outcome"/> in one save: the outcome on the task's stream, and, for an outcome that is not
    /// a requeue, the plan ended too (cancelled, leaving its draft a Draft) when it is still live, so a person's later resume of it
    /// cannot bring the step back to life.
    /// </summary>
    private async Task<StepOutcome> ConcludeAsync(
        IDocumentSession session, StreamState fence, TaskAggregate task, FeedbackStepOutcome outcome, string reason, DateTimeOffset now,
        CancellationToken cancellationToken, PlanAggregate? plan = null, StreamState? planFence = null)
    {
        // A task still held when its step ends without releasing it would sit Blocked with nothing to wait for and nothing to free
        // it. The step ending is never a reason to strand it, so the release is what ends the step when the hold is still there.
        FeedbackStepOutcome ending = task.FeedbackStepHeld && task.State == TaskState.Blocked && !outcome.ReleasesTask
            ? FeedbackStepOutcome.ReleasedAfterFailedCheck
            : outcome;
        FeedbackStepOutcomeRecorded recorded = FeedbackStepDecider.Conclude(task, ending, reason, now);
        session.Events.Append(task.Id, expectedVersion: fence.Version + 1, recorded);
        if (plan is { IsLive: true } && planFence is not null)
        {
            session.Events.Append(
                plan.Id, expectedVersion: planFence.Version + 1,
                new PlanCancelled(plan.Id, plan.ProjectId, "the task's feedback step ended, so the plan that carried it ends too", null, now));
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} or its plan changed while its feedback step was ending; deciding it again", task.Id);
            return StepOutcome.Nothing;
        }

        logger.LogInformation("Task {TaskId}: feedback step ended: {Outcome} ({Reason})", task.Id, ending.Description, reason);
        return StepOutcome.Stepped;
    }

    private static string ParkText(PlanParkReason reason) => reason.Description;

    private LocalFleetReader? FleetReader(Guid projectId) =>
        fleets is null
            ? null
            : async token => (await fleets.GetAsync(projectId, token))?.NodeIds;

    /// <summary>
    /// Why the check may not start now, or null: the node's launch hold, or the project's max parallel tasks of 0. The step
    /// waits, holding the task, and says so once, because starting a session is exactly what those two stop. The spend budget
    /// is not asked: the check reads a few notes, and a task held for it would wait on a budget that stops its own dispatch anyway.
    /// </summary>
    private async Task<string?> LaunchGateAsync(
        IDocumentSession session, Guid taskId, ProjectDetails project, CancellationToken cancellationToken)
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

        if (reason is null)
        {
            _reportedGate.Remove(taskId);
        }
        else if (!_reportedGate.TryGetValue(taskId, out string? reported) || reported != reason)
        {
            _reportedGate[taskId] = reason;
            logger.LogInformation("Task {TaskId}: its feedback check is not started: {Reason}", taskId, reason);
        }

        return reason;
    }

    private void Terminate(Guid taskId, SpawnedAgent? agent)
    {
        if (agent is not { } session)
        {
            return;
        }

        try
        {
            processManager.Terminate(session.ProcessId, session.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Task {TaskId}: could not terminate the feedback check session (pid {ProcessId})", taskId, session.ProcessId);
        }
    }
}
