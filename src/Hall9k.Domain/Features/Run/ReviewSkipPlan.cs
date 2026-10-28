namespace Hall9k.Domain.Features.Run;

/// <summary>
/// What <c>h9k task skip-to-pr</c> bypasses for a run, computed from where the run stands
/// (idea 6e6d0200, Card 3) so the recorded list is an observation of the run, not a boilerplate
/// sentence. Names come from the run's own state: each review track from
/// <see cref="ReviewLens.CycleLenses"/> filtered to the tracks the run's recorded stage composition
/// still has active, never a hard-coded pair, so a third lens appears here without an edit.
/// </summary>
public static class ReviewSkipPlan
{
    public const string Gates = "the verification gates";

    public const string FinalPass = "the final full pass";

    public const string SettlingGate = "the settling gate";

    public static string Track(ReviewLens lens) => $"the {lens.Value} review track";

    /// <summary>
    /// The things this run would still have gone through before its pull request. Works for both
    /// entry points: a run still in the build (nothing has run yet) and a review-parked run, whose
    /// phase is the one the park interrupted.
    /// </summary>
    public static IReadOnlyList<string> Compute(RunAggregate run)
    {
        ReviewPhase phase = run.State == RunState.ReviewParked ? run.ParkedFromReviewPhase : run.ReviewPhase;
        List<string> skipped = [];

        // Owed when the gates never ran, or when a fix has landed commits since they last did. A run
        // parked at the fix-to-re-review boundary sits in Reverify but on the far side of the fix's
        // own gates, so the phase alone cannot say whether a skip bypasses them.
        if (!run.GatesPassed || run.GatesOwedSinceFix)
        {
            skipped.Add(Gates);
        }

        IReadOnlyList<ReviewLens> active = run.ActiveReviewLenses;
        skipped.AddRange(ReviewLens.CycleLenses.Where(active.Contains).Select(Track));

        if (run.ReviewStageComposition.OpeningLenses().Count > 0
            && !run.ReviewStageComposition.WaivesFinalFullPassGuarantee
            && !FinalPassAlreadySatisfied(run, phase))
        {
            skipped.Add(FinalPass);
        }

        skipped.Add(SettlingGate);
        return skipped;
    }

    /// <summary>
    /// Whether the mandatory final full pass is no longer owed. The settling phases are not enough on
    /// their own: the interactive boundary in front of the final pass parks in Settling before that
    /// pass has run, as does a settling-gate repair that precedes it. Past MergeReady the review is
    /// done; in a settling phase the pass is satisfied only when the loop could settle, which mirrors
    /// the clauses of <c>ReviewEngine.MaySettleReason</c> that do not depend on the engine's caps.
    /// </summary>
    private static bool FinalPassAlreadySatisfied(RunAggregate run, ReviewPhase phase)
    {
        if (run.HumanEndedTheLoop || phase == ReviewPhase.MergeReady)
        {
            return true;
        }

        if (phase is not (ReviewPhase.Settling or ReviewPhase.AwaitingSettlingGateRepair
            or ReviewPhase.SettlingGateRepairCapReached or ReviewPhase.SettlingGateRepairNeeded))
        {
            return false;
        }

        return true switch
        {
            _ when run.PreFinalPassRebaseAwaitingReview => false,
            _ when run.CurrentCycleMode == ReviewMode.Discovery && run.CycleSinceSha is not null => false,
            _ when run.CurrentCycleMode == ReviewMode.FinalFullPass
                && run.LastReviewVerdict == ReviewVerdict.MergeReady
                && run.CompletedReviewPasses.Any(pass => pass.Findings.Count > 0) => true,
            _ => run.CurrentCycleMode != ReviewMode.Verify && !run.FixDispatchedThisCycle,
        };
    }
}
