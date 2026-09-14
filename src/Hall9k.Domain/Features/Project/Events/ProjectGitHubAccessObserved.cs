using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// This install's own role on the project's GitHub repository, read from the repository object
/// itself (<c>viewerPermission</c>) — the one fact obtainable at any access level, unlike the full
/// collaborator list <see cref="ProjectGitHubCollaboratorsObserved"/> carries, which GitHub only
/// hands out to an account that already has push (idea 202383dc, A2b, item 3). Appended to the
/// project's own stream, never the connection's: the role is a fact about this project's
/// repository, not about the account generally, and the same account can hold a different role on
/// a different project. Appended only when <see cref="Role"/> or the account changed since the
/// last observation (<c>ProjectDecider.ObserveGitHubAccess</c>) — Hall9k never writes a
/// permission, only ever records what GitHub already decided.
/// </summary>
public sealed record ProjectGitHubAccessObserved(
    Guid ProjectId,
    long AccountId,
    string Login,
    GitHubRepositoryRole Role,
    DateTimeOffset ObservedAt);
