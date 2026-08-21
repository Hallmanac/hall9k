using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// The owner changed a standing preference that applies to every project they own
/// (Decisions Log #62). Each setting is <see cref="Optional{T}"/> so an unmentioned one is
/// left alone rather than reset to a default the command never asked for — the
/// ProjectSettingsChanged shape, for the same reason.
/// </summary>
public sealed record OwnerSettingsChanged(
    Guid Id,
    Optional<ReviewRerequestPolicy> ReviewRerequest,
    DateTimeOffset ChangedAt);
