using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// One collaborator on a project's GitHub repository, exactly as GitHub's own collaborator list
/// reported them (idea 202383dc, A2b) — never a permission Hall9k assigns. Equality is by value,
/// which is what lets <c>ProjectDecider.ObserveGitHubCollaborators</c> tell an unchanged list from
/// a changed one with a plain sequence comparison.
/// </summary>
public sealed record GitHubCollaboratorRole(long AccountId, string Login, GitHubRepositoryRole Role);
