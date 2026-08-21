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
    /// <summary>The draft this idea became; null unless it was promoted.</summary>
    public Guid? PromotedTaskId { get; set; }
    public DateTimeOffset? PromotedAt { get; set; }
    public string? DiscardReason { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; }

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
    };

    public void Apply(IEvent<IdeaRevised> @event, IdeaDetails view)
    {
        view.Text = @event.Data.Text;
        view.History.Add(new IdeaNote { Text = @event.Data.Text, WrittenAt = @event.Data.RevisedAt });
    }

    public void Apply(IEvent<IdeaAssignedToProject> @event, IdeaDetails view) =>
        view.ProjectId = @event.Data.ProjectId;

    public void Apply(IEvent<IdeaPromoted> @event, IdeaDetails view)
    {
        view.PromotedTaskId = @event.Data.TaskId;
        view.ProjectId = @event.Data.ProjectId;
        view.PromotedAt = @event.Data.PromotedAt;
        view.State = IdeaState.Promoted;
    }

    public void Apply(IEvent<IdeaDiscarded> @event, IdeaDetails view)
    {
        view.DiscardReason = @event.Data.Reason;
        view.DiscardedAt = @event.Data.DiscardedAt;
        view.State = IdeaState.Discarded;
    }
}
