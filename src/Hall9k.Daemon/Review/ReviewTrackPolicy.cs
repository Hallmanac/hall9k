using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// What one review track does with the cycle it just finished (Decisions Log #63). This is the
/// convergence rule in one place, kept out of the aggregate (deciders own the judgment, the
/// aggregate records it) and out of the engine (which owns dispatching, waiting, and writing
/// the stream).
/// <para>
/// <b>Conformance</b> now grades every finding the same way adversarial does (Decisions Log
/// #87): <see cref="ReviewFinding.Disposition"/> reads <see cref="ReviewSeverity.MeetsFixBar"/>
/// on either lens, and <c>ReviewEngine.RecordReviewPassAsync</c> demotes a needs-fixes verdict to
/// merge-ready the moment every finding it attached is RideAlong-dispositioned — so this track
/// ends on "nothing that meets the fix bar" whether that means it came back clean or came back
/// with polish alone. <see cref="Decide"/> itself never gates conformance on severity: a
/// needs-fixes verdict that survives that reclassification always sets <c>Continues: true</c>
/// below, so the track otherwise runs until <see cref="DaemonOptions.MaxComplianceReviewCycles"/>,
/// where the run parks because nothing automated is left to try.
/// </para>
/// <para>
/// <b>Adversarial</b> disposes every finding against the same fix bar at every cycle, gate or no
/// gate: a Low or an ungraded finding is never itself Fix-dispositioned, so it rides along
/// instead of being fixed on its own from cycle 1 onward — the severity gate never turns that
/// rule on. What the gate (<see cref="DaemonOptions.AdversarialSeverityGateFromCycle"/>) actually
/// decides is whether the track is FORCED into another cycle regardless of severity: before it,
/// a needs-fixes verdict with a Route finding still runs the track again even though nothing
/// attached meets the fix bar (<c>ReviewEngine.RecordReviewPassAsync</c> demotes a needs-fixes
/// verdict to merge-ready the moment every attached finding is a ride-along, so a Route finding —
/// alone or alongside ride-alongs — is what actually reaches this pre-gate rule, exactly as
/// <see cref="Decide"/>'s own merge-ready branch documents), because early cycles get full rigor
/// while the code is still converging. From that cycle onward only a High still forces the next
/// one, and a Medium is fixed this cycle without forcing another — the ride-along a Low or
/// ungraded finding already was simply continues, which is the nit-churn tail the gate exists to
/// stop paying an extra cycle for. Its cap is <see cref="DaemonOptions.MaxAdversarialReviewCycles"/>,
/// and reaching it is not a budget quietly running out: it means the machine kept finding real
/// high-severity problems, and the park says so.
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
            // A merge-ready pass never forces another cycle, whatever it attached (Decisions Log
            // #87): route and ride-along findings still split out of it exactly as they would
            // from a needs-fixes one, but nothing here is ever a Fix — RecordReviewPassAsync
            // reclassifies verdict against Disposition, not severity, before this method ever
            // sees it, in both directions: a pass is only ever recorded merge-ready when EVERY
            // stated finding is RideAlong-disposed (a needs-fixes pass that meets that bar is
            // demoted; a merge-ready pass that does not — because it still carries a Fix, or even
            // just a Route, a mis-graded or ungraded one included — is promoted to needs-fixes),
            // so by the time a verdict reaches here as merge-ready, `mergeReadyRoute` below is
            // always empty in practice and no attached finding is ever Fix. A Route finding is
            // deliberately NOT treated like a Fix finding for that reclassification, only kept
            // out of a merge-ready verdict the same way Fix is: a route-only needs-fixes pass
            // stays needs-fixes so this method's own pre-gate rule below can still keep the track
            // alive for a tip the OTHER track's fix session may yet rewrite. Settlement reflects
            // what was actually attached: a pass that carried nothing really did find nothing
            // (Clean), one that carried a route or a ride-along did not (Settled), the same
            // distinction a needs-fixes cycle draws.
            List<ReviewFinding> mergeReadyRoute = [.. findings.Where(finding => finding.Disposition == ReviewFindingDisposition.Route)];
            List<ReviewFinding> mergeReadyRideAlong = [.. findings.Where(finding => finding.Disposition == ReviewFindingDisposition.RideAlong)];
            ReviewSettlement mergeReadySettlement = mergeReadyRoute.Count > 0 || mergeReadyRideAlong.Count > 0
                ? ReviewSettlement.Settled
                : ReviewSettlement.Clean;
            return new ReviewTrackPlan(lens, Continues: false, mergeReadySettlement, [], mergeReadyRoute, mergeReadyRideAlong, []);
        }

        // Stated()'s placeholder for a needs-fixes verdict the parser could read nothing
        // structured out of is always Fix (ReviewFinding.Disposition's own blank-Location-and-
        // Text case), so it lands in `fix` below exactly as before rather than joining the
        // ride-along split (Decisions Log #87) — that split only ever applies to a genuinely
        // parsed finding, never to the placeholder standing in for one the platform could not read.
        IReadOnlyList<ReviewFinding> stated = Stated(findings);
        List<ReviewFinding> fix = [.. stated.Where(finding => finding.Disposition == ReviewFindingDisposition.Fix)];
        List<ReviewFinding> route = [.. stated.Where(finding => finding.Disposition == ReviewFindingDisposition.Route)];
        List<ReviewFinding> rideAlong = [.. stated.Where(finding => finding.Disposition == ReviewFindingDisposition.RideAlong)];
        bool gated = GateApplies(lens, cycle, options);
        // Before the gate, a needs-fixes verdict always runs the track again: every finding of
        // every grade forces the next cycle, and a cycle that only routed still leaves the
        // track owed a look at whatever the other track's fix session does to the branch.
        bool forcesAnotherCycle = !gated || fix.Any(finding => finding.Severity.ForcesAnotherCycle);

        return forcesAnotherCycle
            ? new ReviewTrackPlan(lens, Continues: true, Settlement: null, fix, route, rideAlong, [])
            : new ReviewTrackPlan(lens, Continues: false, ReviewSettlement.Settled, fix, route, rideAlong,
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

    /// <summary>
    /// Internal rather than private (Decisions Log #87): <c>ReviewEngine.RecordReviewPassAsync</c>
    /// builds the identical shape of residual for a ride-along a cycle's own fix session swept
    /// up for free, and the two must never drift into constructing it two different ways.
    /// </summary>
    internal static ReviewResidual Residual(
        ReviewLens lens, int cycle, ReviewFinding finding, ReviewResidualDisposition disposition) =>
        new(lens, cycle, finding.Severity, finding.Scope, disposition, finding.Location);
}
