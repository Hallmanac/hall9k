using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The name a member has chosen for teammates to see them by: set per project, or as this
/// machine's own default beneath every project that has no entry of its own (task e6744304). A
/// label only: it never feeds a trust or cross-check decision the way a declared GitHub account
/// does, and it travels no further than the node file it is written into.
/// <para>
/// <see cref="None"/> is the honest absence: no default and no per-project entry, which is how a
/// blank value clears either one. It serializes as the empty string, so an Owner stream written
/// before this type replays into it unchanged.
/// </para>
/// </summary>
[JsonConverter(typeof(DisplayNameJsonConverter))]
public sealed record DisplayName
{
    /// <summary>No display name set. Distinct from a value nobody could parse, which is refused.</summary>
    public static readonly DisplayName None = new("");

    /// <summary>
    /// Long enough for any name a person would actually go by and short enough that a pasted
    /// paragraph is refused as the mistake it is.
    /// </summary>
    private const int MaximumLength = 64;

    public string Value { get; }

    private DisplayName(string value) => Value = value;

    /// <summary>True when a name is actually set.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>
    /// The trimmed name, or a refusal naming the rule. Blank is <see cref="None"/> rather than an
    /// error: clearing the name is a legitimate thing to ask for, and it is how a member says
    /// "nothing here, defer to whatever else applies".
    /// </summary>
    public static DisplayName Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            return None;
        }

        return IsWellFormed(trimmed)
            ? new DisplayName(trimmed)
            : throw new DomainValidationException(
                $"'{Legible(trimmed)}' is not a display name: it must be 1 to {MaximumLength} characters "
                + "with no control characters (a newline, a carriage return, and a tab included). Pass an "
                + "empty value to clear it instead.");
    }

    /// <summary>The rule itself, so a caller can ask without catching.</summary>
    public static bool IsWellFormed(string value) =>
        value.Length is > 0 and <= MaximumLength && !value.Any(char.IsControl);

    /// <summary>
    /// Wraps a value already recorded somewhere else (a node file's own <c>display_name</c> line) with
    /// no re-validation, the same reason the JSON converter's own <c>Read</c> below is not
    /// <see cref="Parse"/>: a rule tightened after the value was written must never make it
    /// unreadable. Blank is <see cref="None"/>, on the same terms <see cref="Parse"/> uses.
    /// </summary>
    public static DisplayName Trusted(string? value) => value.IsNotBlank() ? new DisplayName(value) : None;

    /// <summary>
    /// What a refused value is safe to echo back: every control character replaced with a visible
    /// placeholder, since those are the one thing this rule forbids and a raw newline or tab in a
    /// message would otherwise be unreadable or, printed to a terminal, actively misleading.
    /// </summary>
    private static string Legible(string value) =>
        new([.. value.Take(MaximumLength).Select(character => char.IsControl(character) ? '?' : character)]);

    public override string ToString() => Value;

    private sealed class DisplayNameJsonConverter : JsonConverter<DisplayName>
    {
        // Reading is deliberately not Parse, the same reason VoiceSkillName's own converter states:
        // a value already on an event stream is a record of what was set, and a rule tightened
        // later must not make an old document unreadable.
        public override DisplayName Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } stored && stored.IsNotBlank() ? new DisplayName(stored) : None;

        public override void Write(Utf8JsonWriter writer, DisplayName value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
