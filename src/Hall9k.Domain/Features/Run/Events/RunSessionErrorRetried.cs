namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A session's terminal result carried a generic error (<c>result.IsError</c>) other than the
/// recognizable usage-limit shape <see cref="RunBudgetExhausted"/> already parks on (task: a
/// session that reports an error result is retried once in place). Measured 2026-09-05 from
/// the event store: 41 runs failed this way, landing in only 18 distinct hours with single
/// hours holding 7, 5, 4 and 4 — the shape of a provider-side overload or rate-limit burst, not
/// a steady code defect — after the run had already paid for the build session, the gate, and
/// usually one or both review lenses.
/// <para>
/// This is the FIRST such error for <see cref="Leg"/> on this exact <see cref="Cycle"/>/
/// <see cref="Lens"/> combination: the same leg is redispatched fresh after a short backoff
/// (<c>DaemonOptions.SessionErrorRetryBackoff</c>) instead of failing the run outright, through
/// the same dispatch mechanics an ordinary crash-recovery top-up already uses. A SECOND
/// consecutive error on the identical combination fails the run exactly as before
/// (<see cref="RunFailed"/>, unchanged reason text) — this event is never appended twice for
/// the same leg/cycle/lens.
/// </para>
/// </summary>
public sealed record RunSessionErrorRetried(
    Guid Id,
    RunSessionLeg Leg,
    int? Cycle,
    ReviewLens? Lens,
    string ObservedMessage,
    DateTimeOffset RetriedAt);
