using System.Collections.ObjectModel;
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
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The pre-PR review loop (Decisions Log #23), between VerificationRunner and
/// PullRequestOpener: independent review agents — fresh headless sessions that never
/// saw the implementation reasoning — read the run's diff; a needs-fixes verdict
/// dispatches a fix session in the same worktree, gates re-run, and fresh reviewers
/// look again. Bounded by DaemonOptions.MaxAutomaticReviewFixRuns (the closeout
/// retry-budget pattern); the budget spent or a disputed finding parks the run for the
/// human, and a missing verdict gets ONE same-session re-prompt before parking. A park
/// is resolved with h9k review resolve (ReviewParkResolved re-enters the loop here).
/// The loop is a state machine over the run stream, so a restarted daemon resumes it
/// exactly where the events left off.
/// <para>
/// Each cycle runs one pass per lens (Decisions Log #59) — conformance and adversarial —
/// dispatched together and awaited one at a time, so the wall clock is the slower pass
/// rather than their sum. The cycle concludes when its last pass lands: the findings merge
/// into one document, the verdicts merge into one ReviewCompleted, and one fix session
/// addresses all of it. The budgets stay per cycle, never per lens: one automatic fix run
/// per cycle against MaxAutomaticReviewFixRuns, and one verdict re-prompt per cycle however
/// many passes ended without a verdict.
/// </para>
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
    /// The environment every review session gets. A cycle's lenses read one worktree at the
    /// same time (log #59), and git's opportunistic index refresh — the one `git status` and
    /// `git diff` do on their way to answering — takes `.git/index.lock`, which the second
    /// reader cannot create while the first holds it. `GIT_OPTIONAL_LOCKS=0` turns that
    /// refresh off, so read-only git never contends; the locks that commands like `git add`
    /// genuinely need are untouched, and a read-only reviewer runs none of those. Without it
    /// the loser of the race reads `fatal: Unable to create '.../index.lock'` as evidence
    /// about the diff and can spend the cycle's one fix run on a platform failure.
    /// </summary>
    private static readonly ReadOnlyDictionary<string, string> ReviewSessionEnvironment =
        new(new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" });

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
            // Cancellation is excluded above on purpose: a stopping daemon leaves its agents
            // running and reattaches (log #2). A crash is the other case — nobody will ever
            // read what these sessions produce, so they do not get to keep running.
            await TerminateInFlightSessionsAsync(runId, cancellationToken);
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
                    await DispatchReviewPassesAsync(
                        context, run.ReviewCycle + 1, ReviewLens.CycleLenses, cancellationToken);
                    break;

                case ReviewPhase.AwaitingVerdict:
                    if (!await AwaitReviewPassAsync(context, run, cancellationToken))
                    {
                        return false;
                    }

                    break;

                case ReviewPhase.AwaitingFix:
                    if (!await AwaitFixSessionAsync(context, run, cancellationToken))
                    {
                        return false;
                    }

                    break;

                case ReviewPhase.MergeReady:
                    logger.LogInformation(
                        "Run {RunId}: review verdict merge-ready after {Cycle} cycle(s) — every lens clean, the pull request may open",
                        context.RunId, run.ReviewCycle);
                    return true;

                case ReviewPhase.VerdictMissing when run.VerdictRepromptedCycle >= run.ReviewCycle:
                    // The cycle's one re-prompt is spent; guessing what the reviewer meant
                    // would be worse than asking (never guess at unobserved facts).
                    await ParkAsync(context.RunId,
                        $"A review pass (cycle {run.ReviewCycle}, {VerdictlessLensList(run)}) returned no parseable " +
                        "verdict, even after this cycle's re-prompt. " +
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

                    await DispatchReviewPassesAsync(
                        context, run.ReviewCycle + 1, ReviewLens.CycleLenses, cancellationToken);
                    break;

                case ReviewPhase.Parked:
                    return false;
            }
        }
    }

    /// <summary>
    /// Waits for the next in-flight pass of the current cycle and records what it found.
    /// False means the run was failed (a pass died, or the stream cannot name what it is
    /// waiting for). The passes were spawned together, so waiting them in order costs the
    /// slowest one, not the sum.
    /// </summary>
    private async Task<bool> AwaitReviewPassAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        if (await DispatchMissingPassesAsync(context, run, cancellationToken))
        {
            // A cycle that lost a lens (the daemon died between the two spawns) tops itself
            // up rather than concluding on one lens; the reloaded run picks them all up.
            return true;
        }

        if (run.InFlightReviewPasses.Count == 0)
        {
            await FailAsync(context.RunId, context.TaskId,
                $"Run stream records review cycle {run.ReviewCycle} awaiting a verdict with no session in flight.",
                cancellationToken);
            return false;
        }

        ReviewPassSession pass = run.InFlightReviewPasses[0];
        string streamFile = RunPaths.SessionStreamFile(
            context.RunId, ReviewArtifactName(run.ReviewCycle, pass.SessionId, pass.Lens));
        AgentResult? result = await WaitForSessionResultAsync(
            context.RunId, streamFile, pass.ProcessId, pass.ProcessStartedAt, cancellationToken);
        if (result is null || result.IsError)
        {
            // The run is over; a sibling pass still reading the diff would burn tokens on a
            // verdict nobody will collect, so it goes down with this one.
            TerminateSiblingPasses(run, pass);
            await FailAsync(context.RunId, context.TaskId, result is null
                ? $"The {LensLabel(pass.Lens)} session (cycle {run.ReviewCycle}) died without a result."
                : $"The {LensLabel(pass.Lens)} session (cycle {run.ReviewCycle}) reported an error result.",
                cancellationToken);
            return false;
        }

        await RecordReviewPassAsync(context.RunId, run, pass, result, cancellationToken);
        return true;
    }

    private async Task<bool> AwaitFixSessionAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        if (run.ActiveFixSessionId is not { } sessionId
            || run.ActiveFixProcessId is not { } processId
            || run.ActiveFixProcessStartedAt is not { } processStartedAt)
        {
            await FailAsync(context.RunId, context.TaskId,
                "Run stream records an in-flight fix session without its identity.", cancellationToken);
            return false;
        }

        string streamFile = RunPaths.SessionStreamFile(
            context.RunId, FixArtifactName(run.ReviewCycle, sessionId));
        AgentResult? result = await WaitForSessionResultAsync(
            context.RunId, streamFile, processId, processStartedAt, cancellationToken);
        if (result is null || result.IsError)
        {
            await FailAsync(context.RunId, context.TaskId, result is null
                ? $"The fix session (cycle {run.ReviewCycle}) died without a result."
                : $"The fix session (cycle {run.ReviewCycle}) reported an error result.", cancellationToken);
            return false;
        }

        await RecordFixResultAsync(context.RunId, run.ReviewCycle, result, cancellationToken);
        return true;
    }

    /// <summary>
    /// Dispatches any lens of the current cycle that is neither in flight nor already
    /// answered, and reports whether it dispatched anything. This is what makes a cycle's
    /// dispatch idempotent: a daemon that died between the cycle's two spawns resumes with
    /// one lens recorded, and the missing lens is spawned here instead of being lost.
    /// </summary>
    private async Task<bool> DispatchMissingPassesAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReviewLens> missing = ReviewLens.MissingFrom(
            [.. run.InFlightReviewPasses.Select(pass => pass.Lens), .. run.CompletedReviewPasses.Select(pass => pass.Lens)]);
        if (missing.Count == 0)
        {
            return false;
        }

        logger.LogWarning(
            "Run {RunId}: review cycle {Cycle} was missing the {Lenses} pass(es) — dispatching now",
            context.RunId, run.ReviewCycle, string.Join(", ", missing.Select(lens => lens.Slug)));
        await DispatchReviewPassesAsync(context, run.ReviewCycle, missing, cancellationToken);
        return true;
    }

    private async Task DispatchReviewPassesAsync(
        ReviewContext context, int cycle, IEnumerable<ReviewLens> lenses, CancellationToken cancellationToken)
    {
        foreach (ReviewLens lens in lenses)
        {
            await DispatchReviewPassAsync(context, cycle, lens, cancellationToken);
        }
    }

    /// <summary>
    /// Spawns one lens's pass and records it before spawning the next: each session exists
    /// the moment it is recorded, and a daemon that dies between the two leaves a stream
    /// that says exactly which passes were started.
    /// </summary>
    private async Task DispatchReviewPassAsync(
        ReviewContext context, int cycle, ReviewLens lens, CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildReview(
            context.Task, context.Project, context.Run.Branch, cycle, lens);
        ExecutorMode mode = context.Run.ExecutorMode;
        // Every lens is review work, so they resolve the same role in the chain (log #33) —
        // and each dispatch records the model it actually got, per pass.
        AgentModel model = _options.ResolveModel(AgentRole.Review, context.Task.Model, context.Project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, prompt, mode, model,
            context.Project.SkipPermissions, ReviewArtifactName(cycle, sessionId, lens))
        {
            Environment = ReviewSessionEnvironment,
        }, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model, lens));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: {Lens} agent dispatched with fresh context (cycle {Cycle}, session {SessionId}, pid {ProcessId}, model {Model})",
            context.RunId, LensLabel(lens), cycle, sessionId, agent.ProcessId, model.Value);
    }

    /// <summary>
    /// Resumes a verdict-less review session (claude -p --resume) and tells it to
    /// conclude — the cycle's one re-prompt before a park. The spawn carries a fresh artifact
    /// identity so the resumed leg's stream file never collides with the original's
    /// (which already ended in a result event the waiter must not re-read).
    /// </summary>
    private async Task<bool> RepromptForVerdictAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        ReviewPassResult? verdictless = run.CompletedReviewPasses
            .FirstOrDefault(pass => pass.Verdict == ReviewVerdict.Unknown);
        if (verdictless?.SessionId is not { } resumeSessionId)
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
        AgentModel model = verdictless.Model;
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, artifactId, context.Run.WorktreePath, prompt, context.Run.ExecutorMode, model,
            context.Project.SkipPermissions, ReviewArtifactName(run.ReviewCycle, artifactId, verdictless.Lens),
            ResumeSessionId: resumeSessionId)
        {
            Environment = ReviewSessionEnvironment,
        }, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewVerdictReprompted(
            context.RunId, artifactId, resumeSessionId, run.ReviewCycle,
            agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model, verdictless.Lens));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Run {RunId}: the {Lens} pass of cycle {Cycle} ended without a verdict — session {SessionId} resumed for the cycle's one re-prompt (pid {ProcessId})",
            context.RunId, LensLabel(verdictless.Lens), run.ReviewCycle, resumeSessionId, agent.ProcessId);
        return true;
    }

    /// <summary>
    /// Dispatches a fix session over the cycle's merged findings — or, after a needs-fixes
    /// h9k review resolve, over the human's stated findings (the event carries them; the
    /// findings file still holds the reviewers' own last words). One fix session per cycle,
    /// whichever lenses found something.
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
            context.Project.SkipPermissions, FixArtifactName(cycle, sessionId)), cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewFixDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: fix run dispatched over the cycle-{Cycle} findings (session {SessionId}, pid {ProcessId}, model {Model})",
            context.RunId, cycle, sessionId, agent.ProcessId, model.Value);
    }

    /// <summary>
    /// Records one lens's findings and verdict, and — when it was the cycle's last pass —
    /// merges the cycle: one findings document with a section per lens, and one
    /// ReviewCompleted carrying the merged verdict, appended in the same transaction as the
    /// pass milestone so the two can never disagree.
    /// </summary>
    private async Task RecordReviewPassAsync(
        Guid runId, RunAggregate run, ReviewPassSession pass, AgentResult result, CancellationToken cancellationToken)
    {
        int cycle = run.ReviewCycle;
        string findings = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(LensFindingsFile(runId, cycle, pass.Lens), findings, cancellationToken);

        ReviewVerdict verdict = ReviewResultParser.ParseVerdict(findings);
        List<ReviewPassResult> completed = MergeCompleted(
            run.CompletedReviewPasses, new ReviewPassResult(pass.Lens, pass.TranscriptSessionId, pass.Model, verdict));
        // The cycle concludes only when nothing else is reading AND no lens is still missing:
        // a merged verdict over a lens that never looked would be the single-sample blind spot
        // this whole mechanism exists to close.
        bool cycleConcluded = run.InFlightReviewPasses.All(inFlight => inFlight.Lens == pass.Lens)
            && ReviewLens.MissingFrom(completed.Select(finished => finished.Lens)).Count == 0;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));
        session.Events.Append(runId, new ReviewPassCompleted(runId, cycle, pass.Lens, verdict, now));
        if (cycleConcluded)
        {
            await WriteMergedFindingsAsync(runId, cycle, completed, cancellationToken);
            session.Events.Append(runId, new ReviewCompleted(
                runId, cycle, ReviewVerdict.Merge(completed.Select(finished => finished.Verdict)), now));
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: the {Lens} pass of cycle {Cycle} completed — verdict {Verdict} ({Input}in/{Output}out tokens)",
            runId, LensLabel(pass.Lens), cycle,
            verdict == ReviewVerdict.Unknown ? "(none)" : verdict.Value, result.TotalInputTokens, result.OutputTokens);
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

    /// <summary>The cycle's results with this pass's own result replacing its earlier one, in dispatch order.</summary>
    private static List<ReviewPassResult> MergeCompleted(
        IReadOnlyList<ReviewPassResult> completed, ReviewPassResult landed)
    {
        List<ReviewPassResult> merged = [.. completed];
        int index = merged.FindIndex(pass => pass.Lens == landed.Lens);
        if (index >= 0)
        {
            merged[index] = landed;
        }
        else
        {
            merged.Add(landed);
        }

        return merged;
    }

    /// <summary>
    /// Writes the cycle's merged findings document: every lens's own words under a heading
    /// that names the lens and its verdict, so the fix session (and the human reading a park)
    /// sees one list and still knows which attention budget produced each finding.
    /// </summary>
    private static async Task WriteMergedFindingsAsync(
        Guid runId, int cycle, IReadOnlyList<ReviewPassResult> passes, CancellationToken cancellationToken)
    {
        string mergedPath = RunPaths.ReviewFindingsFile(runId, cycle);
        if (passes.Count == 1 && LensFindingsFile(runId, cycle, passes[0].Lens) == mergedPath)
        {
            // A pre-lens run resumed mid-review: its one lens-less pass already wrote this
            // file, and re-wrapping its own text around it would say nothing new.
            return;
        }

        StringBuilder merged = new();
        merged.AppendLine($"# Independent pre-PR review — cycle {cycle}");
        merged.AppendLine();
        merged.AppendLine("Each section below is one independent pass over the same diff, with its own fresh");
        merged.AppendLine("context. A finding belongs to the lens whose section it appears under.");
        foreach (ReviewPassResult pass in passes)
        {
            string path = LensFindingsFile(runId, cycle, pass.Lens);
            string text = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken)
                : string.Empty;
            merged.AppendLine();
            merged.AppendLine($"## {LensHeading(pass.Lens)} — verdict: {VerdictLabel(pass.Verdict)}");
            merged.AppendLine();
            merged.AppendLine(text.IsBlank() ? "(this pass recorded no output)" : text.Trim());
        }

        await File.WriteAllTextAsync(mergedPath, merged.ToString(), cancellationToken);
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
        // review loop is entered, and the loop awaits one review pass at a time, so
        // the writers are sequential phases, never concurrent.
        await using IDocumentSession session = store.LightweightSession();
        RunActivity activity = await session.LoadAsync<RunActivity>(runId, cancellationToken)
            ?? new RunActivity { Id = runId };
        activity.LastActivityAt = DateTimeOffset.UtcNow;
        session.Store(activity);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Terminates the cycle's other in-flight passes when one of them takes the run down.
    /// The failed run will never collect their verdicts, and a reviewer left reading a diff
    /// nobody will act on is spend with no reader (log #30's whole point).
    /// </summary>
    private void TerminateSiblingPasses(RunAggregate run, ReviewPassSession failed)
    {
        foreach (ReviewPassSession sibling in run.InFlightReviewPasses.Where(pass => pass.SessionId != failed.SessionId))
        {
            processManager.Terminate(sibling.ProcessId, sibling.ProcessStartedAt);
            logger.LogWarning(
                "Run {RunId}: terminated the in-flight {Lens} pass (pid {ProcessId}) — the run failed on another pass",
                run.Id, LensLabel(sibling.Lens), sibling.ProcessId);
        }
    }

    /// <summary>
    /// Best-effort cleanup on the crash path: whatever the stream still shows in flight is
    /// terminated, review passes and the fix session alike — the crash can land in either
    /// phase, and either one left running is spend with no reader. Failures here are swallowed
    /// deliberately — this runs inside error handling, and a run that cannot be read is
    /// already being failed for that reason.
    /// </summary>
    private async Task TerminateInFlightSessionsAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            RunAggregate run = await LoadRunAsync(runId, cancellationToken);
            foreach (ReviewPassSession pass in run.InFlightReviewPasses)
            {
                processManager.Terminate(pass.ProcessId, pass.ProcessStartedAt);
                logger.LogWarning(
                    "Run {RunId}: terminated the in-flight {Lens} pass (pid {ProcessId}) after the loop crashed",
                    runId, LensLabel(pass.Lens), pass.ProcessId);
            }

            if (run.ActiveFixProcessId is { } fixProcessId && run.ActiveFixProcessStartedAt is { } fixProcessStartedAt)
            {
                processManager.Terminate(fixProcessId, fixProcessStartedAt);
                logger.LogWarning(
                    "Run {RunId}: terminated the in-flight fix session (pid {ProcessId}) after the loop crashed",
                    runId, fixProcessId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Run {RunId}: could not terminate the run's in-flight sessions", runId);
        }
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
    /// Per-session artifact prefix. The session id suffix keeps a redispatched pass (daemon
    /// died between spawn and record) from colliding with its orphan's files, and the lens
    /// keeps a cycle's two passes apart. A lens-less pass keeps the pre-lens name, which is
    /// what lets a daemon upgraded mid-review still find the running session's stream file.
    /// </summary>
    private static string ReviewArtifactName(int cycle, Guid sessionId, ReviewLens lens) =>
        lens.Slug.IsBlank()
            ? $"review-{cycle}-{Short(sessionId)}"
            : $"review-{lens.Slug}-{cycle}-{Short(sessionId)}";

    private static string FixArtifactName(int cycle, Guid sessionId) => $"review-fix-{cycle}-{Short(sessionId)}";

    private static string Short(Guid sessionId) => sessionId.ToString("N")[..8];

    /// <summary>
    /// Where one lens's own findings live. A lens-less pass writes the cycle's findings file
    /// itself, exactly as the single-lens loop did — for that stream, its output IS the cycle.
    /// </summary>
    private static string LensFindingsFile(Guid runId, int cycle, ReviewLens lens) =>
        lens.Slug.IsBlank()
            ? RunPaths.ReviewFindingsFile(runId, cycle)
            : RunPaths.ReviewLensFindingsFile(runId, cycle, lens.Slug);

    private static string LensLabel(ReviewLens lens) =>
        lens.Slug.IsBlank() ? "review" : $"{lens.Slug} review";

    private static string LensHeading(ReviewLens lens) => lens switch
    {
        _ when lens == ReviewLens.Conformance =>
            "Conformance lens (the work against its objective, acceptance criteria, and repo doctrine)",
        _ when lens == ReviewLens.Adversarial =>
            "Adversarial lens (a defect hunt, told nothing about what the work was meant to do)",
        _ => "Review pass (no lens recorded)",
    };

    private static string VerdictLabel(ReviewVerdict verdict) =>
        verdict == ReviewVerdict.Unknown ? "(none stated)" : verdict.Value;

    private static string VerdictlessLensList(RunAggregate run)
    {
        List<string> lenses = [.. run.CompletedReviewPasses
            .Where(pass => pass.Verdict == ReviewVerdict.Unknown)
            .Select(pass => LensLabel(pass.Lens))];
        return lenses.Count == 0 ? "lens not recorded" : string.Join(", ", lenses);
    }

    private sealed record ReviewContext(Guid RunId, Guid TaskId, RunDetails Run, TaskDetails Task, ProjectDetails Project);
}
