using System.Text;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The pre-PR review loop (Decisions Log #23), between VerificationRunner and
/// PullRequestOpener: an independent review agent — a fresh headless session that never
/// saw the implementation reasoning — reads the run's diff; a needs-fixes verdict
/// dispatches a fix session in the same worktree, gates re-run, and a fresh reviewer
/// looks again. Bounded by DaemonOptions.MaxAutomaticReviewFixRuns (the closeout
/// retry-budget pattern); the budget spent, a disputed finding, or a missing verdict
/// parks the run for the human. The loop is a state machine over the run stream, so a
/// restarted daemon resumes it exactly where the events left off.
/// </summary>
public sealed class ReviewEngine(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    VerificationRunner verification,
    IOptions<DaemonOptions> options,
    ILogger<ReviewEngine> logger)
{
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeadProcessGrace = TimeSpan.FromSeconds(5);

    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// Drives the loop until merge-ready (true — PullRequestOpener may proceed), or a
    /// park/failure (false). Entered fresh after the gates pass, and re-entered by
    /// adoption for runs stranded UnderReview — the run stream carries the phase.
    /// </summary>
    public async Task<bool> ReviewAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        ReviewContext? context = await LoadContextAsync(runId, taskId, cancellationToken);
        if (context is null)
        {
            return false;
        }

        try
        {
            return await DriveAsync(context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Review loop crashed for run {RunId}", runId);
            await FailAsync(runId, taskId, $"Review loop failed: {exception.Message}", cancellationToken);
            return false;
        }
    }

    private async Task<bool> DriveAsync(ReviewContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunAggregate run = await LoadRunAsync(context.RunId, cancellationToken);

            switch (run.ReviewPhase)
            {
                case ReviewPhase.None:
                    await DispatchReviewSessionAsync(context, run.ReviewCycle + 1, cancellationToken);
                    break;

                case ReviewPhase.AwaitingVerdict:
                case ReviewPhase.AwaitingFix:
                {
                    bool awaitingFix = run.ReviewPhase == ReviewPhase.AwaitingFix;
                    string leg = awaitingFix ? "fix" : "review";
                    if (run.ActiveReviewSessionId is not { } sessionId
                        || run.ActiveReviewProcessId is not { } processId
                        || run.ActiveReviewProcessStartedAt is not { } processStartedAt)
                    {
                        await FailAsync(context.RunId, context.TaskId,
                            $"Run stream records an in-flight {leg} session without its identity.", cancellationToken);
                        return false;
                    }

                    string streamFile = RunPaths.SessionStreamFile(
                        context.RunId, SessionArtifactName(run.ReviewCycle, sessionId, awaitingFix));
                    AgentResult? result = await WaitForSessionResultAsync(
                        context.RunId, streamFile, processId, processStartedAt, cancellationToken);
                    if (result is null || result.IsError)
                    {
                        await FailAsync(context.RunId, context.TaskId, result is null
                            ? $"The {leg} session (cycle {run.ReviewCycle}) died without a result."
                            : $"The {leg} session (cycle {run.ReviewCycle}) reported an error result.", cancellationToken);
                        return false;
                    }

                    if (awaitingFix)
                    {
                        await RecordFixResultAsync(context.RunId, run.ReviewCycle, result, cancellationToken);
                    }
                    else
                    {
                        await RecordReviewResultAsync(context.RunId, run.ReviewCycle, result, cancellationToken);
                    }

                    break;
                }

                case ReviewPhase.MergeReady:
                    logger.LogInformation(
                        "Run {RunId}: review verdict merge-ready after {Cycle} cycle(s) — the pull request may open",
                        context.RunId, run.ReviewCycle);
                    return true;

                case ReviewPhase.FixNeeded when run.LastReviewVerdict != ReviewVerdict.NeedsFixes:
                    // The reviewer followed no verdict format; guessing what it meant would
                    // be worse than asking (never guess at unobserved facts).
                    await ParkAsync(context.RunId,
                        $"The review session (cycle {run.ReviewCycle}) returned no parseable verdict. " +
                        $"Its output: {RunPaths.ReviewFindingsFile(context.RunId, run.ReviewCycle)}. " +
                        "Judge the diff yourself, then fix in the worktree or abandon the task.", cancellationToken);
                    return false;

                case ReviewPhase.FixNeeded when run.ReviewFixRuns >= _options.MaxAutomaticReviewFixRuns:
                    await ParkAsync(context.RunId,
                        $"Review still finds defects after {run.ReviewFixRuns} automatic fix run(s) — the budget is spent. " +
                        $"Unresolved findings: {RunPaths.ReviewFindingsFile(context.RunId, run.ReviewCycle)}. " +
                        "Resolve them in the worktree or abandon the task.", cancellationToken);
                    return false;

                case ReviewPhase.FixNeeded:
                    await DispatchFixSessionAsync(context, run.ReviewCycle, cancellationToken);
                    break;

                case ReviewPhase.Disputed:
                    await ParkAsync(context.RunId,
                        $"The fix run disputed a review finding as not-a-defect or human-territory (cycle {run.ReviewCycle}). " +
                        $"Review position: {RunPaths.ReviewFindingsFile(context.RunId, run.ReviewCycle)}; " +
                        $"fix position: {RunPaths.ReviewFixPositionFile(context.RunId, run.ReviewCycle)}. " +
                        "Decide between them and act by hand.", cancellationToken);
                    return false;

                case ReviewPhase.Reverify:
                    if (!await verification.VerifyAsync(context.RunId, context.TaskId, cancellationToken))
                    {
                        // VerificationRunner already failed the run and task honestly.
                        return false;
                    }

                    await DispatchReviewSessionAsync(context, run.ReviewCycle + 1, cancellationToken);
                    break;

                case ReviewPhase.Parked:
                    return false;
            }
        }
    }

    private async Task DispatchReviewSessionAsync(ReviewContext context, int cycle, CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildReview(context.Task, context.Project, context.Run.Branch, cycle);
        ExecutorMode mode = context.Run.ExecutorMode;
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, prompt, mode,
            context.Project.SkipPermissions, SessionArtifactName(cycle, sessionId, isFix: false)), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: review agent dispatched with fresh context (cycle {Cycle}, session {SessionId}, pid {ProcessId})",
            context.RunId, cycle, sessionId, agent.ProcessId);
    }

    private async Task DispatchFixSessionAsync(ReviewContext context, int cycle, CancellationToken cancellationToken)
    {
        string findings = await File.ReadAllTextAsync(
            RunPaths.ReviewFindingsFile(context.RunId, cycle), cancellationToken);
        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildReviewFix(context.Task, context.Run.Branch, findings, cycle);
        ExecutorMode mode = context.Run.ExecutorMode;
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, prompt, mode,
            context.Project.SkipPermissions, SessionArtifactName(cycle, sessionId, isFix: true)), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewFixDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: fix run dispatched over the cycle-{Cycle} findings (session {SessionId}, pid {ProcessId})",
            context.RunId, cycle, sessionId, agent.ProcessId);
    }

    private async Task RecordReviewResultAsync(Guid runId, int cycle, AgentResult result, CancellationToken cancellationToken)
    {
        string findings = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(RunPaths.ReviewFindingsFile(runId, cycle), findings, cancellationToken);

        ReviewVerdict verdict = ReviewResultParser.ParseVerdict(findings);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new TokensRecorded(runId, result.InputTokens, result.OutputTokens, result.CostUsd, now));
        session.Events.Append(runId, new ReviewCompleted(runId, cycle, verdict, now));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: review cycle {Cycle} completed — verdict {Verdict} ({Input}in/{Output}out tokens)",
            runId, cycle, verdict == ReviewVerdict.Unknown ? "(none)" : verdict.Value, result.InputTokens, result.OutputTokens);
    }

    private async Task RecordFixResultAsync(Guid runId, int cycle, AgentResult result, CancellationToken cancellationToken)
    {
        string summary = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(RunPaths.ReviewFixPositionFile(runId, cycle), summary, cancellationToken);

        ReviewFixOutcome outcome = ReviewResultParser.ParseFixOutcome(summary);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new TokensRecorded(runId, result.InputTokens, result.OutputTokens, result.CostUsd, now));
        session.Events.Append(runId, new ReviewFixCompleted(runId, cycle, outcome, now));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: fix run for cycle {Cycle} completed — outcome {Outcome} ({Input}in/{Output}out tokens)",
            runId, cycle, outcome == ReviewFixOutcome.Unknown ? "(undeclared)" : outcome.Value, result.InputTokens, result.OutputTokens);
    }

    /// <summary>
    /// Waits for the session's terminal result: tail its stream file incrementally
    /// (cursor in memory — a restarted daemon re-enters the loop and re-reads from the
    /// start once), keep the run's last-activity fresh while output flows (so h9k status
    /// stall detection covers review legs), and give buffered output a grace period
    /// after process death. Null means the session genuinely died without a result.
    /// </summary>
    private async Task<AgentResult?> WaitForSessionResultAsync(
        Guid runId, string streamFile, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken)
    {
        DateTimeOffset? deadSince = null;
        long cursor = 0;
        StringBuilder partialLine = new();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (long newCursor, bool sawResult, AgentResult? result) =
                await StreamTailReader.ReadNewLinesAsync(streamFile, cursor, partialLine, cancellationToken);
            if (sawResult)
            {
                return result;
            }

            if (newCursor > cursor)
            {
                cursor = newCursor;
                await TouchActivityAsync(runId, cancellationToken);
            }

            if (!processManager.IsAlive(processId, processStartedAt))
            {
                // The grace window keeps polling above, so buffered output that lands
                // after death still gets read before this gives up.
                deadSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                {
                    return null;
                }
            }
            else
            {
                deadSince = null;
            }

            await Task.Delay(TailInterval, cancellationToken);
        }
    }

    private async Task TouchActivityAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Preserve StreamBytesRead — that cursor belongs to the main session's tail.
        // Load-then-store is safe here: RunSupervisor.SaveActivityAsync writes only
        // inside the main-session monitor loop, which has always returned before the
        // review loop is entered, so the two writers are sequential phases, never
        // concurrent.
        await using IDocumentSession session = store.LightweightSession();
        RunActivity activity = await session.LoadAsync<RunActivity>(runId, cancellationToken)
            ?? new RunActivity { Id = runId };
        activity.LastActivityAt = DateTimeOffset.UtcNow;
        session.Store(activity);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task ParkAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        // The task stays Claimed and the lease is retained: the worktree is the human's
        // workspace for resolving the park (the CloseoutParked pattern, pre-PR).
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new ReviewParked(runId, reason, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Run {RunId}: review parked for the human — {Reason}", runId, reason);
    }

    private async Task FailAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not null && !task.State.IsTerminal)
        {
            session.Events.Append(taskId, TaskDecider.Fail(task, runId, reason, now));
        }

        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Run {RunId} failed in the review loop: {Reason}", runId, reason);
    }

    private async Task<ReviewContext?> LoadContextAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null ? null : await query.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (run is null || task is null || project is null)
        {
            logger.LogError("Cannot review run {RunId}: run, task, or project missing", runId);
            return null;
        }

        return new ReviewContext(runId, taskId, run, task, project);
    }

    private async Task<RunAggregate> LoadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        return await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken)
            ?? throw new InvalidOperationException($"Run stream {runId} not found.");
    }

    /// <summary>
    /// Per-session artifact prefix. The session id suffix keeps a redispatched cycle
    /// (daemon died between spawn and record) from colliding with its orphan's files.
    /// </summary>
    private static string SessionArtifactName(int cycle, Guid sessionId, bool isFix) =>
        $"{(isFix ? "review-fix" : "review")}-{cycle}-{sessionId.ToString("N")[..8]}";

    private sealed record ReviewContext(Guid RunId, Guid TaskId, RunDetails Run, TaskDetails Task, ProjectDetails Project);
}
