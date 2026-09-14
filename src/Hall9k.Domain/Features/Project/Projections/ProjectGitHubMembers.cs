using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Project.Projections;

/// <summary>One account's last-observed standing on a project's GitHub repository (idea 202383dc, A2b, item 3) — never a permission Hall9k assigns, only ever what GitHub itself reported.</summary>
public sealed record ProjectGitHubMemberView(long AccountId, string Login, GitHubRepositoryRole Role, DateTimeOffset LastObservedAt);

/// <summary>
/// The read side of Hall9k's own GitHub access mirror: every account this install has ever
/// observed holding a role on a project's repository, keyed by GitHub's own numeric account id so
/// a rename never splits one member into two rows. Built from two different facts, observed at
/// different access levels (<see cref="ProjectDetailsProjection"/>'s own doc comment draws the
/// same "no multi-stream projection" line, but this is one stream read two ways, not two):
/// <see cref="ProjectGitHubAccessObserved"/>, this install's own role, obtainable at any access
/// level and so always present; and <see cref="ProjectGitHubCollaboratorsObserved"/>, the full
/// roster, only ever present when this install's own account already has push. An install that
/// never reaches push therefore shows exactly one member here — itself — which is an honest
/// reflection of what it can actually see, not a gap to be filled in.
/// <para>
/// A member missing from a later, more current collaborator list is left exactly as last observed
/// rather than removed: a revocation-aware mirror is a later piece (idea 202383dc, A2b's own list
/// parks GitHub key registration and account switching the identical way). Both events are appended
/// only when something about the member actually changed (<see cref="Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ObserveGitHubAccess"/>/
/// <see cref="Hall9k.Domain.Features.Project.Handlers.ProjectDecider.ObserveGitHubCollaborators"/>), so
/// <see cref="ProjectGitHubMemberView.LastObservedAt"/> is the time of that member's last recorded
/// change, not the time it was last read: a member re-read unchanged on every later sweep still
/// shows the date of its first observation.
/// </para>
/// </summary>
public sealed class ProjectGitHubMembers
{
    /// <summary>The project this roster belongs to — named <c>Id</c> rather than <c>ProjectId</c> so Marten can find it as this document's own identity, the same convention every sibling single-stream projection in this codebase follows.</summary>
    public Guid Id { get; set; }
    public Dictionary<long, ProjectGitHubMemberView> Members { get; set; } = [];
}

public sealed class ProjectGitHubMembersProjection : SingleStreamProjection<ProjectGitHubMembers, Guid>
{
    public ProjectGitHubMembers Create(IEvent<ProjectGitHubAccessObserved> @event) => Upsert(new(), @event.Data);

    public void Apply(IEvent<ProjectGitHubAccessObserved> @event, ProjectGitHubMembers view) => Upsert(view, @event.Data);

    public ProjectGitHubMembers Create(IEvent<ProjectGitHubCollaboratorsObserved> @event) => ApplyRoster(new(), @event.Data);

    public void Apply(IEvent<ProjectGitHubCollaboratorsObserved> @event, ProjectGitHubMembers view) => ApplyRoster(view, @event.Data);

    private static ProjectGitHubMembers Upsert(ProjectGitHubMembers view, ProjectGitHubAccessObserved observed)
    {
        view.Id = observed.ProjectId;
        view.Members[observed.AccountId] = new ProjectGitHubMemberView(observed.AccountId, observed.Login, observed.Role, observed.ObservedAt);
        return view;
    }

    private static ProjectGitHubMembers ApplyRoster(ProjectGitHubMembers view, ProjectGitHubCollaboratorsObserved observed)
    {
        view.Id = observed.ProjectId;
        foreach (GitHubCollaboratorRole collaborator in observed.Collaborators)
        {
            view.Members[collaborator.AccountId] =
                new ProjectGitHubMemberView(collaborator.AccountId, collaborator.Login, collaborator.Role, observed.ObservedAt);
        }

        return view;
    }
}
