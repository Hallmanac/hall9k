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

    public string Value { get; }

    private MessageKind(string value) => Value = value;

    public static MessageKind Parse(string raw) => raw switch
    {
        "note" => Note,
        _ => new MessageKind(raw),
    };

    /// <summary>Whether this is a kind Hall9k actually interprets, rather than one stored as-is for
    /// a future version — or a future kind this version has not learned yet — to make sense of.</summary>
    public bool IsRecognized => this == Note;

    public override string ToString() => Value;
}
