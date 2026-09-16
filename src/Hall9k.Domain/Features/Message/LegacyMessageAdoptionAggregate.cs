namespace Hall9k.Domain.Features.Message;

/// <summary>This install's own single stream for <see cref="LegacyMessageAdoptionAssigned"/>, keyed
/// by <c>Hall9k.Connectors.Messaging.MessageStreamId.ForLegacyAdoption</c> — never more than one
/// event ever lands here, since the decider that appends it only ever starts this stream once
/// (<c>Hall9k.Connectors.Messaging.LegacyMessageAdoption.AssignAsync</c>'s own doc).</summary>
public sealed class LegacyMessageAdoptionAggregate
{
    /// <summary>The stream id itself (<c>MessageStreamId.ForLegacyAdoption</c>) — Marten's own live
    /// aggregation needs a settable <c>Id</c> to compile this type's schema, even though nothing
    /// here is ever stored as a document.</summary>
    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }

    public void Apply(LegacyMessageAdoptionAssigned @event)
    {
        Id = MessageStreamId.ForLegacyAdoption();
        ProjectId = @event.ProjectId;
        AssignedAt = @event.At;
    }
}
