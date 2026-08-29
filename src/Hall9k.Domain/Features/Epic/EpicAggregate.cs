namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// A named grouping of tasks: its own id, title, and open state, event-sourced like
/// everything else (Decisions Log #100). Membership itself is not tracked here — a
/// task records which epic it belongs to, not the other way around — so this aggregate stays
/// exactly as small as the concept it represents: a name, a project, and whether it is still
/// open.
/// </summary>
public sealed class EpicAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public EpicState State { get; private set; } = EpicState.Unknown;
    /// <summary>The Jira epic this one points at, identity only; null until linked.</summary>
    public string? JiraReference { get; private set; }
    public string? CloseReason { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
    public Guid AddedByOwnerId { get; private set; }

    public void Apply(EpicAdded @event)
    {
        Id = @event.Id;
        ProjectId = @event.ProjectId;
        Title = @event.Title;
        AddedAt = @event.AddedAt;
        AddedByOwnerId = @event.AddedByOwnerId;
        State = EpicState.Open;
    }

    public void Apply(EpicLinkedToJira @event) => JiraReference = @event.Reference;

    public void Apply(EpicClosed @event)
    {
        CloseReason = @event.Reason;
        State = EpicState.Closed;
    }
}
