namespace Hall9k.Domain.Features.Owner;

public sealed class OwnerAggregate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }

    public void Apply(OwnerRegistered @event)
    {
        Id = @event.Id;
        Name = @event.Name;
        Email = @event.Email;
        RegisteredAt = @event.RegisteredAt;
    }
}
