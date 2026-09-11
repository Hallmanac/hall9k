namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The Settling-gate repair round cap is spent (task: a pre-final-pass rebase that applies
/// cleanly but breaks the mandatory gate gets a repair lap inside the same run instead of failing
/// it) — appended immediately ahead of the <see cref="ReviewParked"/> that actually parks the run,
/// the same ordering <see cref="ReviewDisagreementParked"/> already uses, so
/// <see cref="RunAggregate.ParkedFromReviewPhase"/> reads
/// <see cref="ReviewPhase.SettlingGateRepairCapReached"/> by the time the park is applied. That is
/// what lets <see cref="RunAggregate.Apply(ReviewParkResolved)"/> route a human's resolve through
/// this feature's own rules — one bought repair round on needs-fixes, the counter left untouched —
/// rather than the ordinary generic park's fix-and-reset behavior.
/// </summary>
public sealed record SettlingGateRepairCapReached(Guid Id, DateTimeOffset ReachedAt);
