namespace Hall9k.Connectors.Trust;

/// <summary>
/// The one shared "is this write authorized by an owner's own chain" rule
/// (<see cref="GitLedgerChainReader"/>'s own <c>IsAuthorizedByOwnerChainAsync</c>, idea 6be68ee2
/// trust-ledger finding 6): signed by the root's own key, or by any node currently vouched into it —
/// never a snapshot, always the chain as <paramref name="owner"/> already reports it right now.
/// Pulled out here so a second caller outside <see cref="GitLedgerChainReader"/> (a prompt-addenda
/// materialize walk over a DIFFERENT ledger ref, using a raw-commit-bytes signature check rather
/// than <see cref="GitLedgerChainReader"/>'s own sha-based one) can apply the identical rule without
/// duplicating it — <paramref name="isSignedByKeyAsync"/> is the one seam that differs between the
/// two callers' own signature-check shapes.
/// </summary>
public static class OwnerChainAuthorization
{
    /// <summary>Whether a commit is authorized by exactly one owner's own chain: signed by that
    /// root's own key, or by any node currently vouched into it.</summary>
    public static async Task<bool> IsAuthorizedByOwnerAsync(
        TrustedOwner owner, Func<string, CancellationToken, Task<bool>> isSignedByKeyAsync,
        CancellationToken cancellationToken)
    {
        if (await isSignedByKeyAsync(owner.RootPublicKeyLine, cancellationToken))
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
