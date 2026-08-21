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
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The pre-PR review loop (Decisions Log #23), between VerificationRunner and
/// PullRequestOpener: an independent review agent — a fresh headless session that never
/// saw the implementation reasoning — reads the run's diff; a needs-fixes verdict
/// dispatches a fix session in the same worktree, gates re-run, and a fresh reviewer
/// looks again. Bounded by DaemonOptions.MaxAutomaticReviewFixRuns (the closeout
/// retry-budget pattern); the budget spent or a disputed finding parks the run for the
/// human, and a missing verdict gets ONE same-session re-prompt before parking. A park
/// is resolved with h9k review resolve (ReviewParkResolved re-enters the loop here).
/// The loop is a state machine over the run stream, so a restarted daemon resumes it
/// exactly where the events left off.
/// </summary>
public sealed class ReviewEngine(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    VerificationRunner verification,
    IOptions<DaemonOptions> options,
    ILogger<ReviewEngine> logger)
{
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

                case ReviewPhase.VerdictMissing when run.VerdictRepromptedCycle >= run.ReviewCycle:
                    // The one re-prompt is spent; guessing what the reviewer meant would
                    // be worse than asking (never guess at unobserved facts).
                    await ParkAsync(context.RunId,
                        $"The review session (cycle {run.ReviewCycle}) returned no parseable verdict, " +
                        "even after a re-prompt. " +
                        $"Its output: {RunPaths.ReviewFindingsFile(context.RunId, run.ReviewCycle)}. " +
                        "Judge the diff yourself, then resolve with h9k review resolve or abandon the task.",
                        cancellationToken);
                    return false;

                case ReviewPhase.VerdictMissing:
                    // One same-session re-prompt: the reviewer already read the diff;
                    // it only needs to conclude (wait for its checks, then the verdict).
                    if (!await RepromptForVerdictAsync(context, run, cancellationToken))
                    {
                        return false;
                    }

                    break;

                case ReviewPhase.FixNeeded when run.ReviewFixRuns >= _options.MaxAutomaticReviewFixRuns:
                    await ParkAsync(context.RunId,
                        $"Review still finds defects after {run.ReviewFixRuns} automatic fix run(s) — the budget is spent. " +
                        $"Unresolved findings: {RunPaths.ReviewFindingsFile(context.RunId, run.ReviewCycle)}. " +
                        "Fix in the worktree and resolve with h9k review resolve --merge-ready, grant a fix " +
                        "session with --needs-fixes, or abandon the task.", cancellationToken);
                    return false;

                case ReviewPhase.FixNeeded:
                    await DispatchFixSessionAsync(context, run.ReviewCycle, run.PendingHumanFindings, cancellationToken);
                    break;

                case ReviewPhase.Disputed:
                    await ParkAsync(context.RunId,
                        $"The fix run disputed a review finding as not-a-defect or human-territory (cycle {run.ReviewCycle}). " +
                        $"Review position: {RunPaths.ReviewFindingsFile(context.RunId, run.ReviewCycle)}; " +
                        $"fix position: {RunPaths.ReviewFixPositionFile(context.RunId, run.ReviewCycle)}. " +
                        "Decide between them, then resolve with h9k review resolve.", cancellationToken);
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
        // The reviewer resolves the chain in its own right: a review session reads far more
        // than it writes and may warrant a different tier than the build session did (log #33).
        AgentModel model = _options.ResolveModel(AgentRole.Review, context.Task.Model, context.Project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, prompt, mode, model,
            context.Project.SkipPermissions, SessionArtifactName(cycle, sessionId, isFix: false)), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: review agent dispatched with fresh context (cycle {Cycle}, session {SessionId}, pid {ProcessId}, model {Model})",
            context.RunId, cycle, sessionId, agent.ProcessId, model.Value);
    }

    /// <summary>
    /// Resumes the verdict-less review session (claude -p --resume) and tells it to
    /// conclude — the one re-prompt before a park. The spawn carries a fresh artifact
    /// identity so the resumed leg's stream file never collides with the original's
    /// (which already ended in a result event the waiter must not re-read).
    /// </summary>
    private async Task<bool> RepromptForVerdictAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        if (run.LastReviewSessionId is not { } resumeSessionId)
        {
            await FailAsync(context.RunId, context.TaskId,
                $"Run stream records a verdict-less review (cycle {run.ReviewCycle}) without its session identity.",
                cancellationToken);
            return false;
        }

        Guid artifactId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildReviewVerdictReprompt(run.ReviewCycle);
        // The resumed session keeps the model it was dispatched on: the chain is NOT
        // re-resolved here, or the milestone would record a model the session never ran on
        // (log #33). An older stream that recorded no model stays honestly Unknown.
        AgentModel model = run.LastReviewSessionModel;
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, artifactId, context.Run.WorktreePath, prompt, context.Run.ExecutorMode, model,
            context.Project.SkipPermissions, SessionArtifactName(run.ReviewCycle, artifactId, isFix: false),
            ResumeSessionId: resumeSessionId), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewVerdictReprompted(
            context.RunId, artifactId, resumeSessionId, run.ReviewCycle,
            agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Run {RunId}: review cycle {Cycle} ended without a verdict — session {SessionId} resumed for its one re-prompt (pid {ProcessId})",
            context.RunId, run.ReviewCycle, resumeSessionId, agent.ProcessId);
        return true;
    }

    /// <summary>
    /// Dispatches a fix session over the reviewer's findings for the cycle — or, after a
    /// needs-fixes h9k review resolve, over the human's stated findings (the event
    /// carries them; the findings file still holds the reviewer's own last words).
    /// </summary>
    private async Task DispatchFixSessionAsync(
        ReviewContext context, int cycle, string? humanFindings, CancellationToken cancellationToken)
    {
        string findings = humanFindings.IsNotBlank()
            ? $"Human review verdict (h9k review resolve): needs fixes.\n\n{humanFindings}"
            : await File.ReadAllTextAsync(RunPaths.ReviewFindingsFile(context.RunId, cycle), cancellationToken);
        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildReviewFix(context.Task, context.Run.Branch, findings, cycle);
        ExecutorMode mode = context.Run.ExecutorMode;
        // Fix is its own role: applying findings someone else reasoned out is a different
        // shape of work from producing them, so it resolves separately (log #33).
        AgentModel model = _options.ResolveModel(AgentRole.Fix, context.Task.Model, context.Project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, prompt, mode, model,
            context.Project.SkipPermissions, SessionArtifactName(cycle, sessionId, isFix: true)), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewFixDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: fix run dispatched over the cycle-{Cycle} findings (session {SessionId}, pid {ProcessId}, model {Model})",
            context.RunId, cycle, sessionId, agent.ProcessId, model.Value);
    }

    private async Task RecordReviewResultAsync(Guid runId, int cycle, AgentResult result, CancellationToken cancellationToken)
    {
        string findings = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(RunPaths.ReviewFindingsFile(runId, cycle), findings, cancellationToken);

        ReviewVerdict verdict = ReviewResultParser.ParseVerdict(findings);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));
        session.Events.Append(runId, new ReviewCompleted(runId, cycle, verdict, now));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: review cycle {Cycle} completed — verdict {Verdict} ({Input}in/{Output}out tokens)",
            runId, cycle, verdict == ReviewVerdict.Unknown ? "(none)" : verdict.Value, result.TotalInputTokens, result.OutputTokens);
    }

    private async Task RecordFixResultAsync(Guid runId, int cycle, AgentResult result, CancellationToken cancellationToken)
    {
        string summary = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(RunPaths.ReviewFixPositionFile(runId, cycle), summary, cancellationToken);

        ReviewFixOutcome outcome = ReviewResultParser.ParseFixOutcome(summary);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));
        session.Events.Append(runId, new ReviewFixCompleted(runId, cycle, outcome, now));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: fix run for cycle {Cycle} completed — outcome {Outcome} ({Input}in/{Output}out tokens)",
            runId, cycle, outcome == ReviewFixOutcome.Unknown ? "(undeclared)" : outcome.Value, result.TotalInputTokens, result.OutputTokens);
    }

    /// <summary>
    /// Waits for the session's terminal result through the shared waiter, keeping the run's
    /// last-activity fresh while output flows so h9k status stall detection covers review
    /// legs. Null means the session genuinely died without a result.
    /// </summary>
    private Task<AgentResult?> WaitForSessionResultAsync(
        Guid runId, string streamFile, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken) =>
        SessionResultWaiter.WaitAsync(
            streamFile, processId, processStartedAt, processManager,
            token => TouchActivityAsync(runId, token), cancellationToken);

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
        if (task is not null && TaskDecider.CanFail(task))
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
