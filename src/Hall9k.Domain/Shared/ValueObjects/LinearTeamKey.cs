using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// A Linear team's key, the ENG in ENG-123, as a value object per the house type discipline
/// (TASK-MODEL.md §8) rather than a bare string on a settings event. It is the Linear counterpart of
/// <see cref="JiraProjectKey"/>, and a project's binding to one is what makes Linear that project's
/// tracker.
/// <para>
/// The rule is deliberately loose: a team key is letters and digits starting with a letter, which is
/// the shape <c>ENG-123</c> needs to parse, and the length bound only stops a pasted paragraph. Whether
/// a team with the key exists is a fact about the workspace, which only Linear can answer, so
/// <c>h9k project set --linear</c> asks it before recording anything. Input is upper-cased, the one
/// edit this type makes: Linear writes a team key in capitals, so "eng" and "ENG" are one key.
/// <see cref="None"/> is a binding's honest absence and serializes as the empty string.
/// </para>
/// </summary>
[JsonConverter(typeof(LinearTeamKeyJsonConverter))]
public sealed record LinearTeamKey
{
    /// <summary>No team bound. Distinct from a key nobody could parse, which is refused outright.</summary>
    public static readonly LinearTeamKey None = new("");

    private const int MaximumLength = 20;

    public string Value { get; }

    private LinearTeamKey(string value) => Value = value;

    /// <summary>True when a team is actually bound.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>
    /// The key as Linear writes it, or a refusal naming the rule. Blank is <see cref="None"/> rather
    /// than an error: clearing the binding is a legitimate thing to ask for.
    /// </summary>
    public static LinearTeamKey Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            return None;
        }

        string upper = trimmed.ToUpperInvariant();
        return IsWellFormed(upper)
            ? new LinearTeamKey(upper)
            : throw new DomainValidationException(
                $"'{Relayed(trimmed)}' is not a Linear team key. A key starts with a letter and continues "
                + "with letters or digits (ENG, PLAT2); it is the part before the dash in ENG-123, not the "
                + "team's name and not an issue identifier.");
    }

    /// <summary>The rule itself, so a caller can ask without catching.</summary>
    public static bool IsWellFormed(string value) =>
        value.Length is > 0 and <= MaximumLength
        && char.IsAsciiLetterUpper(value[0])
        && value.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character));

    /// <summary>
    /// What a refused key is safe to be quoted as: a whitelist of what a key could legally have been
    /// made of, plus the dash, because the commonest mistake is an issue identifier (ENG-123) passed
    /// where a team key belongs and echoing it back unrecognisable would teach nothing. A whitelist
    /// for the reason <see cref="JiraProjectKey"/> states: a control character or a bidirectional
    /// override in a quoted refusal would repaint or reverse the sentence that explains it.
    /// </summary>
    private static string Relayed(string value)
    {
        string visible = new([.. value.Take(MaximumLength).Select(Legible)]);
        return value.Length > MaximumLength ? visible + "…" : visible;
    }

    private static char Legible(char character) =>
        char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '?';

    public override string ToString() => Value;

    private sealed class LinearTeamKeyJsonConverter : JsonConverter<LinearTeamKey>
    {
        // Reading is deliberately not Parse: a value already on an event stream is a record of what
        // was set, and a rule tightened later must not make an old document unreadable.
        public override LinearTeamKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } stored && stored.IsNotBlank() ? new LinearTeamKey(stored) : None;

        public override void Write(Utf8JsonWriter writer, LinearTeamKey value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
