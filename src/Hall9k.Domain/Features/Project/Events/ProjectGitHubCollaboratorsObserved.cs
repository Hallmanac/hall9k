namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// The project's GitHub repository collaborator list with roles, read only when this install's
/// account can see it — GitHub itself requires push to list collaborators, so an install with mere
/// read access never appends this event at all, and that is not a failure to be worked around: the
/// gap is exactly what <see cref="ProjectGitHubAccessObserved"/> exists to cover for every account
/// regardless of its own role (idea 202383dc, A2b, item 3). Appended only when the set of
/// (account, role) pairs actually changed since the last observation
/// (<c>ProjectDecider.ObserveGitHubCollaborators</c>). Hall9k never writes a permission here,
/// only ever mirrors what GitHub already decided.
/// </summary>
public sealed record ProjectGitHubCollaboratorsObserved(
    Guid ProjectId,
    IReadOnlyList<GitHubCollaboratorRole> Collaborators,
    DateTimeOffset ObservedAt);
