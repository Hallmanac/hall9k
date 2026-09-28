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
/// Recorded only once, the moment <c>EventReplicationInbox.ApplyAsync</c> starts this task's stream
/// fresh from a DIRECT delivery — <c>senderNodeId == record.OriginNodeId</c>, the one shape this
/// node's own transport has actually verified the sender's key against for this exact node id.
/// A genesis that arrives forwarded (a catch-up answer relayed by some other project member) never
/// sets this record: the relay's own verified key proves nothing about who actually authored the
/// genesis, and recording the relay's root here would let it impersonate the true creator for the
/// rest of the stream's life. Left absent in that case, which reads to that gate as "not yet
/// known" — held rather than trusted on an unverifiable claim — rather than as "nobody's", never
/// guessed at from an unverified field.
/// </para>
/// </summary>
public sealed class TaskCreatorRootRecord
{
    /// <summary>The task's own id — this record's own stream id, never a foreign coordinate.</summary>
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string CreatorRootFingerprint { get; set; } = string.Empty;
}
