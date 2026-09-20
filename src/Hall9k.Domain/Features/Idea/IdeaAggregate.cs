using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// One thought, from capture through discovery to whatever it became. Small on purpose: the
/// idea holds its note, its project (when it has one), the tasks discovery has fanned out into
/// so far, and its ending — everything discovery itself produces lives in the workspace
/// directory on disk, not here (Decisions Log #35).
/// </summary>
public sealed class IdeaAggregate
{
    private readonly List<Guid> cutTaskIds = [];

    public Guid Id { get; private set; }
    /// <summary>Whose thought this is. Ideas are owner-scoped from the first keystroke.</summary>
    public Guid OwnerId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    /// <summary>Null until an idea turns out to belong somewhere; an honest absence, not a gap.</summary>
    public Guid? ProjectId { get; private set; }
    public IdeaState State { get; private set; } = IdeaState.Unknown;
    /// <summary>How many times the note has been rewritten — the shape of the discovery so far.</summary>
    public int Revisions { get; private set; }
    /// <summary>
    /// Every task this idea has fanned out into, cut order. Repeatable — an idea is not "used up"
    /// by its first cut, because discovery may keep producing (Brian, 2026-08-21). Includes the
    /// one task a legacy <see cref="IdeaPromoted"/> named, so a promoted idea's own history reads
    /// as a fan-out of one rather than a gap.
    /// </summary>
    public IReadOnlyList<Guid> CutTaskIds => cutTaskIds;
    /// <summary>Why discovery ended with something to show for it, or null on a legacy promotion, which never asked.</summary>
    public string? ConcludeReason { get; private set; }
    public DateTimeOffset? ConcludedAt { get; private set; }
    /// <summary>Why discovery ended with nothing to show for it.</summary>
    public string? ArchiveReason { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }
    /// <summary>The home the discovery workspace was captured under, or <see cref="ProjectHome.None"/> — see <see cref="IdeaCaptured"/>.</summary>
    public ProjectHome WorkspaceHome { get; private set; } = ProjectHome.None;
    /// <summary>
    /// How far this idea's own events travel (idea 8c5993c5): <see cref="ReplicationScope.Private"/>
    /// never leaves this node, <see cref="ReplicationScope.Fleet"/> reaches every node this same
    /// owner runs, <see cref="ReplicationScope.Team"/> reaches every project member's own fleet.
    /// Defaults to <see cref="ReplicationScope.Team"/> — every idea captured before this field
    /// existed replays at the scope it already had under the old private/not-private flag, since a
    /// not-private idea was already fully team-visible. <see cref="Apply(IdeaCaptured)"/> reads a
    /// fresh capture's own <see cref="IdeaCaptured.InitialScope"/> instead, which every capture from
    /// here on names <see cref="ReplicationScope.Fleet"/>.
    /// </summary>
    public ReplicationScope Scope { get; private set; } = ReplicationScope.Team;

    /// <summary>The pre-8c5993c5 two-valued read of <see cref="Scope"/>, kept for callers that only ever asked "private or not".</summary>
    public bool IsPrivate => Scope == ReplicationScope.Private;

    public void Apply(IdeaCaptured @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        Text = @event.Text;
        ProjectId = @event.ProjectId;
        CapturedAt = @event.CapturedAt;
        WorkspaceHome = ProjectHome.Parse(@event.WorkspaceHomeDirectory);
        State = IdeaState.Captured;
        Scope = @event.InitialScope ?? ReplicationScope.Team;
    }

    public void Apply(IdeaRevised @event)
    {
        Text = @event.Text;
        Revisions++;
    }

    public void Apply(IdeaAssignedToProject @event) => ProjectId = @event.ProjectId;

    public void Apply(IdeaTaskCut @event) => cutTaskIds.Add(@event.TaskId);

    /// <summary>
    /// Bookkeeping only — <c>h9k idea show</c> renders a spike's verdict off the task's own live
    /// projection, joined through <see cref="CutTaskIds"/>, never off this replay (see
    /// <see cref="IdeaSpikeConcluded"/>'s own doc for why).
    /// </summary>
    public void Apply(IdeaSpikeConcluded @event) { }

    public void Apply(IdeaConcluded @event)
    {
        ConcludeReason = @event.Reason;
        ConcludedAt = @event.ConcludedAt;
        State = IdeaState.Concluded;
    }

    public void Apply(IdeaArchived @event)
    {
        ArchiveReason = @event.Reason;
        ArchivedAt = @event.ArchivedAt;
        State = IdeaState.Archived;
    }

    /// <summary>Historical replay only — see <see cref="IdeaPromoted"/>'s own doc comment.</summary>
    public void Apply(IdeaPromoted @event)
    {
        cutTaskIds.Add(@event.TaskId);
        ProjectId = @event.ProjectId;
        ConcludedAt = @event.PromotedAt;
        State = IdeaState.Concluded;
    }

    /// <summary>Historical replay only — see <see cref="IdeaDiscarded"/>'s own doc comment.</summary>
    public void Apply(IdeaDiscarded @event)
    {
        ArchiveReason = @event.Reason;
        ArchivedAt = @event.DiscardedAt;
        State = IdeaState.Archived;
    }

    /// <summary>
    /// Historical replay only — every write from idea 8c5993c5 on appends <see cref="IdeaScopeSet"/>
    /// instead. A cleared flag reads as <see cref="ReplicationScope.Team"/>, never
    /// <see cref="ReplicationScope.Fleet"/>: before fleet scope existed, "not private" already meant
    /// fully team-visible, and an existing idea keeps that effective scope rather than being quietly
    /// narrowed by a field it never asked for.
    /// </summary>
    public void Apply(IdeaPrivacySet @event) => Scope = @event.IsPrivate ? ReplicationScope.Private : ReplicationScope.Team;

    public void Apply(IdeaScopeSet @event) => Scope = @event.Scope;
}
