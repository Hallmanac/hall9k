using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The closed set of writes the executor will perform against a Jira item (Brian's design,
/// 2026-08-28: hall9k is the sole executor, composition is an agent's or an operator's). Create,
/// Update, and Comment are the only members on purpose — a transition or a close is refused
/// regardless of who composed the payload, because which workflow state a card belongs in is a
/// team's own configuration, never a fact this platform gets to have an opinion on (the same
/// doctrine <see cref="Hall9k.Domain.Features.Project.BacklogPolicy"/>'s doc comment draws for
/// card authoring). <see cref="Unknown"/> is what a transition, a close, or any other word parses
/// to, and the executor refuses it outright rather than guessing which of the three was meant.
/// </summary>
[JsonConverter(typeof(JiraWriteOperationJsonConverter))]
public sealed record JiraWriteOperation
{
    public static readonly JiraWriteOperation Create = new("Create");
    public static readonly JiraWriteOperation Update = new("Update");
    public static readonly JiraWriteOperation Comment = new("Comment");

    /// <summary>Not one of the three the executor performs — includes a transition or a close, by design.</summary>
    public static readonly JiraWriteOperation Unknown = new("");

    public string Value { get; }

    private JiraWriteOperation(string value) => Value = value;

    public static implicit operator string(JiraWriteOperation? operation) => operation?.Value ?? string.Empty;

    public static implicit operator JiraWriteOperation(string? value) => FromInput(value);

    /// <summary>Lenient mapping for a value already on the stream; unrecognized reads as Unknown rather than throwing.</summary>
    public static JiraWriteOperation FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "create" => Create,
        "update" => Update,
        "comment" => Comment,
        _ => Unknown,
    };

    /// <summary>
    /// The strict form an operator's or an agent's own <c>--op</c> goes through: anything that is
    /// not create, update, or comment — including "transition" and "close" spelled out plainly —
    /// is refused here, before a payload is even read, which is what makes the refusal apply
    /// regardless of who composed the request.
    /// </summary>
    public static JiraWriteOperation Parse(string? value)
    {
        JiraWriteOperation parsed = FromInput(value);
        return parsed != Unknown
            ? parsed
            : throw new DomainValidationException(
                $"'{value}' is not a Jira write hall9k executes. Use create, update, or comment. A "
                + "transition or a close is refused regardless of who composed it: which workflow state "
                + "a card belongs in is this team's own configuration, done in Jira directly, never a "
                + "write hall9k makes on anyone's behalf.");
    }

    public bool Equals(JiraWriteOperation? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class JiraWriteOperationJsonConverter : JsonConverter<JiraWriteOperation>
    {
        // Reading is deliberately not Parse: a value already on an event stream is a record of
        // what was requested, and a rule tightened later must not make an old document unreadable.
        public override JiraWriteOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, JiraWriteOperation value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
