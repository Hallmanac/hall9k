using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Model is this task's optional model override, the most specific link in the resolution
/// chain (Decisions Log #33), Unknown when the task states no preference. Appended with a
/// default so streams written before the chain existed replay as Unknown, never as a guess.
/// </summary>
public sealed record TaskAdded(
    Guid Id,
    Guid ProjectId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    TaskType Type,
    string? AgentContext,
    TaskConstraints? Constraints,
    ExternalReference? ExternalReference,
    DateTimeOffset AddedAt,
    Guid AddedByOwnerId,
    AgentModel? Model = null);
