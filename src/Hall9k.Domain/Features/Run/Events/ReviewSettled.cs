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
/// </summary>
public sealed record ReviewSettled(
    Guid Id,
    int Cycle,
    ReviewSettlement Settlement,
    int ResidualsFixed,
    int ResidualsRouted,
    int ResidualsRoutingFailed,
    DateTimeOffset SettledAt,
    int ResidualsRideAlong = 0);
