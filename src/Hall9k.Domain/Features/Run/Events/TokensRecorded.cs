using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Parsed from the stream-json result payload; rolls up per task/project (§6.4).
/// The input side is three separate counts because they price differently: fresh prompt
/// input, cache reads, and cache writes. Summing them into one number would make the
/// roll-up cheap to compute and impossible to cost. The cache fields are appended with
/// defaults so streams written before they existed replay as zero rather than as a guess.
/// <see cref="Model"/> is the session's resolved model, an observed fact the same way
/// <see cref="Events.RunDispatched.Model"/> is (Decisions Log #33): nullable rather than
/// defaulted to <see cref="AgentModel.Unknown"/> directly, because <c>AgentModel</c> is a
/// reference type and a record default parameter must be a compile-time constant — every
/// reader treats a null the same as <see cref="AgentModel.Unknown"/>, so a stream written
/// before this field existed replays as unknown, never as a guessed model — the same rule
/// the cache fields above already document. It is what the platform's per-period spend
/// budget (backlog: spend-governor step three) breaks its display down by; the budget
/// itself still gates on the summed total across every model, never per model, since
/// weighting one token differently from another would smuggle in the price list
/// Decisions Log #30 forbids the platform from holding.
/// </summary>
public sealed record TokensRecorded(
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt,
    long CacheReadInputTokens = 0,
    long CacheCreationInputTokens = 0,
    AgentModel? Model = null);
