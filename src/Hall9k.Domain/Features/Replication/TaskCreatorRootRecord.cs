namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// The verified root fingerprint of whichever project member's own node this receiver resolved as
/// the sender delivering a task's own genesis (<c>TaskAdded</c>) directly — the "root to protect"
/// idea 6be68ee2's own pre-assignment gate
/// (<c>Hall9k.Connectors.Replication.EventReplicationInbox.EvaluateTaskActVerdict</c>'s own
/// <c>PreAssignmentCapableConditionalTypes</c> case) needs for <c>TaskPublished</c> and its siblings
/// on a task nobody has assigned or held yet: an unassigned, unheld task is not ownerless, it is
/// simply still its own creator's, and only that creator's own root (or the project owner) may
/// revise, scope, pre-approve, publish, return it to draft, or abandon it before any assignment
/// exists.
/// <para>
/// Stored the moment <c>EventReplicationInbox.ApplyAsync</c> starts this task's stream fresh,
/// whether the genesis arrived directly or forwarded, so <see cref="ClaimedOriginNodeId"/> —
/// the node id the genesis's own wire record claims as its author — is always known even when it
/// is not yet verified. <see cref="CreatorRootFingerprint"/> itself, though, is set only once this
/// node has DIRECT proof that <see cref="ClaimedOriginNodeId"/> really is what it claims:
/// <c>senderNodeId == record.OriginNodeId</c>, the one shape this node's own transport has actually
/// verified the sender's key against for this exact node id. A genesis that arrives forwarded (a
/// catch-up answer relayed by some other project member, which is how
/// <c>EventCatchUpResponder</c>'s own broadcast answers most bootstraps and stream repairs) leaves
/// it empty: the relay's own verified key proves nothing about who actually authored the genesis,
/// and trusting the relay's claim here would let it impersonate the true creator for the rest of
/// the stream's life.
/// </para>
/// <para>
/// An empty <see cref="CreatorRootFingerprint"/> is never permanent, though — a stream that started
/// from a relayed genesis is exactly the ordinary shape catch-up produces, and the one fact that
/// closes it is the SAME direct-delivery proof the genesis itself would have carried: the first
/// later Task act this node ever receives directly (<c>senderNodeId == record.OriginNodeId</c>)
/// FROM <see cref="ClaimedOriginNodeId"/> itself backfills <see cref="CreatorRootFingerprint"/> from
/// that verified sender, the identical trust this record would have recorded at genesis time had
/// that same node shipped it directly instead of a relay answering first. Read as "not yet known" —
/// held rather than trusted on an unverifiable claim — until then, never as "nobody's", and never
/// guessed at from an unverified field.
/// </para>
/// </summary>
public sealed class TaskCreatorRootRecord
{
    /// <summary>The task's own id — this record's own stream id, never a foreign coordinate.</summary>
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The node id the task's own genesis wire record claims authored it — recorded
    /// whether or not that claim has been verified yet, so a later direct delivery from this exact
    /// node id can still confirm it (this class's own doc on backfill).</summary>
    public Guid ClaimedOriginNodeId { get; set; }

    public string CreatorRootFingerprint { get; set; } = string.Empty;
}
