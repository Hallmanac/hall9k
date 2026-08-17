namespace Hall9k.Domain.Features.Run.Events;

/// <summary>Terminal: the pipeline finished — verified and (where applicable) PR opened.</summary>
public sealed record RunCompleted(
    Guid Id,
    DateTimeOffset CompletedAt);
