namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A track the loop had already concluded is genuinely reawakened (task: review cycles after the
/// first): the mandatory <see cref="ReviewMode.FinalFullPass"/> immediately before the run may
/// settle reads every lens again, including one that went dormant cycles ago, and this time it
/// found something real. The inverse of <see cref="ReviewTrackConcluded"/> — it removes the
/// track's earlier conclusion from the run's bookkeeping rather than replacing it, so
/// <see cref="RunAggregate.ActiveReviewLenses"/> correctly treats the track as active again from
/// the very next cycle onward.
/// <para>
/// Never fired for a track FinalFullPass merely re-confirms clean: that track earns a fresh
/// <see cref="ReviewTrackConcluded"/> at this cycle instead, which already replaces its own earlier
/// entry. This event exists only for the case a clean re-conclusion cannot cover — a track whose
/// plan says <c>Continues: true</c>, which needs to be dispatched again rather than concluded.
/// </para>
/// <para>
/// Reawakening does not relax the track's own cycle cap (Decisions Log #63): the cap is measured
/// against the absolute cycle number and the run's budget base, both untouched by this event, so a
/// track that had already spent its cycles before it went dormant parks on the very next
/// <c>FixNeeded</c> check rather than quietly resuming an automatic budget it no longer has.
/// </para>
/// </summary>
public sealed record ReviewTrackReactivated(
    Guid Id,
    ReviewLens Lens,
    int Cycle,
    DateTimeOffset ReactivatedAt);
