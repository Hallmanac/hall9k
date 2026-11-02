using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// A plan's discovery session's own observed usage, riding the plan's stream because a discovery session has no task to carry
/// it. Node-scoped like <see cref="Courier.CourierTokensRecorded"/>, whose shape it follows so
/// <see cref="Run.PeriodSpend"/> folds it in with one identical projection: the spend stays on the node that spent it.
/// <c>PublicationTokensRecorded</c> cannot serve, because it is project-scoped and rides a task's stream.
/// </summary>
public sealed record PlanTokensRecorded(
    Guid Id,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    DateTimeOffset RecordedAt,
    long CacheReadInputTokens = 0,
    long CacheCreationInputTokens = 0,
    AgentModel? Model = null);
