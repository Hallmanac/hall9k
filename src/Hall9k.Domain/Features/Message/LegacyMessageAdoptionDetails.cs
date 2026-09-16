using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Message;

/// <summary>
/// <c>Hall9k.Connectors.Messaging.LegacyMessageAdoption.IsAdoptingProjectAsync</c>'s own read view of
/// the permanent legacy-adoption decision — a plain document query rather than
/// <c>LegacyMessageAdoptionAggregate</c> itself, since that call only ever has an
/// <c>IQuerySession</c> (a read-only caller such as <c>MessageOutbox.NextSeqAsync</c> never needs a
/// full <c>IDocumentSession</c> just to ask this question).
/// </summary>
public sealed class LegacyMessageAdoptionDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
}

public sealed class LegacyMessageAdoptionDetailsProjection : SingleStreamProjection<LegacyMessageAdoptionDetails, Guid>
{
    public LegacyMessageAdoptionDetails Create(IEvent<LegacyMessageAdoptionAssigned> @event) => new()
    {
        Id = @event.StreamId,
        ProjectId = @event.Data.ProjectId,
        AssignedAt = @event.Data.At,
    };
}
