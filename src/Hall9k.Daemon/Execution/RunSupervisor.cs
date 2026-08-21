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
using Hall9k.Domain.Features.Tasks.Handlers;
using Marten;
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

    public void StartMonitoring(Guid runId, Guid taskId, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken)
    {
        _monitors.TryAdd(runId, Task.Run(
            () => MonitorAsync(runId, taskId, processId, processStartedAt, cancellationToken),
            cancellationToken));
    }

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
                "d.data ->> 'state' in (?, ?, ?, ?, ?)",
                RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value,
                RunState.UnderReview.Value, RunState.ReviewParked.Value))
            .ToListAsync(cancellationToken);

        int adopted = 0;
        int failed = 0;
        foreach (RunDetails run in candidates)
        {
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
                adopted++;
                continue;
            }

            if (run.State == RunState.ReviewParked)
            {
                // Parked means waiting on a human, not abandoned: refresh the lease so
                // the expiry sweep never requeues the task out from under its worktree.
                await RefreshParkedLeaseAsync(run, cancellationToken);
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
            bool resultOnDisk = await ResultAlreadyWrittenAsync(run.Id, cancellationToken);
            if (alive || resultOnDisk)
            {
                logger.LogInformation(
                    "Adopting run {RunId} (pid {ProcessId}, alive: {Alive}, result on disk: {Result})",
                    run.Id, run.ProcessId, alive, resultOnDisk);
                StartMonitoring(run.Id, run.TaskId, run.ProcessId.Value, run.ProcessStartedAt.Value, cancellationToken);
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

    private async Task MonitorAsync(Guid runId, Guid taskId, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            string streamFile = RunPaths.StreamFile(runId);
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
                    await CompleteRunAsync(runId, taskId, result!, cancellationToken);
                    return;
                }

                if (!processManager.IsAlive(processId, processStartedAt))
                {
                    // Give buffered output a moment to land before declaring death.
                    deadSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                    {
                        await FailRunAsync(runId, taskId, ReadStandardErrorTail(runId), cancellationToken);
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

    private async Task CompleteRunAsync(Guid runId, Guid taskId, AgentResult result, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await CaptureHandoffAsync(runId, result, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();

        session.Events.Append(runId, new AgentSessionCompleted(runId, now));
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));

        if (result.IsError)
        {
            session.Events.Append(runId, new RunFailed(runId, "Agent reported an error result.", now));
            await FailTaskInSessionAsync(session, runId, taskId, "Agent reported an error result.", now, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId} agent session completed ({Input} input tokens = {Fresh} fresh + {CacheRead} cache-read + {CacheWrite} cache-write, {Output} output, error: {IsError})",
            runId,
            result.TotalInputTokens,
            result.InputTokens,
            result.CacheReadInputTokens,
            result.CacheCreationInputTokens,
            result.OutputTokens,
            result.IsError);

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
    private async Task CaptureHandoffAsync(Guid runId, AgentResult result, CancellationToken cancellationToken)
    {
        try
        {
            string? handoff = HandoffParser.Parse(result.Summary);
            Directory.CreateDirectory(RunPaths.RunDirectory(runId));
            await File.WriteAllTextAsync(RunPaths.HandoffFile(runId), handoff ?? string.Empty, cancellationToken);
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
    /// A review-parked run holds its task Claimed on purpose — the worktree is the
    /// human's workspace. Refreshing the heartbeat at adoption (before the sweep) keeps
    /// the lease from expiring over a daemon outage; the heartbeat service carries it
    /// from here.
    /// </summary>
    private async Task RefreshParkedLeaseAsync(RunDetails run, CancellationToken cancellationToken)
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
            "Run {RunId} is review-parked — lease refreshed so the task stays with its worktree", run.Id);
    }

    private async Task FailRunAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));
        await FailTaskInSessionAsync(session, runId, taskId, reason, now, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Run {RunId} failed: {Reason}", runId, reason);
    }

    private static async Task FailTaskInSessionAsync(
        IDocumentSession session, Guid runId, Guid taskId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not null && TaskDecider.CanFail(task))
        {
            session.Events.Append(taskId, TaskDecider.Fail(task, runId, reason, now));
        }

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

    private async Task<bool> ResultAlreadyWrittenAsync(Guid runId, CancellationToken cancellationToken)
    {
        string streamFile = RunPaths.StreamFile(runId);
        if (!File.Exists(streamFile))
        {
            return false;
        }

        using StreamReader reader = new(new FileStream(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (StreamJsonParser.TryParseResult(line, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadStandardErrorTail(Guid runId)
    {
        try
        {
            string stderrFile = RunPaths.StandardErrorFile(runId);
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
