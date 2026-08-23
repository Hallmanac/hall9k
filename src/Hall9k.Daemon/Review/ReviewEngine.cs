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
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The pre-PR review loop (Decisions Log #23), between VerificationRunner and
/// PullRequestOpener: independent review agents — fresh headless sessions that never
/// saw the implementation reasoning — read the run's diff; a needs-fixes verdict
/// dispatches a fix session in the same worktree, gates re-run, and fresh reviewers
/// look again. A disputed finding parks the run for the human, and a missing verdict gets
/// ONE same-session re-prompt before parking. A park is resolved with h9k review resolve
/// (ReviewParkResolved re-enters the loop here). The loop is a state machine over the run
/// stream, so a restarted daemon resumes it exactly where the events left off.
/// <para>
/// A cycle runs one pass per still-active <b>track</b> (Decisions Log #59, #63) — conformance
/// and adversarial — dispatched together and awaited one at a time, so the wall clock is the
/// slower pass rather than their sum. Each track converges on its own terms
/// (<see cref="ReviewTrackPolicy"/>) and drops out of the loop when it does: a clean track goes
/// dormant while the other continues alone, and a dormant track is deliberately never
/// reawakened by the other's fix sessions. Conformance parks the run if it is still finding
/// things at <see cref="DaemonOptions.MaxComplianceReviewCycles"/>; adversarial runs under the
/// severity gate up to <see cref="DaemonOptions.MaxAdversarialReviewCycles"/>.
/// </para>
/// <para>
/// What stays per cycle rather than per track: one fix session over every live track's
/// findings, and one verdict re-prompt however many passes ended without a verdict. Two tracks
/// do not double the fixing or the parking math.
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
    /// <summary>
    /// The artifact name a pass with no lens recorded files its findings under. It is an
    /// honest label rather than <c>conformance</c>: the pass covers the conformance track
    /// (<c>ReviewLens.Covers</c>), but it never said so itself, and an artifact name is not the
    /// place to put words in its mouth.
    /// </summary>
    private const string UnlensedSlug = "unlensed";

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

            // The stale-generation fence (backlog 39), checked before this iteration does
            // anything: a requeue-and-reclaim elsewhere in the platform may have moved the
            // task on to a new generation while this run's sessions were in flight, and a
            // stale lane must stop at the first check rather than keep spending cycles —
            // dispatching a review pass or a fix session into a worktree the live
            // generation now owns. Each dispatch below re-checks immediately before its own
            // executor.SpawnAsync, so a reclaim mid-iteration stops the next spawn too.
            if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
            {
                return false;
            }

            switch (run.ReviewPhase)
            {
                case ReviewPhase.None:
                    if (!await DispatchReviewPassesAsync(
                        context, run.ReviewCycle + 1, run.ActiveReviewLenses, cancellationToken))
                    {
                        return false;
                    }

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

                case ReviewPhase.Settling:
                    await SettleAsync(run, cancellationToken);
                    break;

                case ReviewPhase.MergeReady:
                    logger.LogInformation(
                        "Run {RunId}: review merge-ready ({Settlement}) after {Cycle} cycle(s) — the pull request may open",
                        context.RunId, SettlementLabel(run), run.ReviewCycle);
                    return true;

                case ReviewPhase.VerdictMissing when run.VerdictRepromptedCycle >= run.ReviewCycle:
                    // The cycle's one re-prompt is spent; guessing what the reviewer meant
                    // would be worse than asking (never guess at unobserved facts).
                    await ParkAsync(context.RunId, context.TaskId,
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

                case ReviewPhase.FixNeeded when CappedTrack(run) is { } capped:
                    await ParkAsync(context.RunId, context.TaskId, CapParkReason(context.RunId, run, capped), cancellationToken);
                    return false;

                case ReviewPhase.FixNeeded:
                    if (!await DispatchFixSessionAsync(context, run.ReviewCycle, run.PendingHumanFindings, cancellationToken))
                    {
                        return false;
                    }

                    break;

                case ReviewPhase.Disputed:
                    await ParkAsync(context.RunId, context.TaskId,
                        "The fix run disputed a review finding — as not-a-defect, as human territory, or as " +
                        $"wrongly graded (cycle {run.ReviewCycle}). " +
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

                    if (run.ActiveReviewLenses.Count == 0)
                    {
                        // Every track has concluded, so there is nobody left to re-review for and
                        // the loop goes on to record that it settled (log #63). The gates above
                        // still ran: a settled ending ships the terminal fix unread by a reviewer,
                        // never unbuilt and untested.
                        await SettleAsync(run, cancellationToken);
                        break;
                    }

                    if (!await DispatchReviewPassesAsync(
                        context, run.ReviewCycle + 1, run.ActiveReviewLenses, cancellationToken))
                    {
                        return false;
                    }

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
        switch (await DispatchMissingPassesAsync(context, run, cancellationToken))
        {
            case MissingPassDispatch.Dispatched:
                // A cycle that lost a track (the daemon died between the two spawns) tops
                // itself up rather than concluding on one; the reloaded run picks them all up.
                return true;
            case MissingPassDispatch.Stale:
                // The generation check immediately before the spawn rejected it; the run is
                // already retired (EnsureCurrentGenerationAsync) — stop the loop.
                return false;
            case MissingPassDispatch.NothingMissing:
                break;
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

        await RecordReviewPassAsync(context, run, pass, result, cancellationToken);
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
    /// Dispatches any active track of the current cycle that is neither in flight nor already
    /// answered, and reports whether it dispatched anything. This is what makes a cycle's
    /// dispatch idempotent: a daemon that died between the cycle's two spawns resumes with
    /// one track recorded, and the missing one is spawned here instead of being lost. A track
    /// that has already concluded is not missing — it is finished, and stays that way.
    /// </summary>
    private enum MissingPassDispatch { NothingMissing, Dispatched, Stale }

    private async Task<MissingPassDispatch> DispatchMissingPassesAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReviewLens> missing = ReviewLens.MissingFrom(
            run.ActiveReviewLenses,
            [.. run.InFlightReviewPasses.Select(pass => pass.Lens), .. run.CompletedReviewPasses.Select(pass => pass.Lens)]);
        if (missing.Count == 0)
        {
            return MissingPassDispatch.NothingMissing;
        }

        logger.LogWarning(
            "Run {RunId}: review cycle {Cycle} was missing the {Lenses} pass(es) — dispatching now",
            context.RunId, run.ReviewCycle, string.Join(", ", missing.Select(lens => lens.Slug)));
        bool dispatched = await DispatchReviewPassesAsync(context, run.ReviewCycle, missing, cancellationToken);
        return dispatched ? MissingPassDispatch.Dispatched : MissingPassDispatch.Stale;
    }

    /// <summary>Reports false the moment a spawn is rejected as stale, without dispatching the rest.</summary>
    private async Task<bool> DispatchReviewPassesAsync(
        ReviewContext context, int cycle, IEnumerable<ReviewLens> lenses, CancellationToken cancellationToken)
    {
        foreach (ReviewLens lens in lenses)
        {
            if (!await DispatchReviewPassAsync(context, cycle, lens, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Spawns one track's pass and records it before spawning the next: each session exists
    /// the moment it is recorded, and a daemon that dies between the two leaves a stream
    /// that says exactly which passes were started. Checks the generation fence immediately
    /// before the spawn (Copilot review, PR #30) rather than relying on the caller's
    /// once-per-iteration check, which this same cycle can outlive across several lenses.
    /// </summary>
    private async Task<bool> DispatchReviewPassAsync(
        ReviewContext context, int cycle, ReviewLens lens, CancellationToken cancellationToken)
    {
        if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
        {
            return false;
        }

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
        return true;
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
        // The re-prompt is the pass's own contract restated: its lens decides whether the
        // findings come back structured, and the resumed output replaces what was read before.
        string prompt = AgentPromptBuilder.BuildReviewVerdictReprompt(
            context.Project, verdictless.Lens, run.ReviewCycle);
        // The resumed session keeps the model it was dispatched on: the chain is NOT
        // re-resolved here, or the milestone would record a model the session never ran on
        // (log #33). An older stream that recorded no model stays honestly Unknown.
        AgentModel model = verdictless.Model;

        // Checked immediately before the spawn (Copilot review, PR #30), not only by the
        // caller's once-per-iteration check: the reprompt is its own dispatch decision.
        if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
        {
            return false;
        }

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
    /// whichever tracks found something. Checks the generation fence immediately before the
    /// spawn (Copilot review, PR #30): the fix session reads the findings file first, which
    /// is enough of a gap for a reclaim to land in.
    /// </summary>
    private async Task<bool> DispatchFixSessionAsync(
        ReviewContext context, int cycle, string? humanFindings, CancellationToken cancellationToken)
    {
        string findings = humanFindings.IsNotBlank()
            ? $"Human review verdict (h9k review resolve): needs fixes.\n\n{humanFindings}"
            : await File.ReadAllTextAsync(RunPaths.ReviewFindingsFile(context.RunId, cycle), cancellationToken);
        if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
        {
            return false;
        }

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
        return true;
    }

    /// <summary>
    /// Records one track's findings and verdict, and — when it was the cycle's last pass —
    /// concludes the cycle: every track decides whether it runs again, out-of-scope non-Highs
    /// are routed to draft bug tasks, the findings merge into one document, and the milestones
    /// land in one transaction so the pass, the cycle, and the tracks can never disagree.
    /// </summary>
    private async Task RecordReviewPassAsync(
        ReviewContext context, RunAggregate run, ReviewPassSession pass, AgentResult result,
        CancellationToken cancellationToken)
    {
        Guid runId = context.RunId;
        int cycle = run.ReviewCycle;
        string output = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(LensFindingsFile(runId, cycle, pass.Lens), output, cancellationToken);

        ReviewVerdict verdict = ReviewResultParser.ParseVerdict(output);
        // A merge-ready pass reports no findings by contract; anything it listed anyway is not
        // turned into work behind its own verdict. A needs-fixes pass always carries at least
        // one, even when nothing structured could be read out of it.
        IReadOnlyList<ReviewFinding> findings = verdict == ReviewVerdict.NeedsFixes
            ? ReviewTrackPolicy.Stated(ReviewResultParser.ParseFindings(output))
            : [];

        List<ReviewPassResult> completed = MergeCompleted(
            run.CompletedReviewPasses,
            new ReviewPassResult(pass.Lens, pass.TranscriptSessionId, pass.Model, verdict,
                [.. findings.Select(finding => finding.ToRecord())]));
        // The cycle concludes only when nothing else is reading AND no active track is still
        // missing: a merged verdict over a track that never looked would be the single-sample
        // blind spot this whole mechanism exists to close.
        bool cycleConcluded = run.InFlightReviewPasses.All(inFlight => inFlight.Lens == pass.Lens)
            && ReviewLens.MissingFrom(run.ActiveReviewLenses, completed.Select(finished => finished.Lens)).Count == 0;
        ReviewVerdict cycleVerdict = ReviewVerdict.Merge(completed.Select(finished => finished.Verdict));

        // A cycle with an unreadable verdict has decided nothing: its re-prompt is what happens
        // next, and grading tracks against a pass nobody could read would be the guess the
        // re-prompt exists to avoid.
        IReadOnlyList<ReviewTrackPlan> plans = cycleConcluded && cycleVerdict != ReviewVerdict.Unknown
            ? await PlanCycleAsync(runId, run, completed, cancellationToken)
            : [];
        IReadOnlyList<RoutedFinding> routed = await RouteFindingsAsync(context, run, cycle, plans, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));
        session.Events.Append(runId, new ReviewPassCompleted(
            runId, cycle, pass.Lens, verdict, now, [.. findings.Select(finding => finding.ToRecord())]));
        if (cycleConcluded)
        {
            await WriteMergedFindingsAsync(runId, cycle, completed, plans, routed, cancellationToken);
            session.Events.Append(runId, new ReviewCompleted(runId, cycle, cycleVerdict, now));
            foreach (ReviewTrackPlan plan in plans.Where(plan => !plan.Continues))
            {
                session.Events.Append(runId, new ReviewTrackConcluded(
                    runId, plan.Lens, cycle, plan.Settlement ?? ReviewSettlement.Unknown, plan.Residuals, now));
                logger.LogInformation(
                    "Run {RunId}: the {Lens} track concluded at cycle {Cycle} — {Settlement}, {Residuals} residual(s)",
                    runId, LensLabel(plan.Lens), cycle, plan.Settlement?.Value ?? "unknown", plan.Residuals.Count);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: the {Lens} pass of cycle {Cycle} completed — verdict {Verdict}, {Findings} finding(s) ({Input}in/{Output}out tokens)",
            runId, LensLabel(pass.Lens), cycle,
            verdict == ReviewVerdict.Unknown ? "(none)" : verdict.Value, findings.Count,
            result.TotalInputTokens, result.OutputTokens);
    }

    /// <summary>
    /// What each active track does with the cycle it just finished. Every track's findings are
    /// re-read from its own artifact rather than threaded through the wait loop, which is what
    /// makes the decision reproducible on a resumed daemon: the files are the record, and the
    /// policy over them is pure.
    /// </summary>
    private async Task<IReadOnlyList<ReviewTrackPlan>> PlanCycleAsync(
        Guid runId, RunAggregate run, IReadOnlyList<ReviewPassResult> completed, CancellationToken cancellationToken)
    {
        List<ReviewTrackPlan> plans = [];
        foreach (ReviewPassResult finished in completed.Where(
            finished => run.ActiveReviewLenses.Any(lens => finished.Lens.Covers(lens))))
        {
            plans.Add(ReviewTrackPolicy.Decide(
                finished.Lens, run.ReviewCycle, finished.Verdict,
                await ReadFindingsAsync(runId, run.ReviewCycle, finished, cancellationToken),
                _options));
        }

        return plans;
    }

    private static async Task<IReadOnlyList<ReviewFinding>> ReadFindingsAsync(
        Guid runId, int cycle, ReviewPassResult pass, CancellationToken cancellationToken)
    {
        string path = LensFindingsFile(runId, cycle, pass.Lens);
        return pass.Verdict == ReviewVerdict.NeedsFixes && File.Exists(path)
            ? ReviewResultParser.ParseFindings(await File.ReadAllTextAsync(path, cancellationToken))
            : [];
    }

    /// <summary>
    /// Turns this cycle's out-of-scope, non-High findings into draft bug tasks (log #63), in
    /// their own transaction ahead of the cycle's milestones. Nothing in here may fail the
    /// review: routing is a courtesy paid to a defect this pull request is not fixing, and a
    /// courtesy that fails is recorded as having failed rather than allowed to take a finished
    /// review cycle down with it.
    /// <para>
    /// A finding is routed <b>once per run</b>, and that is load-bearing rather than tidiness.
    /// A routed defect is deliberately left in the tree — the fix session is told to leave it
    /// alone — and every later cycle's reviewer has fresh context, so the same pre-existing
    /// line comes back as a finding for as long as the other track keeps the loop alive.
    /// Without this check, one defect becomes one inert draft per cycle, one routing event per
    /// cycle, and a residual tally that tells the human "3 routed" about a single exported
    /// defect. The key is the place the reviewer stated, because that is what the run stream
    /// records, and two statements of it are compared as places rather than as strings
    /// (<see cref="ReviewFindingLocations"/>): `./src/Legacy.cs:40`, `src\Legacy.cs:40`, and
    /// `Legacy.cs:40` are one defect written three ways, and only string equality would call
    /// them three. A finding the reviewer never placed on a line — no location at all, or a
    /// file with no line on it — cannot be matched to an earlier one, so it routes again rather
    /// than being silently collapsed into a defect it may not be — and so does the same file at
    /// a different stated line, which is the deliberate boundary
    /// <see cref="ReviewFindingLocations"/> explains. A routing that failed is not in the set
    /// either — no draft exists, so the next cycle to report the same place tries again, which
    /// is the courtesy working rather than a duplicate.
    /// </para>
    /// <para>
    /// The same check closes the crash seam between this transaction and the cycle's: a daemon
    /// that dies in between re-reads the same pass on resume, and the routing already on the
    /// stream is what stops the second draft.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<RoutedFinding>> RouteFindingsAsync(
        ReviewContext context, RunAggregate run, int cycle, IReadOnlyList<ReviewTrackPlan> plans,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<(ReviewLens Lens, ReviewFinding Finding)> pending, IReadOnlyList<RoutedFinding> repeats) =
            SplitAlreadyRouted(run, cycle, plans);
        if (pending.Count == 0)
        {
            return repeats;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<RoutedFinding> routed = [];
        await using IDocumentSession session = store.LightweightSession();
        foreach ((ReviewLens lens, ReviewFinding finding) in pending)
        {
            Guid draftTaskId = DomainId.New();
            try
            {
                TaskAdded added = ReviewDraftBugTask.Compose(
                    draftTaskId, context.Task, context.RunId, context.Run.Branch, context.Project.BaseBranch,
                    lens, cycle, finding, now, context.Run.OwnerId);
                session.Events.StartStream<TaskAggregate>(draftTaskId, added);
                routed.Add(new RoutedFinding(lens, finding, draftTaskId, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Run {RunId}: could not compose a draft bug task for the out-of-scope finding at {Location}",
                    context.RunId, finding.Location);
                routed.Add(new RoutedFinding(lens, finding, null, exception.Message));
            }
        }

        AppendRouted(session, context.RunId, cycle, routed, now);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Run {RunId}: routed {Count} out-of-scope finding(s) of cycle {Cycle} to draft bug tasks",
                context.RunId, routed.Count(entry => entry.DraftTaskId is not null), cycle);
            return [.. routed, .. repeats];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: creating draft bug tasks for cycle {Cycle} failed — the review loop continues and the findings are recorded as unrouted",
                context.RunId, cycle);
            routed = [.. routed.Select(entry => entry with { DraftTaskId = null, FailureReason = exception.Message })];
            await RecordRoutingFailureAsync(context.RunId, cycle, routed, now, cancellationToken);
            return [.. routed, .. repeats];
        }
    }

    /// <summary>
    /// This cycle's routable findings split into the ones this run has not routed yet and the
    /// ones it already has. A repeat is carried on rather than dropped: the fix session still
    /// reads the reviewer's text for it in the merged document, and it still has to be told
    /// that the defect is somebody else's work, or it will fix in this pull request exactly
    /// what was exported out of it. Each repeat carries the cycle whose routing it repeats,
    /// because that is the observed fact the merged document then states rather than a guess
    /// about which cycle exported it.
    /// </summary>
    private static (IReadOnlyList<(ReviewLens Lens, ReviewFinding Finding)> Pending, IReadOnlyList<RoutedFinding> Repeats)
        SplitAlreadyRouted(RunAggregate run, int cycle, IReadOnlyList<ReviewTrackPlan> plans)
    {
        List<(string Location, int Cycle)> routedLocations = [.. run.ReviewResiduals
            .Where(residual => residual.Disposition == ReviewResidualDisposition.Routed
                && residual.Location.IsNotBlank())
            .Select(residual => (residual.Location, residual.Cycle))];

        List<(ReviewLens Lens, ReviewFinding Finding)> pending = [];
        List<RoutedFinding> repeats = [];
        foreach (ReviewTrackPlan plan in plans)
        {
            foreach (ReviewFinding finding in plan.Route)
            {
                // Both tracks can report the same pre-existing line in one cycle, which the
                // fix prompt already calls agreement rather than two defects; the same list
                // therefore grows as this cycle routes, not only across cycles.
                int? alreadyRoutedIn = routedLocations
                    .Where(routed => ReviewFindingLocations.SamePlace(routed.Location, finding.Location))
                    .Select(routed => (int?)routed.Cycle)
                    .FirstOrDefault();
                if (alreadyRoutedIn is { } earlierCycle)
                {
                    repeats.Add(new RoutedFinding(
                        plan.Lens, finding, null, null, AlreadyRoutedInCycle: earlierCycle));
                    continue;
                }

                if (finding.Location.IsNotBlank())
                {
                    routedLocations.Add((finding.Location, cycle));
                }

                pending.Add((plan.Lens, finding));
            }
        }

        return (pending, repeats);
    }

    /// <summary>
    /// The fallback record when the routing transaction itself could not be written: the
    /// findings are still recorded as routed-and-failed, on their own, with no draft streams to
    /// take down with them. If even this cannot be written the loop still continues — the
    /// review's own verdict does not depend on it.
    /// </summary>
    private async Task RecordRoutingFailureAsync(
        Guid runId, int cycle, IReadOnlyList<RoutedFinding> routed, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            AppendRouted(session, runId, cycle, routed, now);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: could not record cycle {Cycle}'s failed routing on the stream either", runId, cycle);
        }
    }

    private static void AppendRouted(
        IDocumentSession session, Guid runId, int cycle, IReadOnlyList<RoutedFinding> routed, DateTimeOffset now)
    {
        foreach (RoutedFinding entry in routed)
        {
            session.Events.Append(runId, new ReviewFindingRouted(
                runId, entry.Lens, cycle, entry.Finding.Severity, entry.Finding.Location,
                entry.DraftTaskId, entry.FailureReason, now));
        }
    }

    /// <summary>
    /// Writes down how the loop ended (log #63). The verdict the rest of the pipeline reads is
    /// MergeReady either way; this is the sentence beside it that says whether a reviewer
    /// confirmed the final tip or the severity gate ended the loop over findings nobody read
    /// again, and how many residuals that left.
    /// <para>
    /// A track the run outlives is concluded here, because the run ending is that track's
    /// ending too. It is not the track's own convergence rule that retires it — a track can
    /// still be saying "continue" when the run settles, which is the empty terminal case (a
    /// cycle whose findings all routed away leaves no track owed a fix) and the human's own
    /// merge-ready park resolution. Without this the per-track record simply has no entry for
    /// that lens, and the history cannot answer how or at which cycle it ended: the one
    /// question <see cref="ReviewTrackConcluded"/> exists to answer.
    /// </para>
    /// </summary>
    private async Task SettleAsync(RunAggregate run, CancellationToken cancellationToken)
    {
        // Derived before the conclusions below are appended, and unaffected by them: a track
        // still active here returned needs-fixes over findings that all routed away, so the run
        // already carries their residuals — or a human ended the loop, which is Settled by
        // itself. Those conclusions carry no residuals of their own for the same reason. What
        // the track left behind, it left behind when it was routed (Apply(ReviewFindingRouted)),
        // and nothing was fixed on the cycle the run stopped on or the phase would be FixNeeded.
        ReviewSettlement settlement = run.DeriveSettlement();
        // Counted per defect rather than per recorded residual (log #63): a routing that failed
        // is offered again next cycle, so one defect can leave both records on the stream, and
        // counting the records would report a defect as unrouted when a draft bug task exists.
        ReviewResidualTally residuals = run.DeriveResidualTally();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        foreach (ReviewLens lens in run.ActiveReviewLenses)
        {
            session.Events.Append(run.Id, new ReviewTrackConcluded(
                run.Id, lens, run.ReviewCycle, ReviewSettlement.Settled, [], now));
            logger.LogInformation(
                "Run {RunId}: the {Lens} track ended at cycle {Cycle} with the run — settled, no reviewer read the final tip",
                run.Id, LensLabel(lens), run.ReviewCycle);
        }

        session.Events.Append(run.Id, new ReviewSettled(
            run.Id, run.ReviewCycle, settlement,
            residuals.FixedUnreviewed, residuals.Routed, residuals.RoutingFailed,
            now));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: review settled {Settlement} at cycle {Cycle} — {Fixed} residual(s) fixed unreviewed, {Routed} routed, {Unrouted} left unrouted",
            run.Id, settlement.Value, run.ReviewCycle,
            residuals.FixedUnreviewed, residuals.Routed, residuals.RoutingFailed);
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
    /// Writes the cycle's merged findings document: every track's own words under a heading
    /// that names the lens and its verdict, followed by what the platform decided about each
    /// finding. The fix session reads this, and so does the human reading a park, so it has to
    /// carry both halves — the reviewers' text, and the disposition machinery put on it.
    /// <para>
    /// Every section is read from the pass's own artifact, and no pass writes this document
    /// (<see cref="LensFindingsFile"/>), so a cycle recorded twice re-derives it rather than
    /// nesting its previous self.
    /// </para>
    /// </summary>
    private static async Task WriteMergedFindingsAsync(
        Guid runId, int cycle, IReadOnlyList<ReviewPassResult> passes, IReadOnlyList<ReviewTrackPlan> plans,
        IReadOnlyList<RoutedFinding> routed, CancellationToken cancellationToken)
    {
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

        AppendDispositions(merged, cycle, plans, routed);
        await File.WriteAllTextAsync(
            RunPaths.ReviewFindingsFile(runId, cycle), merged.ToString(), cancellationToken);
    }

    /// <summary>
    /// What the platform decided about each finding, written by machinery beneath the
    /// reviewers' own words (log #63). Severity and scope are the reviewer's observations; this
    /// section is the decision over them, and stating it here is what keeps the fix session
    /// from having to re-derive a policy it does not know.
    /// </summary>
    private static void AppendDispositions(
        StringBuilder merged, int cycle, IReadOnlyList<ReviewTrackPlan> plans, IReadOnlyList<RoutedFinding> routed)
    {
        List<(ReviewLens Lens, ReviewFinding Finding)> here =
            [.. plans.SelectMany(plan => plan.Fix.Select(finding => (plan.Lens, Finding: finding)))];
        if (here.Count == 0 && routed.Count == 0)
        {
            return;
        }

        merged.AppendLine();
        merged.AppendLine($"## {ReviewFindingDispositions.Heading}");
        merged.AppendLine();
        merged.AppendLine("Recorded by the platform from each finding's declared severity and scope tag, not by");
        merged.AppendLine("a reviewer. It is the disposition the fix session must follow.");

        AppendDispositionGroup(merged, ReviewFindingDispositions.FixHere, null,
            [.. here.Where(entry => !entry.Finding.Scope.IsRoutable)]);
        AppendDispositionGroup(merged, ReviewFindingDispositions.FixHereInItsOwnCommit,
            "These defects are pre-existing. Cleaning them up while you are here is right, and keeping "
            + "each in its own commit is what keeps the branch's real work separable in the history.",
            [.. here.Where(entry => entry.Finding.Scope.IsRoutable)]);

        if (routed.Count == 0)
        {
            return;
        }

        merged.AppendLine();
        merged.AppendLine($"### {ReviewFindingDispositions.DoNotFixHere}");
        merged.AppendLine();
        merged.AppendLine("These are pre-existing defects outside this branch's work. Each one is now a draft");
        merged.AppendLine("bug task waiting for a human; fixing them here would grow this diff with unrelated");
        merged.AppendLine("changes.");
        foreach (RoutedFinding entry in routed)
        {
            // The cycle that exported it is stated because it is the one that was observed:
            // both tracks report the same pre-existing line in the cycle they share, so a
            // repeat is as often this cycle's own routing as an earlier cycle's.
            string destination = entry switch
            {
                { AlreadyRoutedInCycle: { } earlier } when earlier == cycle =>
                    "already routed to a draft bug task earlier in this cycle",
                { AlreadyRoutedInCycle: { } earlier } =>
                    $"already routed to a draft bug task by cycle {earlier} of this run",
                { DraftTaskId: { } draftTaskId } => $"draft task {draftTaskId}",
                _ => $"NOT routed — creating the draft failed ({entry.FailureReason})",
            };
            merged.AppendLine($"- {FindingLabel(entry.Lens, entry.Finding)} → {destination}");
        }
    }

    private static void AppendDispositionGroup(
        StringBuilder merged, string heading, string? note,
        IReadOnlyList<(ReviewLens Lens, ReviewFinding Finding)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        merged.AppendLine();
        merged.AppendLine($"### {heading}");
        merged.AppendLine();
        if (note is not null)
        {
            merged.AppendLine(note);
            merged.AppendLine();
        }

        foreach ((ReviewLens lens, ReviewFinding finding) in entries)
        {
            merged.AppendLine($"- {FindingLabel(lens, finding)}");
        }
    }

    private static string FindingLabel(ReviewLens lens, ReviewFinding finding)
    {
        string location = finding.Location.IsBlank() ? "(no location stated)" : $"`{finding.Location}`";
        string severity = finding.Severity == ReviewSeverity.Unknown ? "ungraded" : finding.Severity.Value.ToLowerInvariant();
        return $"{location} — {LensLabel(lens)}, {severity}";
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

    /// <summary>
    /// Parks the run for the human, fenced on generation (backlog 39): a stale generation's
    /// park still protects its lease from the expiry sweep (DispatchEngine's parked guard
    /// reads the task's CurrentRunId), so a rejected park is not a formality here — it is
    /// what stops a superseded lane from pinning the live generation's lease. Internal
    /// rather than private so the fence-rejection branch can be asserted directly (the
    /// stale generation it must react to is a narrow in-process race with DriveAsync's own
    /// loop-top check, not one a test can land reliably through the public entry point).
    /// </summary>
    internal async Task ParkAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        RunAggregate run = await LoadRunAsync(runId, cancellationToken);
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, run.LeaseGeneration, nameof(ReviewParked), cancellationToken))
        {
            // A reclaim can land in the gap between DriveAsync's loop-top fence check and
            // this one, so the rejection here must retire the run with RunSuperseded like
            // every other fence rejection in this file — returning bare would leave the
            // run live in a non-terminal ReviewPhase with no monitor watching it until the
            // next poll cycle's ResumeResolvedReviewsAsync stumbled onto it (Copilot
            // review, PR #30).
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                session.Events.Append(
                    runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? run.LeaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Run {RunId}: retired as superseded — the review loop's park found it was no longer task {TaskId}'s current generation",
                    runId, taskId);
            }

            return;
        }

        // The task stays Claimed and the lease is retained: the worktree is the human's
        // workspace for resolving the park (the CloseoutParked pattern, pre-PR).
        session.Events.Append(runId, new ReviewParked(runId, reason, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Run {RunId}: review parked for the human — {Reason}", runId, reason);
    }

    /// <summary>
    /// Fails the run — an honest fact about this session regardless of generation — and
    /// fences the task-level half on generation (backlog 39): a stale generation's failure
    /// must not fail the task a live generation is still working, nor take that
    /// generation's lease with it.
    /// </summary>
    private async Task FailAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // that AllowsAsync never gets asked about (adversarial review, cycle 2).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && TaskDecider.CanFail(current.Task)
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            // One transaction with the RunFailed append above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with it on purpose — see
            // RunSupervisor.AppendFencedTaskFailureAsync): a lost race here rolling back
            // the run's own failure fact too is a smaller cost than a reader observing the
            // run Failed while its task still reads Claimed.
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
                "Task {TaskId}: lost the generation race recording a review-loop failure for run {RunId} — a newer claim committed first",
                taskId, runId);
            return;
        }

        logger.LogWarning("Run {RunId} failed in the review loop: {Reason}", runId, reason);
    }

    /// <summary>
    /// The dispatch-time half of the generation fence (backlog 39), read fresh through
    /// <paramref name="context"/> rather than a cached aggregate — and checked immediately
    /// before every dispatch decision, not once per while-loop iteration: one iteration can
    /// outlive several spawns (every lens in a cycle, plus the reprompt and fix helpers), and
    /// a reclaim landing mid-iteration must stop the next spawn, not just the next iteration
    /// (Copilot review, PR #30). <see cref="RunAggregate.LeaseGeneration"/> is stamped once
    /// at dispatch and never changes, so <see cref="ReviewContext.Run"/>'s copy is exactly as
    /// current as one just reloaded.
    /// <para>
    /// A rejection here also retires the run with <see cref="RunSuperseded"/> before
    /// reporting false: returning false alone only unwinds this call stack and lets the
    /// supervisor drop its monitor, but the run itself stays in a non-terminal
    /// <see cref="RunState"/> — NodeLoad keeps counting it live, and
    /// <c>ResumeResolvedReviewsAsync</c> or the next startup adoption sweep would relaunch it
    /// (Copilot review, PR #30).
    /// </para>
    /// </summary>
    private async Task<bool> EnsureCurrentGenerationAsync(ReviewContext context, CancellationToken cancellationToken)
    {
        await using (IQuerySession query = store.QuerySession())
        {
            if (await GenerationFence.AllowsAsync(
                query, logger, context.TaskId, context.RunId, context.Run.LeaseGeneration,
                "to continue the review loop", cancellationToken))
            {
                return true;
            }
        }

        await RetireStaleRunAsync(context, cancellationToken);
        return false;
    }

    private async Task RetireStaleRunAsync(ReviewContext context, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        if (await session.Events.FetchStreamStateAsync(context.RunId, cancellationToken) is null)
        {
            return;
        }

        TaskDetails? task = await session.LoadAsync<TaskDetails>(context.TaskId, cancellationToken);
        session.Events.Append(context.RunId, new RunSuperseded(
            context.RunId, task?.LeaseGeneration ?? context.Run.LeaseGeneration, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: retired as superseded — the review loop found it was no longer task {TaskId}'s current generation",
            context.RunId, context.TaskId);
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

    /// <summary>The first still-active track that has run as many cycles as it may, or null while every track has room.</summary>
    private ReviewLens? CappedTrack(RunAggregate run) =>
        run.ActiveReviewLenses.FirstOrDefault(lens =>
            ReviewTrackPolicy.CapReached(lens, run.ReviewCycle, run.ReviewBudgetBaseCycle, _options));

    /// <summary>
    /// Why a capped track parks, in that track's own terms. The two caps mean different things
    /// and the reason says which: conformance running out is "nothing automated is left to
    /// try", while adversarial running out is "the machine kept finding real problems, and
    /// somebody should look at why" — not a failure, and not a budget quietly spent.
    /// </summary>
    private string CapParkReason(Guid runId, RunAggregate run, ReviewLens capped)
    {
        string findings = RunPaths.ReviewFindingsFile(runId, run.ReviewCycle);
        string levers =
            $"Unresolved findings: {findings}. Fix in the worktree and resolve with " +
            "h9k review resolve --merge-ready, grant a fresh round with --needs-fixes, or abandon the task.";
        int cycles = run.ReviewCycle - run.ReviewBudgetBaseCycle;

        return capped == ReviewLens.Adversarial
            ? $"{AdversarialCapReason(run, cycles)} {levers}"
            : $"The conformance review is still returning findings after {cycles} cycles, its cap — the work " +
              $"has been told the same thing {cycles} times, so nothing automated is left to try. " + levers;
    }

    /// <summary>
    /// What the adversarial track was actually still returning when it hit its cap, read off
    /// the cycle's recorded findings rather than assumed from the fact that it continued.
    /// <para>
    /// A High is not the only thing that keeps this track alive: an <i>ungraded</i> finding
    /// forces another cycle too (<c>ReviewSeverity.Unknown.ForcesAnotherCycle</c>), by design,
    /// and a reviewer whose grades never parsed — a word the platform cannot read, or findings
    /// written without their FINDING header — produces exactly that. Telling a human "still
    /// returning high-severity findings" in that case steers them to restart correct work over
    /// a defect nobody ever graded, so the reason says which of the two it observed.
    /// </para>
    /// </summary>
    private static string AdversarialCapReason(RunAggregate run, int cycles)
    {
        List<ReviewFindingRecord> owed = [.. run.CompletedReviewPasses
            .Where(pass => pass.Lens.Covers(ReviewLens.Adversarial))
            .SelectMany(pass => pass.Findings)
            .Where(finding => finding.Disposition == ReviewFindingDisposition.Fix)];
        int high = owed.Count(finding => finding.Severity == ReviewSeverity.High);
        int ungraded = owed.Count(finding => finding.Severity == ReviewSeverity.Unknown);

        if (high > 0)
        {
            return $"The adversarial review is still returning high-severity findings after {cycles} cycles, " +
                $"its cap — {high} of cycle {run.ReviewCycle}'s findings are graded high. That is not a spent " +
                "budget: the machine kept finding real problems in this diff, and a human should look at why " +
                "rather than let the loop keep grinding. Restarting the work with a fresh agent is one way to " +
                "resolve it.";
        }

        if (ungraded > 0)
        {
            return $"The adversarial review is still returning findings after {cycles} cycles, its cap, and " +
                $"{ungraded} of cycle {run.ReviewCycle}'s findings carry no grade the platform could read — " +
                "none is graded high. An ungraded finding forces another cycle deliberately, so the loop may " +
                "have been kept alive by a reviewer whose grades did not parse rather than by defects that " +
                "matter. Read the findings before deciding whether it is the diff or the grading that needs " +
                "your attention.";
        }

        return $"The adversarial review is still returning findings after {cycles} cycles, its cap, and none " +
            $"of cycle {run.ReviewCycle}'s findings is graded high. A human should look at what the loop has " +
            "been spending its cycles on.";
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
    /// Where one lens's own findings live — always a file of its own, never the cycle's merged
    /// document. A lens-less pass gets the <c>unlensed</c> name rather than the
    /// <c>review-N-findings.md</c> the single-lens loop wrote, because that name belongs to the
    /// merge: <see cref="WriteMergedFindingsAsync"/> overwrites it, and the track decision
    /// re-reads these files, so a shared name hands the lens-less track another lens's findings
    /// on any second recording of the same cycle — a verdict re-prompt, or a resume after the
    /// daemon died between the merge and the cycle's transaction. Borrowed findings suppress
    /// the "something must be fixed" placeholder a needs-fixes verdict implies, which can
    /// settle a track that in fact found something.
    /// </summary>
    private static string LensFindingsFile(Guid runId, int cycle, ReviewLens lens) =>
        RunPaths.ReviewLensFindingsFile(runId, cycle, lens.Slug.IsBlank() ? UnlensedSlug : lens.Slug);

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

    private static string SettlementLabel(RunAggregate run) => run.ReviewSettlement == ReviewSettlement.Unknown
        ? "settlement not recorded"
        : run.ReviewSettlement.Value.ToLowerInvariant();

    private static string VerdictlessLensList(RunAggregate run)
    {
        List<string> lenses = [.. run.CompletedReviewPasses
            .Where(pass => pass.Verdict == ReviewVerdict.Unknown)
            .Select(pass => LensLabel(pass.Lens))];
        return lenses.Count == 0 ? "lens not recorded" : string.Join(", ", lenses);
    }

    /// <summary>
    /// One out-of-scope finding and where it went: the draft task it became, why it could not
    /// become one, or the cycle of this run that already exported it
    /// (<paramref name="AlreadyRoutedInCycle"/> — no second draft, and no second routing event).
    /// That cycle can be the current one, because both tracks report the same pre-existing line
    /// in the cycle they share, so it is carried rather than assumed to be an earlier one.
    /// </summary>
    private sealed record RoutedFinding(
        ReviewLens Lens, ReviewFinding Finding, Guid? DraftTaskId, string? FailureReason,
        int? AlreadyRoutedInCycle = null);

    private sealed record ReviewContext(Guid RunId, Guid TaskId, RunDetails Run, TaskDetails Task, ProjectDetails Project);
}
