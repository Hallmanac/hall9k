using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// The read side of <see cref="ProjectMemberLabelsObserved"/> (task b7d8222e): this project's own
/// current labels, as this node's own message sweep last computed them. Every surface that names a
/// member by their fingerprint or a node by its owning member resolves from this alone
/// (<see cref="MemberLabelResolver"/>) — never a live ledger read.
/// </summary>
public sealed class ProjectMemberLabels
{
    /// <summary>The project this roster belongs to — named <c>Id</c> rather than <c>ProjectId</c>
    /// so Marten can find it as this document's own identity, the same convention every sibling
    /// single-stream projection in this codebase follows.</summary>
    public Guid Id { get; set; }

    public IReadOnlyList<ProjectMemberLabel> Labels { get; set; } = [];
}

public sealed partial class ProjectMemberLabelsProjection : SingleStreamProjection<ProjectMemberLabels, Guid>
{
    public ProjectMemberLabels Create(IEvent<ProjectMemberLabelsObserved> @event) => Apply(new(), @event.Data);

    public void Apply(IEvent<ProjectMemberLabelsObserved> @event, ProjectMemberLabels view) => Apply(view, @event.Data);

    private static ProjectMemberLabels Apply(ProjectMemberLabels view, ProjectMemberLabelsObserved observed)
    {
        view.Id = observed.ProjectId;
        view.Labels = observed.Labels;
        return view;
    }
}
