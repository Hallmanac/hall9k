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
    /// A holder's nudge that a task's handoff note changed (idea 202383dc, item 3): the note itself
    /// travels on the task's own event stream and lands in the ledger record, never in this
    /// envelope's body — this kind carries no payload beyond pointing the reader at the task, and
    /// nothing but <c>h9k status</c>'s own unread count reads it (the same "the message is the nudge
    /// only" rule <see cref="Note"/> does not need to state, since a note's whole payload IS its
    /// body).
    /// </summary>
    public static readonly MessageKind Handoff = new("handoff");

    public string Value { get; }

    private MessageKind(string value) => Value = value;

    public static MessageKind Parse(string raw) => raw switch
    {
        "note" => Note,
        "events" => Events,
        "events-request" => EventsRequest,
        "events-unavailable" => EventsUnavailable,
        "handoff" => Handoff,
        _ => new MessageKind(raw),
    };

    /// <summary>Whether this is a kind Hall9k actually interprets, rather than one stored as-is for
    /// a future version — or a future kind this version has not learned yet — to make sense of.</summary>
    public bool IsRecognized =>
        this == Note || this == Events || this == EventsRequest || this == EventsUnavailable || this == Handoff;

    public override string ToString() => Value;
}
