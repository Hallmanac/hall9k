namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// Whether THIS node's own key is currently a live root key of its own owner, per project (idea
/// 6be68ee2, PR B of the succession chain), as of the daemon's own last message-sweep tick for that
/// project. A plain document, not an event-sourced aggregate, for the identical reason
/// <see cref="NodeSuccessionStateDetails"/> is one: purely local, mechanical bookkeeping derived
/// from a ledger read the sweep already performs for another reason.
/// <para>
/// Read by <c>h9k status</c> alone, to name "rotation missing in &lt;project&gt;" on the promoting
/// node while a fan-out started by <c>h9k owner promote</c> is still partial: this node's own key
/// already lives in <em>some</em> project's copy of the chain but not this one's — never a live
/// ledger walk from <c>h9k status</c> itself, the same rule <see cref="NodeSuccessionStateDetails"/>
/// already follows.
/// </para>
/// </summary>
public sealed class NodeRootKeyProjectDetails
{
    /// <summary>This project's own id — one document per project this node is registered to.</summary>
    public Guid Id { get; set; }

    public Guid NodeId { get; set; }

    public string RootFingerprint { get; set; } = string.Empty;

    public bool IsLiveRootKey { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
