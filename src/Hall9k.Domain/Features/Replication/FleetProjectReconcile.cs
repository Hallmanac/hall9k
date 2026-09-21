namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// One fleet sibling's own reconcile of one project with this node (task 252bc5cf): the standing
/// record that this node has asked node <see cref="PeerNodeId"/> for everything it holds of project
/// <see cref="ProjectId"/>, and how far that exchange got. A plain document, not an event-sourced
/// aggregate, for the identical reason <see cref="EventCatchUpRequest"/> is one: purely local,
/// mechanical bookkeeping about an exchange this node is waiting on.
/// <para>
/// Keyed by (peer, project) through <see cref="EventReplicationStreamId.ForFleetReconcile"/> rather
/// than by a fresh id, and that IS the guard: the sweep asks a fleet peer exactly once because the
/// existence of this document is what tells it the pair has already been asked, and the answering
/// peer's own reverse ask writes its own copy before queuing anything, so two nodes settle into one
/// exchange each way rather than asking each other forever.
/// </para>
/// <para>
/// Completion is a fact rather than a guess. <see cref="CompletedAt"/> is set only on reading the
/// peer's own terminal <c>events-answer-complete</c> envelope
/// (<see cref="Hall9k.Domain.Features.Message.MessageKind.EventsAnswerComplete"/>), which carries
/// the envelope count this record keeps as <see cref="AnswerEnvelopeCount"/> — never on an answer
/// merely having applied something, since a whole-project answer over history this node already
/// holds legitimately applies nothing at all. A peer that holds nothing for the project declines
/// instead, and <see cref="UnavailableReason"/> is that answer recorded verbatim.
/// </para>
/// </summary>
public sealed class FleetProjectReconcile
{
    /// <summary><see cref="EventReplicationStreamId.ForFleetReconcile"/> of (<see cref="PeerNodeId"/>, <see cref="ProjectId"/>).</summary>
    public Guid Id { get; set; }

    /// <summary>The fleet sibling this reconcile is with — one node of this owner's own fleet
    /// (<c>TrustedOwner.FleetNodeIds</c>), never a teammate's node.</summary>
    public Guid PeerNodeId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The <see cref="EventCatchUpRequest"/> currently carrying this reconcile — replaced
    /// by a re-ask, so a terminal envelope stamped with a superseded request id no longer closes
    /// this record.</summary>
    public Guid RequestId { get; set; }

    /// <summary>When the ask this record is currently waiting on went out — reset by a re-ask, so
    /// the retention clock below always measures the live ask rather than the first one.</summary>
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>When the one automatic re-ask went out, or null while none has. The re-ask exists
    /// because the outbox squash window (<c>DaemonOptions.MessageRetention</c>, 48 hours) can prune
    /// an answer a requester's daemon never read, and a reconcile with no completion by then has no
    /// other way to notice.</summary>
    public DateTimeOffset? ReAskedAt { get; set; }

    /// <summary>When the first envelope of the peer's answer was read here, or null while none has
    /// been — an observation, never a guess: a peer that has not answered yet and a peer whose
    /// answer was pruned before this node read it both leave this null, and the retention clock is
    /// what tells them apart.</summary>
    public DateTimeOffset? FirstAnswerAt { get; set; }

    /// <summary>How many of the peer's own answering <c>events</c> envelopes this node actually read
    /// for this reconcile. Counted separately from <see cref="RecordsApplied"/> because an envelope
    /// whose every record this node already held applies nothing and is still an envelope that
    /// arrived.</summary>
    public int EnvelopesRead { get; set; }

    /// <summary>How many replicated event records this reconcile's own answer actually applied here
    /// — legitimately far below the peer's own event count for a project this node mostly already
    /// holds, since every record dedupes by origin event id.</summary>
    public int RecordsApplied { get; set; }

    /// <summary>The envelope count the peer's own terminal envelope claimed, or null until one
    /// arrives. Kept beside <see cref="EnvelopesRead"/> rather than replacing it: the two
    /// disagreeing is the honest shape of an answer partly lost to a squash, and flattening them
    /// into one number would hide it.</summary>
    public int? AnswerEnvelopeCount { get; set; }

    /// <summary>How many streams this node holds ONLY as <see cref="HeldReplicatedEventRecord"/>s
    /// once this reconcile's answer landed: a tail whose genesis no answer carried, so the stream
    /// cannot be started here and the tail cannot be applied in front of nothing. Counted rather
    /// than silently skipped, and reported by <c>h9k status</c>, because a held tail is exactly the
    /// shape a reconcile cannot fix on its own.</summary>
    public int HeldTailOnlyStreams { get; set; }

    /// <summary>The peer's own <c>events-unavailable</c> reason, when it answered that it holds
    /// nothing matching at all — the record saying so rather than the reconcile standing open
    /// against a peer that already gave its final answer.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>When the peer's own terminal envelope was read here, or null while this reconcile
    /// is still in progress.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When this node observed that even the re-ask went unanswered past the retention
    /// window — the "and then reported" half of the re-ask-once rule. Informational: nothing
    /// automatic fires off it, and <c>h9k project reconcile</c> is the hand lever that clears it.</summary>
    public DateTimeOffset? StalledAt { get; set; }

    /// <summary>
    /// When this node observed that <see cref="PeerNodeId"/> is no longer a node of this owner's
    /// fleet at all — revoked, or otherwise gone from this project's own ledger — while this
    /// exchange was still in flight, or null while it is still a sibling. An exchange with a node
    /// that is not a sibling any more cannot be finished and must not be re-asked: nothing this node
    /// sends it will be answered, and <c>h9k project reconcile</c> walks the CURRENT fleet, so it
    /// could never clear the record either (independent pre-PR review, cycle 1, adversarial lens,
    /// medium).
    /// <para>
    /// Deliberately not <see cref="CompletedAt"/> and deliberately not
    /// <see cref="UnavailableReason"/>: no answer was ever observed, and that reason field holds the
    /// peer's own words verbatim. This is this node's own observation about the fleet, kept as its
    /// own fact. A peer vouched back in later has its exchange restarted from the top rather than
    /// resumed, since whatever it did or did not answer while it was out is unknown.
    /// </para>
    /// </summary>
    public DateTimeOffset? PeerLeftFleetAt { get; set; }

    /// <summary>Still waiting on the peer's own terminal envelope — the set <c>h9k status</c> shows
    /// as in progress.</summary>
    public bool IsInProgress => CompletedAt is null;
}
