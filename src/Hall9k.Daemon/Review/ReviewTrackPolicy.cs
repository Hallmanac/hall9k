using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// What one review track does with the cycle it just finished (Decisions Log #62). This is the
/// convergence rule in one place, kept out of the aggregate (deciders own the judgment, the
/// aggregate records it) and out of the engine (which owns dispatching, waiting, and writing
/// the stream).
/// <para>
/// <b>Conformance</b> has no grades to reason about: a criterion is met or it is not. It ends
/// the moment it comes back clean, and otherwise runs until <see cref="DaemonOptions.MaxComplianceReviewCycles"/>,
/// where the run parks because nothing automated is left to try.
/// </para>
/// <para>
/// <b>Adversarial</b> runs under the severity gate. Before
/// <see cref="DaemonOptions.AdversarialSeverityGateFromCycle"/> every finding of every grade is
/// fixed and forces a fresh re-review — early cycles get full rigor while the code is still
/// converging. From that cycle onward only a High forces the next one; Mediums and Lows are
/// still fixed, they simply stop re-triggering the loop, which is the nit-churn tail the gate
/// exists for. Its cap is <see cref="DaemonOptions.MaxAdversarialReviewCycles"/>, and reaching
/// it is not a budget quietly running out: it means the machine kept finding real high-severity
/// problems, and the park says so.
/// </para>
/// <para>
/// The cap is measured from the run's budget base and the gate is not, because they are not the
/// same kind of number: the cap is a budget a human's park resolution may re-grant (log #22),
/// while the gate is a statement about how converged the diff is by that cycle.
/// </para>
/// </summary>
public static class ReviewTrackPolicy
{
    /// <summary>
    /// What the track does next, given the verdict it just returned and the findings behind it.
    /// A clean verdict ends the track; otherwise the findings split into what this pull request
    /// fixes and what routes away, and whether anything left forces another cycle.
    /// <para>
    /// A needs-fixes verdict with nothing left to fix ends the track as settled — the empty
    /// terminal case — and only <b>from the gate cycle</b>, which is where the acceptance
    /// criteria put it. Before the gate, every stated finding keeps the track alive, a routed
    /// one included: ending there would retire the track over a tip it is about to stop
    /// recognizing, because the OTHER track can still be forcing fix sessions that rewrite the
    /// branch, and a dormant track is deliberately never reawakened. That is how a lens ends up
    /// having never read the fix commits, which is where PR #21's two regressions came from.
    /// </para>
    /// <para>
    /// This cannot loop forever on an unchanged tip. A cycle where nothing at all is left to fix
    /// derives <c>ReviewPhase.Settling</c> (<c>RunAggregate.DeriveReviewPhase</c>) and the run
    /// settles whatever this decided, so "continues" only ever means "look again at what the
    /// other track's fix session changes" — and if nothing changes it, there is nothing to look
    /// at and the loop is already over.
    /// </para>
    /// <para>
    /// The residuals a plan carries are only the fixed-unreviewed half. A routed finding leaves
    /// its residual behind the moment it is routed, which is why the routing event records it
    /// (<c>RunAggregate.Apply(ReviewFindingRouted)</c>) rather than the track's conclusion: a
    /// cycle that routes one finding and is forced to run again by another still exported the
    /// first one, and a residual carried only on a terminal plan would go unrecorded there.
    /// The fixed-unreviewed half has no such gap — a fix is re-read next cycle whenever the
    /// track continues, so it is a residual only on the cycle the track ends on.
    /// </para>
    /// </summary>
    public static ReviewTrackPlan Decide(
        ReviewLens lens,
        int cycle,
        ReviewVerdict verdict,
        IReadOnlyList<ReviewFinding> findings,
        DaemonOptions options)
    {
        if (verdict == ReviewVerdict.MergeReady)
        {
            return new ReviewTrackPlan(lens, Continues: false, ReviewSettlement.Clean, [], [], []);
        }

        IReadOnlyList<ReviewFinding> stated = Stated(findings);
        List<ReviewFinding> fix = [.. stated.Where(finding => finding.IsFixedHere)];
        List<ReviewFinding> route = [.. stated.Where(finding => !finding.IsFixedHere)];
        bool gated = GateApplies(lens, cycle, options);
        // Before the gate, a needs-fixes verdict always runs the track again: every finding of
        // every grade forces the next cycle, and a cycle that only routed still leaves the
        // track owed a look at whatever the other track's fix session does to the branch.
        bool forcesAnotherCycle = !gated || fix.Any(finding => finding.Severity.ForcesAnotherCycle);

        return forcesAnotherCycle
            ? new ReviewTrackPlan(lens, Continues: true, Settlement: null, fix, route, [])
            : new ReviewTrackPlan(lens, Continues: false, ReviewSettlement.Settled, fix, route,
                [.. fix.Select(finding => Residual(lens, cycle, finding, ReviewResidualDisposition.FixedUnreviewed))]);
    }

    /// <summary>
    /// What a needs-fixes pass actually found, as far as anything downstream is concerned. A
    /// reviewer that said needs-fixes found something, whatever the parser could read of it, so
    /// an unreadable set becomes one ungraded, unplaced stand-in rather than nothing at all.
    /// Recording nothing would turn "we could not read this finding" into "there was no
    /// finding", which is the one reading that lets a real defect out the far end.
    /// <para>
    /// Everything that classifies a pass's findings goes through here — the pass milestone and
    /// the track decision alike — so the two can never disagree about whether a fix is owed.
    /// Applying it twice is harmless: a list that already has findings is returned unchanged.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReviewFinding> Stated(IReadOnlyList<ReviewFinding> findings) =>
        findings.Count > 0
            ? findings
            : [new ReviewFinding(ReviewSeverity.Unknown, ReviewFindingScope.Unknown, string.Empty, string.Empty)];

    /// <summary>
    /// Whether this track has run as many cycles as it may. Measured from
    /// <paramref name="budgetBaseCycle"/> so a human's needs-fixes park resolution is the fresh
    /// grant it is meant to be (log #22) rather than one cycle before an immediate re-park.
    /// </summary>
    public static bool CapReached(ReviewLens lens, int cycle, int budgetBaseCycle, DaemonOptions options) =>
        cycle - budgetBaseCycle >= CapFor(lens, options);

    /// <summary>The cycle cap for a track. An unrecognized lens takes the conformance cap — the stricter of the two.</summary>
    public static int CapFor(ReviewLens lens, DaemonOptions options) =>
        lens == ReviewLens.Adversarial ? options.MaxAdversarialReviewCycles : options.MaxComplianceReviewCycles;

    /// <summary>
    /// Whether the severity gate is in force for this track, measured in absolute cycles and
    /// deliberately NOT from the budget base the caps use. The two numbers answer different
    /// questions: a cap is a budget, so a human's fresh grant re-measures it, while the gate is
    /// about how converged the diff is by now. Re-opening full rigor at cycle eleven because a
    /// human said "keep going" at cycle ten would restart the nit-churn tail at the point the
    /// code is most converged, which is the opposite of what the gate is for.
    /// </summary>
    private static bool GateApplies(ReviewLens lens, int cycle, DaemonOptions options) =>
        lens == ReviewLens.Adversarial && cycle >= options.AdversarialSeverityGateFromCycle;

    private static ReviewResidual Residual(
        ReviewLens lens, int cycle, ReviewFinding finding, ReviewResidualDisposition disposition) =>
        new(lens, cycle, finding.Severity, finding.Scope, disposition, finding.Location);
}
