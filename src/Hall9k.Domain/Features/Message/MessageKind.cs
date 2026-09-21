namespace Hall9k.Domain.Features.Message;

/// <summary>
/// The envelope's payload kind. Value object per the house type discipline (TASK-MODEL.md §8):
/// the closed set of kinds Hall9k actually understands is defined once here, and — unlike
/// <see cref="MessageAudience"/> — an unrecognized kind round-trips as itself rather than failing,
/// per idea 202383dc's own ruling: an unknown kind is stored and skipped, never refused.
/// </summary>
public sealed record MessageKind
{
    /// <summary>The first, and for M1, only payload kind: a free-text note between two nodes.</summary>
    public static readonly MessageKind Note = new("note");

    /// <summary>
    /// A batch of replicated project-scoped events (idea 202383dc, M2a) — the body is a JSON array
    /// of <see cref="Hall9k.Domain.Features.Replication.EventReplicationCodec.ReplicatedEventRecord"/>,
    /// never user-visible prose. Rides the same per-node outbox ref as every other kind, but is
    /// applied by <c>Hall9k.Connectors.Replication.EventReplicationInbox</c> rather than recorded as
    /// an ordinary received message — it never shows up in <c>h9k messages</c>.
    /// </summary>
    public static readonly MessageKind Events = new("events");

    /// <summary>
    /// A catch-up request (idea 202383dc, M2b, task 9408d525) — the body is a JSON
    /// <see cref="Hall9k.Domain.Features.Replication.EventReplicationCodec.EventsRequestRecord"/>
    /// asking one peer (or, for the ledger-record adoption path, the whole project) to forward
    /// whatever it holds for a gap (since a sequence), for a brand-new node (everything), or for one
    /// specific stream. Rides the same per-node outbox ref as every other kind, read only by
    /// <c>Hall9k.Connectors.Replication.EventCatchUpInbox</c> — never shown in <c>h9k messages</c>,
    /// the same reason <see cref="Events"/> is not.
    /// </summary>
    public static readonly MessageKind EventsRequest = new("events-request");

    /// <summary>
    /// A peer's own answer that it cannot satisfy an <see cref="EventsRequest"/> at all — the body
    /// is a JSON <see cref="Hall9k.Domain.Features.Replication.EventReplicationCodec.EventsUnavailableRecord"/>
    /// naming the request it answers and why (idea 202383dc, M2b: "a peer that cannot answer says
    /// so"). Never shown in <c>h9k messages</c>, the same reason <see cref="Events"/> is not.
    /// </summary>
    public static readonly MessageKind EventsUnavailable = new("events-unavailable");

    /// <summary>
    /// The terminal envelope one catch-up answer ends with (task 252bc5cf) — the body is a JSON
    /// <see cref="Hall9k.Domain.Features.Replication.EventReplicationCodec.EventsAnswerCompleteRecord"/>
    /// naming the request it closes and how many <see cref="Events"/> envelopes that answer was
    /// batched into. It is what makes a fleet reconcile's own completion a fact rather than a guess:
    /// the requester marks its own reconcile record complete only on reading this, never on an
    /// answer merely having applied something, since a whole-project answer over history the
    /// requester already holds legitimately applies nothing. A build that predates this kind does
    /// not recognize it and so stores it as an ordinary received message, under this type's own "an
    /// unknown kind is stored and skipped, never refused" rule — one raw note per answer in that
    /// node's <c>h9k messages</c>, and no reconcile completed there, which leaves the exchange
    /// incomplete and re-asked rather than wrongly closed. Never shown in <c>h9k messages</c> on a
    /// build that does know the kind, the same reason <see cref="Events"/> is not.
    /// </summary>
    public static readonly MessageKind EventsAnswerComplete = new("events-answer-complete");

    /// <summary>
    /// A holder's nudge that a task's handoff note changed (idea 202383dc, item 3): the note itself
    /// travels on the task's own event stream and lands in the ledger record, never in this
    /// envelope's body — this kind carries no payload beyond pointing the reader at the task, and
    /// nothing but <c>h9k status</c>'s own unread count reads it (the same "the message is the nudge
    /// only" rule <see cref="Note"/> does not need to state, since a note's whole payload IS its
    /// body).
    /// </summary>
    public static readonly MessageKind Handoff = new("handoff");

    /// <summary>
    /// A member asks a holder for a task (idea 202383dc, item 5, "the cooperative take"): the
    /// body is a JSON <see cref="ClaimEnvelopeCodec.ClaimRequestRecord"/> naming the task, the
    /// requester's node and owner, and the reason. Addressed to the holder's own node
    /// (<see cref="MessageAudience.Node"/>) so only that node ever reacts to it.
    /// </summary>
    public static readonly MessageKind ClaimRequest = new("claim-request");

    /// <summary>
    /// The holder's own answer that a <see cref="ClaimRequest"/> is granted — the body is a JSON
    /// <see cref="ClaimEnvelopeCodec.ClaimGrantedRecord"/> naming the task. The ledger holder is
    /// already released by the time this is queued; the requester's own node claims through the
    /// ordinary lock once it sees this.
    /// </summary>
    public static readonly MessageKind ClaimGranted = new("claim-granted");

    /// <summary>
    /// The holder's own answer that a <see cref="ClaimRequest"/> is refused — the body is a JSON
    /// <see cref="ClaimEnvelopeCodec.ClaimRefusedRecord"/> naming the task and the reason (a live
    /// run's own start time, or the holder's own human's stated reason).
    /// </summary>
    public static readonly MessageKind ClaimRefused = new("claim-refused");

    public string Value { get; }

    private MessageKind(string value) => Value = value;

    public static MessageKind Parse(string raw) => raw switch
    {
        "note" => Note,
        "events" => Events,
        "events-request" => EventsRequest,
        "events-unavailable" => EventsUnavailable,
        "events-answer-complete" => EventsAnswerComplete,
        "handoff" => Handoff,
        "claim-request" => ClaimRequest,
        "claim-granted" => ClaimGranted,
        "claim-refused" => ClaimRefused,
        _ => new MessageKind(raw),
    };

    /// <summary>Whether this is a kind Hall9k actually interprets, rather than one stored as-is for
    /// a future version — or a future kind this version has not learned yet — to make sense of.</summary>
    public bool IsRecognized =>
        this == Note || this == Events || this == EventsRequest || this == EventsUnavailable
        || this == EventsAnswerComplete || this == Handoff
        || this == ClaimRequest || this == ClaimGranted || this == ClaimRefused;

    /// <summary>
    /// Every kind the replication and catch-up readers own outright: read by
    /// <c>EventReplicationInbox</c> and <c>EventCatchUpInbox</c> on their own cursors, and never
    /// stored as an ordinary <c>MessageDetails</c>, so <c>h9k messages</c> never shows a raw batch
    /// of replicated events or a protocol message as though it were a note. One property rather
    /// than the comparison chain each reader used to spell out for itself, so a further protocol
    /// kind is skipped everywhere the moment it is named here — the shape that let
    /// <see cref="EventsAnswerComplete"/> be added without a second reader quietly storing it.
    /// </summary>
    public bool IsReplicationProtocol =>
        this == Events || this == EventsRequest || this == EventsUnavailable || this == EventsAnswerComplete;

    /// <summary>
    /// Every kind stored as an ordinary received message (unlike every
    /// <see cref="IsReplicationProtocol"/> kind, which never is) but still
    /// carrying a JSON payload for a daemon reactor alone rather than user-visible prose — so
    /// <c>h9k messages</c> and <c>h9k status</c>'s own unread count both skip it the same way they
    /// already skip a kind that is never stored at all. A plain string array, not a computed
    /// property, so a caller's own Marten query can push it down as a SQL <c>IN</c> list.
    /// </summary>
    public static readonly IReadOnlyList<string> MechanicalKindValues = [ClaimRequest.Value, ClaimGranted.Value, ClaimRefused.Value];

    public override string ToString() => Value;
}
