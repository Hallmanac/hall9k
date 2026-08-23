using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// JiraProjectKey is the project's binding to a board (backlog 18): the key new cards are
/// created under and the key an agent-reported card is checked against. Optional like every
/// other setting here — absent means left alone, and present-but-empty clears the binding.
/// Appended with a default so streams written before Jira existed replay unchanged.
/// <para>
/// HomeDirectory is where the project lives on disk (backlog 47). It is a setting like the
/// rest, because the location is the owner's call while the shape inside it is the platform's;
/// <see cref="ProjectHome.None"/> clears it, which is what a project that no longer has a home
/// on this machine reads as. RepositoryPath moves with it: a home whose repo/ was materialised
/// is the repository the daemon cuts worktrees from, and a recorded path still naming the old
/// clone would leave the two disagreeing about where this project's code is.
/// </para>
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
    Optional<JiraProjectKey> JiraProjectKey = default,
    Optional<ProjectHome> HomeDirectory = default,
    Optional<string> RepositoryPath = default);
