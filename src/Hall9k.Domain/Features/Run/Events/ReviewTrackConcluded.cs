namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One review track finished and went dormant (Decisions Log #62). Clean means a reviewer read
/// the tip and found nothing; Settled means the track ended over findings no reviewer will
/// confirm resolved — the severity gate closing an adversarial track over Mediums and Lows, or
/// a cycle whose findings were all routed to draft bug tasks.
/// <para>
/// A concluded track stops being dispatched and is deliberately never reawakened by the other
/// track's fix sessions (the accepted trade-off: conformance converges in one or two cycles,
/// fix sessions are small, and the gates, the external reviewer, and the human merge gate
/// stand behind the loop).
/// </para>
/// <para>
/// Every track has one of these by the time the run reaches merge-ready, but a track does not
/// always get to decide its own ending. The run settles the moment no track is owed a fix, so
/// a track still asking for another cycle can be outlived by the run — an empty terminal cycle
/// whose findings all routed away, or a human's merge-ready park resolution. That ending is
/// recorded here too, as Settled at the cycle the run stopped on, because a track the record
/// has no entry for cannot be told from one that is somehow still running.
/// </para>
/// <para>
/// <see cref="Residuals"/> is what the track left behind for want of a re-read — empty for
/// Clean, and for Settled the findings it fixed on the cycle it ended on, each with its grade
/// and scope. Routed findings are not counted here: their residual belongs to the cycle they
/// were routed in, which may be one the track went on running past, so
/// <see cref="ReviewFindingRouted"/> is what records it. Between the two, a settled
/// merge-ready never reads like a clean one.
/// </para>
/// </summary>
public sealed record ReviewTrackConcluded(
    Guid Id,
    ReviewLens Lens,
    int Cycle,
    ReviewSettlement Settlement,
    IReadOnlyList<ReviewResidual> Residuals,
    DateTimeOffset ConcludedAt);
