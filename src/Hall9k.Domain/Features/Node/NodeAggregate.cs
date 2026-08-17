namespace Hall9k.Domain.Features.Node;

public sealed class NodeAggregate
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string MachineName { get; private set; } = string.Empty;
    public string OperatingSystem { get; private set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; private set; }

    public void Apply(NodeRegistered @event)
    {
        Id = @event.Id;
        OwnerId = @event.OwnerId;
        MachineName = @event.MachineName;
        OperatingSystem = @event.OperatingSystem;
        RegisteredAt = @event.RegisteredAt;
    }
}
