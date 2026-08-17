namespace Hall9k.Domain.Features.Tasks.Events;

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
    Guid AddedByOwnerId);
