using System.Collections.Concurrent;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// The step that runs after a task's merge has closed it out and decides whether one of the task's
/// notes becomes a project lesson. One small session reads the notes this node may put in a prompt
/// and answers with <c>NONE</c> or one <c>KEEP</c> line; the daemon reads the answer strictly
/// (<see cref="NotePromotionAnswer"/>), applies the ceiling and the claim bounds
/// (<see cref="NotePromotionVerdict"/>), and writes the lesson and the one <see cref="NotePromotionRecorded"/>
/// in a single transaction.
/// <para>
/// It runs in the background and closeout never waits on it, the pattern <see cref="CloseoutTriageSessions"/>
/// follows: <c>PollOnceAsync</c> runs inside the dispatch loop and the pull request monitor, so a wait
/// there would hold the watch. Closeout has already committed, released the task's dependents and told
/// its card or issue by the time <see cref="Start"/> is called, and removes the worktree and branch
/// whatever this does. There is no dispatch event and no recovery sweep: a step takes seconds, and a
/// daemon that stops or is killed during one records nothing, so its notes read as undecided. It is not a
/// hosted service, so the host never waits for it, and a record written during shutdown would race
/// process exit. <see cref="Dispose"/> cancels the job and terminates its session on a best-effort basis.
/// </para>
/// <para>
/// At most one step ever starts per task in a process, and at most one record per task ever lands: a
/// second merge of the same task, a sweep observing the same merge again, or two closeout paths racing
/// find the task already started here, or already holding a record, and change nothing. The step is
/// not retried. Across nodes the lesson is the fence: its stream id is derived from the task alone
/// (<see cref="LearningDecider.PromotedLessonId"/>), so two nodes of one owner that each promote the
/// same task start the same stream and the inbox discards the second genesis, and a node that already
/// holds the stream, written here or replicated, stands down. That fence is not atomic across nodes,
/// and nothing here makes it so: two nodes that run the step inside the same window (a session lasts
/// up to <see cref="DaemonOptions.NotePromotionTimeout"/>) each write their own claim and their own
/// record locally before either sees the other's. Each node's task projection keeps the first record in
/// its own stream, which is its own, and each node's inbox keeps the first lesson genesis to arrive,
/// which is also its own; so the two nodes can hold different statements under the one lesson id, and
/// the task's stream holds two records. Both are visible and a person can retire the lesson. A task whose scope is narrower than the team's
/// promotes nothing, because the lesson would replicate to every member.
/// </para>
/// <para>
/// The session has one turn, no tools and no project settings, and runs in a directory of its own
/// outside the project home's task tree, because the render sweep moves a closed-out task's directory
/// into <c>tasks/_archive/</c> within one pass of the merge and a stream under it would move too. Its
/// model and effort are the courier's for this project and node, and a task's own override never moves
/// them.
/// </para>
/// </summary>
public sealed class NotePromotionSessions(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<NotePromotionSessions> logger,
    LocalFleetProvider? fleets = null) : IDisposable
{
    private readonly DaemonOptions _options = options.Value;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The jobs in flight, so a test can wait for them to end; the daemon never does.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _running = [];

    /// <summary>
    /// The tasks a session has been started for in this process, kept for the process's life and taken
    /// before the session starts, which is what makes two closeout paths racing to the same merge start
    /// one session. A step that stood down (the task had moved on, or held nothing to offer) is not in
    /// it, so a stale merge observed first cannot use up the step the merge that does close the task
    /// out is owed; a step that started a session is, so it is never retried, whether or not its
    /// record was written.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _attempted = [];

    /// <summary>
    /// Starts the step for a merged task in the background and returns at once. Called by closeout
    /// after the closeout has committed. The job reads the task fresh and stands down when it is not
    /// Done, when <paramref name="runId"/> is no longer its current run, when it is a spike or a
    /// pull-request review, or when it already holds a record or its lesson, or when its scope does not
    /// reach the whole team. Never throws.
    /// </summary>
    public void Start(Guid taskId, Guid runId, Guid projectId)
    {
        CancellationToken stopping;
        try
        {
            stopping = _stopping.Token;
        }
        catch (ObjectDisposedException)
        {
            // The daemon has stopped; a step started now would record nothing anyway.
            return;
        }

        Guid jobId = DomainId.New();
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _running[jobId] = finished.Task;

        // Not given the stopping token: a job that never starts would never complete its entry, and the
        // job observes the token itself at every wait.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(taskId, runId, projectId, stopping);
                }
                finally
                {
                    _running.TryRemove(jobId, out _);
                    finished.SetResult();
                }
            },
            CancellationToken.None);
    }

    /// <summary>Completes once every step started so far has ended; the daemon never waits on it.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task RunAsync(Guid taskId, Guid runId, Guid projectId, CancellationToken stopping)
    {
        try
        {
            await PromoteAsync(taskId, runId, projectId, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The daemon is stopping: nothing is recorded, and the notes read as undecided.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Run {RunId}: the note promotion for task {TaskId} failed before an outcome could be recorded",
                runId, taskId);
        }
    }

    /// <summary>
    /// Decides whether there is anything to do, runs the session, and records what came of it. Returns
    /// the record it wrote, or null when it stood down, found nothing to offer, or could not write.
    /// </summary>
    internal async Task<NotePromotionRecorded?> PromoteAsync(
        Guid taskId, Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        NotePromotionOffer offer;
        TaskDetails task;
        ProjectDetails project;
        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails? current = await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
            if (current is null
                || current.State != TaskState.Done
                || current.CurrentRunId != runId
                || current.Type == TaskType.Spike
                || current.Type == TaskType.PrReview
                || current.NotePromotion is not null
                // A lesson replicates to every project member, so a task that does not already reach the
                // whole team (Private never leaves this node, Fleet stays on the owner's nodes) promotes nothing.
                || !current.Scope.IsAtLeast(ReplicationScope.Team)
                // The lesson stream a task promotes into has one id (LearningDecider.PromotedLessonId); a node
                // that already holds it, written here or replicated from another node, has nothing left to do.
                || await query.Events.FetchStreamStateAsync(LearningDecider.PromotedLessonId(taskId), cancellationToken) is not null
                || await query.LoadAsync<ProjectDetails>(projectId, cancellationToken) is not { } loaded)
            {
                return null;
            }

            task = current;
            project = loaded;
            offer = NotePromotionOffer.Select(await TaskNoteFeed.LoadViewsAsync(
                query, taskId,
                fleets is null
                    ? null
                    : async token => (await fleets.GetAsync(projectId, token))?.NodeIds,
                cancellationToken));
        }

        if (!offer.Any || !_attempted.TryAdd(taskId, 0))
        {
            return null;
        }

        AgentModel model = _options.ResolveCourierModel(project.Model);
        AgentEffort effort = _options.ResolveEffort(AgentRole.Courier, taskEffort: null, project.Effort);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Attempt attempt = await RunSessionAsync(runId, task, offer, model, effort, cancellationToken);
        logger.LogInformation(
            "Task {TaskId}: note promotion {Outcome}: {Offered} note(s) offered, {Held} held, {Ignored} answer line(s) ignored (model {Model})",
            taskId, attempt.Outcome.Value, offer.Offered.Count, offer.HeldCount, attempt.Reading.IgnoredLineCount, model.Value);

        return await RecordAsync(runId, task, project, offer, attempt, model, startedAt, cancellationToken);
    }

    /// <summary>What one promotion session came to, before it is written down.</summary>
    private sealed record Attempt(NotePromotionOutcome Outcome, NotePromotionReading Reading, AgentResult? Result);

    /// <summary>
    /// Starts the session, waits for it under the bound and reads its answer. Every failure is an
    /// outcome here rather than an exception; only the daemon's own cancellation escapes, after the
    /// session is terminated.
    /// </summary>
    private async Task<Attempt> RunSessionAsync(
        Guid runId,
        TaskDetails task,
        NotePromotionOffer offer,
        AgentModel model,
        AgentEffort effort,
        CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        string sessionDirectory = RunPaths.GlobalDirectory(sessionId);
        string artifactName = $"note-promoter-{sessionId.ToString("N")[..8]}";
        SpawnedAgent? unfinished = null;
        AgentResult? result = null;
        try
        {
            SpawnedAgent agent;
            try
            {
                agent = await executor.SpawnAsync(new AgentSpawnRequest(
                    runId, sessionId, sessionDirectory, sessionDirectory,
                    NotePromotionPromptBuilder.Compose(task.Objective, offer),
                    // Subscription, as the courier and the dispatcher pass it: the merged run's own mode is blank on a reconstructed run.
                    ExecutorMode.Subscription, model, effort,
                    SkipPermissions: false,
                    SessionArtifactName: artifactName,
                    UntrustedWorkingDirectory: true,
                    MaxTurns: 1,
                    NoTools: true)
                {
                    TaskId = task.Id,
                    SessionName = SessionRoleName.For(DomainId.Short(task.Id), SessionRoleName.NotePromoter),
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Task {TaskId}: the note promotion session could not be started", task.Id);
                return new Attempt(NotePromotionOutcome.StartFailed, NotePromotionReading.NotUsable, null);
            }

            unfinished = agent;
            bool timedOut;
            (result, timedOut) = await WaitWithinBudgetAsync(runId, sessionDirectory, artifactName, agent, cancellationToken);

            // The wait either read the session's own result or terminated it on expiry, so from here
            // on there is nothing of ours still running to abandon.
            unfinished = null;

            if (timedOut)
            {
                return new Attempt(NotePromotionOutcome.TimedOut, NotePromotionReading.NotUsable, result);
            }

            if (result is null || (!result.IsError && result.Summary.IsBlank()))
            {
                return new Attempt(NotePromotionOutcome.NoResult, NotePromotionReading.NotUsable, result);
            }

            if (result.IsError)
            {
                return new Attempt(NotePromotionOutcome.SessionError, NotePromotionReading.NotUsable, result);
            }

            NotePromotionReading reading = NotePromotionAnswer.Read(result.Summary, offer.Offered);
            return reading.Usable
                ? new Attempt(NotePromotionOutcome.NothingKept, reading, result)
                : new Attempt(NotePromotionOutcome.UnusableAnswer, reading, result);
        }
        catch (Exception exception)
        {
            // Any exit that is not the wait's own leaves a session nobody is monitoring: the daemon
            // shutting down mid-wait, or a throw between the spawn and the wait.
            Terminate(runId, unfinished);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            logger.LogWarning(exception, "Task {TaskId}: the note promotion session failed", task.Id);
            return new Attempt(NotePromotionOutcome.SessionError, NotePromotionReading.NotUsable, result);
        }
    }

    /// <summary>
    /// Records the session's tokens on the merged run (under the model it ran on, so the spend ledger
    /// sees it), then the lesson, if one was kept, and the task's one record in a single transaction.
    /// When that transaction fails nothing is promoted: the record is written again naming the lesson
    /// write as the failure and every offered note dropped as step failed. A failure to write even
    /// that is logged with the run and task ids and changes nothing else.
    /// </summary>
    private async Task<NotePromotionRecorded?> RecordAsync(
        Guid runId,
        TaskDetails task,
        ProjectDetails project,
        NotePromotionOffer offer,
        Attempt attempt,
        AgentModel model,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (attempt.Result is { } result)
        {
            try
            {
                await using IDocumentSession tokens = store.LightweightSession();
                tokens.Events.Append(runId, result.ToTokensRecorded(runId, now, model));
                await tokens.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception, "Run {RunId}: could not record the note promotion session's tokens for task {TaskId}",
                    runId, task.Id);
            }
        }

        NotePromotionVerdict verdict = attempt.Outcome.Decided
            ? NotePromotionVerdict.Decide(offer, attempt.Reading)
            : NotePromotionVerdict.Failed(offer);
        NotePromotionOutcome outcome = attempt.Outcome.Decided && verdict.KeptNoteId is not null
            ? NotePromotionOutcome.Kept
            : attempt.Outcome;
        TimeSpan duration = now - startedAt;

        try
        {
            return await WriteAsync(
                runId, task, project, offer, verdict, outcome, attempt.Reading.IgnoredLineCount, model, duration, now,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Run {RunId}: the note promotion for task {TaskId} could not be written, so nothing is promoted",
                runId, task.Id);
        }

        if (verdict.KeptNoteId is null)
        {
            // Nothing was being written but the record, so there is no lesson write to name as the failure.
            return null;
        }

        try
        {
            return await WriteAsync(
                runId, task, project, offer, NotePromotionVerdict.Failed(offer), NotePromotionOutcome.LessonWriteFailed,
                attempt.Reading.IgnoredLineCount, model, duration, now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Run {RunId}: the note promotion record for task {TaskId} could not be written either",
                runId, task.Id);
            return null;
        }
    }

    /// <summary>
    /// The one transaction: the lesson (when <paramref name="verdict"/> keeps a note) and the record.
    /// A task keeps at most one record, so when one is already there nothing is changed and the one
    /// that stands is returned, and a task that has moved on to a newer run than <paramref name="runId"/>
    /// is left alone (null); the same holds when the task's lesson stream already exists, which is how
    /// a second node of the same owner finds the first one's lesson (null, as a stand-down is). Appended unfenced, like the lesson pick: a note landing on the task
    /// during the step must not cost the lesson.
    /// </summary>
    private async Task<NotePromotionRecorded?> WriteAsync(
        Guid runId,
        TaskDetails task,
        ProjectDetails project,
        NotePromotionOffer offer,
        NotePromotionVerdict verdict,
        NotePromotionOutcome outcome,
        int ignoredLineCount,
        AgentModel model,
        TimeSpan duration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? current = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken);
        if (current?.NotePromotion is { } existing)
        {
            return existing;
        }

        if (current is { } latest && latest.CurrentRunId != runId)
        {
            // The task was reopened and claimed during the session, so the run this step ran for is no
            // longer its current one; neither the lesson nor a record naming the old run is written.
            return null;
        }

        Guid? lessonId = null;
        if (verdict.KeptNoteId is { } noteId && verdict.Claim is { } claim)
        {
            lessonId = LearningDecider.PromotedLessonId(task.Id);
            if (await session.Events.FetchStreamStateAsync(lessonId.Value, cancellationToken) is not null)
            {
                // Another node's step (or this one's, in a race) got there first; its lesson stands as the
                // task's one, and writing a second genesis for the same stream would only be discarded.
                return null;
            }

            LearningRecorded lesson = LearningDecider.RecordPromoted(
                lessonId.Value, project.Id, claim, noteId,
                new RecordedProvenance(node.OwnerId, runId, task.Id, HumanAttendance.Unattended), now);
            StreamAction stream = session.Events.StartStream<LearningAggregate>(lessonId.Value, lesson);
            // Stamped at the append: the lesson projection is Inline and reads the recording node off the
            // event, so an unstamped lesson would read as recorded on a node nobody observed and reach no prompt.
            EventRecordingNode.StampAtAppend(stream, node.NodeId);
        }

        NotePromotionRecorded recorded = new(
            task.Id, runId, outcome, verdict.ToDecisions(lessonId), offer.HeldCount, ignoredLineCount, model, duration, now);
        session.Events.Append(task.Id, recorded);
        await session.SaveChangesAsync(cancellationToken);
        return recorded;
    }

    /// <summary>
    /// The waiter under <see cref="DaemonOptions.NotePromotionTimeout"/>. On expiry the stream is
    /// re-read once before the session is terminated, so a result the still-alive session had already
    /// written is not lost to the wait's own cadence: its tokens are real spend. The outcome is still
    /// timed out. Cancellation of the daemon itself is not a timeout and propagates.
    /// </summary>
    private async Task<(AgentResult? Result, bool TimedOut)> WaitWithinBudgetAsync(
        Guid runId, string sessionDirectory, string artifactName, SpawnedAgent agent, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.NotePromotionTimeout);
        try
        {
            SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
                RunPaths.SessionStreamFile(sessionDirectory, artifactName), agent.ProcessId, agent.StartedAt,
                processManager, onOutput: null, budget.Token);
            if (wait.Lingering.Count > 0)
            {
                logger.LogWarning(
                    "Run {RunId}: the note promotion session left {Count} process(es) running after its result, and it was terminated pid(s) {Pids}",
                    runId, wait.Lingering.Count, string.Join(", ", wait.Lingering));
            }

            return (wait.Result, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AgentResult? result = await TryReadAlreadyPresentResultAsync(sessionDirectory, artifactName, cancellationToken);
            logger.LogWarning(
                "Run {RunId}: the note promotion session exceeded {Timeout}, so it was terminated and nothing is promoted",
                runId, _options.NotePromotionTimeout);
            Terminate(runId, agent);
            return (result, true);
        }
    }

    private static async Task<AgentResult?> TryReadAlreadyPresentResultAsync(
        string sessionDirectory, string artifactName, CancellationToken cancellationToken)
    {
        string streamFile = RunPaths.SessionStreamFile(sessionDirectory, artifactName);
        if (!File.Exists(streamFile))
        {
            return null;
        }

        try
        {
            return await StreamTailReader.ReadFinalResultAsync(streamFile, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Kills an abandoned session, never at the cost of the exception on its way out.</summary>
    private void Terminate(Guid runId, SpawnedAgent? agent)
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
            logger.LogWarning(exception,
                "Run {RunId}: could not terminate the abandoned note promotion session (pid {ProcessId})",
                runId, session.ProcessId);
        }
    }
}
