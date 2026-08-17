using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Node;

public sealed class NodeDetails
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class NodeDetailsProjection : SingleStreamProjection<NodeDetails, Guid>
{
    public NodeDetails Create(IEvent<NodeRegistered> @event) => new()
    {
        Id = @event.Data.Id,
        OwnerId = @event.Data.OwnerId,
        MachineName = @event.Data.MachineName,
        OperatingSystem = @event.Data.OperatingSystem,
        RegisteredAt = @event.Data.RegisteredAt,
    };
}
