using Hall9k.Domain.Features.Project;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Idea;

/// <summary>One version of the note and when it was written; the oldest entry is the capture.</summary>
public sealed class IdeaNote
{
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset WrittenAt { get; set; }
}

/// <summary>
/// The one read model the idea slice needs: h9k idea list and h9k idea show both read it.
/// Ideas are few and small (a note, not a contract), so splitting a lean list row off a
/// detail document would buy nothing the Task slice's volume made worth buying.
/// </summary>
public sealed class IdeaDetails
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    /// <summary>The current note — the newest entry in <see cref="History"/>.</summary>
    public string Text { get; set; } = string.Empty;
    public Guid? ProjectId { get; set; }
    public IdeaState State { get; set; } = IdeaState.Unknown;
    /// <summary>Every version the note has had, oldest first: how the thinking moved.</summary>
    public List<IdeaNote> History { get; set; } = [];
    /// <summary>
    /// Every task this idea has fanned out into, cut order (backlog 31). Includes the one task a
    /// legacy promotion named, so a promoted idea's own history reads as a fan-out of one rather
    /// than a gap.
    /// </summary>
    public List<Guid> CutTaskIds { get; set; } = [];
    /// <summary>Why discovery ended with something to show for it, or null on a legacy promotion, which never asked.</summary>
    public string? ConcludeReason { get; set; }
    public DateTimeOffset? ConcludedAt { get; set; }
    /// <summary>Why discovery ended with nothing to show for it.</summary>
    public string? ArchiveReason { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    /// <summary>The home the discovery workspace was captured under, or <see cref="ProjectHome.None"/> — see <see cref="IdeaCaptured"/>.</summary>
    public ProjectHome WorkspaceHome { get; set; } = ProjectHome.None;

    /// <summary>How many times the note was rewritten after capture.</summary>
    public int Revisions => Math.Max(History.Count - 1, 0);
}

public sealed class IdeaDetailsProjection : SingleStreamProjection<IdeaDetails, Guid>
{
    public IdeaDetails Create(IEvent<IdeaCaptured> @event) => new()
    {
        Id = @event.Data.Id,
        OwnerId = @event.Data.OwnerId,
        Text = @event.Data.Text,
        ProjectId = @event.Data.ProjectId,
        State = IdeaState.Captured,
        History = [new IdeaNote { Text = @event.Data.Text, WrittenAt = @event.Data.CapturedAt }],
        CapturedAt = @event.Data.CapturedAt,
        WorkspaceHome = ProjectHome.Parse(@event.Data.WorkspaceHomeDirectory),
    };

    public void Apply(IEvent<IdeaRevised> @event, IdeaDetails view)
    {
        view.Text = @event.Data.Text;
        view.History.Add(new IdeaNote { Text = @event.Data.Text, WrittenAt = @event.Data.RevisedAt });
    }

    public void Apply(IEvent<IdeaAssignedToProject> @event, IdeaDetails view) =>
        view.ProjectId = @event.Data.ProjectId;

    public void Apply(IEvent<IdeaTaskCut> @event, IdeaDetails view) =>
        view.CutTaskIds.Add(@event.Data.TaskId);

    public void Apply(IEvent<IdeaConcluded> @event, IdeaDetails view)
    {
        view.ConcludeReason = @event.Data.Reason;
        view.ConcludedAt = @event.Data.ConcludedAt;
        view.State = IdeaState.Concluded;
    }

    public void Apply(IEvent<IdeaArchived> @event, IdeaDetails view)
    {
        view.ArchiveReason = @event.Data.Reason;
        view.ArchivedAt = @event.Data.ArchivedAt;
        view.State = IdeaState.Archived;
    }

    /// <summary>Historical replay only — see <see cref="IdeaPromoted"/>'s own doc comment.</summary>
    public void Apply(IEvent<IdeaPromoted> @event, IdeaDetails view)
    {
        view.CutTaskIds.Add(@event.Data.TaskId);
        view.ProjectId = @event.Data.ProjectId;
        view.ConcludedAt = @event.Data.PromotedAt;
        view.State = IdeaState.Concluded;
    }

    /// <summary>Historical replay only — see <see cref="IdeaDiscarded"/>'s own doc comment.</summary>
    public void Apply(IEvent<IdeaDiscarded> @event, IdeaDetails view)
    {
        view.ArchiveReason = @event.Data.Reason;
        view.ArchivedAt = @event.Data.DiscardedAt;
        view.State = IdeaState.Archived;
    }
}
