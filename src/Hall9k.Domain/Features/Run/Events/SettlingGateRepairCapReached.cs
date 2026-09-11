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
/// <param name="GateOutput">
/// The mandatory gate's own output from the failure that actually triggered this park —
/// <see cref="RunAggregate.Apply(SettlingGateRepairCapReached)"/> records it over
/// <see cref="RunAggregate.LastSettlingGateRepairOutput"/> (independent pre-PR review, cycle 1,
/// both lenses): without this, that property still held whatever the round BEFORE the parking one
/// carried, since <see cref="SettlingGateRepairDispatched"/> is the only other event that ever sets
/// it, and the park's own round never re-dispatches to refresh it. The bought round a human's
/// needs-fixes resolve earns on <see cref="ReviewPhase.SettlingGateRepairNeeded"/> reads this
/// property back to build its own prompt, so a stale value there showed the bought session an
/// already-fixed failure instead of the one the human actually read and answered.
/// </param>
public sealed record SettlingGateRepairCapReached(Guid Id, DateTimeOffset ReachedAt, string GateOutput);
