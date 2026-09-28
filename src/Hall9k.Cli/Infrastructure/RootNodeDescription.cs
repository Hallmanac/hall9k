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
    /// older ledger, or a caller-built chain with no opinion on it — <c>TrustedOwner.RootNodeId</c>'s
    /// own doc) — appended straight onto a sentence naming the root fingerprint, never a
    /// stand-alone clause a caller has to punctuate itself.</summary>
    public static string Of(TrustChain chain, string root)
    {
        if (!chain.OwnerChains.TryGetValue(root, out TrustedOwner? owner) || owner.RootNodeId is not { } nodeId)
        {
            return string.Empty;
        }

        DisplayName displayName = chain.DisplayNameOf(root);
        return displayName.HasValue
            ? $" (the root node, \"{displayName.Value}\", node {nodeId})"
            : $" (the root node, node {nodeId})";
    }
}
