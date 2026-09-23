using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// The read view of one courier run (idea 89471598, piece 3): what the daemon's own spawn gate
/// reads to answer "is a courier already running for this project" and "when did one last
/// deliver", and what <c>h9k status</c> reads to print the in-flight and last-delivery lines.
/// <see cref="CompletedAt"/> null means still running — the daemon's own gate refuses to spawn a
/// second courier for a project while one exists in that state.
/// </summary>
public sealed class CourierRunDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid NodeId { get; set; }
    public string Model { get; set; } = string.Empty;
    public DateTimeOffset DispatchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Delivered { get; set; }
    public string Outcome { get; set; } = string.Empty;
}

public sealed partial class CourierRunDetailsProjection : SingleStreamProjection<CourierRunDetails, Guid>
{
    public CourierRunDetails Create(IEvent<CourierRunDispatched> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        NodeId = @event.Data.NodeId,
        Model = @event.Data.Model.Value,
        DispatchedAt = @event.Data.DispatchedAt,
    };

    public void Apply(IEvent<CourierRunCompleted> @event, CourierRunDetails view)
    {
        view.CompletedAt = @event.Data.CompletedAt;
        view.Delivered = @event.Data.Delivered;
        view.Outcome = @event.Data.Outcome;
    }

    // CourierTokensRecorded carries no state this view answers questions about (h9k status'
    // spend line reads it directly through PeriodSpend, the same way TokensRecorded and
    // PublicationTokensRecorded do) — an explicit no-op Apply rather than an absent one, so a
    // reader of this projection does not have to go check whether the event was simply
    // forgotten.
    public void Apply(IEvent<CourierTokensRecorded> @event, CourierRunDetails view)
    {
    }
}
