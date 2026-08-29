using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Epic;

/// <summary>
/// The one read model the epic slice needs: h9k epic list and h9k epic show both read it.
/// Epics are named groupings, not contracts, so there is no split between a lean row and a
/// detail document the way Task's volume made worth having.
/// </summary>
public sealed class EpicDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public EpicState State { get; set; } = EpicState.Unknown;
    /// <summary>The Jira epic this one points at, identity only; null until linked.</summary>
    public string? JiraReference { get; set; }
    public string? CloseReason { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public Guid AddedByOwnerId { get; set; }
}

public sealed class EpicDetailsProjection : SingleStreamProjection<EpicDetails, Guid>
{
    public EpicDetails Create(IEvent<EpicAdded> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        Title = @event.Data.Title,
        State = EpicState.Open,
        AddedAt = @event.Data.AddedAt,
        AddedByOwnerId = @event.Data.AddedByOwnerId,
    };

    public void Apply(IEvent<EpicLinkedToJira> @event, EpicDetails view) =>
        view.JiraReference = @event.Data.Reference;

    public void Apply(IEvent<EpicClosed> @event, EpicDetails view)
    {
        view.CloseReason = @event.Data.Reason;
        view.ClosedAt = @event.Data.ClosedAt;
        view.State = EpicState.Closed;
    }
}
