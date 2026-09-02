using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// What one review track does with the cycle it just finished (Decisions Log #63), as
/// <see cref="ReviewTrackPolicy"/> decided it: whether the track runs again, how it ended if
/// not, and how its findings split between this pull request and the drafts that carry the
/// rest away.
/// </summary>
/// <param name="Continues">Whether this track is dispatched again next cycle.</param>
/// <param name="Settlement">How the track ended; null while it continues.</param>
/// <param name="Fix">
/// Findings this pull request resolves this cycle — a Medium or High in the branch's own code,
/// plus out-of-scope Highs. Narrower on the mandatory <see cref="ReviewMode.FinalFullPass"/>
/// cycle (Decisions Log #113): there, only a High in the branch's own code lands here, since an
/// in-scope Medium rides along instead.
/// </param>
/// <param name="Route">Out-of-scope Mediums and Lows, bound for draft bug tasks rather than this diff.</param>
/// <param name="RideAlong">
/// In-scope findings below the fix bar (Decisions Log #87) — a Low, or one nobody graded — not
/// dispatched to a fix session of their own this cycle. On the mandatory
/// <see cref="ReviewMode.FinalFullPass"/> cycle that bar narrows to High alone (Decisions Log
/// #113), so an in-scope Medium rides along here too.
/// </param>
/// <param name="Residuals">
/// What the track leaves unconfirmed if it ended here, and only the fixed-unreviewed half of
/// it: a routed finding's residual is recorded by the routing event, in whatever cycle it was
/// routed, and a ride-along's residual is recorded by <c>ReviewEngine.RecordReviewPassAsync</c>
/// alongside this plan — as <see cref="ReviewResidualDisposition.RideAlong"/> when nothing
/// anywhere in the cycle is dispatching a fix session (the empty terminal case, Decisions Log
/// #63: every active track concludes there regardless of what <see cref="Continues"/> says here,
/// because there is no later cycle left for one to claim it in), or folded in as
/// <see cref="ReviewResidualDisposition.FixedUnreviewed"/> when something else is dispatching one
/// (Decisions Log #87). This field itself is empty while the track continues on its own terms,
/// because a fix is re-read next cycle.
/// </param>
public sealed record ReviewTrackPlan(
    ReviewLens Lens,
    bool Continues,
    ReviewSettlement? Settlement,
    IReadOnlyList<ReviewFinding> Fix,
    IReadOnlyList<ReviewFinding> Route,
    IReadOnlyList<ReviewFinding> RideAlong,
    IReadOnlyList<ReviewResidual> Residuals);
