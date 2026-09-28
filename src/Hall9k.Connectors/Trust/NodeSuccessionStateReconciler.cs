using Hall9k.Domain.Features.Trust;
using Marten;

namespace Hall9k.Connectors.Trust;

/// <summary>
/// Computes and persists <see cref="NodeSuccessionStateDetails"/> from a chain read wherever one is
/// already in hand — the daemon's own message sweep, once per eligible project per tick — the
/// identical "reconcile from a chain already computed for another reason" shape
/// <see cref="OwnerRootVerificationReconciler"/> already uses. <c>h9k status</c> only ever reads the
/// standing record this leaves behind, never walks the ledger itself (independent pre-PR review,
/// cycle 1, conformance lens, medium: <c>StatusCommand</c>'s own succession-state pane previously
/// ran a live <c>ILedgerChainReader.ComputeAsync</c> call, in violation of that command's own "no
/// git or network work in h9k status itself" rule).
/// </summary>
public static class NodeSuccessionStateReconciler
{
    /// <summary>
    /// Pure: this node's own succession state within <paramref name="owner"/>'s chain — "root key"
    /// when a live root key was introduced by this exact node id, "successor" when this node is
    /// currently listed as a successor candidate, "no successor" for a one-node, one-key fleet with
    /// neither (the owner has no heir at all), or null otherwise (an ordinary fleet node with more
    /// than one node around it, and nothing further to report).
    /// </summary>
    public static string? Compute(TrustedOwner owner, Guid nodeId)
    {
        string nodeIdText = nodeId.ToString();
        if (owner.RootKeys.Any(rootKey => rootKey.IntroducedByNodeId == nodeIdText))
        {
            return "root key";
        }

        if (owner.SuccessorNodeIds.Contains(nodeIdText))
        {
            return "successor";
        }

        if (owner.FleetNodeIds().Count() == 1 && owner.SuccessorNodeIds.Count == 0 && owner.RootKeys.Count == 1)
        {
            return "no successor";
        }

        return null;
    }

    /// <summary>Stores <see cref="Compute"/>'s own result as <paramref name="nodeId"/>'s standing
    /// <see cref="NodeSuccessionStateDetails"/> — the caller still owns <c>SaveChangesAsync</c>.</summary>
    public static void Reconcile(IDocumentSession session, Guid nodeId, TrustedOwner owner, DateTimeOffset now)
    {
        session.Store(new NodeSuccessionStateDetails { Id = nodeId, State = Compute(owner, nodeId), UpdatedAt = now });
    }
}
