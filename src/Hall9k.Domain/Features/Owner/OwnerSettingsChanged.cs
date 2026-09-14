using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Owner;

/// <summary>
/// The owner changed a standing preference that applies to every project they own
/// (Decisions Log #62). Each setting is <see cref="Optional{T}"/> so an unmentioned one is
/// left alone rather than reset to a default the command never asked for — the
/// ProjectSettingsChanged shape, for the same reason.
/// </summary>
/// <param name="VoiceSkill">
/// The skill whose prose style every prompt seam that writes text a human reads as this owner's
/// tells the session to load first, by name (#193). Appended after
/// <paramref name="ChangedAt"/> rather than beside its sibling setting so an Owner stream written
/// before this field deserializes into <see cref="Optional{T}.None"/> — "they never said" — rather
/// than shifting a positional argument.
/// </param>
public sealed record OwnerSettingsChanged(
    Guid Id,
    Optional<ReviewRerequestPolicy> ReviewRerequest,
    DateTimeOffset ChangedAt,
    Optional<VoiceSkillName> VoiceSkill = default);
