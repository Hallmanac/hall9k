namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// This node's own succession state (idea 6be68ee2) as of the daemon's own last message-sweep
/// tick — "root key", "successor", "no successor" for a one-node fleet, or null for an ordinary
/// fleet node with neither and more than one node around it
/// (<see cref="Hall9k.Connectors.Trust.NodeSuccessionStateReconciler.Compute"/>'s own rule). A
/// plain document, not an event-sourced aggregate, for the identical reason
/// <c>Hall9k.Domain.Features.Replication.FleetProjectReconcile</c> is one: purely local, mechanical
/// bookkeeping derived from a ledger read the sweep already performs for another reason.
/// <para>
/// Read by <c>h9k status</c> alone, and only ever written by the daemon's own message sweep
/// (<see cref="Hall9k.Connectors.Trust.NodeSuccessionStateReconciler.Reconcile"/>) — never a live
/// ledger walk from <c>h9k status</c> itself, the same "no git or network work in h9k status
/// itself" rule <c>StatusCommand.WriteUnverifiedLedgerWritesAsync</c>'s own doc already states
/// (independent pre-PR review, cycle 1, conformance lens, medium: a live
/// <c>ILedgerChainReader.ComputeAsync</c> call from inside <c>h9k status</c> ran a full git fetch
/// and ledger replay on every status check, with no timeout, and could collide with the daemon's
/// own concurrent ref writes).
/// </para>
/// </summary>
public sealed class NodeSuccessionStateDetails
{
    /// <summary>The node id this state describes.</summary>
    public Guid Id { get; set; }

    /// <summary>"root key", "successor", "no successor", or null — see
    /// <see cref="Hall9k.Connectors.Trust.NodeSuccessionStateReconciler.Compute"/>.</summary>
    public string? State { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
