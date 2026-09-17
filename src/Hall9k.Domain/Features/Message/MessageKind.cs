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

    public string Value { get; }

    private MessageKind(string value) => Value = value;

    public static MessageKind Parse(string raw) => raw switch
    {
        "note" => Note,
        "events" => Events,
        _ => new MessageKind(raw),
    };

    /// <summary>Whether this is a kind Hall9k actually interprets, rather than one stored as-is for
    /// a future version — or a future kind this version has not learned yet — to make sense of.</summary>
    public bool IsRecognized => this == Note || this == Events;

    public override string ToString() => Value;
}
