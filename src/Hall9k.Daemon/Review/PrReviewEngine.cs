using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
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
/// <see cref="SessionResultWaiter"/> — never ReviewEngine's own cycle/track/fix-loop state
/// machine, which is built entirely around a diff this
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
    /// The recovery half of <see cref="RecordAdversarialResultAsync"/>: called unconditionally
    /// at the top of <see cref="DriveAsync"/> so a daemon restart landing between the primary
    /// session's <c>AgentSessionCompleted</c> commit and RunSupervisor's own (immediate but not
    /// atomic with it) call to <see cref="RecordAdversarialResultAsync"/> still gets the file
    /// written before anything downstream reads it. Re-derives the primary session's own result
    /// from its stream file the same way <see cref="RunResultFile.AlreadyWrittenAsync"/> detects
    /// it, rather than assuming; a no-op once the file already exists.
    /// <para>Internal for the re-entrancy unit tests (test: pr-review type guards, coverage follow-up) — pure file I/O, no store needed.</para>
    /// </summary>
    internal async Task EnsureAdversarialResultRecordedAsync(string runDirectory, CancellationToken cancellationToken)
    {
        string path = RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Adversarial.Slug);
        if (File.Exists(path))
        {
            return;
        }

        string streamFile = RunPaths.StreamFile(runDirectory);
        if (!File.Exists(streamFile))
        {
            return;
        }

        string? summary = null;
        using (StreamReader reader = new(new FileStream(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (StreamJsonParser.TryParseResult(line, out AgentResult result))
                {
                    summary = result.Summary ?? string.Empty;
                }
            }
        }

        if (summary is not null)
        {
            await RecordAdversarialResultAsync(runDirectory, summary, cancellationToken);
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

        // RecordAdversarialResultAsync's own doc comment claims this is already on disk by the
        // time ReviewAsync is ever entered — true of the live-monitor path (RunSupervisor calls
        // it immediately after AgentSessionCompleted commits), but a daemon restart landing in
        // the gap between that commit and the file write reaches here instead through the
        // Verifying-adoption sweep, with nothing written yet. Idempotent the same way the direct
        // call is, so this is a no-op once the file is actually there.
        await EnsureAdversarialResultRecordedAsync(runDirectory, cancellationToken);

        // Every other review pass gets this check (ReviewEngine.RecordReviewPassAsync); this
        // engine deliberately never enters that method (own class doc), so nothing else screens
        // the adversarial lens's raw session summary before it becomes half the findings report.
        // Read here rather than at write time so a daemon restart re-derives the same verdict
        // from the same file, with nothing extra to persist (cycle-1 conformance finding,
        // PrReviewEngine.cs:374).
        string adversarialPath = RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Adversarial.Slug);
        if (File.Exists(adversarialPath))
        {
            string adversarialSummary = await File.ReadAllTextAsync(adversarialPath, cancellationToken);
            if (await RejectUnusableVerdictAsync(
                runId, taskId, run.LeaseGeneration, "adversarial", adversarialSummary, sawTaskContext: false, task,
                cancellationToken))
            {
                return;
            }
        }

        if (aggregate.PrReviewConformanceSessionId is null
            || aggregate.PrReviewConformanceBudgetExhausted
            || !SessionStillLive(aggregate, runDirectory))
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
            if (!await AwaitConformanceAsync(runId, taskId, runDirectory, aggregate, task, cancellationToken))
            {
                return;
            }
        }

        await ComposeReportAndParkAsync(runId, taskId, runDirectory, run.LeaseGeneration, cancellationToken);
    }

    /// <summary>
    /// A dispatched-but-not-yet-completed conformance session is only genuinely resumable
    /// while its process is still alive or its result already landed on disk; a session that
    /// died in between (a daemon restart racing a crash, a budget exhaustion never recorded)
    /// is treated the same as never dispatched, so <see cref="DriveAsync"/> redispatches a
    /// fresh one rather than waiting forever on a process that is gone.
    /// <para>
    /// "Its result already landed on disk" means a terminal result line, not merely a non-empty
    /// file (cycle-1 adversarial finding): <c>claude -p --output-format stream-json</c> writes
    /// its <c>{"type":"system","subtype":"init",…}</c> line within a second of spawning, so a
    /// process killed before it ever produces a result still leaves a non-empty stream file.
    /// Treating that as live sent this straight to <see cref="AwaitConformanceAsync"/>, which
    /// waits out <c>SessionResultWaiter</c>'s grace period on an already-dead process and fails
    /// the run — exactly the case this method exists to redispatch instead. Parsed the same way
    /// <see cref="EnsureAdversarialResultRecordedAsync"/> parses the primary session's own stream.
    /// </para>
    /// <para>Internal for the liveness-discrimination unit tests (test: pr-review type guards, coverage follow-up).</para>
    /// </summary>
    internal bool SessionStillLive(RunAggregate run, string runDirectory)
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
        return File.Exists(streamFile) && StreamFileHoldsTerminalResult(streamFile);
    }

    private static bool StreamFileHoldsTerminalResult(string streamFile)
    {
        using StreamReader reader = new(new FileStream(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (StreamJsonParser.TryParseResult(line, out _))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> DispatchConformanceAsync(
        Guid runId, Guid taskId, string runDirectory, RunDetails run, TaskDetails task, ProjectDetails project,
        CancellationToken cancellationToken)
    {
        await using (IDocumentSession fenceSession = store.LightweightSession())
        {
            if (!await GenerationFence.AllowsAsync(
                fenceSession, logger, taskId, runId, run.LeaseGeneration, nameof(PrReviewConformanceDispatched), cancellationToken))
            {
                // Mirrors ReviewEngine.ParkAsync's own fence-rejection (Copilot review, PR
                // #30's RunSuperseded fix): retiring the run here, rather than just returning
                // false, is what stops a reclaimed task's stale lane from being left
                // non-terminal in Verifying with no monitor watching it.
                if (await fenceSession.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
                {
                    TaskDetails? currentTask = await fenceSession.LoadAsync<TaskDetails>(taskId, cancellationToken);
                    fenceSession.Events.Append(
                        runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? run.LeaseGeneration, DateTimeOffset.UtcNow));
                    await fenceSession.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Run {RunId}: retired as superseded — the pr-review conformance dispatch found it was no longer task {TaskId}'s current generation",
                        runId, taskId);
                }

                return false;
            }
        }

        // The base RunLauncher already resolved and recorded at dispatch (RunDispatched.PrReviewBaseRefName),
        // not a second live `gh pr view` here: the two lenses must diff against the identical
        // base, and a re-read minutes later can silently disagree with the first — the pull
        // request's base moved, or the read itself failed transiently — leaving the conformance
        // lens filing findings against a different range than the adversarial lens actually read
        // (cycle-3 conformance finding).
        string baseBranch = run.PrReviewBaseRefName.IsNotBlank() ? run.PrReviewBaseRefName : project.BaseBranch;
        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(run.WorktreePath, baseBranch, sinceSha: null, cancellationToken);

        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildPrReviewLens(task, project, run.Branch, ReviewLens.Conformance, packet, baseBranch);
        AgentModel model = _options.ResolveModel(AgentRole.Review, task.Model, project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            runId, sessionId, run.WorktreePath, runDirectory, prompt, (ExecutorMode)run.ExecutorMode, model,
            project.SkipPermissions, ConformanceArtifactName(sessionId), UntrustedWorkingDirectory: true),
            cancellationToken);

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
        Guid runId, Guid taskId, string runDirectory, RunAggregate run, TaskDetails task, CancellationToken cancellationToken)
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
            streamFile, processId, processStartedAt, processManager,
            token => TouchActivityAsync(runId, token), cancellationToken);

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

        // Recorded before the verdict is screened, not alongside PrReviewConformanceCompleted
        // below (adversarial review, cycle 2): the session already spent these tokens whether or
        // not its verdict turns out usable, and RejectUnusableVerdictAsync's own rejection path
        // fails the run without ever reaching that later write, which used to drop a
        // fully-completed session's whole spend from the run stream. ReviewEngine.RecordReviewPassAsync
        // appends tokens before any verdict handling for the identical reason (ReviewEngine.cs:996).
        await using (IDocumentSession tokensSession = store.LightweightSession())
        {
            tokensSession.Events.Append(runId, result.ToTokensRecorded(runId, DateTimeOffset.UtcNow));
            await tokensSession.SaveChangesAsync(cancellationToken);
        }

        string conformanceSummary = result.Summary ?? string.Empty;
        if (await RejectUnusableVerdictAsync(
            runId, taskId, run.LeaseGeneration, "conformance", conformanceSummary, sawTaskContext: true, task,
            cancellationToken))
        {
            return false;
        }

        // Written before PrReviewConformanceCompleted commits, not after (cycle-1 adversarial
        // finding, PrReviewEngine.cs:324): a daemon stopped in the gap between the two used to
        // leave the event recorded with no file behind it, and DriveAsync trusts the event alone
        // to skip both dispatch and await on the next pass, so the report would silently read
        // "(no findings recorded)" while the real findings sat unread in the session's own stream
        // file. ReviewEngine orders these the same way (ReviewEngine.cs:883) for the same reason.
        string path = RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Conformance.Slug);
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(path, conformanceSummary, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new PrReviewConformanceCompleted(runId, sessionId, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task ComposeReportAndParkAsync(
        Guid runId, Guid taskId, string runDirectory, int leaseGeneration, CancellationToken cancellationToken)
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

        // Mirrors DispatchConformanceAsync's and FinalizeAsync's own fence-rejection (Copilot
        // review, PR #30's RunSuperseded fix): without it, a run reclaimed while the
        // conformance lens was still running would append ReviewParked here unfenced after a
        // fresh generation already claimed the task, stranding this run non-terminal in
        // ReviewParked with no monitor and no RunSuperseded (adversarial review, cycle 1).
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, nameof(ReviewParked), cancellationToken))
        {
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                session.Events.Append(
                    runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? leaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Run {RunId}: retired as superseded — the pr-review park found it was no longer task {TaskId}'s current generation",
                    runId, taskId);
            }

            return;
        }

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

        // run.Branch is "pr/<n>" for every pr-review run (CreatePrReviewCheckoutAsync's own
        // Worktree.Branch) — the only record of which pull request this run's now-removed
        // worktree was fetched against, and so the only way to name the tracking ref left
        // behind in the bare clone (adversarial review, cycle 1: nothing else ever deletes it).
        if (PullRequestNumberFromBranch(run.Branch) is { } pullRequestNumber)
        {
            try
            {
                await worktrees.DeletePrReviewTrackingRefAsync(project.RepositoryPath, pullRequestNumber, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception, "Pr-review tracking ref cleanup failed for pull request #{Number} (safe to delete by hand)",
                    pullRequestNumber);
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // (the same ordering ReviewEngine.FailAsync and RunLauncher.RecordLaunchFailureAsync
        // use for the same reason).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, run.LeaseGeneration, nameof(RunCompleted), cancellationToken))
        {
            // A reclaim landed between the owner's resolve and this finalize: the live
            // generation now owns the task and its own lease, so this stale run must retire
            // instead of completing a task — or deleting a lease — that is no longer its own.
            // Mirrors ReviewEngine.ParkAsync's own fence-rejection: leaving the run
            // non-terminal here would strand it with no monitor until the next adoption
            // sweep stumbled onto it (Copilot review, PR #30's RunSuperseded fix).
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                session.Events.Append(
                    runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? run.LeaseGeneration, now));
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Run {RunId}: retired as superseded — the pr-review finalize found it was no longer task {TaskId}'s current generation",
                    runId, taskId);
            }

            return;
        }

        string? pullRequestUrl = task.ExternalReference.IsNotBlank()
            ? new GitHubPullRequestProvider().WebUrl(ExternalReference.Parse(task.ExternalReference))?.ToString()
            : null;

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

    private async Task FailAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // (ReviewEngine.FailAsync uses the same ordering for the same reason).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && TaskDecider.CanFail(current.Task)
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
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

    /// <summary>Keeps the run's last-activity fresh while the conformance lens works, so h9k status stall detection covers it the same way ReviewEngine's own passes are covered.</summary>
    private async Task TouchActivityAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        RunActivity activity = await session.LoadAsync<RunActivity>(runId, cancellationToken)
            ?? new RunActivity { Id = runId };
        activity.LastActivityAt = DateTimeOffset.UtcNow;
        session.Store(activity);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Internal so the liveness-discrimination unit tests can name the same file <see cref="SessionStillLive"/> reads.</summary>
    internal static string ConformanceArtifactName(Guid sessionId) => $"pr-review-conformance-{sessionId:N}";

    /// <summary>The pull request number out of a pr-review run's own <c>pr/&lt;n&gt;</c> branch name.</summary>
    private static int? PullRequestNumberFromBranch(string branch) =>
        branch.StartsWith("pr/", StringComparison.Ordinal)
        && int.TryParse(branch.AsSpan(3), out int number)
            ? number
            : null;

    private static async Task<string> ReadIfExistsAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "(no findings recorded)";

    /// <summary>
    /// The same missing-verdict / unnamed-finding gate <see cref="ReviewEngine.RecordReviewPassAsync"/>
    /// applies to every other review pass — reused here rather than reimplemented, since this
    /// engine has neither a fix-and-re-review cycle nor a re-prompt of its own to spend on a bad
    /// answer (own class doc): a session that fails this check fails the run outright, so
    /// `h9k task retry` — a real, already-documented lever — is what the owner gets instead of a
    /// findings report built from a promise never kept (cycle-1 conformance finding,
    /// PrReviewEngine.cs:374). <paramref name="sawTaskContext"/> mirrors
    /// <c>ReviewEngine.RecordReviewPassAsync</c>'s own <c>sawTaskContext</c>: true for the
    /// conformance lens, which is the only one <see cref="AgentPromptBuilder.BuildPrReviewLens"/>
    /// ever hands the task's objective, acceptance criteria, or agent context; false for the
    /// adversarial lens, which never sees any of them.
    /// </summary>
    private static bool HasUsableVerdict(string summary, bool sawTaskContext, TaskDetails task)
    {
        ReviewVerdict verdict = ReviewResultParser.ParseVerdict(summary);
        if (verdict == ReviewVerdict.Unknown)
        {
            return false;
        }

        return verdict != ReviewVerdict.NeedsFixes
            || ReviewVerdictValidation.NamesAFinding(
                summary,
                sawTaskContext ? task.Objective : null,
                sawTaskContext ? task.AcceptanceCriteria : null,
                taskAgentContext: sawTaskContext ? task.AgentContext : null);
    }

    /// <summary>
    /// <see cref="HasUsableVerdict"/>'s write half: true (caller must stop) when the summary
    /// failed the check, false (caller proceeds) when it passed. Fenced the same way every other
    /// terminal write in this class already is (<see cref="DispatchConformanceAsync"/>,
    /// <see cref="ComposeReportAndParkAsync"/>, <see cref="FinalizeAsync"/>): a reclaim landing
    /// between the summary arriving and this check must retire the stale run as
    /// <see cref="RunSuperseded"/>, never mark it <see cref="RunFailed"/> unconditionally the way
    /// a bare call into <see cref="FailAsync"/> would — that would leave a run history entry
    /// blaming a session for a bad verdict on a lane a fresh generation had already taken over.
    /// </summary>
    private async Task<bool> RejectUnusableVerdictAsync(
        Guid runId, Guid taskId, int leaseGeneration, string lensName, string summary, bool sawTaskContext,
        TaskDetails task, CancellationToken cancellationToken)
    {
        if (HasUsableVerdict(summary, sawTaskContext, task))
        {
            return false;
        }

        await using IDocumentSession session = store.LightweightSession();
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, $"PrReview{lensName}VerdictRejected", cancellationToken))
        {
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                session.Events.Append(
                    runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? leaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Run {RunId}: retired as superseded — the pr-review {Lens} verdict check found it was no longer task {TaskId}'s current generation",
                    runId, lensName, taskId);
            }

            return true;
        }

        await FailAsync(
            runId, taskId,
            $"The pr-review {lensName} session ended without a usable verdict — no VERDICT line, or a "
            + "needs-fixes verdict naming no finding. Retry the task to dispatch a fresh review.",
            cancellationToken);
        return true;
    }
}
