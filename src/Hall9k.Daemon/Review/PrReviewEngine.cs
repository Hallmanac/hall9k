using Hall9k.Connectors.Text;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.AutoPrReview;
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
using Marten.Linq.MatchesSql;
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
/// <see cref="AgentPromptBuilder.BuildPrReviewLens"/>, <see cref="SessionResultWaiter"/> —
/// never ReviewEngine's own cycle/track/fix-loop state machine, which is built entirely
/// around a diff this platform may fix and merge. Reusing that machine's own events
/// (ReviewDispatched, ReviewPassCompleted) would risk a restarted daemon's adoption sweep
/// resuming a pr-review run through ReviewEngine.DriveAsync itself; the two small events
/// this class owns (PrReviewConformanceDispatched/Completed, PrReviewDelivered) exist so
/// that can never happen.
/// </para>
/// </summary>
public sealed class PrReviewEngine(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IWorktreeManager worktrees,
    LaunchHoldEngine launchHold,
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
            // Cancellation is excluded above on purpose, same as ReviewEngine.ReviewAsync: a
            // stopping daemon leaves its agents running and reattaches. A crash is the other
            // case — a still-live conformance session is a live agent process in the untrusted
            // foreign checkout with nobody left to read its findings, so it does not get to
            // keep running (adversarial review, cycle 7).
            await TerminateInFlightConformanceSessionAsync(runId, cancellationToken);
            await FailAsync(runId, taskId, $"Pr-review loop failed: {exception.Message}", cancellationToken);
        }
    }

    /// <summary>
    /// Best-effort cleanup on the crash path, mirroring
    /// <see cref="ReviewEngine.TerminateInFlightSessionsAsync"/>: failures here are swallowed
    /// deliberately, since this runs inside error handling and a run that cannot be read is
    /// already being failed for that reason.
    /// </summary>
    private async Task TerminateInFlightConformanceSessionAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await using IQuerySession query = store.QuerySession();
            RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken);
            if (run is { PrReviewConformanceCompleted: false }
                && run.PrReviewConformanceProcessId is { } processId
                && run.PrReviewConformanceProcessStartedAt is { } startedAt)
            {
                processManager.Terminate(processId, startedAt);
                logger.LogWarning(
                    "Run {RunId}: terminated the in-flight pr-review conformance session (pid {ProcessId}) after the loop crashed",
                    runId, processId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Run {RunId}: could not terminate the run's in-flight pr-review conformance session", runId);
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

        // A bounded mention follow-up lap (idea 2f079bcd) never enters the two-lens dance below at
        // all: it dispatched one narrowly-scoped session already, at launch, and this is that
        // session's own completion reaching the daemon — the identical entry point an ordinary
        // review's primary session completion reaches, just for a run RunLauncher.LaunchPrReviewMentionFollowUpAsync
        // shaped differently from the start.
        if (aggregate.PrReviewMentionCommentId is not null)
        {
            await DriveMentionFollowUpAsync(runId, taskId, run, task, cancellationToken);
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
            || aggregate.PrReviewConformanceLaunchHeld
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
            if (!await AwaitConformanceAsync(
                runId, taskId, runDirectory, aggregate, LaunchHoldNodeOf(run), task, cancellationToken))
            {
                return;
            }
        }

        await ComposeReportAndParkAsync(runId, taskId, runDirectory, run.LeaseGeneration, task, cancellationToken);
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
                fenceSession, logger, taskId, runId, run.LeaseGeneration, nameof(PrReviewConformanceDispatched), cancellationToken,
                refuseAbandonedTask: true))
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

        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildPrReviewLens(
            task, project, run.Branch, ReviewLens.Conformance, baseBranch, commandTimeout: _options.VerifyGateTimeout);
        AgentModel model = _options.ResolveModel(AgentRole.Review, task.Model, project.Model);
        // pr-review has no cycle loop — one adversarial pass (the run's own primary session)
        // and one conformance pass — so this reads as cycle 1 always, never RunDetails.ReviewCycle,
        // which pr-review never sets.
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.ReviewConformance(1));
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            runId, sessionId, run.WorktreePath, runDirectory, prompt, (ExecutorMode)run.ExecutorMode, model,
            project.SkipPermissions, ConformanceArtifactName(sessionId), UntrustedWorkingDirectory: true)
        {
            SessionName = sessionName,
        },
            cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new PrReviewConformanceDispatched(
            runId, sessionId, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model, sessionName));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: pr-review conformance lens dispatched (session {SessionId}, pid {ProcessId}, model {Model})",
            runId, sessionId, agent.ProcessId, model.Value);
        return true;
    }

    private async Task<bool> AwaitConformanceAsync(
        Guid runId, Guid taskId, string runDirectory, RunAggregate run, Guid launchHoldNodeId, TaskDetails task,
        CancellationToken cancellationToken)
    {
        if (run.PrReviewConformanceSessionId is not { } sessionId
            || run.PrReviewConformanceProcessId is not { } processId
            || run.PrReviewConformanceProcessStartedAt is not { } processStartedAt)
        {
            await FailAsync(runId, taskId, "Run stream records an in-flight pr-review conformance session without its identity.", cancellationToken);
            return false;
        }

        string streamFile = RunPaths.SessionStreamFile(runDirectory, ConformanceArtifactName(sessionId));
        SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
            streamFile, processId, processStartedAt, processManager,
            token => TouchActivityAsync(runId, token), cancellationToken);
        AgentResult? result = wait.Result;
        await ClearLaunchHoldIfEvidencedAsync(launchHoldNodeId, result, processStartedAt, cancellationToken);

        if (wait.EndedAfterResultGrace)
        {
            logger.LogWarning(
                "Run {RunId}: the pr-review conformance session was ended after its result because it did not exit",
                runId);
        }

        if (wait.Lingering.Count > 0)
        {
            logger.LogWarning(
                "Run {RunId}: the pr-review conformance session left {Count} process(es) still running after its terminal result arrived — terminated pid(s) {Pids}",
                runId, wait.Lingering.Count, string.Join(", ", wait.Lingering));
        }

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

        if (result is { IsError: true } errorResult
            && LaunchFailureClassifier.IsLaunchFailure(errorResult, _options.LaunchFailureMaxDuration))
        {
            // The node never actually launched a working conformance session — not this run's
            // own fault (task: a session that exits at once with no work done is treated as the
            // node failing to launch sessions). Mirrors ReviewEngine.HoldForLaunchFailureAsync's
            // identical branch for its own four legs; the conformance lens was the one
            // completion site this task's own launch-hold check never reached (independent
            // pre-PR review, cycle 3, conformance lens).
            string observedMessage = errorResult.Summary ?? "(no message)";
            await launchHold.RaiseOrJoinAsync(launchHoldNodeId, runId, observedMessage, cancellationToken);

            await using IDocumentSession holdSession = store.LightweightSession();
            holdSession.Events.Append(
                runId, errorResult.ToTokensRecorded(runId, DateTimeOffset.UtcNow, run.PrReviewConformanceModel));
            holdSession.Events.Append(runId, new RunLaunchHeld(runId, observedMessage, DateTimeOffset.UtcNow));
            await holdSession.SaveChangesAsync(cancellationToken);
            return false;
        }

        // Unlike ReviewEngine's own review-pass, fix, and rebase-recovery legs, the conformance
        // lens has no in-place retry to protect (independent pre-PR review, cycle 1, conformance
        // lens, criterion 5, "the ordinary failure path stays unchanged") — every ordinary error
        // here already fails outright below, hold or no hold, so joining a standing hold on this
        // session's own genuine error would give this leg a resume it never had and was never
        // meant to get, rather than mirroring ReviewEngine's bounded, retry-preserving join.

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
            tokensSession.Events.Append(runId, result.ToTokensRecorded(runId, DateTimeOffset.UtcNow, run.PrReviewConformanceModel));
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

    /// <summary>
    /// Writes a mention follow-up's own primary (and only) session result to disk — the sibling of
    /// <see cref="RecordAdversarialResultAsync"/>, kept as its own method rather than a shared one
    /// with an extra path parameter so a caller can never hand the wrong file to the wrong reader:
    /// an ordinary review's adversarial file and a follow-up's addendum draft mean different things
    /// to different readers, and a shared method risks one bug silently mixing the two up.
    /// </summary>
    public async Task RecordMentionFollowUpResultAsync(
        string runDirectory, string summary, CancellationToken cancellationToken)
    {
        string path = MentionFollowUpResultFile(runDirectory);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(path, summary, cancellationToken);
        }
    }

    /// <summary>The recovery half of <see cref="RecordMentionFollowUpResultAsync"/>, mirroring <see cref="EnsureAdversarialResultRecordedAsync"/>'s identical daemon-restart gap.</summary>
    private async Task EnsureMentionFollowUpResultRecordedAsync(string runDirectory, CancellationToken cancellationToken)
    {
        string path = MentionFollowUpResultFile(runDirectory);
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
            await RecordMentionFollowUpResultAsync(runDirectory, summary, cancellationToken);
        }
    }

    private static string MentionFollowUpResultFile(string runDirectory) =>
        Path.Combine(runDirectory, "mention-followup-session-result.md");

    /// <summary>
    /// Drives a bounded mention follow-up lap from its single session's completion (idea 2f079bcd,
    /// decision 2 and 3): reads the session's own final answer, writes it verbatim as the addendum
    /// beside the original report, and parks the task exactly as the original review's own
    /// <see cref="ComposeReportAndParkAsync"/> does — the SAME <see cref="ReviewParked"/> event, so
    /// the task stays Claimed and the run's own ReviewParked state is what every existing
    /// board-rendering and <c>h9k review resolve</c> surface already reads. Resolving the park
    /// (still <c>h9k review resolve --merge-ready</c>, since a pr-review task takes no other
    /// verdict) records <see cref="PrReviewDelivered"/> exactly as an ordinary review's park does,
    /// so <see cref="DriveAsync"/>'s own unconditional <see cref="FinalizeAsync"/> branch re-parks
    /// the task waiting on the pull request without needing to know this park came from a follow-up
    /// at all — the same reuse the class doc promises.
    /// </summary>
    private async Task DriveMentionFollowUpAsync(
        Guid runId, Guid taskId, RunDetails run, TaskDetails task, CancellationToken cancellationToken)
    {
        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        await EnsureMentionFollowUpResultRecordedAsync(runDirectory, cancellationToken);

        string resultPath = MentionFollowUpResultFile(runDirectory);
        if (!File.Exists(resultPath))
        {
            // The session has not completed yet, or a daemon restart landed before its result was
            // ever recorded — the next adoption sweep or completion notification re-enters here and
            // re-derives it, exactly as the ordinary review's own EnsureAdversarialResultRecordedAsync
            // gap is handled.
            return;
        }

        string summary = await File.ReadAllTextAsync(resultPath, cancellationToken);
        if (summary.IsBlank())
        {
            await FailAsync(
                runId, taskId,
                "The pr-review mention follow-up session ended with no answer to record. Retry the task "
                + "to dispatch a fresh follow-up.",
                cancellationToken);
            return;
        }

        string addendumPath = Path.Combine(runDirectory, "mention-followup-addendum.md");
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(addendumPath, summary, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, run.LeaseGeneration, nameof(ReviewParked), cancellationToken,
            refuseAbandonedTask: true))
        {
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                session.Events.Append(
                    runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? run.LeaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Run {RunId}: retired as superseded — the mention follow-up park found it was no longer task {TaskId}'s current generation",
                    runId, taskId);
            }

            return;
        }

        // Named here, in the needs-you line itself, rather than left for the addendum file to say
        // (idea 2f079bcd, decision 3): the pull request, who tagged the owner, and the first line
        // of their comment are exactly what the orchestrator window's own board reads off
        // run.ParkedReason for every other park, and a reader deciding what to look at next should
        // not have to open the file to learn who is asking and about what.
        //
        // Read off the ObservedReviewMention row for the exact comment THIS run answers
        // (run.PrReviewMentionCommentId, frozen at dispatch), never task.LatestMention* — a second
        // mention attaching while this run was in flight moves those fields to the newer comment,
        // and this park would otherwise credit the wrong author with the answer it actually gives
        // (independent pre-PR review, cycle 1, both lenses).
        ObservedReviewMention? answeredMention = run.PrReviewMentionCommentId is { } answeredCommentId
            ? await session.Query<ObservedReviewMention>()
                .Where(mention => mention.CommentId == answeredCommentId)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        string pullRequestName = task.ExternalReference.IsNotBlank()
            ? ExternalReference.Parse(task.ExternalReference).Reference
            : "this pull request";
        string firstCommentLine = FirstLine(answeredMention?.CommentBody ?? string.Empty);

        // This follow-up is one of two things AwaitsPrReviewMentionFollowUp admits (idea 2f079bcd,
        // decision 2 and 3), and only one of them leaves an earlier report stranded: when the
        // claim moved CurrentRunId here because an earlier run's own findings report was still
        // parked and unwalked, that run's own ReviewParked state is untouched by the claim — its
        // report sits exactly where ComposeReportAndParkAsync left it, but this park reason is now
        // the only thing the board or h9k task show surfaces, and it used to say nothing about that
        // report at all (independent pre-PR review, cycle 3, conformance lens). The other admitted
        // case (AwaitsPrReviewFollowThrough: the review was already delivered and resolved, and the
        // task is only waiting on the pull request or the author) has no such report left behind —
        // the previous run there is Done or AwaitingAuthor-parked, never ReviewParked — so the note
        // is empty and this addendum is the only thing outstanding, exactly as before.
        Guid previousRunId = task.RunIds.LastOrDefault(id => id != runId);
        RunDetails? previousRun = previousRunId != Guid.Empty
            ? await session.LoadAsync<RunDetails>(previousRunId, cancellationToken)
            : null;
        string unwalkedReportNote = previousRun is { State: var previousState } && previousState == RunState.ReviewParked
            ? $" Its own findings report is also still parked and unwalked: "
              + $"{RunPaths.ReviewFindingsFile(RunPaths.ResolveCurrentDirectory(previousRun.RunDirectory), 1)}."
            : string.Empty;

        session.Events.Append(runId, new ReviewParked(
            runId,
            $"{pullRequestName}: {answeredMention?.CommentAuthorLogin ?? "someone"} tagged you"
            + (firstCommentLine.IsNotBlank() ? $" — \"{firstCommentLine}\"" : string.Empty)
            + $". Addendum: {addendumPath}."
            + unwalkedReportNote
            + " Walk it with walk-pr-review-findings — show the drafted reply, "
            + "take any edits, and post it only on the owner's explicit go — then resolve with "
            + "h9k review resolve --merge-ready.",
            DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: pr-review mention follow-up ready and parked for the human — {Path}", runId, addendumPath);
    }

    private async Task ComposeReportAndParkAsync(
        Guid runId, Guid taskId, string runDirectory, int leaseGeneration, TaskDetails task,
        CancellationToken cancellationToken)
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
            + "when you are done; it opens or merges nothing of its own, and parks the task waiting on "
            + "the pull request until it merges or closes.\n\n"
            + "## Adversarial (full depth)\n\n" + adversarial + "\n\n"
            + "## Conformance (weighted — thin basis reads as context notes, not blockers)\n\n" + conformance;

        // A mint whose own trigger was a mention (idea 2f079bcd, decision 2 and 3): the primary
        // session was also asked to write this file, and its content already opens with the
        // report's own "# You were asked" heading, so it is appended verbatim rather than
        // reformatted. Read off the file's own presence, not task.LatestMentionCommentId: a
        // mention that attached to this same task WHILE this run was already in flight (recorded
        // on the stream, but too late for RunLauncher to have asked this session to answer it)
        // leaves that field non-null with no file to show for it — the park reason below must
        // never claim an answer this report does not actually carry.
        string mentionAnswerPath = Path.Combine(runDirectory, "mention-answer.md");
        string? mentionAnswer = File.Exists(mentionAnswerPath)
            ? await File.ReadAllTextAsync(mentionAnswerPath, cancellationToken)
            : null;
        if (mentionAnswer.IsNotBlank())
        {
            report += "\n\n" + mentionAnswer;
        }

        string reportPath = RunPaths.ReviewFindingsFile(runDirectory, 1);
        await File.WriteAllTextAsync(reportPath, report, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();

        // Mirrors DispatchConformanceAsync's and FinalizeAsync's own fence-rejection (Copilot
        // review, PR #30's RunSuperseded fix): without it, a run reclaimed while the
        // conformance lens was still running would append ReviewParked here unfenced after a
        // fresh generation already claimed the task, stranding this run non-terminal in
        // ReviewParked with no monitor and no RunSuperseded (adversarial review, cycle 1).
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, nameof(ReviewParked), cancellationToken,
            refuseAbandonedTask: true))
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

        // Named here, in the needs-you line itself, when this report actually answers a mention
        // (idea 2f079bcd, decision 3) — the same "pull request, who tagged you, first line of the
        // comment" shape DriveMentionFollowUpAsync's own park reason carries for the attach case.
        // Gated on mentionAnswer, not task.LatestMentionCommentId: a mention that attached to this
        // task while this same run was already in flight leaves that field set with no answer in
        // the report to point at, and the line must never claim one that is not there.
        //
        // Read off the ObservedReviewMention row this task was actually MINTED from (Outcome ==
        // TaskCreated) in preference to task.LatestMention* — a second mention attaching to this
        // task while this same run was still in flight moves those fields to the newer comment,
        // which this report never answered (independent pre-PR review, cycle 1, both lenses). No
        // such row exists for a task this mention only EXTENDED (Outcome == Attached) rather than
        // minted — a review-request-origin task that picked up a mention before its own first
        // dispatch is exactly this shape, and it still gets the addendum and mentionAnswer.md, so
        // it must still get a needs-you prefix naming who asked — falling back to task.LatestMention*
        // there, the same data RunLauncher's own mint addendum falls back to, on the same accepted
        // trade: a further mention landing mid-run can still move it before this park line reads it.
        ObservedReviewMention? mintingMention = mentionAnswer.IsNotBlank()
            ? await session.Query<ObservedReviewMention>()
                .Where(mention => mention.TaskId == taskId)
                .Where(mention => mention.MatchesSql("d.data ->> 'outcome' = ?", ReviewMentionOutcome.TaskCreated.Value))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        string? mentionAuthorLogin = mintingMention?.CommentAuthorLogin ?? task.LatestMentionAuthorLogin;
        string mentionBody = mintingMention?.CommentBody ?? task.LatestMentionBody ?? string.Empty;
        string mentionPrefix = mentionAnswer.IsNotBlank()
            ? $"{(task.ExternalReference.IsNotBlank() ? ExternalReference.Parse(task.ExternalReference).Reference : "This pull request")}: "
              + $"{mentionAuthorLogin ?? "someone"} tagged you"
              + (FirstLine(mentionBody) is { Length: > 0 } firstLine ? $" — \"{firstLine}\". " : ". ")
            : string.Empty;
        session.Events.Append(runId, new ReviewParked(
            runId,
            $"{mentionPrefix}Pull request review complete. Findings: {reportPath}. Walk them, direct each one, "
            + "then resolve with h9k review resolve --merge-ready — nothing was posted to the pull request.",
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
        // A run with no recorded worktree is a reviewer's own lap opened with h9k pr review
        // --no-worktree (Decisions Log #149): no checkout was ever made, so no tracking ref was
        // fetched either, and both cleanups below have nothing to act on. The run's own record is
        // the only place a checkout is ever named — a lap deliberately cannot gain one it did not
        // open with, precisely so this read stays the single source of truth for every consumer
        // (this finalize, RunLauncher.CleanUpPreviousPrReviewWorktreesAsync, h9k task show) rather
        // than each growing its own fallback (self-review, round one). Skipped rather than
        // attempted-and-swallowed, because `git worktree remove ""` and `git update-ref -d` on a
        // ref that was never created both fail, and a warning logged for a cleanup that was never
        // needed reads as a leak that has to be chased (AGENTS.md: an honest absence beats a
        // plausible-looking failure).
        bool hasCheckout = run.WorktreePath.IsNotBlank();
        if (hasCheckout)
        {
            try
            {
                await worktrees.RemoveAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", run.WorktreePath);
            }
        }

        // run.Branch is "pr/<n>" for every pr-review run (CreatePrReviewCheckoutAsync's own
        // Worktree.Branch, and the lap's own RunDispatched records the identical name) — the only
        // record of which pull request this run's now-removed worktree was fetched against, and
        // so the only way to name the tracking ref left behind in the bare clone (adversarial
        // review, cycle 1: nothing else ever deletes it).
        if (hasCheckout && PullRequestNumberFromBranch(run.Branch) is { } pullRequestNumber)
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

        // The posted review is not the ending any more (task: a pr-review task stays open while
        // the pull request's review threads are unresolved): the task parks on the pull request
        // and the closeout watcher's own follow-through sweep decides when it is actually over —
        // only the pull request itself merging or closing, or a human's own h9k task abandon
        // (Decisions Log #178: every thread the reviewer opened resolving no longer ends the wait
        // by itself, because a task that closed out just because it had nothing left to watch
        // would leave a later mention with no live task to attach to). Which of the three delivery
        // routes got here (h9k pr approve, h9k pr request-changes, or a review the owner posted by
        // hand and then closed with h9k review resolve --merge-ready) is deliberately not
        // re-derived: what the follow-through watches is the pull request, and it says the same
        // thing whoever typed the review. A review with nothing outstanding on it — an approval
        // with no threads, a findings report dismissed without posting anything — waits exactly as
        // long as one that posted plenty: one pr-review task per pull request per install stays
        // open until the pull request itself merges or closes, so a later mention on the same pull
        // request always has a live task to attach to instead of minting a second one.
        //
        // Done stays the answer for a pr-review task whose own reference cannot be read: there is
        // genuinely nothing to poll, so waiting would park it forever on a watch nothing performs.
        if (fenced is { } current && current.Task.State == TaskState.Claimed)
        {
            session.Events.Append(
                taskId,
                expectedVersion: current.Version + 1,
                pullRequestUrl is not null
                    ? TaskDecider.OpenPrReviewFollowThrough(
                        current.Task, runId, pullRequestUrl, ReviewedHeadShaOf(task), now)
                    : TaskDecider.Complete(current.Task, runId, pullRequestUrl, now));
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

        logger.LogInformation(
            "Run {RunId} task {TaskId}: pull-request review delivered — {Ending}",
            runId, taskId,
            pullRequestUrl is not null
                ? "the task now waits on the pull request's author, and the closeout watcher polls it"
                : "task complete, no readable pull-request reference left to watch");
    }

    /// <summary>
    /// The head the review was posted against, or null when no verdict recorded one — a review
    /// closed the older way (<c>h9k review resolve --merge-ready</c>, nothing posted from here)
    /// carries none. It is the follow-through's own starting point for "has the author pushed",
    /// and a null simply means the first poll establishes that baseline instead, which is what it
    /// does for the thread counts either way.
    /// </summary>
    private static string? ReviewedHeadShaOf(TaskDetails task) =>
        task.ReviewerVerdictHeadSha.IsNotBlank() ? task.ReviewerVerdictHeadSha : null;

    /// <summary>
    /// The pr-review conformance lens's own equivalent of <c>ReviewEngine.ClearLaunchHoldIfEvidencedAsync</c>
    /// (task: a session that exits at once with no work done is treated as the node failing to
    /// launch sessions): a completion that recorded tokens is evidence this node can still launch
    /// working sessions, whether or not a hold happens to be standing — evidence is "this launch
    /// spent tokens", never merely "this launch did not match the narrow zero-work shape"
    /// (independent pre-PR review, cycle 3, conformance lens, criterion 3).
    /// </summary>
    private async Task ClearLaunchHoldIfEvidencedAsync(
        Guid nodeId, AgentResult? result, DateTimeOffset sessionStartedAt, CancellationToken cancellationToken)
    {
        if (result is not null && LaunchFailureClassifier.RecordedTokens(result))
        {
            await launchHold.ClearIfEvidencedAsync(nodeId, sessionStartedAt, cancellationToken);
        }
    }

    /// <summary>
    /// The node whose launch hold this run's conformance lens raises, joins, or clears: the run's
    /// own <see cref="RunDetails.NodeId"/>, except for a Now-speed auto-pr-review run, which
    /// carries the ceiling-exempt <see cref="Guid.Empty"/> there and names the daemon that
    /// actually launched it only on <see cref="RunDetails.DispatchingNodeId"/>. Reading
    /// <c>NodeId</c> alone raised that run's hold on a phantom <see cref="Guid.Empty"/> node
    /// stream no probe ever reads, while the real node's own hold stayed inactive, so
    /// <c>LaunchHoldMonitor</c>'s inactive-branch sweep (which does find the run, through
    /// <c>LaunchHoldEngine.HeldRunsAsync</c>'s own sentinel widening) resumed it into the same
    /// broken node on every poll tick (independent pre-PR review, cycle 4, found by the fix
    /// session). A build run never needs this: delivery rewrites its <c>NodeId</c> to the
    /// delivering node (<c>AgentSessionCompleted.DeliveredByNodeId</c>) before any review leg runs.
    /// </summary>
    private static Guid LaunchHoldNodeOf(RunDetails run) =>
        run.NodeId == Guid.Empty
            ? run.DispatchingNodeId
            : run.NodeId;

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
    /// The mentioning comment's own first line, one-lined, relayed-text-defused and bounded to
    /// <see cref="FirstLineMaxLength"/> — what the mention needs-you line quotes, never the whole
    /// comment. Every other relayed-text-into-one-line site in the daemon pairs
    /// <see cref="RelayedText.OneLine"/> with <see cref="RelayedText.Truncate"/>; this one used to
    /// be the exception, leaving an externally-authored comment with no newline free to reach
    /// <c>ParkedReason</c> (and every <c>h9k status</c>/<c>h9k task show</c> that prints it)
    /// unbounded (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    private static string FirstLine(string? body)
    {
        if (body.IsBlank())
        {
            return string.Empty;
        }

        string first = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
        return RelayedText.Truncate(RelayedText.OneLine(first).Trim(), FirstLineMaxLength);
    }

    private const int FirstLineMaxLength = 200;

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
