using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Owner;

public sealed class OwnerDetails
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class OwnerDetailsProjection : SingleStreamProjection<OwnerDetails, Guid>
{
    public OwnerDetails Create(IEvent<OwnerRegistered> @event) => new()
    {
        Id = @event.Data.Id,
        Name = @event.Data.Name,
        Email = @event.Data.Email,
        RegisteredAt = @event.Data.RegisteredAt,
    };
}
