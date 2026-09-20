using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// A courier session's own observed usage, riding the courier's own stream rather than a run's
/// or a task's — the identical reason <c>PublicationTokensRecorded</c> is its own type rather
/// than a second appearance of <c>TokensRecorded</c>: that type's own projection mints a phantom
/// run the moment it sees one on a stream no <c>RunDispatched</c> ever opened, and a courier run
/// opens neither a run nor a task stream at all. Field order and shape mirror both siblings so
/// <see cref="Run.PeriodSpend"/> can fold all three into one total with one identical projection.
/// </summary>
public sealed record CourierTokensRecorded(
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt,
    long CacheReadInputTokens = 0,
    long CacheCreationInputTokens = 0,
    AgentModel? Model = null);
