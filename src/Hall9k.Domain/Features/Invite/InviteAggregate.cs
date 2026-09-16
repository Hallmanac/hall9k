using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Invite;

public sealed class InviteAggregate
{
    public Guid Id { get; private set; }
    public Guid MinterNodeId { get; private set; }
    public string MinterOwnerFingerprint { get; private set; } = string.Empty;
    public InviteClaimKind Claim { get; private set; } = InviteClaimKind.Unknown;
    public ProjectMemberRole? Role { get; private set; }
    public Guid? ProjectId { get; private set; }
    public string? ProjectRepositoryPath { get; private set; }
    public string Secret { get; private set; } = string.Empty;
    public string SecretHash { get; private set; } = string.Empty;
    public DateTimeOffset MintedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public bool Spent { get; private set; }
    public DateTimeOffset? SpentAt { get; private set; }
    public Guid? ClaimedByNodeId { get; private set; }
    public string? ClaimedByRootFingerprint { get; private set; }

    /// <summary>Every project this invite's own vouch write already landed into, which candidate it
    /// vouched there, and the <c>issued_at</c> that write actually used — this node's own sole
    /// record of "already mine", checked instead of anything read back from ledger content
    /// (independent pre-PR review, cycle 6, adversarial lens, high). Recording the candidate's own
    /// identity alongside the project, not only the project by itself, is what lets the sweep tell a
    /// retry of this exact candidate apart from a different candidate that later starts matching the
    /// same still-outstanding invite — only the former may skip the collision guard (independent
    /// pre-PR review, cycle 1, adversarial lens, medium).</summary>
    public IReadOnlyDictionary<Guid, VouchedProjectRecord> VouchedProjects => _vouchedProjects;

    private readonly Dictionary<Guid, VouchedProjectRecord> _vouchedProjects = [];

    public void Apply(InviteMinted @event)
    {
        Id = @event.InviteId;
        MinterNodeId = @event.MinterNodeId;
        MinterOwnerFingerprint = @event.MinterOwnerFingerprint;
        Claim = @event.Claim;
        Role = @event.Role;
        ProjectId = @event.ProjectId;
        ProjectRepositoryPath = @event.ProjectRepositoryPath;
        Secret = @event.Secret;
        SecretHash = @event.SecretHash;
        MintedAt = @event.MintedAt;
        ExpiresAt = @event.ExpiresAt;
    }

    public void Apply(InviteSpent @event)
    {
        Spent = true;
        SpentAt = @event.SpentAt;
        ClaimedByNodeId = @event.ClaimedByNodeId;
        ClaimedByRootFingerprint = @event.ClaimedByRootFingerprint;
    }

    public void Apply(InviteProjectVouched @event)
    {
        _vouchedProjects[@event.ProjectId] = new VouchedProjectRecord(
            @event.VouchedAt, @event.CandidateNodeId, @event.CandidateKeyFingerprint, @event.CandidateOwnerFingerprint);
    }
}

/// <summary>One project's own vouch write, and which candidate it was written for — see
/// <see cref="InviteAggregate.VouchedProjects"/>.</summary>
public sealed record VouchedProjectRecord(
    DateTimeOffset VouchedAt, Guid CandidateNodeId, string CandidateKeyFingerprint, string CandidateOwnerFingerprint);
