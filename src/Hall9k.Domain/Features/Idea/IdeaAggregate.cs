namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// One thought, from capture through discovery to whatever it became. Small on purpose: the
/// idea holds its note, its project (when it has one), and its ending — everything discovery
/// produces lives in the workspace directory on disk, not here (Decisions Log #35).
/// </summary>
public sealed class IdeaAggregate
{
    public Guid Id { get; private set; }
    /// <summary>Whose thought this is. Ideas are owner-scoped from the first keystroke.</summary>
    public Guid OwnerId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    /// <summary>Null until an idea turns out to belong somewhere; an honest absence, not a gap.</summary>
    public Guid? ProjectId { get; private set; }
    public IdeaState State { get; private set; } = IdeaState.Unknown;
    /// <summary>How many times the note has been rewritten — the shape of the discovery so far.</summary>
    public int Revisions { get; private set; }
    /// <summary>The draft this idea became; null unless it was promoted.</summary>
    public Guid? PromotedTaskId { get; private set; }
    public string? DiscardReason { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }

    public void Apply(IdeaCaptured @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        Text = @event.Text;
        ProjectId = @event.ProjectId;
        CapturedAt = @event.CapturedAt;
        State = IdeaState.Captured;
    }

    public void Apply(IdeaRevised @event)
    {
        Text = @event.Text;
        Revisions++;
    }

    public void Apply(IdeaAssignedToProject @event) => ProjectId = @event.ProjectId;

    public void Apply(IdeaPromoted @event)
    {
        PromotedTaskId = @event.TaskId;
        ProjectId = @event.ProjectId;
        State = IdeaState.Promoted;
    }

    public void Apply(IdeaDiscarded @event)
    {
        DiscardReason = @event.Reason;
        State = IdeaState.Discarded;
    }
}
