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

    /// <summary>
    /// The skill this owner writes in, named by its directory name and never copied here
    /// (PLACEHOLDER-ef2ba8b3). Every prompt seam where a session composes text a human will read
    /// as this owner's — a pull request description, a review-thread reply, a commit message, a
    /// drafted reply — tells the session to load it before writing.
    /// <see cref="VoiceSkillName.None"/> until they name one, which renders every seam exactly as
    /// it renders with no preference at all.
    /// </summary>
    public VoiceSkillName VoiceSkill { get; private set; } = VoiceSkillName.None;

    public DateTimeOffset RegisteredAt { get; private set; }

    /// <summary>
    /// This owner's cross-node identity (idea 202383dc, A2a): the ed25519 fingerprint of the root
    /// key one of this owner's nodes established or claimed on their behalf. Null until the first
    /// <c>h9k project join</c> from any of this owner's nodes.
    /// </summary>
    public string? RootFingerprint { get; private set; }

    /// <summary>
    /// Whether <see cref="RootFingerprint"/> is this owner's own key (a node here established it
    /// with no <c>--owner</c>) rather than a claim awaiting the team half's vouch (not yet built).
    /// </summary>
    public bool RootFingerprintVerified { get; private set; }

    public DateTimeOffset? RootClaimedAt { get; private set; }

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

        if (@event.VoiceSkill.HasValue)
        {
            VoiceSkill = @event.VoiceSkill.Value ?? VoiceSkillName.None;
        }
    }

    public void Apply(OwnerRootClaimed @event)
    {
        RootFingerprint = @event.RootFingerprint;
        RootFingerprintVerified = @event.Verified;
        RootClaimedAt = @event.ClaimedAt;
    }
}
