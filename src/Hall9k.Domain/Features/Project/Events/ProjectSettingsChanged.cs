using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// JiraProjectKey is the project's binding to a board (backlog 18): the key new cards are
/// created under and the key an agent-reported card is checked against. Optional like every
/// other setting here — absent means left alone, and present-but-empty clears the binding.
/// Appended with a default so streams written before Jira existed replay unchanged.
/// </summary>
public sealed record ProjectSettingsChanged(
    Guid Id,
    Optional<IReadOnlyList<VerifyCommand>> VerifyCommands,
    Optional<bool> SkipPermissions,
    Optional<int> MaxParallelAgents,
    Optional<IReadOnlyList<ContextLink>> ContextLinks,
    DateTimeOffset ChangedAt,
    Guid ChangedByOwnerId,
    Optional<CommitStyle> CommitStyle = default,
    Optional<AgentModel> Model = default,
    Optional<ReviewRerequestPolicy> ReviewRerequest = default,
    Optional<JiraProjectKey> JiraProjectKey = default);
