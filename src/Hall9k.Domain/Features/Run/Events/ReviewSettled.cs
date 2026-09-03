namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The pre-PR review loop ended and the run may open its pull request (Decisions Log #63).
/// The terminal verdict is always <see cref="ReviewVerdict.MergeReady"/> — that is what the
/// rest of the pipeline reads — and <see cref="Settlement"/> is how it was reached: Clean when
/// a reviewer read the final tip and found nothing, Settled when the severity gate closed the
/// last track, findings were routed to draft bug tasks, or a human resolved a park.
/// <para>
/// The residual counts are carried here rather than recomputed downstream so one number is not
/// derived two ways: they are the run's whole residual tally — what every
/// <see cref="ReviewTrackConcluded"/> left unconfirmed, plus every
/// <see cref="ReviewFindingRouted"/> the loop wrote along the way. A run whose review was
/// already in flight before tracks existed reaches merge-ready without this event, and its
/// settlement is honestly unknown rather than assumed clean.
/// </para>
/// <para>
/// <see cref="ResidualsRoutingFailed"/> is counted apart from <see cref="ResidualsRouted"/>
/// rather than folded into it: a routing that failed left no draft bug task, so folding the two
/// would report a defect as exported to a task a human can find when it exists nowhere but this
/// stream. It is normally zero, and when it is not it is the number that most needs saying.
/// </para>
/// <para>
/// <see cref="ResidualsRideAlong"/> (Decisions Log #87) is a ride-along still unclaimed at this
/// point — never folded into a fix session the run dispatched for another reason. It defaults to
/// zero for the same reason the rest of this event's counts are always present rather than
/// optional: a run whose stream predates ride-alongs genuinely had none.
/// </para>
/// <para>
/// <see cref="RideAlongFindings"/> (independent pre-PR review, cycle 2, conformance finding) is
/// the same tally named rather than merely counted: each entry's severity and location, so a
/// reader of the pull request body or <c>h9k task show</c> can identify what actually rode along
/// instead of learning only how many did. Defaults to empty for a stream written before this
/// field existed — <see cref="ResidualsRideAlong"/> may still be non-zero there, an honest gap
/// between "we know how many" and "we know which ones" rather than a count this list should be
/// expected to reconstruct.
/// </para>
/// <para>
/// <see cref="ResidualsUnfixed"/> is the opposite fact from <see cref="ResidualsRideAlong"/>
/// (adversarial review, routed finding at ReviewEngine.cs:1146): a
/// <see cref="ReviewFindingDisposition.Fix"/>-dispositioned finding — the platform's own decision
/// that this one had to be fixed here — whose track was still active (most often capped) when the
/// run settled without a fix session ever reading it, typically a human resolving a capped park
/// with <c>h9k review resolve --merge-ready</c>. Folding it into <see cref="ResidualsRideAlong"/>
/// would understate it as polish nobody was owed; dropping it, which <c>SettleAsync</c> once did,
/// hides an in-scope medium or high finding behind a settled line that reads as though nothing
/// serious was left behind. <see cref="UnfixedFindings"/> names each one the same way
/// <see cref="RideAlongFindings"/> names its own tally. Both default to zero/empty for the same
/// reason the rest of this event's counts do: a stream written before this disposition existed
/// genuinely predates it.
/// </para>
/// </summary>
public sealed record ReviewSettled(
    Guid Id,
    int Cycle,
    ReviewSettlement Settlement,
    int ResidualsFixed,
    int ResidualsRouted,
    int ResidualsRoutingFailed,
    DateTimeOffset SettledAt,
    int ResidualsRideAlong = 0,
    IReadOnlyList<ReviewRideAlongFinding>? RideAlongFindings = null,
    int ResidualsUnfixed = 0,
    IReadOnlyList<ReviewUnfixedFinding>? UnfixedFindings = null);
