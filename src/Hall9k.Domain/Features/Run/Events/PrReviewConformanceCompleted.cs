namespace Hall9k.Domain.Features.Run.Events;

/// <summary>The conformance lens finished; its findings are on disk. Pairs with PrReviewConformanceDispatched.</summary>
public sealed record PrReviewConformanceCompleted(
    Guid Id,
    Guid SessionId,
    DateTimeOffset CompletedAt);
