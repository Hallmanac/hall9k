using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A card-publication agent session's observed token spend (backlog 18), read alongside
/// <see cref="Hall9k.Domain.Features.Run.Events.TokensRecorded"/> by
/// <see cref="Hall9k.Domain.Features.Run.PeriodSpend"/>, which is what the dispatcher's
/// period-spend budget gates on. A publication errand has no run of its own to carry that event
/// on, so this one rides the task's own stream instead — and is its own event type rather than a
/// second appearance of <see cref="Hall9k.Domain.Features.Run.Events.TokensRecorded"/> on
/// purpose: that type's own projection
/// (<see cref="Hall9k.Domain.Features.Run.Projections.RunDetailsProjection"/>) builds a document
/// straight off a stream's parameterless constructor whenever it sees one on a stream no
/// <c>RunDispatched</c> ever created, so appending the run event here would mint a phantom run
/// keyed by this task's own id. Origin: routed from the pre-PR review of task
/// 01a05cef-b7d8-722c-bb14-2a2c3e340005 (2026-09-04), which found the session's
/// <c>AgentResult</c> discarded here entirely.
/// </summary>
public sealed record PublicationTokensRecorded(
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt,
    long CacheReadInputTokens = 0,
    long CacheCreationInputTokens = 0,
    AgentModel? Model = null);
