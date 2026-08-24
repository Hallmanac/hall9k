using Hall9k.Domain.Infrastructure.Storage;
using System.Collections.Concurrent;
using System.Text;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// What startup adoption found: runs picked back up (reattached, pipeline-resumed, or
/// park-refreshed) versus runs whose agent died without a result while the daemon was
/// down. Feeds the catch-up report (Decisions Log #31).
/// </summary>
public sealed record OrphanAdoption(int RunsAdopted, int RunsFailed);

/// <summary>
/// Owns every live run: tails its stream file (cursor persisted in RunActivity, so a
/// restarted daemon resumes where it left off), detects the terminal result event, and
/// adopts orphans at startup — reattach before declaring anything dead (log #7).
/// </summary>
public sealed class RunSupervisor(
    IDocumentStore store,
    NodeContext node,
    IProcessManager processManager,
    VerificationRunner verification,
    ReviewEngine review,
    PullRequestOpener pullRequests,
    ILogger<RunSupervisor> logger)
{
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeadProcessGrace = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, Task> _monitors = new();

    public int ActiveCount => _monitors.Count;

    public void StartMonitoring(
        Guid runId, string runDirectory, Guid taskId, int processId, DateTimeOffset processStartedAt,
        CancellationToken cancellationToken)
    {
        _monitors.TryAdd(runId, Task.Run(
            () => MonitorAsync(runId, runDirectory, taskId, processId, processStartedAt, cancellationToken),
            cancellationToken));
    }

    /// <summary>
    /// Re-enters the review loop for a run whose budget park caught a review pass or the fix
    /// session rather than the primary agent session (backlog 40). <c>TokenBudgetRetryEngine</c>
    /// calls this instead of resuming the primary session: <c>RunAggregate.Apply(RunBudgetExhausted)</c>
    /// already cleared the exhausted leg, so re-entering here is the same "pick the phase back
    /// up" adoption already uses for a run stranded UnderReview — the loop redispatches
    /// whatever the park left missing.
    /// </summary>
    public void ResumeReviewLoop(RunDetails run, CancellationToken cancellationToken) =>
        ResumePipeline(run, cancellationToken);

    /// <summary>
    /// Startup adoption: every non-terminal run recorded for this node either gets its
    /// monitor back (process alive, or result already on disk) or is failed honestly.
    /// Returns the tally for the startup catch-up report.
    /// </summary>
    public async Task<OrphanAdoption> AdoptOrphansAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> candidates = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?, ?, ?, ?)",
                RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value,
                RunState.UnderReview.Value, RunState.ReviewParked.Value, RunState.BudgetParked.Value))
            .ToListAsync(cancellationToken);

        int adopted = 0;
        int failed = 0;
        foreach (RunDetails run in candidates)
        {
            TaskDetails? owningTask = await query.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            if (owningTask is null || owningTask.CurrentRunId != run.Id)
            {
                // Every non-terminal candidate for this node, not grouped by task (Copilot
                // review, PR #30): a requeue-and-reclaim that landed while the daemon was
                // down can leave one task with two non-terminal runs here, and adopting
                // both — resuming or restarting an agent for each — double-books the task
                // exactly like the live-process check above exists to prevent. This run is
                // not the task's current one, so it is retired rather than adopted.
                await RetireStaleAdoptionCandidateAsync(run, cancellationToken);
                continue;
            }

            if (run.State == RunState.Verifying || run.State == RunState.UnderReview)
            {
                // Daemon died between the agent's result and the PR: the work is done,
                // re-enter the pipeline where the run stream left off (gates from
                // Verifying; the review loop resumes its own phase from UnderReview).
                // Backgrounded — gates and review sessions run for minutes and startup
                // must not wait on them.
                logger.LogInformation(
                    "Adopting run {RunId} stranded in {State} — resuming the pre-PR pipeline", run.Id, run.State.Value);
                ResumePipeline(run, cancellationToken);
                await RefreshAdoptedLeaseAsync(run, cancellationToken);
                adopted++;
                continue;
            }

            if (run.State == RunState.ReviewParked || run.State == RunState.BudgetParked)
            {
                // Parked means waiting on a human or waiting on the clock, not abandoned:
                // refresh the lease so the expiry sweep never requeues the task out from
                // under its worktree. A budget park is cleared by the hourly retry sweep
                // (TokenBudgetRetryEngine), never by adoption.
                await RefreshAdoptedLeaseAsync(run, cancellationToken);
                adopted++;
                continue;
            }

            if (run.ProcessId is null || run.ProcessStartedAt is null)
            {
                await FailRunAsync(run.Id, run.TaskId, "Dispatched but never started before the daemon stopped.", cancellationToken);
                failed++;
                continue;
            }

            bool alive = processManager.IsAlive(run.ProcessId.Value, run.ProcessStartedAt.Value);
            bool resultOnDisk = await RunResultFile.AlreadyWrittenAsync(run.RunDirectory, cancellationToken);
            if (alive || resultOnDisk)
            {
                logger.LogInformation(
                    "Adopting run {RunId} (pid {ProcessId}, alive: {Alive}, result on disk: {Result})",
                    run.Id, run.ProcessId, alive, resultOnDisk);
                StartMonitoring(
                    run.Id, run.RunDirectory, run.TaskId, run.ProcessId.Value, run.ProcessStartedAt.Value,
                    cancellationToken);
                await RefreshAdoptedLeaseAsync(run, cancellationToken);
                adopted++;
            }
            else
            {
                await FailRunAsync(run.Id, run.TaskId, "Agent process died without a result while the daemon was down.", cancellationToken);
                failed++;
            }
        }

        return new OrphanAdoption(adopted, failed);
    }

    /// <summary>
    /// The running-daemon counterpart of adoption for h9k review resolve: a resolved
    /// park moves the run back to UnderReview while no monitor owns it, and this sweep
    /// (each dispatch cycle; the resolve rings the doorbell) re-enters the pipeline.
    /// Runs already being driven are in the monitor set and are never double-entered.
    /// </summary>
    public async Task ResumeResolvedReviewsAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> underReview = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.UnderReview.Value))
            .ToListAsync(cancellationToken);

        foreach (RunDetails run in underReview.Where(r => !_monitors.ContainsKey(r.Id)))
        {
            logger.LogInformation(
                "Run {RunId} is under review with no monitor (review park resolved) — resuming the pipeline", run.Id);
            ResumePipeline(run, cancellationToken);
        }
    }

    private async Task MonitorAsync(
        Guid runId, string runDirectory, Guid taskId, int processId, DateTimeOffset processStartedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            string streamFile = RunPaths.StreamFile(runDirectory);
            long cursor = await LoadCursorAsync(runId, cancellationToken);
            DateTimeOffset? deadSince = null;
            StringBuilder partialLine = new();

            while (!cancellationToken.IsCancellationRequested)
            {
                (long newCursor, bool sawResult, AgentResult? result) =
                    await StreamTailReader.ReadNewLinesAsync(streamFile, cursor, partialLine, cancellationToken);

                if (newCursor > cursor)
                {
                    cursor = newCursor;
                    await SaveActivityAsync(runId, cursor, cancellationToken);
                }

                if (sawResult)
                {
                    await CompleteRunAsync(runId, runDirectory, taskId, result!, cancellationToken);
                    return;
                }

                if (!processManager.IsAlive(processId, processStartedAt))
                {
                    // Give buffered output a moment to land before declaring death.
                    deadSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                    {
                        await FailRunAsync(runId, taskId, ReadStandardErrorTail(runDirectory), cancellationToken);
                        return;
                    }
                }
                else
                {
                    deadSince = null;
                }

                await Task.Delay(TailInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Daemon shutdown: the agent keeps running; adoption picks this run back up.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Monitor for run {RunId} crashed", runId);
        }
        finally
        {
            _monitors.TryRemove(runId, out _);
        }
    }

    private async Task CompleteRunAsync(
        Guid runId, string runDirectory, Guid taskId, AgentResult result, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await CaptureHandoffAsync(runId, runDirectory, result, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();

        session.Events.Append(runId, new AgentSessionCompleted(runId, now));
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));

        if (result.IsError && result.Summary is { } summary && BudgetExhaustionParser.IsBudgetExhausted(summary))
        {
            // External and clock-recoverable, not a machine or code fault (backlog 40): the run
            // parks with the task still Claimed — worktree and lease intact — instead of
            // failing. TokenBudgetRetryEngine's hourly sweep is what clears this.
            session.Events.Append(runId, new RunBudgetExhausted(runId, summary, now));
            logger.LogWarning(
                "Run {RunId}: token budget exhausted — parked rather than failed; the daemon retries hourly. {Message}",
                runId, summary);
        }
        else if (result.IsError)
        {
            session.Events.Append(runId, new RunFailed(runId, "Agent reported an error result.", now));
            // One transaction with the run-stream events above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with them on purpose): splitting the
            // task-level write into its own session opened a window where a poller could
            // observe this run Failed while its task still read Claimed. Losing the
            // run-stream facts too on the rare lost generation race is the smaller cost.
            await AppendFencedTaskFailureAsync(session, runId, taskId, "Agent reported an error result.", now, cancellationToken);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording {Transition} for run {RunId} — a newer claim committed first",
                taskId, nameof(TaskFailed), runId);
            return;
        }

        logger.LogInformation(
            "Run {RunId} agent session completed ({Input} input tokens = {Fresh} fresh + {CacheRead} cache-read + {CacheWrite} cache-write, {Output} output, error: {IsError})",
            runId,
            result.TotalInputTokens,
            result.InputTokens,
            result.CacheReadInputTokens,
            result.CacheCreationInputTokens,
            result.OutputTokens,
            result.IsError);

        if (!result.IsError)
        {
            switch (await ParkedOnThreadDisputeAsync(runId, taskId, result, cancellationToken))
            {
                case ThreadDisputeOutcome.Parked:
                    return;
                case ThreadDisputeOutcome.Stale:
                    // The fence already rejected this lane (Copilot review, PR #30):
                    // stopping here instead of falling through saves a verification cycle
                    // the review loop's own fence would only reject one step later anyway.
                    return;
                case ThreadDisputeOutcome.NoDispute:
                    break;
            }
        }

        // The pre-PR pipeline (log #24): gates, then the independent review loop, and
        // only a merge-ready verdict lets the pull request open.
        if (!result.IsError
            && await verification.VerifyAsync(runId, taskId, cancellationToken)
            && await review.ReviewAsync(runId, taskId, cancellationToken))
        {
            await pullRequests.OpenAsync(runId, taskId, cancellationToken);
        }
    }

    /// <summary>
    /// A follow-up that met a review thread it could not honestly judge parks the run for
    /// the human instead of pushing (Decisions Log #62). The never-loop rule the pre-PR fix
    /// session already runs on, applied to a reviewer's thread: one honest attempt, and a
    /// design disagreement goes to a person with both positions recorded rather than being
    /// settled by whichever side the agent found more persuasive.
    /// <para>
    /// Read only from the follow-up runs that were ASKED the question, because a marker in
    /// any other session's summary is text, not an answer. That is narrower than "this is a
    /// follow-up": the closeout monitor dispatches two kinds, and only the review-feedback
    /// prompt teaches this vocabulary, so a CI-fix session quoting the skill file's marker
    /// line would otherwise park a run with a dispute reason pointing at a CI narrative. The
    /// gate is <c>RunLauncher</c>'s own prompt-selection condition read back — FailingChecks
    /// got BuildFixChecks, Rebase got BuildRebase, and everything else (including the Unknown
    /// of reopens recorded before the vocabulary existed) got BuildFollowUp — so the runs that
    /// may park are exactly the runs that were taught how, off the same field that chose the
    /// prompt.
    /// </para>
    /// <para>
    /// The park reuses <see cref="ReviewParked"/> whole: it already surfaces as NeedsHuman,
    /// keeps the lease refreshed through adoption, and is resolved with h9k review resolve.
    /// It lands from Verifying rather than UnderReview, which is what tells that resolution
    /// to re-enter at the gates instead of reporting merge-ready (RunAggregate.ParkedFromState).
    /// </para>
    /// </summary>
    /// <summary>
    /// Distinguishes "nothing to park" from "would have parked, but this generation is
    /// stale" (Copilot review, PR #30): <see cref="CompleteRunAsync"/> treated both as the
    /// same false and fell through to a verification cycle a stale lane has no business
    /// spending — the later review-loop fence would only reject it one step further in.
    /// </summary>
    private enum ThreadDisputeOutcome { NoDispute, Parked, Stale }

    private async Task<ThreadDisputeOutcome> ParkedOnThreadDisputeAsync(
        Guid runId, Guid taskId, AgentResult result, CancellationToken cancellationToken)
    {
        if (ReviewResultParser.ParseFixOutcome(result.Summary) != ReviewFixOutcome.Disputed)
        {
            return ThreadDisputeOutcome.NoDispute;
        }

        await using IDocumentSession session = store.LightweightSession();
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (run is null || !run.IsFollowUp)
        {
            return ThreadDisputeOutcome.NoDispute;
        }

        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        if (task is null || task.FollowUpKind == FollowUpKind.FailingChecks)
        {
            logger.LogInformation(
                "Run {RunId} carried the dispute marker but was dispatched to fix CI, which never asked "
                + "the question — reading it as a verdict would park on a narrative about checks", runId);
            return ThreadDisputeOutcome.NoDispute;
        }

        // Rebase follow-ups teach the same RESOLUTION vocabulary for a different question
        // (backlog 44, AgentPromptBuilder.AppendRebaseDisputeRules): a merge conflict neither
        // side of which is honestly resolvable, rather than a review thread neither side of
        // which is honestly judgeable. Same marker, same mechanism, different reason text and
        // artifact so the human reads a park about the actual obstruction.
        bool isRebaseDispute = task.FollowUpKind == FollowUpKind.Rebase;

        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, run.LeaseGeneration, nameof(ReviewParked), cancellationToken))
        {
            // A reclaim can land between CompleteRunAsync's earlier fenced append and this
            // check, so the rejection here must retire the run with RunSuperseded like every
            // other fence rejection in this diff (ReviewEngine.EnsureCurrentGenerationAsync,
            // ReviewEngine.ParkAsync): returning bare leaves the run live in the non-terminal
            // Verifying state with no monitor watching it, permanently pinning a NodeLoad
            // concurrency slot until the next daemon restart's orphan adoption sweep finds it
            // (adversarial review, cycle 3).
            session.Events.Append(
                runId, new RunSuperseded(runId, task?.LeaseGeneration ?? run.LeaseGeneration, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retired as superseded — the thread-dispute park found it was no longer task {TaskId}'s current generation",
                runId, taskId);
            return ThreadDisputeOutcome.Stale;
        }

        string disputeFilePath = isRebaseDispute
            ? RunPaths.RebaseConflictDisputeFile(run.RunDirectory)
            : RunPaths.ReviewThreadDisputeFile(run.RunDirectory);
        await WriteDisputePositionAsync(disputeFilePath, result.Summary, cancellationToken);
        string reason = isRebaseDispute
            ? "A follow-up could not honestly resolve a rebase conflict — both sides changed the same "
              + $"behavior, not just the same lines. Conflicting files and both positions: {disputeFilePath}. "
              + "Decide between them, then resolve with h9k review resolve --needs-fixes \"<your resolution>\" "
              + "— nothing has been pushed. (--merge-ready is refused here: nothing has been rebased yet.)"
            : "A follow-up disputed a review thread as a design call it cannot honestly make. "
              + $"Both positions: {disputeFilePath}. "
              + "Decide between them, then resolve with h9k review resolve — nothing has been pushed.";
        session.Events.Append(runId, new ReviewParked(runId, reason, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            isRebaseDispute
                ? "Run {RunId}: rebase conflict disputed — parked for the human. {Reason}"
                : "Run {RunId}: review thread disputed — parked for the human. {Reason}",
            runId, reason);
        return ThreadDisputeOutcome.Parked;
    }

    /// <summary>
    /// The agent's closing summary, saved as the second position a human reads. Appended rather
    /// than overwritten (<see cref="RunPaths.AppendDisputePositionAsync"/>): a resumed dispute
    /// parks on this same well-known path again, and a plain overwrite would erase the first
    /// position the moment the second one lands. Written best-effort for the same reason the
    /// handoff artifact is: losing the file must not turn a park into a failure, and the park
    /// reason names the path either way.
    /// </summary>
    private async Task WriteDisputePositionAsync(string filePath, string? summary, CancellationToken cancellationToken)
    {
        if (!await RunPaths.AppendDisputePositionAsync(filePath, summary, cancellationToken))
        {
            logger.LogWarning("Could not write the dispute position to {FilePath}", filePath);
        }
    }

    /// <summary>
    /// The capture half of the handoff's capture-then-land split (Decisions Log #36). The
    /// agent's own session-end result is the only place a handoff comes from, and this is
    /// the moment it exists — but the run has no merge yet, so nothing may travel: the text
    /// goes to the run directory as an artifact and waits there for CloseoutEngine to land
    /// RunHandoffRecorded at true closeout. Two moments, one fact, honestly ordered.
    /// <para>
    /// The file is written even when the result carried no handoff, empty, because the three
    /// file states are the three observations closeout reads: non-blank means the agent
    /// authored one, empty means the result was read and carried none, and absent means
    /// there was no session-end capture at all. Writing nothing would collapse the middle
    /// case into the last and lose a fact the platform actually observed.
    /// </para>
    /// </summary>
    private async Task CaptureHandoffAsync(
        Guid runId, string runDirectory, AgentResult result, CancellationToken cancellationToken)
    {
        try
        {
            string? handoff = HandoffParser.Parse(result.Summary);
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(RunPaths.HandoffFile(runDirectory), handoff ?? string.Empty, cancellationToken);
            if (handoff is null)
            {
                logger.LogInformation(
                    "Run {RunId}: the agent's result carried no handoff block — recorded as authored-none, not as empty",
                    runId);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run itself succeeded; losing the artifact must not fail it. Closeout then
            // reads an absent file and records NotCaptured, which is exactly what happened.
            logger.LogWarning(exception, "Could not write the handoff artifact for run {RunId}", runId);
        }
    }

    /// <summary>
    /// Adoption re-entry for the post-agent pipeline, tracked in the monitor set so
    /// ActiveCount stays honest while gates and review sessions run.
    /// </summary>
    private void ResumePipeline(RunDetails run, CancellationToken cancellationToken)
    {
        _monitors.TryAdd(run.Id, Task.Run(async () =>
        {
            try
            {
                bool mergeReady = run.State == RunState.Verifying
                    ? await verification.VerifyAsync(run.Id, run.TaskId, cancellationToken)
                        && await review.ReviewAsync(run.Id, run.TaskId, cancellationToken)
                    : await review.ReviewAsync(run.Id, run.TaskId, cancellationToken);
                if (mergeReady)
                {
                    await pullRequests.OpenAsync(run.Id, run.TaskId, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Daemon shutdown: the run stream holds the phase; the next adoption resumes it.
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Resumed pipeline for run {RunId} crashed", run.Id);
            }
            finally
            {
                _monitors.TryRemove(run.Id, out _);
            }
        }, cancellationToken));
    }

    /// <summary>
    /// The retirement half of the AdoptOrphansAsync grouping fix: a run this node still has
    /// non-terminal but its task has moved past — a fresh claim, a requeue, a retry, or a
    /// reopen already superseded it. Terminates the agent process if the OS still reports it
    /// alive (a requeue racing dispatch can leave one running even though its lease expired)
    /// and appends <see cref="RunSuperseded"/> so this run drops out of every later
    /// non-terminal query — AdoptOrphansAsync's own next sweep included — instead of being
    /// rediscovered as "recoverable" forever.
    /// </summary>
    private async Task RetireStaleAdoptionCandidateAsync(RunDetails run, CancellationToken cancellationToken)
    {
        if (run.ProcessId is { } processId && run.ProcessStartedAt is { } startedAt
            && processManager.IsAlive(processId, startedAt))
        {
            processManager.Terminate(processId, startedAt);
        }

        await using IDocumentSession session = store.LightweightSession();
        if (await session.Events.FetchStreamStateAsync(run.Id, cancellationToken) is null)
        {
            return;
        }

        TaskDetails? task = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
        session.Events.Append(run.Id, new RunSuperseded(run.Id, task?.LeaseGeneration ?? run.LeaseGeneration, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId} left stranded in {State} is not task {TaskId}'s current run — retired instead of adopted",
            run.Id, run.State.Value, run.TaskId);
    }

    /// <summary>
    /// An adopted run — reattached mid-pipeline, resumed in review, or review-parked —
    /// holds its task Claimed on purpose: the worktree is either a live session's or the
    /// human's workspace. Refreshing the heartbeat at adoption, before the expiry sweep
    /// runs one line later in the startup order (Decisions Log #7), is what makes adoption
    /// win outright: a task the sweep would otherwise see as stale-by-heartbeat is no
    /// longer stale by the time it looks (backlog 39). Origin incident (2026-08-21
    /// evening, twice observed): every adopted case except ReviewParked skipped this, so
    /// the sweep requeued the very tasks adoption had just reattached, and both
    /// generations ran a full review cycle in parallel.
    /// </summary>
    private async Task RefreshAdoptedLeaseAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(run.TaskId, token: cancellationToken);
        if (task is null || task.State != TaskState.Claimed || task.CurrentRunId != run.Id)
        {
            return;
        }

        session.Store(new TaskLease
        {
            Id = run.TaskId,
            NodeId = run.NodeId,
            LeaseGeneration = run.LeaseGeneration,
            HeartbeatAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId} adopted in {State} — lease refreshed so catch-up's sweep never requeues it out from under its worktree",
            run.Id, run.State.Value);
    }

    private async Task FailRunAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));
        await AppendFencedTaskFailureAsync(session, runId, taskId, reason, now, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording {Transition} for run {RunId} — a newer claim committed first",
                taskId, nameof(TaskFailed), runId);
            return;
        }

        logger.LogWarning("Run {RunId} failed: {Reason}", runId, reason);
    }

    /// <summary>
    /// The task-level half of a run failure, fenced on generation (backlog 39): a run
    /// whose generation no longer matches its task's current one is a lane a
    /// requeue-and-reclaim already superseded, and its failure must not fail the task a
    /// live generation is still working — nor take that generation's lease with it. Queues
    /// its append onto the CALLER's session rather than committing its own (Copilot review,
    /// PR #30): an earlier version ran this in its own transaction so a lost race would
    /// never roll back the run-stream facts the caller already saved, but that opened a
    /// window where a reader could observe the run Failed while its task still read
    /// Claimed — worse than the rare cost of losing this run's own failure record too when
    /// the generation race is actually lost. The caller's single SaveChangesAsync is what
    /// reserves the version <see cref="GenerationFence.LoadFencedAsync"/> read: a claim that
    /// lands in the gap loses the whole commit rather than silently absorbing this write.
    /// </summary>
    private async Task AppendFencedTaskFailureAsync(
        IDocumentSession session, Guid runId, Guid taskId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (fenced is not { } current)
        {
            return;
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (run is not null
            && !await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken))
        {
            return;
        }

        if (!TaskDecider.CanFail(current.Task))
        {
            session.Delete<TaskLease>(taskId);
            return;
        }

        session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(current.Task, runId, reason, now));
        session.Delete<TaskLease>(taskId);
    }

    private async Task<long> LoadCursorAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunActivity? activity = await query.LoadAsync<RunActivity>(runId, cancellationToken);
        return activity?.StreamBytesRead ?? 0;
    }

    private async Task SaveActivityAsync(Guid runId, long cursor, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new RunActivity
        {
            Id = runId,
            LastActivityAt = DateTimeOffset.UtcNow,
            StreamBytesRead = cursor,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    private static string ReadStandardErrorTail(string runDirectory)
    {
        try
        {
            string stderrFile = RunPaths.StandardErrorFile(runDirectory);
            if (File.Exists(stderrFile))
            {
                string content = File.ReadAllText(stderrFile).Trim();
                if (content.IsNotBlank())
                {
                    return $"Agent process died without a result. stderr: {Truncate(content, 500)}";
                }
            }
        }
        catch (IOException)
        {
        }

        return "Agent process died without a result.";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[^max..];
}
