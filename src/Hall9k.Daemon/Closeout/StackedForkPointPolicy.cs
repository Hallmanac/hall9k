namespace Hall9k.Daemon.Closeout;

/// <summary>
/// The one question closeout's <c>ParentMergedAligned</c> arm asks of a run's own recorded fork
/// point before it decides whether a no-op stack replay is still owed, as a pure function of two
/// strings so it can be asserted without a repository.
/// <para>
/// The no-op replay exists to move <c>RunDetails.BaseCommit</c> onto the base branch's tip once the
/// stacked pull request has been aimed there. A run's own pre-push rebase can have done exactly that
/// already: it moves the recorded fork point onto the base tip it rebased onto, so a child whose
/// parent merged while it was still building reaches closeout with nothing left for the replay to
/// record. Reopening it anyway costs a slot wait and a host gate for a lap that changes nothing.
/// </para>
/// </summary>
public static class StackedForkPointPolicy
{
    /// <summary>
    /// True when the run's recorded fork point already names the base tip the branch sits on
    /// (<see cref="StackedParentObservation.BoundaryCommit"/> of a
    /// <see cref="StackedParentVerdict.ParentMergedAligned"/> observation), so the follow-up that would
    /// move it has nothing to move. A blank on either side is no evidence either way, so it never
    /// counts as moved: a run whose fork point was never recorded keeps the replay that records one.
    /// </summary>
    public static bool ForkPointAlreadyOnBaseTip(string recordedForkPoint, string observedBaseTip) =>
        recordedForkPoint.IsNotBlank()
        && observedBaseTip.IsNotBlank()
        && string.Equals(recordedForkPoint.Trim(), observedBaseTip.Trim(), StringComparison.OrdinalIgnoreCase);
}
