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
    /// session to load (#193). <see cref="VoiceSkillName.None"/> is a legal
    /// explicit value on the same terms Unknown is above: it clears the preference, which is what
    /// <c>--clear-voice-skill</c> records. Whether the name resolves to a skill on THIS machine is
    /// deliberately not checked here — that is a filesystem question the CLI answers where the
    /// human types it (<see cref="Hall9k.Domain.Infrastructure.Storage.VoiceSkillLocation"/>), and
    /// an owner's nodes do not all have the same directories.
    /// </param>
    /// <param name="reviewPersonas">
    /// The review personas this owner declares (idea b9b09779, piece 1). An empty list is a legal
    /// explicit value on the same terms Unknown and <see cref="VoiceSkillName.None"/> are above: it
    /// clears the declaration, which is what <c>--clear-personas</c> records, and reads as the
    /// engineer's review everywhere. Normalized here rather than at the command, so every writer
    /// records the same canonical set: recognized personas once each, in
    /// <see cref="ReviewPersona.All"/>'s fixed order. A word nobody could read is refused rather
    /// than silently dropped — an owner who typed <c>--persona qaa</c> must not walk away believing
    /// they declared a QA review.
    /// </param>
    public static OwnerSettingsChanged ChangeSettings(
        OwnerAggregate owner,
        Optional<ReviewRerequestPolicy> reviewRerequest,
        DateTimeOffset changedAt,
        Optional<VoiceSkillName> voiceSkill = default,
        Optional<IReadOnlyList<ReviewPersona>> reviewPersonas = default)
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

        // An unreadable persona reaching here carries no word to quote back — ReviewPersona.Unknown
        // serializes as the empty string, and the word the human actually typed was already
        // refused by ReviewPersona.Parse where they typed it. Refused rather than silently dropped
        // all the same: an owner must never walk away believing they declared a review nothing
        // recorded.
        if (reviewPersonas.HasValue
            && reviewPersonas.Value?.Any(persona => persona is not { HasValue: true }) == true)
        {
            throw new DomainValidationException(
                "One of these review personas is not one the platform recognizes. The set is fixed: "
                + $"{string.Join(", ", ReviewPersona.All.Select(persona => persona.Value))} — each one maps "
                + "to its own review prompt and criteria in the platform's persona registry "
                + "(idea b9b09779). Declaring none reads as the engineer's review.");
        }

        // A voice skill's own shape is VoiceSkillName.Parse's rule, enforced where the string is
        // parsed; nothing is left for this decider to re-check, since a name that got this far is
        // either well-formed or None.
        return new OwnerSettingsChanged(
            owner.Id,
            reviewRerequest,
            changedAt,
            voiceSkill,
            reviewPersonas.HasValue
                ? Optional<IReadOnlyList<ReviewPersona>>.Of(ReviewPersona.Declared(reviewPersonas.Value))
                : Optional<IReadOnlyList<ReviewPersona>>.None);
    }

    public static OwnerRootClaimed ClaimRoot(OwnerAggregate owner, string rootFingerprint, bool verified, DateTimeOffset claimedAt)
    {
        if (rootFingerprint.IsBlank())
        {
            throw new DomainValidationException("An owner's root claim needs the fingerprint it is claiming.");
        }

        return new OwnerRootClaimed(owner.Id, rootFingerprint, verified, claimedAt);
    }

    /// <summary>
    /// Flips an already-claimed root to verified from a live chain read (task f53fecfd, criterion
    /// 4) — never the reverse: nothing here ever un-verifies a root, and a caller with no chain
    /// evidence simply never calls this at all. Refused when there is no claim yet to verify:
    /// reconciliation only ever runs against an owner that already has <see cref="OwnerAggregate.RootFingerprint"/>
    /// set, so reaching here with none is a caller defect, not a legitimate "nothing to verify yet".
    /// </summary>
    public static OwnerRootVerified VerifyRoot(OwnerAggregate owner, DateTimeOffset verifiedAt)
    {
        if (owner.RootFingerprint is null)
        {
            throw new DomainValidationException("An owner's root cannot be verified before it has claimed one.");
        }

        return new OwnerRootVerified(owner.Id, owner.RootFingerprint, verifiedAt);
    }

    public static NodeVouched VouchNode(OwnerAggregate owner, Guid nodeId, string nodeFingerprint, DateTimeOffset issuedAt)
    {
        if (nodeId == Guid.Empty)
        {
            throw new DomainValidationException("A node vouch needs the node's own id.");
        }

        if (nodeFingerprint.IsBlank())
        {
            throw new DomainValidationException("A node vouch needs the node's own key fingerprint.");
        }

        return new NodeVouched(owner.Id, nodeId, nodeFingerprint, issuedAt);
    }

    public static NodeRevoked RevokeNode(OwnerAggregate owner, Guid nodeId, DateTimeOffset revokedAt)
    {
        if (nodeId == Guid.Empty)
        {
            throw new DomainValidationException("A node revocation needs the node's own id.");
        }

        return new NodeRevoked(owner.Id, nodeId, revokedAt);
    }
}
