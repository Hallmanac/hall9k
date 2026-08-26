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
/// <param name="Fix">Findings this pull request resolves this cycle — a Medium or High in the branch's own code, plus out-of-scope Highs.</param>
/// <param name="Route">Out-of-scope Mediums and Lows, bound for draft bug tasks rather than this diff.</param>
/// <param name="RideAlong">
/// In-scope findings below the fix bar (Decisions Log #87) — a Low, or one nobody graded — not
/// dispatched to a fix session of their own this cycle.
/// </param>
/// <param name="Residuals">
/// What the track leaves unconfirmed if it ended here, and only the fixed-unreviewed half of
/// it: a routed finding's residual is recorded by the routing event, in whatever cycle it was
/// routed, and a ride-along's residual (if it is never claimed) is recorded once the whole
/// run's review concludes. Empty while the track continues, because a fix is re-read next cycle.
/// </param>
public sealed record ReviewTrackPlan(
    ReviewLens Lens,
    bool Continues,
    ReviewSettlement? Settlement,
    IReadOnlyList<ReviewFinding> Fix,
    IReadOnlyList<ReviewFinding> Route,
    IReadOnlyList<ReviewFinding> RideAlong,
    IReadOnlyList<ReviewResidual> Residuals);
