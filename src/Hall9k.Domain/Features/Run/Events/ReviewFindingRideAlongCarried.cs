namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A cycle's ride-along findings (<see cref="ReviewFindingRideAlong"/>) were folded into a fix
/// session the run dispatched for another reason (Decisions Log #87) — the "next
/// naturally-occurring fix run on that track" the ride-along was waiting for. Removes
/// <see cref="OriginalCycle"/>'s entry from <see cref="RunAggregate"/>'s pending list so the same
/// findings are never handed to a later fix session a second time; whatever is still pending when
/// the run's review concludes becomes a residual instead (<see cref="ReviewResidualDisposition.RideAlong"/>).
/// </summary>
public sealed record ReviewFindingRideAlongCarried(
    Guid Id,
    ReviewLens? Lens,
    int OriginalCycle,
    int Cycle,
    DateTimeOffset RecordedAt);
