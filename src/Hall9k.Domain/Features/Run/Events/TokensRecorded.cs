namespace Hall9k.Domain.Features.Run.Events;

/// <summary>Parsed from the stream-json result payload; rolls up per task/project (§6.4).</summary>
public sealed record TokensRecorded(
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt);
