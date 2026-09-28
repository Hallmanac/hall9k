namespace Hall9k.Connectors.Trust;

/// <summary>
/// The shared "is this write authorized by an owner's own chain" rules
/// (<see cref="GitLedgerChainReader"/>'s own <c>IsAuthorizedByOwnerChainAsync</c>, idea 6be68ee2
/// trust-ledger finding 6) — never a snapshot, always the chain as <paramref name="owner"/> already
/// reports it right now. Two rules live here, one wider than the other:
/// <see cref="IsAuthorizedByOwnerAsync"/> ("root or vouched") and, since idea 6be68ee2's own
/// trust-ledger finding 2, the narrower <see cref="IsAuthorizedByOwnerRootKeyAsync"/> ("root key
/// only") a node revocation, a members-ref write, and a role change require. Pulled out here so a
/// second caller outside <see cref="GitLedgerChainReader"/> (a prompt-addenda materialize walk over
/// a DIFFERENT ledger ref, using a raw-commit-bytes signature check rather than
/// <see cref="GitLedgerChainReader"/>'s own sha-based one) can apply the identical rule without
/// duplicating it — <paramref name="isSignedByKeyAsync"/> is the one seam that differs between the
/// two callers' own signature-check shapes.
/// </summary>
public static class OwnerChainAuthorization
{
    /// <summary>Whether a commit is authorized by exactly one owner's own chain: signed by any of
    /// that root's own live keys (idea 6be68ee2's ranked root-key set — K0 or a validated rotation,
    /// never only whichever key is "current"), or by any node currently vouched into it. Stays this
    /// wide — "root or vouched" — for its own remaining callers (<c>PromptAddendaSweepEngine.MaterializeAsync</c>,
    /// via <see cref="IsAuthorizedByAnyOwnerRoleMemberAsync"/>) even though a node revocation, a
    /// members-ref write, or a role change narrowed to <see cref="IsAuthorizedByOwnerRootKeyAsync"/>
    /// alone (idea 6be68ee2, trust-ledger finding 2): a prompt addendum pushed from a vouched node
    /// (an ARX Windows machine, say) must keep materializing.</summary>
    public static async Task<bool> IsAuthorizedByOwnerAsync(
        TrustedOwner owner, Func<string, CancellationToken, Task<bool>> isSignedByKeyAsync,
        CancellationToken cancellationToken)
    {
        if (await IsAuthorizedByOwnerRootKeyAsync(owner, isSignedByKeyAsync, cancellationToken))
        {
            return true;
        }

        foreach (TrustedNode node in owner.Nodes)
        {
            if (await isSignedByKeyAsync(node.PublicKeyLine, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The narrower half of <see cref="IsAuthorizedByOwnerAsync"/>: whether a commit is signed by
    /// any of <paramref name="owner"/>'s own live root keys (idea 6be68ee2's ranked root-key set —
    /// K0 or a validated rotation) — never by a node merely vouched into its fleet. This, not the
    /// wider rule, is what a node revocation, a members-ref write, or a role change requires (idea
    /// 6be68ee2, trust-ledger finding 2): a compromised fleet node must never revoke its own peers
    /// or rewrite membership on its own say-so. Used both by <see cref="GitLedgerChainReader"/>'s
    /// own read-time authorization and, via <c>TrustChain.IsLiveRootKeyOfOwner</c>, by the CLI
    /// commands that write these three shapes, so a command refuses before any push against the
    /// identical test the reader will apply anyway.
    /// </summary>
    public static async Task<bool> IsAuthorizedByOwnerRootKeyAsync(
        TrustedOwner owner, Func<string, CancellationToken, Task<bool>> isSignedByKeyAsync,
        CancellationToken cancellationToken)
    {
        foreach (LiveRootKey rootKey in owner.RootKeys)
        {
            if (await isSignedByKeyAsync(rootKey.PublicKeyLine, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a commit is authorized by SOME member whose <see cref="TrustChain.RoleOf"/> is
    /// currently <see cref="MembershipRole.Owner"/> — the rule a caller with no single owner already
    /// in mind needs (<c>PromptAddendaSweepEngine.MaterializeAsync</c>'s own owner test): every
    /// Owner-role member's own chain is tried in turn, root key first then its vouched nodes, until
    /// one authorizes or none do.
    /// </summary>
    public static async Task<bool> IsAuthorizedByAnyOwnerRoleMemberAsync(
        TrustChain trustChain, Func<string, CancellationToken, Task<bool>> isSignedByKeyAsync,
        CancellationToken cancellationToken)
    {
        foreach ((string root, TrustedOwner owner) in trustChain.OwnerChains)
        {
            if (trustChain.RoleOf(root) != MembershipRole.Owner)
            {
                continue;
            }

            if (await IsAuthorizedByOwnerAsync(owner, isSignedByKeyAsync, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
