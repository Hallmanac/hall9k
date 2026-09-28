using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// Names the node behind a root key in a refusal message, when the chain can name it — criterion
/// 3 asks a root-only refusal to name "the root node and the command to run there", and a bare
/// fingerprint leaves an operator to work out for themselves which machine holds that key, even
/// when the chain already knows (independent pre-PR review, cycle 1, conformance lens, low).
/// </summary>
internal static class RootNodeDescription
{
    /// <summary>Empty when the chain cannot name a node for <paramref name="root"/> at all (an
    /// older ledger, or a caller-built chain with no opinion on it) — appended straight onto a
    /// sentence naming the root fingerprint, never a stand-alone clause a caller has to punctuate
    /// itself. Names the node holding the CURRENT top of <c>TrustedOwner.RootKeys</c> — K0's own
    /// <c>TrustedOwner.RootNodeId</c> only when no rotation has ever landed — rather than always
    /// K0's node: once succession has rotated a successor in, K0's own device is exactly the one
    /// least likely to still hold a usable root key, and criterion 3's hint text exists to point an
    /// operator at a device that actually can act, not at the one that got the owner into this
    /// refusal in the first place (independent pre-PR review, cycle 2, conformance lens, low).
    /// </summary>
    public static string Of(TrustChain chain, string root)
    {
        if (!chain.OwnerChains.TryGetValue(root, out TrustedOwner? owner))
        {
            return string.Empty;
        }

        string? nodeId = owner.RootKeys.Count > 0 ? owner.RootKeys[^1].IntroducedByNodeId : null;
        nodeId ??= owner.RootNodeId;
        if (nodeId is null)
        {
            return string.Empty;
        }

        DisplayName displayName = chain.DisplayNameOf(root);
        return displayName.HasValue
            ? $" (the root node, \"{displayName.Value}\", node {nodeId})"
            : $" (the root node, node {nodeId})";
    }

    /// <summary>
    /// The promotion hint every root-only refusal appends (idea 6be68ee2, PR B of the succession
    /// chain, blocked-by 1bb803e1): none of these five root-only gates
    /// (<c>NodeRevokeCommand</c>, <c>ProjectInviteCommand</c>, <c>ProjectMemberRemoveCommand</c>,
    /// <c>ProjectMemberReaffirmCommand</c>, <c>ProjectAssignKeyCommand</c>) can name whether THIS
    /// refusing node itself holds a live successor record, so the hint is offered unconditionally
    /// rather than only when it would actually apply — the identical shape the root-node-holder hint
    /// above already prints even when the caller cannot reach that node either.
    /// </summary>
    public const string PromotionHint =
        " A surviving node holding a live successor record for this owner can promote itself instead: h9k owner promote.";
}
