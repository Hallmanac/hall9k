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
/// Reawakening deliberately DOES relax the track's own cycle cap (task: review cycles after the
/// first, Decisions Log #92, #93 — corrected here after a cycle-3 finding caught this paragraph
/// stating the opposite of what the code, <c>RunAggregate.TrackBudgetBaseCycle</c>, and this
/// branch's own test (<c>A_track_the_mandatory_final_pass_reawakens_gets_a_genuine_cycle_to_fix_it</c>)
/// all actually do): <see cref="RunAggregate.Apply(ReviewTrackReactivated)"/> records this event's
/// own <see cref="Cycle"/> as the track's new budget base, so its cap is measured from the cycle it
/// was reawakened at rather than the run's absolute cycle count. Measuring from the absolute count
/// would park a track the mandatory pass just reawakened on the very next check, before it ever
/// earns the fix session this event exists to give it a chance at. The mandatory pass's OWN
/// repetition is bounded separately (<c>DaemonOptions.MaxFinalFullPassRounds</c>, Decisions Log
/// #93), precisely because this relaxation means the per-track cap alone cannot bound a track the
/// final pass keeps reawakening.
/// </para>
/// </summary>
public sealed record ReviewTrackReactivated(
    Guid Id,
    ReviewLens Lens,
    int Cycle,
    DateTimeOffset ReactivatedAt);
