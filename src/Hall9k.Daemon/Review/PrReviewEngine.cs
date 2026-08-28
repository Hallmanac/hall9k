using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The pr-review task type's own, deliberately separate driver (task type PrReview,
/// AGENTS.md's "a pull-request-review task type"): a run whose primary session already IS
/// the adversarial lens (dispatched by RunLauncher like any other task, just with the
/// adversarial-review prompt and a read-only PR worktree) is completed here rather than by
/// ReviewEngine — there is no diff of this run's own to fix, re-review, or open a pull
/// request over, only someone else's already-open one to read. Dispatches the conformance
/// lens second, merges both lenses' findings into one report, and parks the run exactly the
/// way ReviewEngine's own park does (NeedsHuman, ReviewParked) — but resolving that park
/// (h9k review resolve --merge-ready on a pr-review task, ReviewResolveCommand) never
/// re-enters a review loop: it records PrReviewDelivered, and the next call here finalizes
/// the task directly (Done, no merge ever observed — AGENTS.md's "closes without any merge
/// observation").
/// <para>
/// Deliberately reuses only the stateless primitives ReviewEngine itself is built from —
/// <see cref="AgentPromptBuilder.BuildPrReviewLens"/>, <see cref="ReviewPacketAssembler"/>,
/// <see cref="ReviewResultParser"/>, <see cref="SessionResultWaiter"/> — never ReviewEngine's
/// own cycle/track/fix-loop state machine, which is built entirely around a diff this
/// platform may fix and merge. Reusing that machine's own events (ReviewDispatched,
/// ReviewPassCompleted) would risk a restarted daemon's adoption sweep resuming a pr-review
/// run through ReviewEngine.DriveAsync itself; the two small events this class owns
/// (PrReviewConformanceDispatched/Completed, PrReviewDelivered) exist so that can never
/// happen.
/// </para>
/// </summary>
public sealed class PrReviewEngine(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IWorktreeManager worktrees,
    IOptions<DaemonOptions> options,
    ILogger<PrReviewEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// Writes the primary session's own result — the adversarial lens — to disk under the
    /// same naming convention <see cref="RunPaths.ReviewLensFindingsFile"/> already uses,
    /// before <see cref="ReviewAsync"/> is ever entered. Idempotent: a resumed call finds the
    /// file already there and this is a no-op, which is what lets <see cref="ReviewAsync"/>
    /// assume it unconditionally rather than re-deriving it from the (by then long exited)
    /// primary session's process.
    /// </summary>
    public async Task RecordAdversarialResultAsync(
        string runDirectory, string summary, CancellationToken cancellationToken)
    {
        string path = RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Adversarial.Slug);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(path, summary, cancellationToken);
        }
    }

    /// <summary>
    /// Drives a pr-review run to its park (first entry) or its finalization (re-entry after
    /// h9k review resolve). Re-entrant from any point a daemon restart could have caught: the
    /// adversarial lens's findings are already on disk by the time this is ever called (see
    /// <see cref="RecordAdversarialResultAsync"/>), and every step after that checks what the
    /// run stream already recorded before dispatching anything.
    /// </summary>
    public async Task ReviewAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await DriveAsync(runId, taskId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Pr-review loop crashed for run {RunId}", runId);
            await FailAsync(runId, taskId, $"Pr-review loop failed: {exception.Message}", cancellationToken);
        }
    }

    private async Task DriveAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null ? null : await query.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (run is null || task is null || project is null)
        {
            logger.LogError("Cannot drive pr-review run {RunId}: run, task, or project missing", runId);
            return;
        }

        RunAggregate? aggregate = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken);
        if (aggregate is null)
        {
            logger.LogError("Cannot drive pr-review run {RunId}: no run stream", runId);
            return;
        }

        if (aggregate.PrReviewDelivered)
        {
            await FinalizeAsync(runId, taskId, run, task, project, cancellationToken);
            return;
        }

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        if (aggregate.PrReviewConformanceSessionId is null || !SessionStillLive(aggregate, runDirectory))
        {
            if (!await DispatchConformanceAsync(runId, taskId, runDirectory, run, task, project, cancellationToken))
            {
                return;
            }

            aggregate = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken);
            if (aggregate is null)
            {
                return;
            }
        }

        if (!aggregate.PrReviewConformanceCompleted)
        {
            if (!await AwaitConformanceAsync(runId, taskId, runDirectory, aggregate, cancellationToken))
            {
                return;
            }
        }

        await ComposeReportAndParkAsync(runId, taskId, runDirectory, cancellationToken);
    }

    /// <summary>
    /// A dispatched-but-not-yet-completed conformance session is only genuinely resumable
    /// while its process is still alive or its result already landed on disk; a session that
    /// died in between (a daemon restart racing a crash, a budget exhaustion never recorded)
    /// is treated the same as never dispatched, so <see cref="DriveAsync"/> redispatches a
    /// fresh one rather than waiting forever on a process that is gone.
    /// </summary>
    private bool SessionStillLive(RunAggregate run, string runDirectory)
    {
        if (run.PrReviewConformanceCompleted)
        {
            return true;
        }

        if (run.PrReviewConformanceProcessId is not { } processId || run.PrReviewConformanceProcessStartedAt is not { } startedAt)
        {
            return false;
        }

        if (processManager.IsAlive(processId, startedAt))
        {
            return true;
        }

        string streamFile = RunPaths.SessionStreamFile(runDirectory, ConformanceArtifactName(run.PrReviewConformanceSessionId!.Value));
        return File.Exists(streamFile) && new FileInfo(streamFile).Length > 0;
    }

    private async Task<bool> DispatchConformanceAsync(
        Guid runId, Guid taskId, string runDirectory, RunDetails run, TaskDetails task, ProjectDetails project,
        CancellationToken cancellationToken)
    {
        await using (IQuerySession fenceQuery = store.QuerySession())
        {
            if (!await GenerationFence.AllowsAsync(
                fenceQuery, logger, taskId, runId, run.LeaseGeneration, nameof(PrReviewConformanceDispatched), cancellationToken))
            {
                return false;
            }
        }

        string baseBranch = await ResolveBaseBranchAsync(task, project, cancellationToken) ?? project.BaseBranch;
        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(run.WorktreePath, baseBranch, sinceSha: null, cancellationToken);

        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildPrReviewLens(task, project, run.Branch, ReviewLens.Conformance, packet);
        AgentModel model = _options.ResolveModel(AgentRole.Review, task.Model, project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            runId, sessionId, run.WorktreePath, runDirectory, prompt, (ExecutorMode)run.ExecutorMode, model,
            project.SkipPermissions, ConformanceArtifactName(sessionId)), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new PrReviewConformanceDispatched(
            runId, sessionId, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: pr-review conformance lens dispatched (session {SessionId}, pid {ProcessId}, model {Model})",
            runId, sessionId, agent.ProcessId, model.Value);
        return true;
    }

    private async Task<bool> AwaitConformanceAsync(
        Guid runId, Guid taskId, string runDirectory, RunAggregate run, CancellationToken cancellationToken)
    {
        if (run.PrReviewConformanceSessionId is not { } sessionId
            || run.PrReviewConformanceProcessId is not { } processId
            || run.PrReviewConformanceProcessStartedAt is not { } processStartedAt)
        {
            await FailAsync(runId, taskId, "Run stream records an in-flight pr-review conformance session without its identity.", cancellationToken);
            return false;
        }

        string streamFile = RunPaths.SessionStreamFile(runDirectory, ConformanceArtifactName(sessionId));
        AgentResult? result = await SessionResultWaiter.WaitAsync(
            streamFile, processId, processStartedAt, processManager, onOutput: null, cancellationToken);

        if (result is { IsError: true, Summary: { } summary } && BudgetExhaustionParser.IsBudgetExhausted(summary))
        {
            await using IDocumentSession budgetSession = store.LightweightSession();
            budgetSession.Events.Append(runId, new RunBudgetExhausted(runId, summary, DateTimeOffset.UtcNow));
            await budgetSession.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Run {RunId}: pr-review conformance session exhausted its token budget — parked; the daemon retries hourly. {Message}",
                runId, summary);
            return false;
        }

        if (result is null || result.IsError)
        {
            await FailAsync(runId, taskId, result is null
                ? "The pr-review conformance session died without a result."
                : "The pr-review conformance session reported an error result.", cancellationToken);
            return false;
        }

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new PrReviewConformanceCompleted(runId, sessionId, DateTimeOffset.UtcNow));
        session.Events.Append(runId, result.ToTokensRecorded(runId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);

        string path = RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Conformance.Slug);
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(path, result.Summary ?? string.Empty, cancellationToken);
        return true;
    }

    private async Task ComposeReportAndParkAsync(
        Guid runId, Guid taskId, string runDirectory, CancellationToken cancellationToken)
    {
        string adversarial = await ReadIfExistsAsync(
            RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Adversarial.Slug), cancellationToken);
        string conformance = await ReadIfExistsAsync(
            RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Conformance.Slug), cancellationToken);

        string report =
            "# Pull request review findings\n\n"
            + "Nothing here was posted to the pull request or the remote — no comments, no review, no "
            + "reactions. Walk the report and direct each finding by hand: dismiss it, comment yourself, "
            + "or have the session post on your behalf. Resolve with h9k review resolve --merge-ready "
            + "when you are done; it closes the task without opening or merging anything.\n\n"
            + "## Adversarial (full depth)\n\n" + adversarial + "\n\n"
            + "## Conformance (weighted — thin basis reads as context notes, not blockers)\n\n" + conformance;

        string reportPath = RunPaths.ReviewFindingsFile(runDirectory, 1);
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new ReviewParked(
            runId,
            $"Pull request review complete. Findings: {reportPath}. Walk them, direct each one, then "
            + "resolve with h9k review resolve --merge-ready — nothing was posted to the pull request.",
            DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Run {RunId}: pr-review findings report ready and parked for the human — {Path}", runId, reportPath);
    }

    /// <summary>
    /// The owner's h9k review resolve --merge-ready verdict (PrReviewDelivered) reached the
    /// run stream; this is the daemon's own resume of that resolve (RunSupervisor's UnderReview
    /// sweep), so the finalize step — removing the worktree, completing the task, dropping the
    /// lease — belongs here rather than in the CLI command that only records the verdict.
    /// Never opens or pushes anything: the deliverable is the delivered review, not a diff.
    /// </summary>
    private async Task FinalizeAsync(
        Guid runId, Guid taskId, RunDetails run, TaskDetails task, ProjectDetails project, CancellationToken cancellationToken)
    {
        try
        {
            await worktrees.RemoveAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", run.WorktreePath);
        }

        string? pullRequestUrl = task.ExternalReference.IsNotBlank()
            ? new GitHubPullRequestProvider().WebUrl(ExternalReference.Parse(task.ExternalReference))?.ToString()
            : null;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (fenced is { } current && current.Task.State == TaskState.Claimed)
        {
            session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Complete(current.Task, runId, pullRequestUrl, now));
        }

        session.Events.Append(runId, new RunCompleted(runId, now));
        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race finalizing the pr-review task for run {RunId} — a newer claim committed first",
                taskId, runId);
            return;
        }

        logger.LogInformation("Run {RunId} task {TaskId}: pull-request review delivered — task complete, no merge ever observed", runId, taskId);
    }

    private static async Task<string?> ResolveBaseBranchAsync(TaskDetails task, ProjectDetails project, CancellationToken cancellationToken)
    {
        if (task.ExternalReference.IsBlank())
        {
            return null;
        }

        try
        {
            PullRequestFacts facts = await new GitHubPullRequestProvider().FetchFactsAsync(
                ExternalReference.Parse(task.ExternalReference).Reference, project.RepositoryPath, cancellationToken);
            return facts.BaseRefName.IsNotBlank() ? facts.BaseRefName : null;
        }
        catch (Domain.Shared.Exceptions.DomainException)
        {
            return null;
        }
    }

    private async Task FailAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (fenced is { } current && TaskDecider.CanFail(current.Task))
        {
            session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(current.Task, runId, reason, now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a pr-review failure for run {RunId} — a newer claim committed first",
                taskId, runId);
            return;
        }

        logger.LogWarning("Run {RunId} pr-review failed: {Reason}", runId, reason);
    }

    private static string ConformanceArtifactName(Guid sessionId) => $"pr-review-conformance-{sessionId:N}";

    private static async Task<string> ReadIfExistsAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "(no findings recorded)";
}
