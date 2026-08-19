namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Parsed from the stream-json result payload; rolls up per task/project (§6.4).
/// The input side is three separate counts because they price differently: fresh prompt
/// input, cache reads, and cache writes. Summing them into one number would make the
/// roll-up cheap to compute and impossible to cost. The cache fields are appended with
/// defaults so streams written before they existed replay as zero rather than as a guess.
/// </summary>
public sealed record TokensRecorded(
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt,
    long CacheReadInputTokens = 0,
    long CacheCreationInputTokens = 0);
