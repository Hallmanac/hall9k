using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Project;
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
/// The pre-PR review loop (Decisions Log #23), between VerificationRunner and
/// PullRequestOpener: independent review agents — fresh headless sessions that never
/// saw the implementation reasoning — read the run's diff; a needs-fixes verdict
/// dispatches a fix session in the same worktree, gates re-run, and fresh reviewers
/// look again. A disputed finding parks the run for the human, and a missing verdict gets
/// ONE same-session re-prompt before parking — and a needs-fixes verdict that names no
/// finding (<see cref="ReviewVerdictValidation"/>) is recorded as missing for exactly this
/// purpose, so it takes the same re-prompt-then-park path rather than being accepted as
/// findings that were never stated. A park is resolved with h9k review resolve
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
/// <para>
/// Only cycle 1 pays discovery's full price (task: review cycles after the first, origin: 576M
/// input tokens in one day re-reading 12k-line diffs with two lenses to judge 40-line fixes).
/// Cycle 1 is always <see cref="ReviewMode.Discovery"/> — both lenses, full diff, fresh context,
/// unchanged. A middle cycle is <see cref="ReviewMode.Verify"/>: one reviewer, handed the prior
/// cycle's own findings and fix summary, told to verify the fix and its blast radius rather than
/// rediscover the diff — its rounds count against the same per-track caps a Discovery cycle's
/// would, and a dispute or a cap-out parks exactly as before. Immediately before the run may
/// settle, one <see cref="ReviewMode.FinalFullPass"/> runs — both lenses, fresh context, whether
/// or not a track had already gone dormant — so nothing reaches the remote on delta-green alone;
/// a track it reawakens with a real finding is recorded reactivated
/// (<see cref="Events.ReviewTrackReactivated"/>) rather than left stuck at an old conclusion. Which
/// mode a cycle ran under is a deterministic engine decision, recorded on <see cref="Events.ReviewDispatched"/>
/// and <see cref="Events.ReviewPassCompleted"/> — only the review content itself is agent judgment.
/// A reactivation resets that track's own cap (<see cref="RunAggregate.TrackBudgetBaseCycle"/>), so
/// the mandatory final pass has its own independent bound (<see cref="DaemonOptions.MaxFinalFullPassRounds"/>,
/// <see cref="FinalFullPassCapReached"/>) rather than relying on a per-track cap it can keep resetting.
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
    /// One project's out-of-scope Low findings fold into a single shared sweep document
    /// (<see cref="SweepDraftTask"/>), unlike every other <c>TaskAggregate</c> writer in this repo,
    /// which owns a fresh stream nobody else ever touches. <see cref="ReviewEngine"/> is a
    /// singleton (registered once for the daemon's lifetime), so this dictionary is the one place
    /// that can serialize two review loops racing to fold into the same project's sweep within
    /// this process — the exact collision <see cref="RouteFindingsAsync"/> guards against
    /// (adversarial and conformance review, cycle 1). <see cref="RouteToSweepAsync"/>'s own
    /// expectedVersion fence adds a second, cross-process layer, but only for the
    /// revise-an-open-sweep path; the branch that starts a brand-new sweep stream carries no such
    /// fence (a fresh <c>StartStream</c> under a new <c>DomainId</c> has no prior version to
    /// assert against). This <c>SemaphoreSlim</c> cannot close that gap — an in-process lock
    /// serializes nothing across processes — so today the create path has no guard at all against
    /// two daemon nodes racing the same database each observing "no open sweep yet" and starting
    /// two (adversarial and conformance review, cycle 4 and 5). Multi-node is design-only today
    /// (<c>HALL9K-P2P-DESIGN.md</c>), so nothing misbehaves on a real install, but this comment
    /// stops short of claiming a safety property the create path does not have.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sweepLocks = new();

    private SemaphoreSlim SweepLockFor(Guid projectId) => _sweepLocks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));

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
                    // Cycle 1 is always Discovery (task: review cycles after the first) — the
                    // adversarial lens's blindness design (log #63) stays intact where discovery
                    // actually happens.
                    string? openingHeadSha = await GetWorktreeHeadShaAsync(context.Run.WorktreePath, cancellationToken);
                    if (!await DispatchReviewPassesAsync(
                        context, run.ReviewCycle + 1, run.ActiveReviewLenses, ReviewMode.Discovery,
                        openingHeadSha, sinceSha: null, run.CurrentCycleMode, cancellationToken))
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
                    // Nothing may reach the remote on scoped green alone (task: a fix cycle's
                    // verification gate): the tip about to settle needs a full-scope gate run
                    // since whatever fix's own — possibly scoped — reverify last ran it, whether
                    // the loop is concluding on its own or a human overruled it. A human's
                    // merge-ready is exempt only from another agent's fresh-context read
                    // (MaySettleReason's own doc says why that half is a human's call to skip); it is
                    // not a claim that the suite ran, so it never substitutes for this gate
                    // (independent pre-PR review, cycle 1 — a human resolving a park that followed
                    // a scoped Verify gate previously reached SettleAsync here having never run the
                    // full suite over the fix's commits). The mode/fix-dispatched check alone misses
                    // a park resolved after HEAD moved without ever going through a fix-dispatch cycle
                    // (independent pre-PR review, cycle 1, adversarial lens): a Discovery-mode park
                    // that gets a same-session commit and a bare `--merge-ready` resolve moves HEAD
                    // without ever setting FixDispatchedThisCycle, so the mode check alone would fall
                    // through to SettleAsync having never gated the tip about to ship. This gap is
                    // reachable only through a human's own resolve (DeriveReviewPhase's own route to
                    // Settling — every review pass in the cycle already concluded clean — can never
                    // land here with HEAD having moved, since nothing commits to the worktree between
                    // a cycle's own gate and its passes landing), so the HEAD comparison's own extra
                    // git call is scoped to <see cref="RunAggregate.HumanEndedTheLoop"/> rather than
                    // applied unconditionally: widening THAT half to every Settling entry forced a
                    // redundant full gate and a whole extra FinalFullPass dispatch onto the run's own
                    // clean, human-free convergence path too, which is not this finding's defect and
                    // not worth paying for on every settle. The verify-commands fingerprint below is
                    // a different, cheaper check with a different gap (a human editing verify
                    // settings mid-run, which moves nothing this HEAD argument covers), so it runs
                    // unconditionally rather than sharing that scoping.
                    bool needsFullGateBeforeSettling = NeedsFullGateBeforeSettling(run);

                    // A pure store read (Marten only, no git call), so unlike the HEAD comparison
                    // below it can run on every Settling entry — including the ordinary
                    // clean-convergence path (mode never left Discovery, no fix dispatched, no
                    // human involved), which is the one path neither needsFullGateBeforeSettling
                    // nor HumanEndedTheLoop ever visits and so is the one path a human editing the
                    // project's verify commands mid-run — nothing else moves: no commit, no fix,
                    // no resolve — would otherwise never be caught by (independent pre-PR review,
                    // cycle 1, adversarial lens: the fingerprint half of
                    // GateAlreadyRanFullOverCurrentHeadAsync was reachable only from inside that
                    // method, which this path never called). Only consulted when there is a
                    // genuinely comparable full gate on record (RanFullScope and a HeadSha) —
                    // when there is not, the fingerprint question is moot and this defers to the
                    // needsFullGateBeforeSettling/HumanEndedTheLoop checks below exactly as before,
                    // rather than forcing a mandatory gate for a run whose most recent pass never
                    // claimed to be a comparable full one in the first place.
                    bool lastGateHasComparableFullScope = run.LastGateRanFullScope && run.LastGateHeadSha is not null;
                    bool verifyCommandsFingerprintChanged = lastGateHasComparableFullScope
                        && !await VerifyCommandsFingerprintMatchesAsync(context, run, cancellationToken);
                    // A fingerprint mismatch already answers the "already ran full over this
                    // head" question on its own (GateAlreadyRanFullOverCurrentHeadAsync's own doc:
                    // a mismatch falls through to false before ever reaching its HEAD comparison),
                    // so this skips calling back into it and paying its store read a second time
                    // (Copilot review, PR #86 — the fingerprint-only trigger path re-queried the
                    // same fingerprint it had just computed above).
                    bool gateAlreadyRanFullOverCurrentHead = verifyCommandsFingerprintChanged
                        ? false
                        : needsFullGateBeforeSettling || run.HumanEndedTheLoop
                            ? await GateAlreadyRanFullOverCurrentHeadAsync(context, run, cancellationToken)
                            : true;
                    if (needsFullGateBeforeSettling
                        || verifyCommandsFingerprintChanged
                        || (run.HumanEndedTheLoop && !gateAlreadyRanFullOverCurrentHead))
                    {
                        // Full, unless the immediately preceding gate already ran full over this
                        // exact tip (cycle-3 finding — a "scoped" Verify cycle whose own reverify
                        // gate fell back to full, most often because the fix only touched a
                        // non-mappable file like a doc, still satisfies "nothing merges on scoped
                        // green alone"; running it again here would be the identical suite over the
                        // identical commits). Whenever the preceding gate was actually scoped, or
                        // any commit landed since, this still runs unconditionally — so nothing
                        // merges on scoped green alone, and, when the loop is concluding on its own,
                        // the reviewers about to read this tip are reading a tree already proven to
                        // build and pass its whole suite.
                        if (!gateAlreadyRanFullOverCurrentHead
                            && !await verification.VerifyAsync(
                                context.RunId, context.TaskId, scopeSinceSha: null,
                                "mandatory final full pass: nothing merges on scoped green alone", cancellationToken))
                        {
                            return false;
                        }

                        // needsFullGateBeforeSettling is the only reason left standing that means a
                        // fresh-context reviewer has never read this cycle's own commits (its own doc:
                        // a Verify-mode cycle's own reverify was scoped, or a fix landed this cycle) —
                        // that is what "a moved HEAD or a dispatched fix earns another reviewer pass"
                        // actually means. A human's own resolution, or a bare verify-commands change
                        // with neither of those true, never moved anything a reviewer would read
                        // differently: the diff already converged clean under this very cycle's own
                        // fresh-context passes (independent pre-PR review, cycle 3, adversarial lens —
                        // dispatching a whole extra FinalFullPass here re-reads a byte-identical tip a
                        // second time and spends it against MaxFinalFullPassRounds for nothing). The
                        // gate above is what neither case ever covered, and it has now actually run,
                        // so the run may settle. This is term-for-term MaySettleReason (the Reverify
                        // branch's own settle check below calls it by name) rather than a second copy of
                        // its logic, so the two branches cannot drift apart the next time either grows a
                        // condition (independent pre-PR review, cycle 5, conformance lens). Because
                        // MaySettleReason's own human clause takes this short-circuit unconditionally, a
                        // human's merge-ready resolve now settles straight from here without ever
                        // reaching FinalFullPassCapReached below — see RunAggregate.Apply(ReviewParkResolved)'s
                        // own note on what that means for its FinalFullPassRounds reset.
                        if (MaySettleReason(run) is { } settlingReason)
                        {
                            LogSettleReason(run, settlingReason);
                            await SettleAsync(run, cancellationToken);
                            break;
                        }

                        // The per-track cycle caps cannot bound this on their own (cycle-3 finding):
                        // a track the final pass keeps reawakening gets its budget base bumped by
                        // that very reactivation (RunAggregate.TrackBudgetBaseCycle's own doc), so it
                        // never trips its own cap. FinalFullPassCapReached is the independent bound
                        // that stops the two-full-passes-plus-fix-session iteration from recurring
                        // forever. Checked here, immediately before the dispatch it actually guards,
                        // rather than before the settle short-circuit above: a fingerprint-only
                        // trigger (verifyCommandsFingerprintChanged with needsFullGateBeforeSettling
                        // false) always takes that short-circuit and was never about to spend a round,
                        // so checking the cap ahead of it parked a run that had converged clean
                        // (cycle-4 finding). The same ordering also means a human's own merge-ready
                        // resolve — MaySettleReason's other unconditional clause — never reaches this check
                        // at all (cycle-5 finding): the cap bounds only the automatic
                        // scoped-then-full-then-fix iteration, never a human-ended run.
                        if (FinalFullPassCapReached(run))
                        {
                            await ParkAsync(
                                context.RunId, context.TaskId, FinalFullPassCapParkReason(run), cancellationToken);
                            return false;
                        }

                        string? settlingHeadSha =
                            await GetWorktreeHeadShaAsync(context.Run.WorktreePath, cancellationToken);
                        if (!await DispatchReviewPassesAsync(
                            context, run.ReviewCycle + 1, ReviewLens.CycleLenses, ReviewMode.FinalFullPass,
                            settlingHeadSha, sinceSha: null, run.CurrentCycleMode, cancellationToken))
                        {
                            return false;
                        }

                        break;
                    }

                    // Reached only when none of needsFullGateBeforeSettling, a fingerprint change,
                    // or an ungated human resolution held — which is exactly
                    // NeedsFullGateBeforeSettling's own negation, so MaySettleReason is guaranteed
                    // non-null here (its NothingOwed clause is that same negation restated) rather
                    // than a fact this call site has to re-establish on its own.
                    LogSettleReason(run, MaySettleReason(run) ?? throw new InvalidOperationException(
                        $"Run {run.Id}: reached the ordinary settle path with no settle reason — " +
                        "NeedsFullGateBeforeSettling's own negation should have guaranteed one."));
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
                        $"Review cycle {run.ReviewCycle}: {await VerdictMissingCauseAsync(run, cancellationToken)}. " +
                        $"Its output: {RunPaths.ReviewFindingsFile(ParkedRunDirectory(run), run.ReviewCycle)}. " +
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
                    await ParkAsync(context.RunId, context.TaskId, CapParkReason(run, capped), cancellationToken);
                    return false;

                case ReviewPhase.FixNeeded:
                    if (!await DispatchFixSessionAsync(context, run, cancellationToken))
                    {
                        return false;
                    }

                    break;

                case ReviewPhase.Disputed:
                    await ParkAsync(context.RunId, context.TaskId, DisputedParkReason(context, run), cancellationToken);
                    return false;

                case ReviewPhase.Reverify:
                    // Whichever tracks are still owed a look get one merged Verify pass (task:
                    // review cycles after the first) — unless nothing is left, in which case this
                    // fix's own commits have never had a fresh-context read, and the mandatory
                    // FinalFullPass is what gives them one before the run may settle. The one
                    // exception is a pre-gate dispute resume (a rebase conflict or a review thread,
                    // Decisions Log #62): ReviewCycle is still 0 there — no review pass has EVER
                    // run on this branch — so the cycle about to dispatch is genuinely this run's
                    // first, and Discovery is what "cycle 1 is unchanged" promises it, not a Verify
                    // pass standing in for a discovery that never happened. Computed BEFORE the
                    // gate run below, not just after it (as it once was) — this run's own aggregate
                    // state does not change in between, and knowing which cycle comes next is
                    // exactly what decides this fix's own gate scope (task: a fix cycle's
                    // verification gate).
                    ReviewMode reverifyMode = run.ReviewCycle == 0
                        ? ReviewMode.Discovery
                        : run.ActiveReviewLenses.Count == 0 ? ReviewMode.FinalFullPass : ReviewMode.Verify;

                    // Only a fix whose next stop is an ordinary Verify cycle scopes its own gate
                    // pass: a Verify cycle can never settle or reach FinalFullPass without another
                    // gate pass first (the Settling branch's own guard above covers that one), so
                    // scoping here never lets an unverified-at-full-scope tip reach the remote. A
                    // fix whose next stop is the mandatory FinalFullPass — or, cycle 0, a pre-gate
                    // dispute resume with no review pass yet — gates at full scope instead of
                    // scoped-then-immediately-re-verified-full: nothing merges on scoped green
                    // alone (task: a fix cycle's verification gate).
                    (string? reverifyScopeSinceSha, string reverifyScopeContext) =
                        reverifyMode == ReviewMode.Verify && run.CycleHeadSha is { } reverifyScopeSha
                            ? (reverifyScopeSha, $"cycle {run.ReviewCycle} fix ({run.CurrentCycleMode.Value})")
                            : (null, reverifyMode == ReviewMode.FinalFullPass
                                ? "mandatory final full pass follows: nothing merges on scoped green alone"
                                : reverifyMode == ReviewMode.Discovery
                                    ? "no review pass has run on this branch yet"
                                    : "no prior cycle head to scope the fix's commits against");
                    if (!await verification.VerifyAsync(
                        context.RunId, context.TaskId, reverifyScopeSinceSha, reverifyScopeContext, cancellationToken))
                    {
                        // VerificationRunner already failed the run and task honestly.
                        return false;
                    }

                    if (run.ActiveReviewLenses.Count == 0 && MaySettleReason(run) is { } reverifySettleReason)
                    {
                        // Every track has concluded AND the mandatory final full pass already ran
                        // (or a human overruled the loop, or the severity bar already called it),
                        // so there is nobody left to re-review for and the loop goes on to record
                        // that it settled (log #63). The gates above still ran: a settled ending
                        // ships the terminal fix unread by a reviewer, never unbuilt and untested.
                        LogSettleReason(run, reverifySettleReason);
                        await SettleAsync(run, cancellationToken);
                        break;
                    }

                    // Same independent bound as the Settling branch above, checked here too: a run
                    // can reach a FinalFullPass dispatch straight from Reverify (every track just
                    // concluded again without ever passing back through Settling), and the per-track
                    // caps still cannot catch a track the final pass itself keeps reawakening.
                    if (reverifyMode == ReviewMode.FinalFullPass && FinalFullPassCapReached(run))
                    {
                        await ParkAsync(
                            context.RunId, context.TaskId, FinalFullPassCapParkReason(run), cancellationToken);
                        return false;
                    }

                    IReadOnlyList<ReviewLens> reverifyLenses = reverifyMode == ReviewMode.Verify
                        ? run.ActiveReviewLenses
                        : ReviewLens.CycleLenses;
                    string? reverifyHeadSha = await GetWorktreeHeadShaAsync(context.Run.WorktreePath, cancellationToken);
                    // The cycle about to be dispatched has not started yet, so run.CycleHeadSha still
                    // holds the cycle THIS reverify is following — exactly the boundary a Verify
                    // pass's "commits since the prior cycle" instruction needs (task: review cycles
                    // after the first). reverifyHeadSha, by contrast, is recorded on the new cycle's
                    // own ReviewDispatched, for whichever cycle comes after this one.
                    if (!await DispatchReviewPassesAsync(
                        context, run.ReviewCycle + 1, reverifyLenses, reverifyMode, reverifyHeadSha,
                        sinceSha: run.CycleHeadSha, run.CurrentCycleMode, cancellationToken))
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
            CurrentRunDirectory(run), ReviewArtifactName(run.ReviewCycle, pass.SessionId, pass.Lens));
        AgentResult? result = await WaitForSessionResultAsync(
            context.RunId, streamFile, pass.ProcessId, pass.ProcessStartedAt, cancellationToken);
        if (result is { IsError: true, Summary: { } summary } && BudgetExhaustionParser.IsBudgetExhausted(summary))
        {
            // External and clock-recoverable, same as the primary session (backlog 40): the
            // sibling pass goes down with it — nobody will read its verdict either while the
            // whole cycle waits on the clock — and the run parks rather than fails.
            TerminateSiblingPasses(run, pass);
            await ParkForBudgetAsync(context.RunId,
                $"the {LensLabel(pass.Lens)} session (cycle {run.ReviewCycle})", summary, cancellationToken);
            return false;
        }

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
            CurrentRunDirectory(run), FixArtifactName(run.ReviewCycle, sessionId));
        AgentResult? result = await WaitForSessionResultAsync(
            context.RunId, streamFile, processId, processStartedAt, cancellationToken);
        if (result is { IsError: true, Summary: { } summary } && BudgetExhaustionParser.IsBudgetExhausted(summary))
        {
            // External and clock-recoverable, same as the primary session (backlog 40): the
            // run parks rather than fails, and the retry sweep redispatches a fresh fix
            // session over the same cycle's findings once the window resets.
            await ParkForBudgetAsync(context.RunId,
                $"the fix session (cycle {run.ReviewCycle})", summary, cancellationToken);
            return false;
        }

        if (result is null || result.IsError)
        {
            await FailAsync(context.RunId, context.TaskId, result is null
                ? $"The fix session (cycle {run.ReviewCycle}) died without a result."
                : $"The fix session (cycle {run.ReviewCycle}) reported an error result.", cancellationToken);
            return false;
        }

        await RecordFixResultAsync(
            context.RunId, CurrentRunDirectory(run), run.ReviewCycle, context.Task.FollowUpKind, result, cancellationToken);
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
            run.CurrentCycleLenses,
            [.. run.InFlightReviewPasses.Select(pass => pass.Lens), .. run.CompletedReviewPasses.Select(pass => pass.Lens)]);
        if (missing.Count == 0)
        {
            return MissingPassDispatch.NothingMissing;
        }

        logger.LogWarning(
            "Run {RunId}: review cycle {Cycle} was missing the {Lenses} pass(es) — dispatching now",
            context.RunId, run.ReviewCycle, string.Join(", ", missing.Select(lens => lens.Slug)));
        // This tops up the CURRENT cycle, so run.CycleHeadSha already holds that cycle's own head
        // (recorded by whichever pass of it dispatched first) — the same value re-recorded here.
        // The "since" boundary a Verify top-up's prompt needs is one cycle further back, which is
        // exactly what run.PriorCycleHeadSha still holds: StartCycleIfNew only moves it when a
        // genuinely NEW cycle starts, and this dispatch is not one.
        bool dispatched = await DispatchReviewPassesAsync(
            context, run.ReviewCycle, missing, run.CurrentCycleMode, run.CycleHeadSha,
            sinceSha: run.PriorCycleHeadSha, run.PriorCycleMode, cancellationToken);
        return dispatched ? MissingPassDispatch.Dispatched : MissingPassDispatch.Stale;
    }

    /// <summary>
    /// Reports false the moment a spawn is rejected as stale, without dispatching the rest. A
    /// <see cref="ReviewMode.Verify"/> cycle dispatches exactly one session standing in for every
    /// lens in <paramref name="lenses"/> (task: review cycles after the first); every other mode
    /// dispatches one session per lens, as review always has. <paramref name="sinceSha"/> is only
    /// ever read for a Verify dispatch — it is the boundary that mode's prompt reads the diff
    /// since, distinct from <paramref name="headSha"/>, which every mode records on its own
    /// <see cref="ReviewDispatched"/> for whichever cycle comes after it. <paramref name="priorCycleMode"/>
    /// is the same kind of value, only read for a Verify dispatch too (cycle-4 conformance finding):
    /// whether the cycle whose findings this pass is quoting was itself a full two-lens read or
    /// another delta-scoped Verify pass, so that pass's prompt can say so honestly instead of always
    /// claiming a full read happened.
    /// </summary>
    private async Task<bool> DispatchReviewPassesAsync(
        ReviewContext context, int cycle, IReadOnlyList<ReviewLens> lenses, ReviewMode mode, string? headSha,
        string? sinceSha, ReviewMode priorCycleMode, CancellationToken cancellationToken)
    {
        if (mode == ReviewMode.Verify)
        {
            return await DispatchVerifyPassAsync(
                context, cycle, lenses, headSha, sinceSha, priorCycleMode, cancellationToken);
        }

        // Discovery and FinalFullPass both read the whole branch diff against the base, whatever
        // sinceSha holds — only a Verify dispatch reads a delta (see this method's own doc) — so
        // one packet, assembled once, covers every lens this cycle dispatches rather than one
        // `git diff` per lens.
        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            context.Run.WorktreePath, context.Project.BaseBranch, sinceSha: null, cancellationToken);
        foreach (ReviewLens lens in lenses)
        {
            if (!await DispatchReviewPassAsync(context, cycle, lens, mode, headSha, packet, cancellationToken))
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
        ReviewContext context, int cycle, ReviewLens lens, ReviewMode mode, string? headSha, ReviewPacket? packet,
        CancellationToken cancellationToken)
    {
        if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
        {
            return false;
        }

        Guid sessionId = DomainId.New();
        // Discovery and FinalFullPass both want the identical full-diff, fresh-context prompt
        // (task: review cycles after the first) — the mandatory final pass is discovery-grade
        // rigor at a later cycle number, not a different prompt.
        string prompt = AgentPromptBuilder.BuildReview(
            context.Task, context.Project, context.Run.Branch, cycle, lens, packet, context.PriorRulings);
        ExecutorMode executorMode = context.Run.ExecutorMode;
        // Every lens is review work, so they resolve the same role in the chain (log #33) —
        // and each dispatch records the model it actually got, per pass.
        AgentModel model = _options.ResolveModel(AgentRole.Review, context.Task.Model, context.Project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, context.Run.RunDirectory, prompt, executorMode, model,
            context.Project.SkipPermissions, ReviewArtifactName(cycle, sessionId, lens))
        {
            Environment = ReviewSessionEnvironment,
        }, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model, lens,
            mode, headSha));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: {Lens} agent dispatched with fresh context (cycle {Cycle}, mode {Mode}, session {SessionId}, pid {ProcessId}, model {Model})",
            context.RunId, LensLabel(lens), cycle, mode.Value, sessionId, agent.ProcessId, model.Value);
        return true;
    }

    /// <summary>
    /// The single reviewer a <see cref="ReviewMode.Verify"/> cycle dispatches (task: review cycles
    /// after the first): one session, standing in for every track named in <paramref name="tracks"/>,
    /// handed the prior cycle's own merged findings and fix-session summary verbatim so discovery is
    /// not paid for twice. Recorded under <see cref="ReviewLens.Verify"/>, whose widened
    /// <c>Covers</c> is what lets the crash-recovery top-up and the cycle-conclusion check treat
    /// this one session as answering for both real lenses.
    /// <para>
    /// <paramref name="headSha"/> and <paramref name="sinceSha"/> are deliberately two different
    /// values, not one read twice: <paramref name="headSha"/> is the worktree's tip right now, read
    /// fresh so it can be recorded on this dispatch's own <see cref="ReviewDispatched"/> for
    /// whichever cycle follows this one, while <paramref name="sinceSha"/> is the PRIOR cycle's own
    /// tip — the boundary this pass's prompt actually reads the diff since. Passing the same value
    /// for both would always resolve to an empty `git log`/`git diff` range, since the freshly-read
    /// current tip already includes whatever this cycle exists to verify.
    /// </para>
    /// </summary>
    private async Task<bool> DispatchVerifyPassAsync(
        ReviewContext context, int cycle, IReadOnlyList<ReviewLens> tracks, string? headSha, string? sinceSha,
        ReviewMode priorCycleMode, CancellationToken cancellationToken)
    {
        if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
        {
            return false;
        }

        string runDirectory = CurrentRunDirectory(context.Run);
        int previousCycle = cycle - 1;
        string priorFindings = await ReadIfExistsAsync(
            RunPaths.ReviewFindingsFile(runDirectory, previousCycle), cancellationToken);
        string priorFixPosition = await ReadIfExistsAsync(
            RunPaths.ReviewFixPositionFile(runDirectory, previousCycle), cancellationToken);
        // sinceSha is the prior cycle's own tip, exactly the delta boundary the packet's diff
        // should read from too; null falls back to the whole base-branch diff, the same fallback
        // this pass's own prompt text already states (BuildReviewVerify's "since" instruction).
        ReviewPacket? packet = await ReviewPacketAssembler.AssembleAsync(
            context.Run.WorktreePath, context.Project.BaseBranch, sinceSha, cancellationToken);

        Guid sessionId = DomainId.New();
        string prompt = AgentPromptBuilder.BuildReviewVerify(
            context.Task, context.Project, context.Run.Branch, cycle, tracks, priorFindings, priorFixPosition,
            sinceSha, priorCycleMode, packet, context.PriorRulings);
        ExecutorMode executorMode = context.Run.ExecutorMode;
        AgentModel model = _options.ResolveModel(AgentRole.Review, context.Task.Model, context.Project.Model);
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, context.Run.RunDirectory, prompt, executorMode, model,
            context.Project.SkipPermissions, ReviewArtifactName(cycle, sessionId, ReviewLens.Verify))
        {
            Environment = ReviewSessionEnvironment,
        }, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, model,
            ReviewLens.Verify, ReviewMode.Verify, headSha));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: verify agent dispatched over {Tracks} with the prior cycle's findings (cycle {Cycle}, session {SessionId}, pid {ProcessId}, model {Model})",
            context.RunId, string.Join(", ", tracks.Select(track => track.Slug)), cycle, sessionId, agent.ProcessId,
            model.Value);
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
        // The re-prompt is the pass's own contract restated — every lens answers in it now
        // (Decisions Log #87) — and the resumed output replaces what was read before. A Verify
        // pass's contract additionally includes the track= tag (independent pre-PR review, cycle
        // 2, adversarial finding): the resumed leg's own output replaces the original's file
        // entirely, so a re-prompt that restated severity and scope but not track would come back
        // untagged and get attributed to every active track by SplitForTrack's own conservative
        // default, rather than the one it actually belongs to.
        string prompt = verdictless.Mode == ReviewMode.Verify
            ? AgentPromptBuilder.BuildReviewVerdictReprompt(context.Project, run.ReviewCycle, run.ActiveReviewLenses)
            : AgentPromptBuilder.BuildReviewVerdictReprompt(context.Project, run.ReviewCycle);
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
            context.RunId, artifactId, context.Run.WorktreePath, context.Run.RunDirectory, prompt,
            context.Run.ExecutorMode, model,
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
    /// <para>
    /// A rebase-conflict dispute (Decisions Log #62, backlog 44) resumes through this same
    /// FixNeeded phase — <c>ParkedOnThreadDisputeAsync</c> reuses the plain review-thread park
    /// mechanism for it — but a generic review-fix prompt knows nothing about the base branch
    /// or the conflict, and the branch is still un-rebased (the parked session ran
    /// <c>git rebase --abort</c> before it exited). It gets the rebase prompt instead, with the
    /// human's stated resolution carried in as the conflict's answer.
    /// </para>
    /// <para>
    /// That resume is the ONLY case that wants the rebase prompt here: a rebase follow-up whose
    /// branch rebased cleanly and pushed also reaches FixNeeded, through its own ordinary review
    /// cycle, with nothing disputed and nothing left un-rebased — that one wants
    /// <see cref="AgentPromptBuilder.BuildReviewFix"/> like any other follow-up's review loop. The
    /// primary discriminator is <c>cycle</c> being 0: <see cref="RunAggregate.ReviewCycle"/>
    /// only reaches 1 at the first ordinary <c>ReviewDispatched</c> (cycle numbers start at 1), so it
    /// stays 0 for the entire dispute-and-resolve round trip no matter how many times the resumed
    /// session disputes again and gets resolved again — unlike <see cref="RunAggregate.ParkedFromState"/>,
    /// which is captured from <see cref="RunAggregate.State"/> at park time and so reads
    /// <see cref="RunState.UnderReview"/>, not <see cref="RunState.Verifying"/>, on every dispute past
    /// the first (<c>Apply(ReviewFixDispatched)</c> moves <c>State</c> to <c>UnderReview</c> before the
    /// resumed session ever parks again — a second-or-later resolve keyed on <c>ParkedFromState</c>
    /// would misroute to the generic review-fix prompt over a conflict still un-rebased). Checking
    /// <c>FollowUpKind.Rebase</c> alone is not enough either, since it stays <c>Rebase</c> for the
    /// whole rest of the run including its ordinary review cycles — <c>cycle == 0</c> is what narrows
    /// it to before any of those ever ran. <c>humanFindings</c> is what actually
    /// distinguishes a dispute resolution from a coincidental cycle-0 dispatch: it is non-null only
    /// for the one dispatch that directly consumes a needs-fixes <c>ReviewParkResolved</c>
    /// (<see cref="RunAggregate.PendingHumanFindings"/> is cleared the moment that fix session
    /// completes), and at cycle 0 a needs-fixes verdict can only ever originate from a human
    /// resolving a dispute park, so the pairing is redundant with <c>cycle == 0</c> in practice but
    /// documents the same intent the check had before.
    /// </para>
    /// <para>
    /// Escalation (task: a second fix round over the same findings) is decided here too, once,
    /// before the model resolves: <see cref="ReviewFixEscalation.Reason"/> compares this round's
    /// own findings against <see cref="RunAggregate.LastFixRoundFindingLocations"/> — the most
    /// recent AUTOMATED round's, not necessarily the immediately preceding one and never the
    /// whole run's history (that field's own doc has why), which is what makes de-escalation
    /// automatic once a repeated defect actually clears. A mechanical redispatch of
    /// the very same round (a budget-exhaustion retry re-enters FixNeeded with the cycle and
    /// <paramref name="run"/>'s <see cref="RunAggregate.PendingHumanFindings"/> both unchanged)
    /// reuses whatever that round already decided instead of asking the question again over
    /// content that has not actually changed.
    /// </para>
    /// </summary>
    private async Task<bool> DispatchFixSessionAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        int cycle = run.ReviewCycle;
        string? humanFindings = run.PendingHumanFindings;
        string runDirectory = CurrentRunDirectory(context.Run);
        // A cycle's own ride-alongs are already inside this text (a cycle that dispatches a fix
        // session writes every track's ride-alongs into its own merged findings document, Decisions
        // Log #87) — there is never an earlier cycle's ride-along left to fold in here: a ride-along
        // is recorded pending only on a cycle where nothing anywhere is being fixed, which is
        // exactly the cycle every active track concludes on (RecordReviewPassAsync), so the review
        // is already over before a later fix session could ever exist to claim it.
        string findings = humanFindings.IsNotBlank()
            ? $"Human review verdict (h9k review resolve): needs fixes.\n\n{humanFindings}"
            : await File.ReadAllTextAsync(RunPaths.ReviewFindingsFile(runDirectory, cycle), cancellationToken);

        if (!await EnsureCurrentGenerationAsync(context, cancellationToken))
        {
            return false;
        }

        Guid sessionId = DomainId.New();
        CommitStyle commitStyle = CommitStyle.Resolve(context.Project.CommitStyle, _options.DefaultCommitStyle);
        bool resumesRebaseDispute =
            context.Task.FollowUpKind == FollowUpKind.Rebase
            && cycle == 0
            && humanFindings.IsNotBlank();
        string prompt = resumesRebaseDispute
            ? AgentPromptBuilder.BuildRebase(
                context.Task, context.Project, context.Run.Branch, context.Task.PullRequestUrl!, commitStyle, findings)
            : AgentPromptBuilder.BuildReviewFix(context.Task, context.Run.Branch, findings, cycle);
        ExecutorMode mode = context.Run.ExecutorMode;

        // A retry of the very same round reuses whatever it already decided rather than asking
        // ReviewFixEscalation a second question over content that has not changed (see this
        // method's own doc comment, and RunAggregate.LastFixRoundCycle's, for why the pairing
        // with humanFindings — not the cycle number alone — is what tells a retry apart from a
        // human granting a genuinely fresh round at the same cycle number, e.g. resolving a
        // dispute with new guidance).
        bool retryOfSameRound = cycle == run.LastFixRoundCycle && humanFindings == run.LastFixRoundHumanFindings;

        // Fix is its own role: applying findings someone else reasoned out is a different shape
        // of work from producing them, so it resolves separately (log #33) — unless this round
        // repeats the previous one's findings (task: a second fix round over the same findings),
        // in which case it resolves the Review role's model instead: the observed dodge-and-redo
        // failure mode gets a stronger model exactly where it recurs. That only means something
        // when the two roles actually resolve to different models — an install that has never
        // set `--model-review`/`--model-fix` (or a task overriding both the same way) resolves
        // them identically, and recording an escalation there would tell a human the mitigation
        // applied when the spawned session ran on the model it would have run on anyway.
        AgentModel fixModel = _options.ResolveModel(AgentRole.Fix, context.Task.Model, context.Project.Model);
        AgentModel reviewModel = _options.ResolveModel(AgentRole.Review, context.Task.Model, context.Project.Model);
        bool escalated;
        string? escalationReason;
        if (retryOfSameRound)
        {
            escalated = run.LastFixSessionEscalated;
            escalationReason = run.LastFixSessionEscalationReason;
        }
        else
        {
            // This round is dispatched over `findings` above, never over the run's automated
            // Fix-dispositioned locations once a human's own reason is in play. What
            // CurrentCycleFixFindingLocations holds at this point depends on how the run parked:
            // a dispute park re-derives the disputed round's own set (ReviewParkResolved starts
            // no new cycle), while a cap park or verdict-missing park can hold fresh locations no
            // fix round was ever dispatched over. Either way the set describes what automation
            // was looking at, not what the human said, so comparing it here would misreport the
            // round — a dispute resolution as a repeat of itself, a cap resolution as a repeat of
            // findings never tried. The human-restatement scan inside ReviewFixEscalation.Reason
            // is the only signal that applies when humanFindings is in play.
            string? repeatReason = ReviewFixEscalation.Reason(
                run.LastFixRoundFindingLocations,
                humanFindings.IsNotBlank() ? [] : run.CurrentCycleFixFindingLocations,
                humanFindings);
            escalated = repeatReason is not null && reviewModel != fixModel;
            escalationReason = escalated ? repeatReason : null;
        }

        AgentModel model = escalated ? reviewModel : fixModel;
        SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
            context.RunId, sessionId, context.Run.WorktreePath, context.Run.RunDirectory, prompt, mode, model,
            context.Project.SkipPermissions, FixArtifactName(cycle, sessionId)), cancellationToken);

        DateTimeOffset dispatchedAt = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(context.RunId, new ReviewFixDispatched(
            context.RunId, sessionId, cycle, agent.ProcessId, agent.StartedAt, dispatchedAt, model,
            escalated, escalationReason));

        await session.SaveChangesAsync(cancellationToken);
        if (escalated)
        {
            logger.LogInformation(
                "Run {RunId}: fix run dispatched over the cycle-{Cycle} findings (session {SessionId}, pid {ProcessId}, model {Model}) — escalated to the review role's model: {Reason}",
                context.RunId, cycle, sessionId, agent.ProcessId, model.Value, escalationReason);
        }
        else
        {
            logger.LogInformation(
                "Run {RunId}: fix run dispatched over the cycle-{Cycle} findings (session {SessionId}, pid {ProcessId}, model {Model})",
                context.RunId, cycle, sessionId, agent.ProcessId, model.Value);
        }

        return true;
    }

    /// <summary>
    /// The park message for a disputed fix session. A pre-gate dispute resume that disputes
    /// again gets the same treatment its first park got
    /// (<c>RunSupervisor.ParkedOnThreadDisputeAsync</c>): no review pass ever ran ahead of it
    /// (<see cref="RunAggregate.ReviewCycle"/> is still 0 — cycle numbers start at 1, at the
    /// first <c>ReviewDispatched</c>), so pointing at <see cref="RunPaths.ReviewFindingsFile"/>
    /// like an ordinary disputed cycle would name a file nothing ever wrote, for either kind of
    /// pre-gate dispute — a rebase conflict or a review thread. <see cref="RecordFixResultAsync"/>
    /// saves this dispute's own closing summary under the same well-known dispute-file name the
    /// first park used (<see cref="RunPaths.RebaseConflictDisputeFile"/> or
    /// <see cref="RunPaths.ReviewThreadDisputeFile"/>), so a human checks one path for a cycle-0
    /// dispute regardless of which attempt it came from. <c>ReviewCycle == 0</c> is also what
    /// tells this apart from a later, ordinary review-cycle dispute on the same task — that one
    /// has already run at least one review pass, so its cycle is never 0.
    /// </summary>
    private static string DisputedParkReason(ReviewContext context, RunAggregate run)
    {
        string runDirectory = ParkedRunDirectory(run);
        if (run.ReviewCycle != 0)
        {
            return "The fix run disputed a review finding — as not-a-defect, as human territory, or as " +
                $"wrongly graded (cycle {run.ReviewCycle}). " +
                $"Review position: {RunPaths.ReviewFindingsFile(runDirectory, run.ReviewCycle)}; " +
                $"fix position: {RunPaths.ReviewFixPositionFile(runDirectory, run.ReviewCycle)}. " +
                "Decide between them, then resolve with h9k review resolve.";
        }

        return context.Task.FollowUpKind == FollowUpKind.Rebase
            ? "A resumed rebase follow-up still could not honestly resolve the conflict — both sides " +
              "change the same behavior, not just the same lines. " +
              $"Conflicting files and its position: {RunPaths.RebaseConflictDisputeFile(runDirectory)}. " +
              "Decide the conflict yourself, then resolve with h9k review resolve --needs-fixes " +
              "\"<your resolution>\" — nothing has been pushed. (--merge-ready is refused here: " +
              "nothing has been rebased yet.)"
            : "A resumed follow-up still could not honestly judge a review thread — as not-a-defect, " +
              "as human territory, or as wrongly graded. No review pass has run yet, so its position " +
              $"is: {RunPaths.ReviewThreadDisputeFile(runDirectory)}. Decide between it and the " +
              "fix session's own read, then resolve with h9k review resolve.";
    }

    /// <summary>
    /// Records one track's findings and verdict, and — when it was the cycle's last pass —
    /// concludes the cycle: every track decides whether it runs again, out-of-scope non-Highs
    /// are routed — a Medium to a draft bug task of its own, a Low folded into the project's
    /// standing sweep (Decisions Log #87, #99) — the findings merge into one document, and the
    /// milestones land in one transaction so the pass, the cycle, and the tracks can never
    /// disagree.
    /// </summary>
    private async Task RecordReviewPassAsync(
        ReviewContext context, RunAggregate run, ReviewPassSession pass, AgentResult result,
        CancellationToken cancellationToken)
    {
        Guid runId = context.RunId;
        string runDirectory = CurrentRunDirectory(run);
        int cycle = run.ReviewCycle;
        string output = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(LensFindingsFile(runDirectory, cycle, pass.Lens), output, cancellationToken);

        // The objective and the acceptance criteria are only ever printed into the conformance
        // lens's own prompt (AgentPromptBuilder.BuildConformanceReview) and the Verify pass's
        // (BuildReviewVerify, which restates the same "what the diff is supposed to do" section);
        // the adversarial lens is deliberately never told either, so it has nothing of that shape
        // to echo, and screening its output for the same text risks deleting a genuine finding
        // that happens to phrase itself the way the task's own text does (cycle-4 adversarial
        // finding, ReviewEngine.cs:614). The agent context is narrower still: only the pr-review
        // lens's own prompt (AgentPromptBuilder.BuildPrReviewLens) ever prints it, and a pr-review
        // run never reaches this method (PrReviewEngine's own class doc — it never enters
        // ReviewEngine's cycle machine), so no pass this method ever records was shown it,
        // whatever lens it covers (cycle-1 adversarial finding: BuildReviewVerify's own prompt
        // carries no Context section at all, so gating the strip on Covers(Conformance) screened
        // a Verify pass's output against text it never saw).
        bool sawTaskContext = pass.Lens.Covers(ReviewLens.Conformance);
        ReviewVerdict verdict = ReviewResultParser.ParseVerdict(output);

        // The structured findings, read whatever the verdict said — a merge-ready pass can still
        // attach ride-alongs (Decisions Log #87), and the reclassification right below needs to
        // see them before findings is ever assigned.
        IReadOnlyList<ReviewFinding> parsedFindings = ReviewResultParser.ParseFindings(output);
        if (verdict != ReviewVerdict.Unknown && parsedFindings.Count > 0)
        {
            // Decisions Log #87: whether this pass earns a fix-and-re-review cycle is decided by
            // each finding's own Disposition, never by severity alone and never by trusting the
            // reviewer's stated VERDICT line over its own attached findings. Disposition and
            // "meets the fix bar" are NOT the same predicate: an out-of-scope finding the
            // reviewer never graded is Fix too (ReviewFinding.Disposition's conservative
            // reading — routing an ungraded defect away would export it on no evidence it is
            // safe to), so checking severity alone let a mis-graded or ungraded finding slip
            // through as merge-ready with a fix silently owed and never dispatched. This runs
            // both directions: a verdict is only ever recorded merge-ready when every stated
            // finding is RideAlong-dispositioned — the one disposition that never itself forces
            // a cycle — so a needs-fixes verdict whose findings are all RideAlong is demoted
            // exactly as before, and — new — a verdict the reviewer itself already returned as
            // merge-ready is promoted back to needs-fixes the moment it carries anything else
            // (Fix or, just as much, Route), so ReviewTrackPolicy.Decide's merge-ready branch
            // never has to reconcile a Fix finding it was never designed to carry. A Route
            // finding is deliberately NOT treated the same as a Fix finding for this purpose,
            // only exempted from becoming a merge-ready verdict the same way: recording the
            // verdict as needs-fixes over a route-only pass is what lets ReviewTrackPolicy.Decide's
            // own pre-gate rule keep the track alive to read a tip the OTHER track's fix session
            // may still rewrite (that rule's own doc — "before the gate, every stated finding
            // keeps the track alive, a routed one included" — draws no severity line, so neither
            // does this). Gated on parsedFindings being genuinely non-empty: an unreadable
            // needs-fixes verdict (parsedFindings empty, the Stated() placeholder standing in
            // below) is a defect the platform could not structure, never evidence it was trivial,
            // and reclassifying that would reopen exactly the gap Decisions Log #86 closed.
            // Findings survive this reclassification exactly as parsed — nothing here is
            // discarded, only recorded as not earning its own cycle (or, newly, as earning one
            // after all).
            verdict = parsedFindings.All(finding => finding.Disposition == ReviewFindingDisposition.RideAlong)
                ? ReviewVerdict.MergeReady
                : ReviewVerdict.NeedsFixes;
        }

        if (verdict == ReviewVerdict.NeedsFixes
            && !ReviewVerdictValidation.NamesAFinding(
                output,
                sawTaskContext ? context.Task.Objective : null,
                sawTaskContext ? context.Task.AcceptanceCriteria : null,
                // Unlike the objective and the acceptance criteria, settled rulings are printed
                // into BOTH lenses' prompts (AgentPromptBuilder.AppendSettledRulings), so this
                // strip is never gated on sawTaskContext.
                AgentPromptBuilder.RulingReasonsShown(context.PriorRulings)))
        {
            // A needs-fixes verdict that names nothing is not a real answer (origin: ten
            // occurrences filed 2026-08-25): recording it as Unknown routes it through the exact
            // same one-reprompt-then-park path an unparseable VERDICT line already takes, rather
            // than parking a human or spending a fix session on content that does not exist.
            // Checked here, after the disposition reclassification above, rather than only on the
            // verdict as it arrived (cycle-3 adversarial finding): a merge-ready pass whose only
            // attached finding echoed the finding contract's own placeholder used to slip past a
            // weaker placeholder screen with a fabricated in-scope High, get promoted to
            // needs-fixes by the reclassification above without ever passing through this gate,
            // and spend a fix session and an extra adversarial cycle on a file the pass never
            // actually touched. A verdict promoted to needs-fixes this way now gets the identical
            // scrutiny an arriving needs-fixes verdict always has.
            verdict = ReviewVerdict.Unknown;
        }

        // A needs-fixes pass always carries at least one finding, even when nothing structured
        // could be read out of it; a merge-ready pass carries whatever it attached, ride-alongs
        // included, never a placeholder; an unread (Unknown) pass carries nothing.
        IReadOnlyList<ReviewFinding> findings = verdict switch
        {
            _ when verdict == ReviewVerdict.Unknown => [],
            _ when verdict == ReviewVerdict.NeedsFixes => ReviewTrackPolicy.Stated(parsedFindings),
            _ => parsedFindings,
        };

        List<ReviewPassResult> completed = MergeCompleted(
            run.CompletedReviewPasses,
            new ReviewPassResult(pass.Lens, pass.TranscriptSessionId, pass.Model, verdict,
                [.. findings.Select(finding => finding.ToRecord())], pass.Mode));
        // The cycle concludes only when nothing else is reading AND no active track is still
        // missing: a merged verdict over a track that never looked would be the single-sample
        // blind spot this whole mechanism exists to close.
        bool cycleConcluded = run.InFlightReviewPasses.All(inFlight => inFlight.Lens == pass.Lens)
            && ReviewLens.MissingFrom(run.CurrentCycleLenses, completed.Select(finished => finished.Lens)).Count == 0;
        ReviewVerdict cycleVerdict = ReviewVerdict.Merge(completed.Select(finished => finished.Verdict));

        // A cycle with an unreadable verdict has decided nothing: its re-prompt is what happens
        // next, and grading tracks against a pass nobody could read would be the guess the
        // re-prompt exists to avoid.
        IReadOnlyList<ReviewTrackPlan> plans = cycleConcluded && cycleVerdict != ReviewVerdict.Unknown
            ? await PlanCycleAsync(runDirectory, run, completed, cancellationToken)
            : [];
        IReadOnlyList<RoutedFinding> routed =
            await RouteFindingsAsync(context, run, runDirectory, cycle, plans, cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, result.ToTokensRecorded(runId, now));
        session.Events.Append(runId, new ReviewPassCompleted(
            runId, cycle, pass.Lens, verdict, now, [.. findings.Select(finding => finding.ToRecord())],
            run.CurrentCycleMode, result.Turns, result.TotalInputTokens));
        if (cycleConcluded)
        {
            // A cycle that dispatches its own fix session already sweeps up every concluding
            // track's ride-alongs for free (DispatchFixSessionAsync reads this cycle's own merged
            // findings file whole): each one shipped the same way its own Fix findings did,
            // unreviewed, so it joins the same residual bucket a Fix finding would (Decisions Log
            // #87) — only concluding plans, here, because a track still saying "continue" gets a
            // fresh look at the fix commits next cycle and is not shipping anything yet. Otherwise
            // nothing anywhere in this cycle is being fixed, which is the empty terminal case
            // (Decisions Log #63) whatever any individual plan's own Continues says: the very next
            // phase derivation (RunAggregate.DeriveReviewPhase) sees PendingFixFindings == 0 and
            // settles the whole run regardless, ending even a track pre-gate rules kept saying
            // "continue" (a route-only pass, say — kept alive only in case the OTHER track's fix
            // session rewrote the branch, which nothing here now will). Concluding every active
            // plan right here, rather than leaving a still-"continuing" one for SettleAsync's own
            // catch-all — which writes no residuals, because before ride-alongs existed a track it
            // force-concludes never had any to lose — is what lets that straggler's ride-along
            // survive as a residual instead of vanishing: there is no later cycle for anything to
            // claim it in either way, so its residual is written the instant the run stops looking.
            bool anyFixFinding = plans.Any(plan => plan.Fix.Count > 0);
            IReadOnlyList<ReviewTrackPlan> concludingNow = anyFixFinding
                ? [.. plans.Where(plan => !plan.Continues)]
                : plans;

            // The mandatory FinalFullPass reads every lens regardless of conclusion
            // (RunAggregate.CurrentCycleLenses's own override), so a plan here can name a track
            // that was already concluded — and this time it found something real (Continues: true)
            // rather than confirming clean. That track is genuinely reawakened, on the record
            // (the ReviewTrackReactivated events below), rather than left stuck at its old
            // conclusion (ReviewTrackReactivated's own doc says why this cannot just be "replace
            // the old ReviewTrackConcluded" — nothing here replaces a conclusion that already read
            // Continues: false). Computed after concludingNow, and gated on the track not being in
            // it (cycle-5 adversarial finding): the empty terminal case above force-concludes every
            // plan, reactivated one included, in this very transaction, and a track the same
            // transaction both reactivates and concludes is not a real reawakening — it is the
            // terminal case, on the record twice. The set is computed here, before the merged
            // findings document below, because the cap check that document's ride-along note
            // depends on needs it already: run is the aggregate loaded at the top of this
            // iteration, before the ReviewTrackReactivated events are appended, so a lens
            // reactivated just now would still read run.TrackBudgetBaseCycle's pre-reactivation
            // value unless this set overrides it with the cycle it was actually reawakened at
            // (cycle-4 conformance finding) — otherwise a track a FinalFullPass reawakens with a
            // real Fix finding could read as already capped in that check while the very next
            // DriveAsync iteration — with the reactivation applied — finds it not capped at all,
            // dispatching a fix session over a ride-along this method already recorded as never
            // claimed.
            HashSet<ReviewLens> reactivatedThisCycle = run.CurrentCycleMode == ReviewMode.FinalFullPass
                ? [.. plans
                    .Where(plan => plan.Continues
                        && run.ConcludedReviewTracks.Any(track => track.Lens == plan.Lens)
                        && !concludingNow.Any(concluding => concluding.Lens == plan.Lens))
                    .Select(plan => plan.Lens)]
                : [];

            // A Fix finding is not by itself a promise that a fix session is coming (cycle-2
            // review, adversarial finding): ReviewTrackPolicy.Decide grades severity and the gate,
            // never the cap, so a track can carry Continues: true and a real Fix finding while
            // already at CappedTrack's own cap — the next DriveAsync iteration then hits
            // `ReviewPhase.FixNeeded when CappedTrack(run) is { } capped` and parks instead of
            // reaching DispatchFixSessionAsync. Recording FixedUnreviewed for this cycle's
            // ride-alongs in that case would assert a fix session read them when none is ever
            // dispatched, so that disposition is additionally gated on no continuing track already
            // being capped — the lens whose Fix finding survives park-or-dispatch is always one
            // still saying Continues: true (a concluding plan's Fix already became its own residual
            // above), so checking the cap only against continuing plans is exactly the set
            // CappedTrack itself will see next. Computed before the merged findings document is
            // written (rather than after, as it once was) because the document's own ride-along
            // note has to say the true thing for THIS cycle: CapParkReason points a human at the
            // same file a dispatching fix session reads, and the note is wrong for one of them
            // unless it knows which cycle it is describing.
            bool fixSessionWillDispatch = anyFixFinding
                && !plans.Any(plan => plan.Continues
                    && ReviewTrackPolicy.CapReached(
                        plan.Lens,
                        run.ReviewCycle,
                        reactivatedThisCycle.Contains(plan.Lens) ? cycle : run.TrackBudgetBaseCycle(plan.Lens),
                        _options));

            await WriteMergedFindingsAsync(
                runDirectory, cycle, run.CurrentCycleMode, completed, plans, routed, fixSessionWillDispatch,
                cancellationToken);
            session.Events.Append(runId, new ReviewCompleted(runId, cycle, cycleVerdict, now));

            foreach (ReviewTrackPlan reawakened in plans.Where(plan => reactivatedThisCycle.Contains(plan.Lens)))
            {
                session.Events.Append(runId, new ReviewTrackReactivated(runId, reawakened.Lens, cycle, now));
                logger.LogInformation(
                    "Run {RunId}: the {Lens} track reactivated at cycle {Cycle} — the mandatory final full pass found something new",
                    runId, LensLabel(reawakened.Lens), cycle);
            }

            ReviewResidualDisposition rideAlongDisposition = fixSessionWillDispatch
                ? ReviewResidualDisposition.FixedUnreviewed
                : ReviewResidualDisposition.RideAlong;
            // Reference identity, not SamePlace: a Verify pass hands the identical ReviewFinding
            // instance to every active track's plan.RideAlong when the finding is untagged
            // (SplitForTrack's own doc), and SamePlace deliberately treats two blank or lineless
            // locations as different defects (ReviewFindingLocations's own doc), so it cannot catch
            // this here either — the same reasoning RouteFindingsAsync.SplitAlreadyRouted and
            // SettleAsync's own "attributed" set already apply to plan.Route and to a still-active
            // track's ride-alongs (cycle-4 adversarial finding). Without this, one reviewer
            // statement shared by two concluding tracks writes — and tallies — two residuals.
            HashSet<ReviewFinding> rideAlongAttributed = new(ReferenceEqualityComparer.Instance);
            foreach (ReviewTrackPlan plan in concludingNow)
            {
                ReviewSettlement settlement = plan.Settlement ?? ReviewSettlement.Settled;
                IReadOnlyList<ReviewResidual> residuals =
                    [.. plan.Residuals, .. plan.RideAlong
                        .Where(finding => rideAlongAttributed.Add(finding))
                        .Select(finding => ReviewTrackPolicy.Residual(plan.Lens, cycle, finding, rideAlongDisposition))];
                session.Events.Append(runId, new ReviewTrackConcluded(
                    runId, plan.Lens, cycle, settlement, residuals, now));
                logger.LogInformation(
                    "Run {RunId}: the {Lens} track concluded at cycle {Cycle} — {Settlement}, {Residuals} residual(s)",
                    runId, LensLabel(plan.Lens), cycle, settlement.Value, residuals.Count);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: the {Lens} pass of cycle {Cycle} completed — verdict {Verdict}, {Findings} finding(s), " +
            "{Turns} turn(s) ({Input}in/{Output}out tokens)",
            runId, LensLabel(pass.Lens), cycle,
            verdict == ReviewVerdict.Unknown ? "(none)" : verdict.Value, findings.Count,
            result.Turns, result.TotalInputTokens, result.OutputTokens);
    }

    /// <summary>
    /// What each active track does with the cycle it just finished. Every track's findings are
    /// re-read from its own artifact rather than threaded through the wait loop, which is what
    /// makes the decision reproducible on a resumed daemon: the files are the record, and the
    /// policy over them is pure.
    /// </summary>
    private async Task<IReadOnlyList<ReviewTrackPlan>> PlanCycleAsync(
        string runDirectory, RunAggregate run, IReadOnlyList<ReviewPassResult> completed, CancellationToken cancellationToken)
    {
        // Iterated by TRACK rather than by completed pass (task: review cycles after the first),
        // which is what decouples "how many sessions ran" from "how many track decisions land": a
        // Discovery or FinalFullPass cycle's two real-lens passes are still a 1:1 match (a pass
        // covers exactly the lens it iterates to), but a Verify cycle's one combined pass answers
        // for every lens named here, and each needs its own ReviewTrackPolicy.Decide call — its own
        // cap, its own gate, its own Continues — decided from that ONE pass's own findings filtered
        // down to the track's own subset.
        List<ReviewTrackPlan> plans = [];
        // Memoized per pass, not re-read per lens: a Verify pass's own findings file is read once
        // here regardless of how many active lenses it covers (task: review cycles after the
        // first). Re-parsing it separately per lens would hand each track its own freshly-parsed
        // ReviewFinding objects for what is otherwise the identical reviewer statement — and
        // RouteFindingsAsync's own same-instance dedup for an untagged, unplaced finding
        // (independent pre-PR review, cycle 2, conformance finding #4) depends on every track
        // that shares a pass actually sharing its finding objects, not just its finding text.
        Dictionary<ReviewPassResult, IReadOnlyList<ReviewFinding>> findingsByPass = [];
        foreach (ReviewLens lens in run.CurrentCycleLenses)
        {
            ReviewPassResult? finished = completed.FirstOrDefault(pass => pass.Lens.Covers(lens));
            if (finished is null)
            {
                continue;
            }

            if (!findingsByPass.TryGetValue(finished, out IReadOnlyList<ReviewFinding>? passFindings))
            {
                passFindings = await ReadFindingsAsync(runDirectory, run.ReviewCycle, finished, cancellationToken);
                findingsByPass[finished] = passFindings;
            }

            (ReviewVerdict trackVerdict, IReadOnlyList<ReviewFinding> trackFindings) = finished.Lens == ReviewLens.Verify
                ? SplitForTrack(lens, passFindings, finished.Verdict, run.CurrentCycleLenses)
                : (finished.Verdict, passFindings);

            plans.Add(ReviewTrackPolicy.Decide(lens, run.ReviewCycle, trackVerdict, trackFindings, _options));
        }

        return plans;
    }

    /// <summary>
    /// A Verify pass's own findings, filtered to the ones this track owns, and reclassified into a
    /// per-track verdict (task: review cycles after the first) — the identical reclassification
    /// <see cref="RecordReviewPassAsync"/> already applies at the whole-session level (Decisions Log
    /// #87), scoped down to one track's own subset: a track with nothing attributed to it, or
    /// nothing but ride-alongs, is merge-ready for its own purposes even when the session's overall
    /// verdict was needs-fixes because of the OTHER track's finding.
    /// <para>
    /// <paramref name="findings"/> empty is two different facts, told apart by
    /// <paramref name="sessionVerdict"/> exactly the way a single-lens pass's own needs-fixes-naming-
    /// nothing-structured case is (Decisions Log #86): a merge-ready session with nothing attached
    /// really did find nothing for anyone, but a needs-fixes session that named nothing the parser
    /// could structure still owes every track it stands in for the same "something must be fixed"
    /// placeholder <see cref="ReviewTrackPolicy.Stated"/> already injects for a single-lens pass —
    /// collapsing it to merge-ready here would silently drop a real, if unstructured, defect the
    /// moment two tracks share one reviewer. An untagged, unplaced finding cannot be attributed to
    /// one track over the other, so — like an untagged <c>track=</c> tag on a genuinely parsed
    /// finding — it counts against every track this pass stands in for rather than none.
    /// </para>
    /// <para>
    /// <paramref name="activeLenses"/> is <see cref="RunAggregate.CurrentCycleLenses"/> — the tracks
    /// this cycle actually plans for. A finding tagged for a real lens that already concluded before
    /// this cycle (the reviewer restated an earlier finding's own track, or reasoned about a track
    /// that is no longer being asked about) has nowhere left to be attributed: <paramref name="lens"/>
    /// never equals that stale tag, since it is never iterated once concluded, so the finding would
    /// otherwise appear in no plan at all and vanish — never Fix-dispositioned, never routed, never a
    /// residual. It is read the same conservative way an untagged finding already is: it counts
    /// against every track this pass still stands in for.
    /// </para>
    /// </summary>
    private static (ReviewVerdict Verdict, IReadOnlyList<ReviewFinding> Findings) SplitForTrack(
        ReviewLens lens, IReadOnlyList<ReviewFinding> findings, ReviewVerdict sessionVerdict,
        IReadOnlyList<ReviewLens> activeLenses)
    {
        if (findings.Count == 0)
        {
            return sessionVerdict == ReviewVerdict.NeedsFixes
                ? (ReviewVerdict.NeedsFixes, [])
                : (ReviewVerdict.MergeReady, []);
        }

        List<ReviewFinding> trackFindings = [.. findings.Where(finding =>
            finding.Track is null
            || finding.Track == lens
            || !activeLenses.Any(active => active == finding.Track))];
        ReviewVerdict verdict = trackFindings.Count == 0
            ? ReviewVerdict.MergeReady
            : trackFindings.All(finding => finding.Disposition == ReviewFindingDisposition.RideAlong)
                ? ReviewVerdict.MergeReady
                : ReviewVerdict.NeedsFixes;
        return (verdict, trackFindings);
    }

    private static async Task<IReadOnlyList<ReviewFinding>> ReadFindingsAsync(
        string runDirectory, int cycle, ReviewPassResult pass, CancellationToken cancellationToken)
    {
        // Read for either verdict a pass can carry here (PlanCycleAsync only ever runs once the
        // cycle's merged verdict is not Unknown, which makes every individual pass's own verdict
        // either NeedsFixes or MergeReady too — never Unknown — so this is not itself a third
        // branch, only the two real ones). A merge-ready pass can carry ride-alongs (Decisions
        // Log #87) exactly as a needs-fixes one can carry findings.
        string path = LensFindingsFile(runDirectory, cycle, pass.Lens);
        return File.Exists(path)
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
    /// <para>
    /// Where a routed finding lands still splits by severity (Decisions Log #87's own
    /// <see cref="ReviewSeverity.MeetsFixBar"/>, reused rather than reinvented): a Medium meets
    /// the fix bar and still mints a draft of its own, exactly as before this consolidated
    /// anything, while a Low does not and instead folds into the project's one standing sweep
    /// draft (<see cref="SweepDraftTask"/>) — so a serious pre-existing defect can never be
    /// buried in a polish pile, and eight one-line Low findings cost one build-gate-review
    /// pipeline instead of eight (Decisions Log #99).
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<RoutedFinding>> RouteFindingsAsync(
        ReviewContext context, RunAggregate run, string runDirectory, int cycle, IReadOnlyList<ReviewTrackPlan> plans,
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
        List<(ReviewLens Lens, ReviewFinding Finding)> sweepBound =
            [.. pending.Where(entry => !entry.Finding.Severity.MeetsFixBar)];

        // The sweep is a document every run in the project shares, unlike a fresh draft bug task's
        // own stream, so folding into it has to be serialized end to end — through this method's
        // own SaveChangesAsync, not just the read inside RouteToSweepAsync — or two review loops
        // committing within milliseconds of each other can both observe "no open sweep yet" and
        // start two, or both revise the one open sweep and have the loser's items silently
        // overwritten (adversarial and conformance review, cycle 1). Held only when this batch
        // actually has a Low to fold, so every other cycle's routing pays nothing for it.
        SemaphoreSlim? sweepLock = sweepBound.Count > 0 ? SweepLockFor(context.Task.ProjectId) : null;
        if (sweepLock is not null)
        {
            await sweepLock.WaitAsync(cancellationToken);
        }

        try
        {
            await using IDocumentSession session = store.LightweightSession();
            foreach ((ReviewLens lens, ReviewFinding finding) in pending.Where(entry => entry.Finding.Severity.MeetsFixBar))
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

            if (sweepBound.Count > 0)
            {
                routed.AddRange(
                    await RouteToSweepAsync(session, context, runDirectory, cycle, sweepBound, now, cancellationToken));
            }

            AppendRouted(session, context.RunId, cycle, routed, now);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                int draftTaskCount = routed.Count(entry => entry.DraftTaskId is not null && !entry.IsSweep);
                int sweepFoldCount = routed.Count(entry => entry.DraftTaskId is not null && entry.IsSweep);
                logger.LogInformation(
                    "Run {RunId}: routed cycle {Cycle}'s out-of-scope findings — {DraftTaskCount} to draft bug tasks, {SweepFoldCount} folded into the standing sweep",
                    context.RunId, cycle, draftTaskCount, sweepFoldCount);
                return [.. routed, .. repeats];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Run {RunId}: routing out-of-scope findings for cycle {Cycle} failed — the review loop continues and the findings are recorded as unrouted",
                    context.RunId, cycle);
                routed = [.. routed.Select(entry => entry with { DraftTaskId = null, FailureReason = exception.Message })];
                await RecordRoutingFailureAsync(context.RunId, cycle, routed, now, cancellationToken);
                return [.. routed, .. repeats];
            }
        }
        finally
        {
            sweepLock?.Release();
        }
    }

    /// <summary>
    /// Every finding this batch graded Low, folded into the project's one open standing sweep
    /// draft (<see cref="SweepDraftTask"/>) — created fresh when none is open. One read-then-write
    /// over the whole batch rather than one per finding: a per-finding query would let two Low
    /// findings in the same cycle each observe "no open sweep yet" and mint two, since neither
    /// write is visible to the other until this method's caller saves them together. Nothing here
    /// may fail the review either, for the same reason <see cref="RouteFindingsAsync"/> itself
    /// never lets a routing failure propagate: a failure is recorded against every finding in the
    /// batch, and the next cycle to report the same defect tries the fold again.
    /// </summary>
    private async Task<IReadOnlyList<RoutedFinding>> RouteToSweepAsync(
        IDocumentSession session, ReviewContext context, string runDirectory, int cycle,
        IReadOnlyList<(ReviewLens Lens, ReviewFinding Finding)> sweepBound, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            string findingsFile = RunPaths.ReviewFindingsFile(runDirectory, cycle);
            List<SweepFindingRoute> routes =
                [.. sweepBound.Select(entry => new SweepFindingRoute(entry.Finding, context.RunId, cycle, findingsFile))];

            // The state filter is matched as SQL rather than compared in LINQ, the same way every
            // TaskState filter in this repo is (TaskAddCommand.RefuseSecondAdoptionAsync,
            // DispatchEngine, TaskDependencyResolver): TaskState is a value object, and Marten
            // refuses to translate a comparison against one.
            TaskListItem? open = await session.Query<TaskListItem>()
                .Where(task => task.ProjectId == context.Task.ProjectId)
                .Where(task => task.Objective == SweepDraftTask.Objective)
                .Where(task => task.MatchesSql("d.data ->> 'state' = ?", TaskState.Draft.Value))
                .OrderByDescending(task => task.AddedAt)
                .FirstOrDefaultAsync(cancellationToken);

            Guid sweepTaskId;
            if (open is null)
            {
                sweepTaskId = DomainId.New();
                TaskAdded added = SweepDraftTask.ComposeNew(sweepTaskId, context.Task.ProjectId, routes, now, context.Run.OwnerId);
                session.Events.StartStream<TaskAggregate>(sweepTaskId, added);
            }
            else
            {
                sweepTaskId = open.Id;

                // Pinned to the stream version observed right now (the write-time half of the
                // fence GenerationFence.LoadFencedAsync documents): a second review loop revising
                // the same open sweep between this read and this method's caller committing would
                // otherwise land its own full-document overwrite on top of this one, and
                // TaskDetails.Apply(TaskRevised) keeps only the later event's AgentContext whole —
                // the earlier append's items would be gone with no trace on the stream
                // (adversarial and conformance review, cycle 1). expectedVersion turns that into a
                // detected conflict rather than a silent loss — but Append below only queues the
                // assertion; it does not throw here. The conflict actually surfaces at
                // RouteFindingsAsync's own SaveChangesAsync, whose catch clause rewrites every
                // entry in that batch's `routed` list as a routing failure, not just this fold's —
                // including any Medium finding this same cycle already composed a real draft bug
                // task for earlier in the loop (cycle-5 adversarial review). The next cycle to
                // report the same defect tries again either way.
                StreamState? state = await session.Events.FetchStreamStateAsync(sweepTaskId, cancellationToken)
                    ?? throw new InvalidOperationException($"Sweep draft {sweepTaskId} named by the query has no stream.");
                TaskAggregate sweep = await session.Events.AggregateStreamAsync<TaskAggregate>(
                    sweepTaskId, version: state.Version, token: cancellationToken)
                    ?? throw new InvalidOperationException($"Sweep draft {sweepTaskId} named by the query has no stream.");
                TaskRevised revised = TaskDecider.Revise(
                    sweep,
                    Optional<string>.None,
                    Optional<IReadOnlyList<string>>.None,
                    Optional<string>.Of(SweepDraftTask.Append(sweep.AgentContext, routes)),
                    Optional<IReadOnlyList<Guid>>.None,
                    Optional<TaskType>.None,
                    Optional<AgentModel>.None,
                    now,
                    context.Run.OwnerId);
                session.Events.Append(sweepTaskId, expectedVersion: state.Version + 1, revised);
            }

            return [.. sweepBound.Select(entry => new RoutedFinding(entry.Lens, entry.Finding, sweepTaskId, null, IsSweep: true))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: could not fold {Count} out-of-scope Low finding(s) of cycle {Cycle} into the standing sweep draft",
                context.RunId, sweepBound.Count, cycle);
            return [.. sweepBound.Select(entry => new RoutedFinding(entry.Lens, entry.Finding, null, exception.Message))];
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
    /// <para>
    /// A place already routed only blocks a new finding at that place when the earlier routing
    /// already covers what the new one needs: either the earlier one itself met the fix bar (its
    /// own draft bug task exists, and nothing about a repeat's own grade changes that), or the new
    /// one does not meet the fix bar either (a Low following a Low the sweep already folded).
    /// Without that condition, whichever grade a place's first report happened to carry — Low from
    /// one lens, Medium from the other, in this cycle or an earlier one — would silently decide
    /// the place's destination for good: a Low reported first would fold to the sweep and block a
    /// later Medium at the identical place from ever earning its own draft, exactly the "buried in
    /// a polish pile" outcome <see cref="RouteFindingsAsync"/>'s own contract rules out
    /// (adversarial review, cycle 4). Processing this cycle's own findings highest-grade-first
    /// closes the same gap when both lenses disagree on one place in the same cycle, so which one
    /// happened to iterate first cannot decide it either.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<(ReviewLens Lens, ReviewFinding Finding)> Pending, IReadOnlyList<RoutedFinding> Repeats)
        SplitAlreadyRouted(RunAggregate run, int cycle, IReadOnlyList<ReviewTrackPlan> plans)
    {
        List<(string Location, int Cycle, ReviewSeverity Severity)> routedLocations = [.. run.ReviewResiduals
            .Where(residual => residual.Disposition == ReviewResidualDisposition.Routed
                && residual.Location.IsNotBlank())
            .Select(residual => (residual.Location, residual.Cycle, residual.Severity))];

        List<(ReviewLens Lens, ReviewFinding Finding)> pending = [];
        List<RoutedFinding> repeats = [];
        // Reference identity, not SamePlace: SplitForTrack hands the identical ReviewFinding
        // instance to every active track's plan when a Verify pass's finding names no track= tag
        // (task: review cycles after the first), so the SAME object can appear in more than one
        // plan.Route here. SamePlace cannot catch that for an unplaced or lineless finding — it
        // deliberately treats two blank locations as different defects rather than risk collapsing
        // two genuinely different ones — so the one reviewer statement behind it would otherwise
        // become two draft bug tasks. This check is narrower and answers first: it is not about
        // whether two DIFFERENT findings share a place (still legitimate agreement, handled by
        // SamePlace below exactly as before), only about not queuing the exact same finding twice.
        HashSet<ReviewFinding> seenThisCycle = new(ReferenceEqualityComparer.Instance);
        List<(ReviewLens Lens, ReviewFinding Finding)> thisCycle = [];
        foreach (ReviewTrackPlan plan in plans)
        {
            foreach (ReviewFinding finding in plan.Route)
            {
                if (seenThisCycle.Add(finding))
                {
                    thisCycle.Add((plan.Lens, finding));
                }
            }
        }

        foreach ((ReviewLens Lens, ReviewFinding Finding) entry in
            thisCycle.OrderByDescending(entry => entry.Finding.Severity.MeetsFixBar))
        {
            (ReviewLens lens, ReviewFinding finding) = entry;

            // Both tracks can report the same pre-existing line in one cycle, which the
            // fix prompt already calls agreement rather than two defects; the same list
            // therefore grows as this cycle routes, not only across cycles.
            //
            // A place can carry more than one prior routing (a swept Low from an earlier
            // cycle alongside a drafted Medium from a later one), so the strongest match
            // decides, never the earliest: picking the earliest would let a place's very
            // first report — Low or Medium, whichever landed first — silently gate every
            // later report at that place for good, including a Medium arriving after that
            // Medium was already drafted, which would mint a fresh duplicate draft every
            // cycle instead of recognizing it as already routed.
            (int Cycle, ReviewSeverity Severity)? alreadyRoutedIn = routedLocations
                .Where(routed => ReviewFindingLocations.SamePlace(routed.Location, finding.Location))
                .OrderByDescending(routed => routed.Severity.MeetsFixBar)
                .Select(routed => ((int Cycle, ReviewSeverity Severity)?)(routed.Cycle, routed.Severity))
                .FirstOrDefault();
            if (alreadyRoutedIn is { } earlier
                && (earlier.Severity.MeetsFixBar || !finding.Severity.MeetsFixBar))
            {
                repeats.Add(new RoutedFinding(
                    lens, finding, null, null, AlreadyRoutedInCycle: earlier.Cycle, IsSweep: !earlier.Severity.MeetsFixBar));
                continue;
            }

            if (finding.Location.IsNotBlank())
            {
                routedLocations.Add((finding.Location, cycle, finding.Severity));
            }

            pending.Add((lens, finding));
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
    /// <para>
    /// A track still saying "continue" here can be carrying ride-alongs from the cycle that
    /// capped it (adversarial cycle-2 review finding): unlike the empty terminal case above, this
    /// track's own last completed pass may have attached a RideAlong-dispositioned finding that
    /// no fix session ever swept up, because none dispatches once the run settles instead of
    /// running another cycle. <see cref="RunAggregate.CompletedReviewPasses"/> still holds that
    /// pass (a merge-ready park resolution never advances <see cref="RunAggregate.ReviewCycle"/>,
    /// so nothing has cleared it), and reading it here is the only chance this finding gets to
    /// become a residual: there is no later cycle for a fix session to read it in, and force-
    /// concluding with an empty residual list would drop it from the tally as if it had never
    /// been reported.
    /// </para>
    /// <para>
    /// Two things about that forced residual mirror rules the rest of this method already obeys
    /// (cycle-3 cap-park finding). First, it collapses per distinct location exactly as
    /// <see cref="RunAggregate.DeriveResidualTally"/>'s own <c>PerDefect</c> does — within one
    /// lens's own findings, across every still-active lens forced-concluding together, and
    /// against whatever this same disposition already holds on the stream from an earlier,
    /// normally-concluded track — so <see cref="ReviewSettled.ResidualsRideAlong"/> (or
    /// <see cref="ReviewSettled.ResidualsFixed"/>, see next) always equals what a fresh
    /// <c>DeriveResidualTally</c> returns once these very events are rehydrated. Second, it is
    /// not always a ride-along: this cycle's merged findings document (<c>WriteMergedFindingsAsync</c>)
    /// writes every active lens's ride-alongs into the SAME file a dispatched fix session reads —
    /// but only when that round dispatches over the file at all.
    /// <see cref="DispatchFixSessionAsync"/> skips it entirely for a round that consumes
    /// <see cref="RunAggregate.PendingHumanFindings"/> instead (a dispute-resolution round: the
    /// human ran <c>h9k review resolve --needs-fixes</c>, that fix session disputed, and a human
    /// then ended the loop with merge-ready), so that round never saw this cycle's ride-alongs no
    /// matter which cycle it ran on. <see cref="RunAggregate.LastFixRoundHumanFindings"/> records
    /// which kind this cycle's round was, so a track still-active here that a fix session actually
    /// ran over this exact cycle counts as having handed this finding to that session — exactly as
    /// <c>RecordReviewPassAsync</c>'s own <c>fixSessionWillDispatch</c> distinction records for a
    /// normally-concluding track, so it ships as fixed-unreviewed rather than as a ride-along
    /// nobody read — only when that round's <c>LastFixRoundHumanFindings</c> is blank. A track
    /// force-concluded with no fix session ever dispatched this cycle (the capped-park path), or
    /// whose only fix session this cycle ran over a human's findings text rather than the merged
    /// document, never handed this finding to anyone, so it stays a ride-along.
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
        // The ride-alongs a still-active track is force-concluding with here are the one
        // exception — counted separately below, because they are not yet on the stream this
        // tally reads from.
        ReviewSettlement settlement = run.DeriveSettlement();
        // Counted per defect rather than per recorded residual (log #63): a routing that failed
        // is offered again next cycle, so one defect can leave both records on the stream, and
        // counting the records would report a defect as unrouted when a draft bug task exists.
        ReviewResidualTally residuals = run.DeriveResidualTally();

        // A fix session dispatches over the exact cycle it is dispatched at (DispatchFixSessionAsync
        // reads run.ReviewCycle at spawn time), so the cycle equality alone is "did a fix session
        // run over THIS cycle" — but DispatchFixSessionAsync only reads this cycle's merged
        // findings document (the one place a RideAlong-dispositioned finding is written) when
        // that round had no human findings to dispatch over; a dispute-resolution round
        // (PendingHumanFindings non-blank, e.g. `h9k review resolve --needs-fixes`) dispatches
        // over the human's reason alone and never opens that file. LastFixRoundHumanFindings
        // records which case this cycle's round was, so checking it here is the same question
        // fixSessionWillDispatch answers for a normally-concluding track, asked in terms the
        // aggregate alone can answer.
        ReviewResidualDisposition forcedDisposition =
            run.LastFixRoundCycle == run.ReviewCycle && run.LastFixRoundHumanFindings.IsBlank()
                ? ReviewResidualDisposition.FixedUnreviewed
                : ReviewResidualDisposition.RideAlong;
        IReadOnlyList<ReviewResidual> alreadyOnStream =
            [.. run.ReviewResiduals.Where(residual => residual.Disposition == forcedDisposition)];

        List<(ReviewLens Lens, ReviewResidual Residual)> forced = [];
        // Reference identity, not SamePlace: a Verify pass's own Findings list is the same
        // instance every time it is reached below, once per active lens it covers (task: review
        // cycles after the first). SamePlace cannot stand in for this — it deliberately treats two
        // blank or lineless locations as different defects (RouteFindingsAsync's own doc says
        // why) — so an unplaced ride-along the reviewer stated exactly once would otherwise be
        // forced into a residual once per lens covering the pass. This dedup answers a narrower
        // question than SamePlace's — "is this literally the same finding I already attributed a
        // few iterations ago" — so it is checked first and independently of it.
        HashSet<ReviewFindingRecord> attributed = new(ReferenceEqualityComparer.Instance);
        foreach (ReviewLens lens in run.ActiveReviewLenses)
        {
            // Covers, not ==: a Verify pass is recorded under the pseudo-lens ReviewLens.Verify,
            // which answers for every still-active track that cycle (task: review cycles after the
            // first) — the same widening every other lens comparison here already needed once a
            // pass could stand in for more than the one lens it was recorded under. The same-place
            // dedup a few lines below is what stops two DIFFERENT passes' findings at the same
            // place from being forced twice; the reference dedup just above is what stops this
            // SAME pass's own finding from being forced twice when two active lenses both cover it.
            foreach (ReviewFindingRecord finding in run.CompletedReviewPasses
                .Where(pass => pass.Lens.Covers(lens))
                .SelectMany(pass => pass.Findings)
                .Where(finding => finding.Disposition == ReviewFindingDisposition.RideAlong))
            {
                if (!attributed.Add(finding))
                {
                    continue;
                }

                // Attribute to the track the reviewer's own tag names, when that track is still
                // active on this run; fall back to iteration order — the lens whose pass covers
                // this finding first — only when the finding is untagged or names a track that is
                // no longer active (cycle-3 finding: this used to always credit whichever active
                // lens the outer loop reached first, regardless of what the finding's own track=
                // tag actually said).
                ReviewLens attributedLens = finding.Track is { } track && run.ActiveReviewLenses.Contains(track)
                    ? track
                    : lens;

                if (alreadyOnStream.Any(existing => ReviewFindingLocations.SamePlace(existing.Location, finding.Location))
                    || forced.Any(kept => ReviewFindingLocations.SamePlace(kept.Residual.Location, finding.Location)))
                {
                    continue;
                }

                forced.Add((attributedLens, new ReviewResidual(
                    attributedLens, run.ReviewCycle, finding.Severity, finding.Scope, forcedDisposition, finding.Location)));
            }
        }

        string forcedSuffix = forcedDisposition == ReviewResidualDisposition.FixedUnreviewed
            ? "claimed by a fix session dispatched this same cycle"
            : "never claimed";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        foreach (ReviewLens lens in run.ActiveReviewLenses)
        {
            IReadOnlyList<ReviewResidual> forcedResiduals = [.. forced
                .Where(entry => entry.Lens == lens)
                .Select(entry => entry.Residual)];

            session.Events.Append(run.Id, new ReviewTrackConcluded(
                run.Id, lens, run.ReviewCycle, ReviewSettlement.Settled, forcedResiduals, now));
            logger.LogInformation(
                "Run {RunId}: the {Lens} track ended at cycle {Cycle} with the run — settled, no reviewer read the final tip{Forced}",
                run.Id, LensLabel(lens), run.ReviewCycle,
                forcedResiduals.Count > 0 ? $", {forcedResiduals.Count} ride-along(s) {forcedSuffix}" : string.Empty);
        }

        int forcedFixedUnreviewed = forcedDisposition == ReviewResidualDisposition.FixedUnreviewed ? forced.Count : 0;
        int forcedRideAlong = forcedDisposition == ReviewResidualDisposition.RideAlong ? forced.Count : 0;
        session.Events.Append(run.Id, new ReviewSettled(
            run.Id, run.ReviewCycle, settlement,
            residuals.FixedUnreviewed + forcedFixedUnreviewed, residuals.Routed, residuals.RoutingFailed,
            now, residuals.RideAlong + forcedRideAlong));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId}: review settled {Settlement} at cycle {Cycle} — {Fixed} residual(s) fixed unreviewed, {Routed} routed, {Unrouted} left unrouted, {RideAlong} ride-along(s) never claimed",
            run.Id, settlement.Value, run.ReviewCycle,
            residuals.FixedUnreviewed + forcedFixedUnreviewed, residuals.Routed, residuals.RoutingFailed,
            residuals.RideAlong + forcedRideAlong);
    }

    private async Task RecordFixResultAsync(
        Guid runId, string runDirectory, int cycle, FollowUpKind followUpKind, AgentResult result,
        CancellationToken cancellationToken)
    {
        string summary = result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(RunPaths.ReviewFixPositionFile(runDirectory, cycle), summary, cancellationToken);

        ReviewFixOutcome outcome = ReviewResultParser.ParseFixOutcome(summary);
        if (outcome == ReviewFixOutcome.Disputed && cycle == 0)
        {
            // A resumed pre-gate dispute disputing again (backlog 44's own rebase prompt invites
            // exactly this: "raise a new dispute if you hit a DIFFERENT conflict"; the review-thread
            // prompt invites the same for a thread neither side can honestly judge). No review pass
            // has run at cycle 0, so this new summary is another position under the same well-known
            // name the first park used, so a human dealing with a cycle-0 dispute always finds every
            // attempt at the one path — appended, per RunPaths.AppendDisputePositionAsync, so the
            // earlier attempt's position survives instead of being overwritten by this one.
            string disputeFile = followUpKind == FollowUpKind.Rebase
                ? RunPaths.RebaseConflictDisputeFile(runDirectory)
                : RunPaths.ReviewThreadDisputeFile(runDirectory);
            Exception? failure = await RunPaths.AppendDisputePositionAsync(disputeFile, summary, cancellationToken);
            if (failure is not null)
            {
                logger.LogWarning(failure, "Could not write the dispute position to {FilePath}", disputeFile);
            }
        }

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
    /// <para>
    /// The intro sentence is <paramref name="mode"/>-aware (cycle-5 conformance finding): a
    /// <see cref="ReviewMode.Verify"/> cycle is one reviewer reading a delta, not independent
    /// per-lens full-diff passes, and this document is what the next cycle's prompt and a
    /// human reading a park both quote verbatim, so it cannot claim more than what happened.
    /// </para>
    /// </summary>
    private static async Task WriteMergedFindingsAsync(
        string runDirectory, int cycle, ReviewMode mode, IReadOnlyList<ReviewPassResult> passes,
        IReadOnlyList<ReviewTrackPlan> plans, IReadOnlyList<RoutedFinding> routed, bool fixSessionWillDispatch,
        CancellationToken cancellationToken)
    {
        StringBuilder merged = new();
        merged.AppendLine($"# Independent pre-PR review — cycle {cycle}");
        merged.AppendLine();
        if (mode == ReviewMode.Verify)
        {
            merged.AppendLine("This cycle dispatched one reviewer standing in for every still-active track,");
            merged.AppendLine("reading the delta since the prior cycle rather than the whole diff. A finding");
            merged.AppendLine("belongs to whichever track its own tag names, not to the section heading below.");
        }
        else
        {
            merged.AppendLine("Each section below is one independent pass over the same diff, with its own fresh");
            merged.AppendLine("context. A finding belongs to the lens whose section it appears under.");
        }
        foreach (ReviewPassResult pass in passes)
        {
            string path = LensFindingsFile(runDirectory, cycle, pass.Lens);
            string text = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken)
                : string.Empty;
            merged.AppendLine();
            merged.AppendLine($"## {LensHeading(pass.Lens)} — verdict: {VerdictLabel(pass.Verdict, text)}");
            merged.AppendLine();
            merged.AppendLine(text.IsBlank() ? "(this pass recorded no output)" : text.Trim());
        }

        AppendDispositions(merged, cycle, plans, routed, fixSessionWillDispatch);
        await File.WriteAllTextAsync(
            RunPaths.ReviewFindingsFile(runDirectory, cycle), merged.ToString(), cancellationToken);
    }

    /// <summary>
    /// What the platform decided about each finding, written by machinery beneath the
    /// reviewers' own words (log #63). Severity and scope are the reviewer's observations; this
    /// section is the decision over them, and stating it here is what keeps the fix session
    /// from having to re-derive a policy it does not know.
    /// </summary>
    private static void AppendDispositions(
        StringBuilder merged, int cycle, IReadOnlyList<ReviewTrackPlan> plans, IReadOnlyList<RoutedFinding> routed,
        bool fixSessionWillDispatch)
    {
        List<(ReviewLens Lens, ReviewFinding Finding)> here =
            [.. Deduplicated(plans.SelectMany(plan => plan.Fix.Select(finding => (plan.Lens, Finding: finding))))];
        List<(ReviewLens Lens, ReviewFinding Finding)> rideAlong =
            [.. Deduplicated(plans.SelectMany(plan => plan.RideAlong.Select(finding => (plan.Lens, Finding: finding))))];
        if (here.Count == 0 && routed.Count == 0 && rideAlong.Count == 0)
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
        string rideAlongNote = fixSessionWillDispatch
            ? "Below the fix bar (Decisions Log #87) on their own, so no cycle was spent earning these a "
                + "fix session of their own. A fix session is already dispatching this cycle for other "
                + "findings — fix these here too, with the same care as the rest: the platform records a "
                + "ride-along as fixed the moment a fix session dispatches, whether or not it is acted on, "
                + "so skipping one makes that record false."
            : "Below the fix bar (Decisions Log #87) on their own, so no cycle was spent earning these a "
                + "fix session of their own, and no fix session is dispatching this cycle for anything "
                + "else either — the platform records these as unfixed residuals (Decisions Log #63), "
                + "left for a human or a later review pass rather than acted on here.";
        AppendDispositionGroup(merged, ReviewFindingDispositions.RideAlong, rideAlongNote, rideAlong);

        if (routed.Count == 0)
        {
            return;
        }

        merged.AppendLine();
        merged.AppendLine($"### {ReviewFindingDispositions.DoNotFixHere}");
        merged.AppendLine();
        merged.AppendLine("These are pre-existing defects outside this branch's work. Each one is now a draft bug");
        merged.AppendLine("task of its own, or folded into the project's standing sweep draft, waiting for a");
        merged.AppendLine("human; fixing them here would grow this diff with unrelated changes.");
        foreach (RoutedFinding entry in routed)
        {
            // The cycle that exported it is stated because it is the one that was observed:
            // both tracks report the same pre-existing line in the cycle they share, so a
            // repeat is as often this cycle's own routing as an earlier cycle's.
            string destination = entry switch
            {
                { AlreadyRoutedInCycle: { } earlier, IsSweep: true } when earlier == cycle =>
                    "already folded into the standing sweep draft earlier in this cycle",
                { AlreadyRoutedInCycle: { } earlier, IsSweep: true } =>
                    $"already folded into the standing sweep draft by cycle {earlier} of this run",
                { AlreadyRoutedInCycle: { } earlier } when earlier == cycle =>
                    "already routed to a draft bug task earlier in this cycle",
                { AlreadyRoutedInCycle: { } earlier } =>
                    $"already routed to a draft bug task by cycle {earlier} of this run",
                { DraftTaskId: { } draftTaskId, IsSweep: true } => $"folded into the standing sweep draft {draftTaskId}",
                { DraftTaskId: { } draftTaskId } => $"draft task {draftTaskId}",
                _ => $"NOT routed — creating the draft failed ({entry.FailureReason})",
            };
            merged.AppendLine($"- {FindingLabel(entry.Lens, entry.Finding)} → {destination}");
        }
    }

    /// <summary>
    /// Reference identity, not SamePlace: a Verify pass hands the identical <see cref="ReviewFinding"/>
    /// instance to every active track's plan when a finding is untagged (<c>SplitForTrack</c>'s own
    /// doc), so a shared instance landing in two tracks' <c>Fix</c> or <c>RideAlong</c> lists is one
    /// reviewer statement, not two — the same reasoning <c>RecordReviewPassAsync</c>'s own
    /// <c>rideAlongAttributed</c> set already applies to residuals, and <c>RouteFindingsAsync</c>'s
    /// own <c>SplitAlreadyRouted</c> applies to routing (cycle-5 adversarial finding). Order is
    /// preserved, so the earlier track in <paramref name="entries"/> wins the shared statement's
    /// byline.
    /// </summary>
    private static IEnumerable<(ReviewLens Lens, ReviewFinding Finding)> Deduplicated(
        IEnumerable<(ReviewLens Lens, ReviewFinding Finding)> entries)
    {
        HashSet<ReviewFinding> seen = new(ReferenceEqualityComparer.Instance);
        foreach ((ReviewLens Lens, ReviewFinding Finding) entry in entries)
        {
            if (seen.Add(entry.Finding))
            {
                yield return entry;
            }
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
            // next poll cycle's ResumeStrandedPipelinesAsync stumbled onto it (Copilot
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
    /// The review loop's side of budget-exhaustion recovery (backlog 40): a review pass or the
    /// fix session died on the same recognizable usage-limit shape <c>RunSupervisor</c> already
    /// catches for the primary session. The task stays Claimed and the lease is retained —
    /// this is a park, not a failure — and <c>RunAggregate.Apply(RunBudgetExhausted)</c> clears
    /// whichever leg was in flight so the retry sweep redispatches it fresh once the window
    /// resets, instead of trying to resume a process that already exited.
    /// </summary>
    private async Task ParkForBudgetAsync(
        Guid runId, string source, string observedMessage, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunBudgetExhausted(runId, observedMessage, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Run {RunId}: token budget exhausted — {Source} parked rather than failed; the daemon retries hourly. {Message}",
            runId, source, observedMessage);
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
    /// <c>ResumeStrandedPipelinesAsync</c> or the next startup adoption sweep would relaunch it
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

        IReadOnlyList<ReviewParkResolution> priorRulings = await LoadPriorRulingsAsync(query, taskId, cancellationToken);
        return new ReviewContext(runId, taskId, run, task, project, priorRulings);
    }

    /// <summary>
    /// Every human verdict this TASK's review park has ever taken, oldest first, across every run
    /// it has had (a retry starts a fresh run stream, so a single run's own history is not
    /// enough) — handed to a fresh review pass so it does not re-raise a question a human already
    /// settled (task: review prompts carry prior rulings). Queried by <c>TaskId</c> off the
    /// <see cref="RunDetails"/> document the way <c>BlockerHandoffQuery.ClosedOutRunsAsync</c>
    /// already reads a task's run history, rather than looping <c>FetchStreamAsync</c> per run id.
    /// </summary>
    private static async Task<IReadOnlyList<ReviewParkResolution>> LoadPriorRulingsAsync(
        IQuerySession query, Guid taskId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> taskRuns = await query.Query<RunDetails>()
            .Where(run => run.TaskId == taskId)
            .ToListAsync(cancellationToken);

        return [.. taskRuns
            .SelectMany(run => run.ReviewParkResolutions)
            .OrderBy(ruling => ruling.ResolvedAt)];
    }

    private async Task<RunAggregate> LoadRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        return await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cancellationToken)
            ?? throw new InvalidOperationException($"Run stream {runId} not found.");
    }

    /// <summary>
    /// Whether "nothing left to review" may actually settle the run without dispatching one more
    /// fresh-context review pass over it (task: review cycles after the first). Both
    /// <see cref="ReviewMode.Discovery"/> and <see cref="ReviewMode.FinalFullPass"/> qualify — each
    /// is a full, fresh-context read of the tip it concluded on — so a run that converges clean at
    /// cycle 1 with nothing ever needing a fix pays no extra pass at all: cycle 1's own two-lens
    /// read already is the fresh look immediately before the pull request opens. Only
    /// <see cref="ReviewMode.Verify"/> fails to qualify, because it never re-derives the whole diff,
    /// which is exactly the gap the mandatory final pass exists to close. A human's own merge-ready
    /// park resolution is exempt from this review half outright: a human overruling the automatic
    /// loop already looked, or deliberately chose not to, and dispatching another agent pass over
    /// their explicit verdict would be presumptuous rather than thorough.
    /// <para>
    /// <b>This method decides only whether another review pass is owed.</b> Whether the test gate
    /// itself must still run at full scope before settling is <see cref="NeedsFullGateBeforeSettling"/>'s
    /// separate question, and the human exemption does not carry over there (independent pre-PR
    /// review, cycle 1 — a human resolving a park that followed a scoped Verify gate previously
    /// reached <c>SettleAsync</c> straight from here having never run the full suite over the fix's
    /// commits): a human's merge-ready excuses the next reviewer's fresh-context read, never the
    /// suite actually running at full scope, so it must never let a tip last gated scoped reach the
    /// remote unread by the full test gate.
    /// </para>
    /// <para>
    /// The mode check alone is not enough (independent pre-PR review, cycle 2, conformance
    /// finding): a cycle's <see cref="RunAggregate.CurrentCycleMode"/> does not change until the
    /// NEXT cycle's own dispatch, so it still reads <see cref="ReviewMode.FinalFullPass"/> even
    /// after a fix session ran on top of that same cycle's own findings (the empty terminal case —
    /// every track already <c>Continues: false</c>, but a Fix finding still owed a session). Ending
    /// there would settle the run over commits that mandatory pass never read, which is exactly the
    /// "nothing reaches the remote on delta-green alone" promise this method exists to keep — so a
    /// fix dispatched on the current tracked cycle (<see cref="RunAggregate.FixDispatchedThisCycle"/>)
    /// also blocks settling, sending the caller back to dispatch one more fresh-context pass over
    /// the fix it just applied.
    /// </para>
    /// <para>
    /// <see cref="SettleReason.Bar"/> (task: a final full pass whose verdict is merge-ready and
    /// whose findings are all below the fix bar counts as a clean settle) names a case this method
    /// already accepted before it had a name of its own: a <see cref="ReviewMode.FinalFullPass"/>
    /// cycle whose merged verdict came back <see cref="ReviewVerdict.MergeReady"/> can only do so
    /// when every finding either lens attached is <see cref="ReviewFindingDisposition.RideAlong"/>
    /// (<c>ReviewEngine.RecordReviewPassAsync</c>'s own reclassification guarantees it), which means
    /// no track was ever left <c>Continues: true</c> for a genuine defect and no fix session ever
    /// dispatched — so the third clause below (nothing owed) was already true for it. Stating it as
    /// its own named clause, checked first, is what lets a track a stray below-bar finding
    /// reawakened on its way to that same conclusion settle on the strength of the severity bar
    /// (Decisions Log #87) rather than by accident of an unrelated flag happening to agree, and what
    /// gives the settle log (<c>LogSettleReason</c>) a name to print: an operator reading it can tell
    /// "the bar closed this out" from "a reviewer read the tip and found nothing at all" instead of
    /// inferring it from the residual tally. Origin (2026-08-29): runs 514ffa6c and 430decdb parked
    /// at <see cref="FinalFullPassCapReached"/>, and the operator dismissed both in seconds with no
    /// changes because the tip each left behind read as fully below the fix bar. This clause does
    /// not itself change which runs reach that park, though (independent pre-PR review, cycles 1
    /// and 2, conformance lens), and the reason is the ordering rather than anything about those
    /// two runs: <see cref="FinalFullPassCapReached"/> is only ever consulted once this method has
    /// already returned null, so on every route that reaches the cap <c>Bar</c> was false by
    /// construction, and every state <c>Bar</c> does match was already matched by <c>NothingOwed</c>
    /// on <c>main</c>. What changes here is legibility, not reachability: the settle log can now say
    /// the severity bar closed a below-bar-only final pass out by name instead of an operator
    /// inferring it from the residual tally. Why runs 514ffa6c and 430decdb reached the cap at all
    /// is a separate, still-open question this task does not address, and this note rules no route
    /// out: a run can arrive at that check with <c>FixDispatchedThisCycle</c> false and
    /// <c>LastReviewVerdict</c> already <see cref="ReviewVerdict.MergeReady"/>, straight off a lone
    /// <see cref="ReviewMode.Verify"/> cycle that concluded its last active track, because the
    /// <c>CurrentCycleMode</c> conjunct <c>Bar</c> and <c>NothingOwed</c> share excludes a
    /// <c>Verify</c> tip on its own.
    /// </para>
    /// </summary>
    private static SettleReason? MaySettleReason(RunAggregate run) => true switch
    {
        _ when run.HumanEndedTheLoop => SettleReason.Human,
        _ when run.CurrentCycleMode == ReviewMode.FinalFullPass
            && run.LastReviewVerdict == ReviewVerdict.MergeReady
            && run.CompletedReviewPasses.Any(pass => pass.Findings.Count > 0) =>
            SettleReason.Bar,
        _ when run.CurrentCycleMode != ReviewMode.Verify && !run.FixDispatchedThisCycle => SettleReason.NothingOwed,
        _ => null,
    };

    /// <summary>Which of <see cref="MaySettleReason"/>'s clauses is about to end the review loop.</summary>
    private enum SettleReason
    {
        /// <summary>A human's own <c>h9k review resolve --merge-ready</c> ended the loop.</summary>
        Human,

        /// <summary>
        /// A mandatory <see cref="ReviewMode.FinalFullPass"/> concluded merge-ready with every
        /// finding below the fix bar (Decisions Log #87) — settled by the severity bar's own
        /// definition of done, not by a reviewer confirming a tip with nothing on it at all.
        /// </summary>
        Bar,

        /// <summary>
        /// Nothing this cycle is owed a fix session and the cycle was a full, fresh-context read
        /// (<see cref="ReviewMode.Discovery"/> or <see cref="ReviewMode.FinalFullPass"/>) — a
        /// reviewer read the tip and found nothing that needed doing.
        /// </summary>
        NothingOwed,
    }

    /// <summary>
    /// The daemon log line for a settle (task: a final full pass whose verdict is merge-ready and
    /// whose findings are all below the fix bar counts as a clean settle) — named separately from
    /// <see cref="SettleAsync"/>'s own log line, which reports what the settlement tally found, not
    /// which of <see cref="MaySettleReason"/>'s clauses is what let the loop stop looking.
    /// </summary>
    private void LogSettleReason(RunAggregate run, SettleReason reason)
    {
        string why = reason switch
        {
            SettleReason.Human => "a human's merge-ready resolution ended the loop",
            SettleReason.Bar =>
                "the mandatory final full pass concluded merge-ready with every finding below the " +
                "fix bar (Decisions Log #87) — a bar settle, not a reviewer confirming a fully clean " +
                "tip; its findings are recorded as residuals",
            SettleReason.NothingOwed => "nothing this cycle is owed a fix session",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unrecognized settle reason."),
        };
        logger.LogInformation("Run {RunId}: settling at cycle {Cycle} — {Reason}", run.Id, run.ReviewCycle, why);
    }

    /// <summary>
    /// Whether the Settling branch must run one more full-scope gate pass before the run may
    /// settle (task: a fix cycle's verification gate) — <see cref="MaySettleReason"/>'s own non-human
    /// condition, deliberately without its human exemption: a human's merge-ready resolution
    /// excuses another reviewer's fresh-context read, never the suite actually running at full
    /// scope over the fix's own commits (independent pre-PR review, cycle 1). This mode/fix-dispatch
    /// check alone is not sufficient to decide branch entry when a human resolved the park, though
    /// (independent pre-PR review, cycle 1, adversarial lens): it says nothing about a tip that
    /// moved without ever setting <see cref="RunAggregate.FixDispatchedThisCycle"/> — a
    /// <see cref="ReviewMode.Discovery"/>-mode park resolved by a same-session worktree commit
    /// followed by a bare merge-ready resolve, for instance. The call site's own extra OR clause
    /// (<see cref="RunAggregate.HumanEndedTheLoop"/> AND the negation of
    /// <see cref="GateAlreadyRanFullOverCurrentHeadAsync"/>) closes that, but deliberately only for
    /// a human-resolved park: <see cref="RunAggregate.DeriveReviewPhase"/>'s own automatic route to
    /// Settling — every pass in the cycle already concluded clean — is never reached with HEAD
    /// having moved since that cycle's own gate, since nothing commits to the worktree between a
    /// cycle's gate and its passes landing, so widening the extra check to every Settling entry
    /// would only pay for a redundant gate and a whole extra <see cref="ReviewMode.FinalFullPass"/>
    /// dispatch on every clean, human-free settle. Whether the gate pass inside the branch can then
    /// be skipped because one already ran is <see cref="GateAlreadyRanFullOverCurrentHeadAsync"/>'s
    /// question again — this method only ever decides its own half of whether the branch is
    /// entered, never that the gate call inside it is redundant.
    /// </summary>
    private static bool NeedsFullGateBeforeSettling(RunAggregate run) =>
        run.CurrentCycleMode == ReviewMode.Verify || run.FixDispatchedThisCycle;

    /// <summary>
    /// Whether the project's CURRENT <see cref="VerifyCommand.Fingerprint"/> still matches
    /// <see cref="RunAggregate.LastGateVerifyCommandsFingerprint"/> — a pure store read, deliberately
    /// split out of <see cref="GateAlreadyRanFullOverCurrentHeadAsync"/> so the call site can consult
    /// it on every Settling entry without paying that method's git call too. Read fresh here rather
    /// than off <see cref="ReviewContext.Project"/>: that snapshot is loaded once at the very top of
    /// <see cref="DriveAsync"/>, before this run's own review passes and fix sessions — which can
    /// each run for real wall-clock minutes to hours — ever dispatch, so a setting changed anywhere
    /// in this run's lifetime would otherwise never be seen by this check. A null
    /// <see cref="RunAggregate.LastGateVerifyCommandsFingerprint"/> — a stream written before this
    /// field existed — reads as a match rather than a mismatch (independent pre-PR review, cycle 3,
    /// adversarial lens): the fingerprint was never observed, not observed-and-different, and
    /// treating an unobserved field as an observed change is exactly the guess AGENTS.md's "never
    /// guess at unobserved facts" rule forbids. Both callers rely on this: it is what keeps a
    /// never-recorded fingerprint from forcing a redundant Settling gate on its own, and what lets
    /// <see cref="GateAlreadyRanFullOverCurrentHeadAsync"/>'s skip fire on such a stream instead of
    /// being permanently denied by a comparison that could never succeed.
    /// </summary>
    private async Task<bool> VerifyCommandsFingerprintMatchesAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        if (run.LastGateVerifyCommandsFingerprint is null)
        {
            return true;
        }

        await using IQuerySession query = store.QuerySession();
        ProjectDetails? project = await query.LoadAsync<ProjectDetails>(context.Project.Id, cancellationToken);
        return project is not null
            && run.LastGateVerifyCommandsFingerprint == VerifyCommand.Fingerprint(project.VerifyCommands);
    }

    /// <summary>
    /// Whether the run's own most recently recorded gate pass already covered the worktree's
    /// current tip at full scope (cycle-3 finding), so the Settling branch's own mandatory full
    /// pass would be re-running the identical suite over the identical commits. True only when
    /// <see cref="RunAggregate.LastGateRanFullScope"/> is set, a fresh read of the worktree's HEAD
    /// matches <see cref="RunAggregate.LastGateHeadSha"/> exactly, AND the project's CURRENT
    /// <see cref="VerifyCommand.Fingerprint"/> — via <see cref="VerifyCommandsFingerprintMatchesAsync"/>
    /// — matches <see cref="RunAggregate.LastGateVerifyCommandsFingerprint"/>: a scoped preceding
    /// gate, an unread HEAD, a HEAD that moved (a fix landed more commits since), or a project
    /// whose verify commands changed since that gate ran (Copilot review, PR #62 — a human editing
    /// verify settings mid-run while HEAD stays put would otherwise skip the gate the new settings
    /// have never actually run) all fall through to false, which is what keeps the unconditional
    /// full gate in every one of those cases (task: a fix cycle's verification gate). The common
    /// case this actually fires for is a "scoped" <see cref="ReviewMode.Verify"/> reverify whose
    /// own gate fell back to full because the fix's commits touched something
    /// <see cref="TestScopeResolver"/> cannot map (a doc file, most often) — the reverify already
    /// paid full price for this exact tip, so a second full pass here would buy nothing.
    /// </summary>
    private async Task<bool> GateAlreadyRanFullOverCurrentHeadAsync(
        ReviewContext context, RunAggregate run, CancellationToken cancellationToken)
    {
        if (!run.LastGateRanFullScope || run.LastGateHeadSha is null)
        {
            return false;
        }

        if (!await VerifyCommandsFingerprintMatchesAsync(context, run, cancellationToken))
        {
            return false;
        }

        string? currentHeadSha = await GetWorktreeHeadShaAsync(context.Run.WorktreePath, cancellationToken);
        return currentHeadSha is not null && currentHeadSha == run.LastGateHeadSha;
    }

    /// <summary>
    /// The worktree's current commit, best-effort (task: review cycles after the first) — what a
    /// later Verify cycle's prompt points its "commits since the prior cycle" instruction at. This
    /// is the only place the review loop itself touches git; every other read is delegated to the
    /// reviewer's own tool calls (<see cref="AgentPromptBuilder.AppendReviewMechanics"/>). Null on
    /// any failure — the daemon never guesses at an unobserved fact, and the Verify prompt falls
    /// back to a full-range diff instruction rather than pretending to know a boundary it does not.
    /// </summary>
    private static async Task<string?> GetWorktreeHeadShaAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = worktreePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("rev-parse");
            process.StartInfo.ArgumentList.Add("HEAD");
            process.Start();
            // Both streams started before the wait (GitWorktreeManager.TryRunGitAsync's own
            // pattern): stderr is never read below, but leaving it undrained lets git block on a
            // full pipe (a dubious-ownership advice block, a noisy hook) with WaitForExitAsync
            // never returning.
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            // Both awaited deterministically before the exit code decides anything, so neither
            // stream's task is ever left unobserved on the failure path.
            string output = await standardOutput;
            await standardError;
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string> ReadIfExistsAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;

    /// <summary>The first still-active track that has run as many cycles as it may, or null while every track has room.</summary>
    private ReviewLens? CappedTrack(RunAggregate run) =>
        run.ActiveReviewLenses.FirstOrDefault(lens =>
            ReviewTrackPolicy.CapReached(lens, run.ReviewCycle, run.TrackBudgetBaseCycle(lens), _options));

    /// <summary>
    /// Whether the mandatory <see cref="ReviewMode.FinalFullPass"/> has already run as many times
    /// as <see cref="DaemonOptions.MaxFinalFullPassRounds"/> allows (task: review cycles after the
    /// first, cycle-3 finding). This is deliberately independent of <see cref="CappedTrack"/>: a
    /// track the final pass keeps reawakening has its own budget base bumped by that very
    /// reactivation (<see cref="RunAggregate.TrackBudgetBaseCycle"/>'s own doc says why), so it can
    /// keep passing <see cref="ReviewTrackPolicy.CapReached"/> forever while the mandatory pass
    /// itself never stops re-running — this is the bound that catches that instead.
    /// </summary>
    private bool FinalFullPassCapReached(RunAggregate run) =>
        run.FinalFullPassRounds >= _options.MaxFinalFullPassRounds;

    /// <summary>
    /// Why hitting <see cref="FinalFullPassCapReached"/> parks the run: not a spent budget in the
    /// ordinary sense, but the mandatory full-rigor read that runs immediately before the run may
    /// settle has had to repeat itself this many times in a row — worth a human's look rather than
    /// another automatic round. The reawakened-track explanation is only ever stated when
    /// <see cref="RunAggregate.ReviewTrackReactivations"/> says one actually happened on this run
    /// (cycle-3 cap-park finding): the counter itself only counts how many consecutive mandatory
    /// full passes have run, which can climb without any track ever being reawakened (an ordinary,
    /// still-active track can keep the cycle going on its own), and the park text must never assert
    /// a reawakening nobody observed.
    /// </summary>
    private string FinalFullPassCapParkReason(RunAggregate run)
    {
        string findings = RunPaths.ReviewFindingsFile(ParkedRunDirectory(run), run.ReviewCycle);
        string why = run.ReviewTrackReactivations > 0
            ? "A track keeps being reawakened just as the loop is about to conclude, which either " +
              "means the fixes keep introducing new issues or the loop is oscillating"
            : "The mandatory pass keeps having to run again, whatever is keeping a track active this " +
              "many times in a row";
        return $"This run has dispatched the mandatory final full review pass — every lens, fresh " +
            $"context, immediately before the run may settle — {run.FinalFullPassRounds} consecutive " +
            $"time(s) without ever reaching a clean settle: its cap. {why}; either way it is worth a " +
            $"human's look rather than another automatic round. Unresolved findings: {findings}. Fix " +
            "in the worktree and resolve with h9k review resolve --merge-ready, grant a fresh round " +
            "with --needs-fixes, or abandon the task.";
    }

    /// <summary>
    /// Why a capped track parks, in that track's own terms. The two caps mean different things
    /// and the reason says which: conformance running out is "nothing automated is left to
    /// try", while adversarial running out is "the machine kept finding real problems, and
    /// somebody should look at why" — not a failure, and not a budget quietly spent.
    /// </summary>
    private string CapParkReason(RunAggregate run, ReviewLens capped)
    {
        string findings = RunPaths.ReviewFindingsFile(ParkedRunDirectory(run), run.ReviewCycle);
        string levers =
            $"Unresolved findings: {findings}. Fix in the worktree and resolve with " +
            "h9k review resolve --merge-ready, grant a fresh round with --needs-fixes, or abandon the task.";
        int cycles = run.ReviewCycle - run.TrackBudgetBaseCycle(capped);

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
    /// <para>
    /// <c>pass.Lens.Covers(Adversarial)</c> alone is not enough to say a finding is this track's:
    /// a <see cref="ReviewMode.Verify"/> pass's single reviewer covers both tracks, so its
    /// findings need the same per-finding attribution <c>SplitForTrack</c> and <c>SettleAsync</c>
    /// already apply — the finding's own <c>track=</c> tag when it names one, otherwise counted
    /// against Adversarial conservatively (independent pre-PR review, cycle 2: this used to credit
    /// a Verify pass's conformance-tagged findings to the adversarial track's cap-park reason).
    /// </para>
    /// </summary>
    private static string AdversarialCapReason(RunAggregate run, int cycles)
    {
        List<ReviewFindingRecord> owed = [.. run.CompletedReviewPasses
            .Where(pass => pass.Lens.Covers(ReviewLens.Adversarial))
            .SelectMany(pass => pass.Findings)
            .Where(finding => finding.Disposition == ReviewFindingDisposition.Fix)
            .Where(finding =>
                finding.Track is null
                || finding.Track == ReviewLens.Adversarial
                || !run.ActiveReviewLenses.Contains(finding.Track))];
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

    /// <summary>
    /// Where a run's files actually sit right now, when <c>RunDirectory</c> was recorded before
    /// a reopen's directory move (adversarial review, backlog 51 cycle 5): a run parked mid pre-PR
    /// review (a rebase or review-thread dispute) can outlive the render sweep moving its task's
    /// directory into or out of <c>tasks/_archive/</c> while nobody is actively touching it, so
    /// every read or write here must re-resolve rather than trust the value <c>RunDispatched</c>
    /// carried at dispatch.
    /// </summary>
    private static string CurrentRunDirectory(RunAggregate run) => RunPaths.ResolveCurrentDirectory(run.RunDirectory);

    /// <summary>
    /// The directory a park reason should name — not where the run's files sit right now
    /// (<see cref="CurrentRunDirectory(RunAggregate)"/>), but where the sweep puts them once the
    /// <c>ReviewParked</c> this text is being composed for lands (adversarial review, backlog 51
    /// cycle 10). A parked run is no longer live, so the reopen guard that can be holding a
    /// reopened task's directory inside <c>tasks/_archive/</c> only because its current run was
    /// still live stops applying the moment this park commits, and the very next sweep moves the
    /// directory back to <c>tasks/</c>. The common case — a task that was never reopened, and
    /// whose directory was never under <c>tasks/_archive/</c> to begin with — is untouched:
    /// <see cref="RunPaths.AnticipateDirectoryAfterSweep"/> only changes an already-archived path.
    /// </summary>
    private static string ParkedRunDirectory(RunAggregate run) =>
        RunPaths.AnticipateDirectoryAfterSweep(CurrentRunDirectory(run), willArchive: false);

    /// <summary>Same resolution as <see cref="CurrentRunDirectory(RunAggregate)"/>, for the projection shape.</summary>
    private static string CurrentRunDirectory(RunDetails run) => RunPaths.ResolveCurrentDirectory(run.RunDirectory);

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
    private static string LensFindingsFile(string runDirectory, int cycle, ReviewLens lens) =>
        RunPaths.ReviewLensFindingsFile(runDirectory, cycle, lens.Slug.IsBlank() ? UnlensedSlug : lens.Slug);

    private static string LensLabel(ReviewLens lens) =>
        lens.Slug.IsBlank() ? "review" : $"{lens.Slug} review";

    private static string LensHeading(ReviewLens lens) => lens switch
    {
        _ when lens == ReviewLens.Conformance =>
            "Conformance lens (the work against its objective, acceptance criteria, and repo doctrine)",
        _ when lens == ReviewLens.Adversarial =>
            "Adversarial lens (a defect hunt, told nothing about what the work was meant to do)",
        _ when lens == ReviewLens.Verify =>
            "Verify pass (one reviewer, standing in for every still-active track, verifying the prior cycle's fix)",
        _ => "Review pass (no lens recorded)",
    };

    /// <summary>
    /// The label a human reads beside a pass's raw output. An <see cref="ReviewVerdict.Unknown"/>
    /// pass is not one fact but three different ones (task filed 2026-08-25; the third added with
    /// the merge-ready demotion path): a needs-fixes verdict that named nothing the platform
    /// could read as a finding, a merge-ready verdict demoted because it attached a finding the
    /// platform could not read as a stated defect, or a pass that truly ended without a
    /// parseable verdict line at all — each still visible in the preserved text right below this
    /// heading. Reporting any of the first two as "(none stated)" contradicts the very text it
    /// introduces, exactly as <see cref="VerdictMissingCauseAsync"/> already distinguishes them.
    /// <para>
    /// A non-<c>Unknown</c> <paramref name="verdict"/> can equally disagree with the pass's own
    /// text (Decisions Log #87's reclassification in <c>RecordReviewPassAsync</c>): a needs-fixes
    /// pass whose findings are all RideAlong-dispositioned is demoted to merge-ready, and a
    /// merge-ready pass that attached a Fix or Route finding is promoted to needs-fixes. Left
    /// unlabeled, the heading this method writes would name the platform's reclassified verdict
    /// immediately above the pass's own text ending in its original, disagreeing <c>VERDICT:</c>
    /// line — the same contradiction the Unknown cases above exist to avoid, so it gets the same
    /// treatment: name the reclassification and why, rather than the reclassified verdict alone.
    /// </para>
    /// </summary>
    private static string VerdictLabel(ReviewVerdict verdict, string rawText) => verdict switch
    {
        _ when verdict != ReviewVerdict.Unknown && ReviewResultParser.ParseVerdict(rawText) is var stated
            && stated != ReviewVerdict.Unknown && stated != verdict =>
            $"{verdict.Value} (reclassified from {stated.Value}: "
                + (verdict == ReviewVerdict.MergeReady
                    ? "every attached finding was RideAlong-dispositioned, below the fix bar on its own"
                    : "it attached a finding beyond ride-alongs, which earns its own fix-and-re-review cycle")
                + " — Decisions Log #87)",
        _ when verdict != ReviewVerdict.Unknown => verdict.Value,
        _ when ReviewResultParser.ParseVerdict(rawText) == ReviewVerdict.NeedsFixes =>
            "needs-fixes (named nothing the platform could read as a finding)",
        _ when ReviewResultParser.ParseVerdict(rawText) == ReviewVerdict.MergeReady =>
            "merge-ready (attached a finding the platform could not read as a stated defect, so the verdict was not trusted)",
        _ => "(none stated)",
    };

    private static string SettlementLabel(RunAggregate run) => run.ReviewSettlement == ReviewSettlement.Unknown
        ? "settlement not recorded"
        : run.ReviewSettlement.Value.ToLowerInvariant();

    /// <summary>
    /// Why the cycle has no readable verdict, per verdict-less pass: a needs-fixes verdict that
    /// named nothing the platform could read as a finding is a different observed fact from a
    /// pass that ended with no VERDICT line at all, and the human parked over it needs to be
    /// told which one actually happened (task filed 2026-08-25) — both routes land here, but
    /// only one of them ever said "needs-fixes". Re-reads each pass's own preserved output
    /// rather than trusting a remembered reason, since the output on disk is the fact and this
    /// is what decides the honest label over it.
    /// <para>
    /// Only one pass per cycle ever receives <see cref="RunAggregate.VerdictRepromptedCycle"/>'s
    /// one re-prompt (<see cref="RepromptForVerdictAsync"/> picks a single verdict-less pass), so
    /// a cycle that ends this method's call with more than one still-verdict-less pass has one
    /// pass that was actually resumed and one or more that were never touched again — a park
    /// reason that says "even after this cycle's re-prompt" about all of them credits a re-prompt
    /// to a lens that never received it (adversarial cycle-1 finding, `ReviewEngine.cs:174`).
    /// Checked against <see cref="RunAggregate.VerdictRepromptedLens"/> per pass instead of
    /// stating it once for the whole cycle.
    /// </para>
    /// </summary>
    private async Task<string> VerdictMissingCauseAsync(RunAggregate run, CancellationToken cancellationToken)
    {
        List<ReviewPassResult> verdictless = [.. run.CompletedReviewPasses
            .Where(pass => pass.Verdict == ReviewVerdict.Unknown)];
        if (verdictless.Count == 0)
        {
            return "a review pass returned no parseable verdict, even after this cycle's re-prompt";
        }

        List<string> causes = [];
        string runDirectory = CurrentRunDirectory(run);
        foreach (ReviewPassResult pass in verdictless)
        {
            string path = LensFindingsFile(runDirectory, run.ReviewCycle, pass.Lens);
            string raw = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
            ReviewVerdict rawVerdict = ReviewResultParser.ParseVerdict(raw);
            // The verdict recorded here is the one the pass actually wrote, before the
            // reclassification above ever touches it — a merge-ready pass whose only attached
            // finding echoed the finding contract's own placeholder lands here too (it is promoted
            // to needs-fixes by that reclassification and then demoted to Unknown by the
            // NamesAFinding gate, never having been genuinely unparseable), and telling a human
            // "no parseable verdict" about it would name a machinery fault where the real one is a
            // reviewer's fabricated finding.
            string outcome = rawVerdict switch
            {
                _ when rawVerdict == ReviewVerdict.NeedsFixes =>
                    $"the {LensLabel(pass.Lens)} returned needs-fixes naming nothing the platform could read as a finding",
                _ when rawVerdict == ReviewVerdict.MergeReady =>
                    $"the {LensLabel(pass.Lens)} returned merge-ready but attached a finding the platform could "
                    + "not read as a stated defect, so the verdict was not trusted",
                _ => $"the {LensLabel(pass.Lens)} returned no parseable verdict",
            };
            string repromptState = run.VerdictRepromptedCycle == run.ReviewCycle && pass.Lens == run.VerdictRepromptedLens
                ? "even after this cycle's re-prompt"
                : "and this lens was never itself re-prompted this cycle — the cycle's one re-prompt went to another lens";
            causes.Add($"{outcome}, {repromptState}");
        }

        return string.Join("; ", causes);
    }

    /// <summary>
    /// One out-of-scope finding and where it went: the draft task it became, why it could not
    /// become one, or the cycle of this run that already exported it
    /// (<paramref name="AlreadyRoutedInCycle"/> — no second draft, and no second routing event).
    /// That cycle can be the current one, because both tracks report the same pre-existing line
    /// in the cycle they share, so it is carried rather than assumed to be an earlier one.
    /// <paramref name="IsSweep"/> says which kind of draft <paramref name="DraftTaskId"/> names —
    /// a task of this finding's own, or the project's standing sweep — purely so the merged
    /// findings document below can say which; nothing about routing or dedup reads it. A repeat
    /// carries it too, read off the earlier routing it repeats, so the merged document never
    /// tells a human "already routed to a draft bug task" about a repeat whose earlier routing was
    /// in fact a sweep fold (adversarial and conformance review, cycle 4).
    /// </summary>
    private sealed record RoutedFinding(
        ReviewLens Lens, ReviewFinding Finding, Guid? DraftTaskId, string? FailureReason,
        int? AlreadyRoutedInCycle = null, bool IsSweep = false);

    private sealed record ReviewContext(
        Guid RunId, Guid TaskId, RunDetails Run, TaskDetails Task, ProjectDetails Project,
        IReadOnlyList<ReviewParkResolution> PriorRulings);
}
