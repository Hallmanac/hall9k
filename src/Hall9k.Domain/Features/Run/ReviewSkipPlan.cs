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

        // A reverify is the gate that follows a fix, so a run parked there still owes one even
        // though an earlier pass of the gates is on its stream.
        if (!run.GatesPassed || phase == ReviewPhase.Reverify)
        {
            skipped.Add(Gates);
        }

        IReadOnlyList<ReviewLens> active = run.ActiveReviewLenses;
        skipped.AddRange(ReviewLens.CycleLenses.Where(active.Contains).Select(Track));

        bool settled = phase is ReviewPhase.Settling or ReviewPhase.MergeReady
            or ReviewPhase.AwaitingSettlingGateRepair or ReviewPhase.SettlingGateRepairCapReached
            or ReviewPhase.SettlingGateRepairNeeded;
        if (run.ReviewStageComposition.OpeningLenses().Count > 0
            && !run.ReviewStageComposition.WaivesFinalFullPassGuarantee
            && !run.HumanEndedTheLoop
            && !settled)
        {
            skipped.Add(FinalPass);
        }

        skipped.Add(SettlingGate);
        return skipped;
    }
}
