using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Owner;

public sealed class OwnerAggregate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    /// <summary>
    /// Whether this owner's pull requests ask their reviewers for another pass after a fix
    /// follow-up pushed (Decisions Log #62). Unknown defers to the node default; a project
    /// setting outranks it.
    /// </summary>
    public ReviewRerequestPolicy ReviewRerequest { get; private set; } = ReviewRerequestPolicy.Unknown;
    public DateTimeOffset RegisteredAt { get; private set; }

    public void Apply(OwnerRegistered @event)
    {
        Id = @event.Id;
        Name = @event.Name;
        Email = @event.Email;
        RegisteredAt = @event.RegisteredAt;
    }

    public void Apply(OwnerSettingsChanged @event)
    {
        if (@event.ReviewRerequest.HasValue)
        {
            ReviewRerequest = @event.ReviewRerequest.Value ?? ReviewRerequestPolicy.Unknown;
        }
    }
}
