using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Owner;

public sealed class OwnerDetails
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    /// <summary>
    /// The owner's standing answer to "ask the reviewers to look again after fixes push?"
    /// (Decisions Log #62). Unknown means they never said, so the project setting or the
    /// node default decides.
    /// </summary>
    public ReviewRerequestPolicy ReviewRerequest { get; set; } = ReviewRerequestPolicy.Unknown;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? SettingsChangedAt { get; set; }

    /// <summary>Mirrors <see cref="OwnerAggregate.RootFingerprint"/>.</summary>
    public string? RootFingerprint { get; set; }

    /// <summary>Mirrors <see cref="OwnerAggregate.RootFingerprintVerified"/>.</summary>
    public bool RootFingerprintVerified { get; set; }

    public DateTimeOffset? RootClaimedAt { get; set; }
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

    public void Apply(IEvent<OwnerSettingsChanged> @event, OwnerDetails view)
    {
        if (@event.Data.ReviewRerequest.HasValue)
        {
            view.ReviewRerequest = @event.Data.ReviewRerequest.Value ?? ReviewRerequestPolicy.Unknown;
        }

        view.SettingsChangedAt = @event.Data.ChangedAt;
    }

    public void Apply(IEvent<OwnerRootClaimed> @event, OwnerDetails view)
    {
        view.RootFingerprint = @event.Data.RootFingerprint;
        view.RootFingerprintVerified = @event.Data.Verified;
        view.RootClaimedAt = @event.Data.ClaimedAt;
    }
}
