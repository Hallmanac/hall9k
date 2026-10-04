namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// The verified root fingerprint of whichever project member's own node delivered an idea's own
/// genesis (<c>IdeaCaptured</c>) to this receiver, the "root to protect" the receive gate needs to
/// judge <c>IdeaConcluded</c>, <c>IdeaArchived</c> and <c>IdeaAssigneeSet</c> on an idea nobody has
/// assigned: an unassigned idea is not ownerless, it is still its creator's, and only that creator's
/// root (or an Owner-role sender) may decide its fate or hand it to someone. An idea's own
/// <c>IdeaCaptured.OwnerId</c> is a per-node Guid no peer can resolve, so it never stands in for
/// this.
/// <para>
/// The twin of <see cref="TaskCreatorRootRecord"/>, with the same trust rule: stored the moment
/// <c>EventReplicationInbox.ApplyAsync</c> starts the idea's stream fresh, with
/// <see cref="ClaimedOriginNodeId"/> always recorded and <see cref="CreatorRootFingerprint"/> set only
/// from a DIRECT delivery (<c>senderNodeId == record.OriginNodeId</c>), the one shape this node's own
/// transport verified. A relayed genesis leaves it empty, and the first later idea act this node
/// receives directly from <see cref="ClaimedOriginNodeId"/> backfills it from that verified sender.
/// Empty reads as "not yet known": an act that needs it is held, never applied or dropped on a
/// claim nobody verified.
/// </para>
/// </summary>
public sealed class IdeaCreatorRootRecord
{
    /// <summary>The idea's own id, this record's own stream id, never a foreign coordinate.</summary>
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The node id the idea's genesis wire record claims authored it, recorded whether or not that claim has been verified yet.</summary>
    public Guid ClaimedOriginNodeId { get; set; }

    public string CreatorRootFingerprint { get; set; } = string.Empty;
}
