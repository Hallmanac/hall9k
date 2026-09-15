namespace Hall9k.Connectors.Trust;

/// <summary>A project member's role — owner or member, idea 202383dc's own closed pair (PLAN.md
/// bearings: "Roles are owner and member only").</summary>
public enum MembershipRole
{
    Owner,
    Member,
}

/// <summary>One node vouched into an owner's fleet — currently active (not the latest thing on its
/// own path was a revocation), key included since that is what a signature is actually checked
/// against.</summary>
public sealed record TrustedNode(string NodeId, string PublicKeyLine, string Fingerprint, DateTimeOffset IssuedAt);

/// <summary>
/// One owner root's own chain: its self-certified root key, plus every node currently vouched into
/// it (latest of vouch or revocation, in ref commit order, already resolved — a revoked node is
/// simply absent here). Computed entirely from <c>refs/hall9k/ledger/owners/&lt;root&gt;</c>,
/// independent of whether this root happens to be a project member — that gate is
/// <see cref="TrustChain.IsAllowedSigner"/>'s, not this type's.
/// </summary>
public sealed record TrustedOwner(string RootFingerprint, string RootPublicKeyLine, IReadOnlyList<TrustedNode> Nodes)
{
    /// <summary>Whether <paramref name="fingerprint"/> is this root's own key or a currently vouched node's.</summary>
    public bool Contains(string fingerprint) =>
        RootFingerprint == fingerprint || Nodes.Any(node => node.Fingerprint == fingerprint);
}

/// <summary>One project member, chain-validated: the write that established or last changed this
/// entry was itself signed by a key already trusted at that point in the members ref's own
/// history (or, for the very first entry, self-written — idea 202383dc's genesis rule).</summary>
public sealed record ProjectMember(string RootFingerprint, MembershipRole Role, DateTimeOffset IssuedAt);

/// <summary>
/// The result of walking a project's ledger — every owner root's own chain, and current project
/// membership derived from replaying <c>refs/hall9k/ledger/members</c> against those chains (idea
/// 202383dc, T1). Recomputed fresh on every read (<see cref="ILedgerChainReader.ComputeAsync"/>),
/// never cached across calls: a revocation or a removal takes effect the moment the next read walks
/// the ledger again.
/// </summary>
public sealed record TrustChain(IReadOnlyDictionary<string, TrustedOwner> OwnerChains, IReadOnlyList<ProjectMember> Members)
{
    public static readonly TrustChain Empty = new(new Dictionary<string, TrustedOwner>(), []);

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is currently allowed to write or sign a ledger or
    /// messages ref for this project: it belongs to some owner's chain, and that owner is itself a
    /// current project member — a stranger's own self-certified root and vouched nodes are always
    /// excluded here, however internally consistent their own owner ref is, because they were never
    /// added to <see cref="Members"/>.
    /// </summary>
    public bool IsAllowedSigner(string fingerprint) =>
        Members.Any(member =>
            OwnerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner) && owner.Contains(fingerprint));

    /// <summary>
    /// Whether <paramref name="fingerprint"/> is already enrolled in <paramref name="root"/>'s own
    /// chain — regardless of project membership, the rule <c>h9k node vouch</c>/<c>revoke</c> is
    /// refused against ("any enrolled node of that owner", idea 202383dc): vouching a node into an
    /// owner is that owner's own fact, prior to and independent of whether the owner has been made
    /// a member of any particular project.
    /// </summary>
    public bool IsEnrolledInOwner(string fingerprint, string root) =>
        OwnerChains.TryGetValue(root, out TrustedOwner? owner) && owner.Contains(fingerprint);

    public MembershipRole? RoleOf(string root) =>
        Members.FirstOrDefault(member => member.RootFingerprint == root) is { } found ? found.Role : null;
}

/// <summary>
/// Computes <see cref="TrustChain"/> for a project's ledger — the one place chain verification
/// happens (idea 202383dc, T1): allowed signers are roots plus nodes vouched into them minus
/// revocations, in ref order, restricted to owners the members ref itself currently recognizes.
/// <see cref="GitLedgerChainReader"/> is the real implementation, over real git plumbing (the
/// second place, besides A1's own <c>GitLedgerTests</c>, a throwaway bare repository is used in
/// tests — Brian's 2026-09-13 testing rule).
/// </summary>
public interface ILedgerChainReader
{
    Task<TrustChain> ComputeAsync(string repositoryPath, CancellationToken cancellationToken);
}
