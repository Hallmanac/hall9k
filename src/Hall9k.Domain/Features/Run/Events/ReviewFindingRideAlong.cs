namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One review pass's ride-along findings landed (Decisions Log #87): <see cref="Count"/> in-scope
/// findings graded below the fix bar, not dispatched to a fix session of their own this cycle.
/// Recorded once per pass that carries any, alongside <see cref="ReviewPassCompleted"/> — the
/// severity distribution behind the decision is on that event's own <c>Findings</c>; this one is
/// what lets <see cref="RunAggregate"/> track which cycles still have unclaimed ride-alongs across
/// a run that may go on for several more cycles on another track, without holding any finding's
/// own text on the stream (log #6).
/// </summary>
public sealed record ReviewFindingRideAlong(
    Guid Id,
    ReviewLens? Lens,
    int Cycle,
    int Count,
    DateTimeOffset RecordedAt);
