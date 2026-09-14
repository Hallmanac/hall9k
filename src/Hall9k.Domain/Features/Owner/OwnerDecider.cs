using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Owner;

public static class OwnerDecider
{
    public static OwnerRegistered Register(Guid id, string name, string? email, DateTimeOffset registeredAt)
    {
        if (name.IsBlank())
        {
            throw new DomainValidationException("An owner requires a name — every node belongs to a human (PLAN.md §6.2).");
        }

        return new OwnerRegistered(id, name, email, registeredAt);
    }

    /// <param name="voiceSkill">
    /// The skill name every prompt seam that writes text a human reads as this owner's tells the
    /// session to load (PLACEHOLDER-ef2ba8b3). <see cref="VoiceSkillName.None"/> is a legal
    /// explicit value on the same terms Unknown is above: it clears the preference, which is what
    /// <c>--clear-voice-skill</c> records. Whether the name resolves to a skill on THIS machine is
    /// deliberately not checked here — that is a filesystem question the CLI answers where the
    /// human types it (<see cref="Hall9k.Domain.Infrastructure.Storage.VoiceSkillLocation"/>), and
    /// an owner's nodes do not all have the same directories.
    /// </param>
    public static OwnerSettingsChanged ChangeSettings(
        OwnerAggregate owner,
        Optional<ReviewRerequestPolicy> reviewRerequest,
        DateTimeOffset changedAt,
        Optional<VoiceSkillName> voiceSkill = default)
    {
        // Unknown is a legal explicit value: it clears the owner's preference so the
        // project setting or the node default decides again (the CommitStyle convention).
        if (reviewRerequest.HasValue
            && reviewRerequest.Value is { } policy
            && policy != ReviewRerequestPolicy.Unknown
            && policy != ReviewRerequestPolicy.Enabled
            && policy != ReviewRerequestPolicy.Disabled)
        {
            throw new DomainValidationException(
                $"The review re-request policy must be {ReviewRerequestPolicy.Enabled} or "
                + $"{ReviewRerequestPolicy.Disabled} (whether closeout asks the reviewers for another "
                + "pass after a fix follow-up pushes, Decisions Log #62).");
        }

        // A voice skill's own shape is VoiceSkillName.Parse's rule, enforced where the string is
        // parsed; nothing is left for this decider to re-check, since a name that got this far is
        // either well-formed or None.
        return new OwnerSettingsChanged(owner.Id, reviewRerequest, changedAt, voiceSkill);
    }

    public static OwnerRootClaimed ClaimRoot(OwnerAggregate owner, string rootFingerprint, bool verified, DateTimeOffset claimedAt)
    {
        if (rootFingerprint.IsBlank())
        {
            throw new DomainValidationException("An owner's root claim needs the fingerprint it is claiming.");
        }

        return new OwnerRootClaimed(owner.Id, rootFingerprint, verified, claimedAt);
    }
}
