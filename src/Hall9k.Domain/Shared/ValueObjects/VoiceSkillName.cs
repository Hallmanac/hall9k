using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The name of the skill an owner has named as their writing voice — the directory name, not a
/// path and not a copy of what is inside it — as a value object per the house type discipline
/// (TASK-MODEL.md §8) rather than a bare string on a settings event.
/// <para>
/// The rule it carries is that a voice skill is referenced by NAME. The text itself is personal
/// and per account (Brian's own <c>my-voice</c>: a <c>SKILL.md</c> plus <c>contexts/chat.md</c>,
/// <c>email.md</c>, <c>explainer.md</c>, <c>code-review.md</c>), so it lives in the owner's own
/// skill directory and never in this repository, in a prompt template, or on an event — a prompt
/// seam names it and the session loads it. Which is also why the shape is narrow: a skill
/// directory's name, so nothing here can become a path that walks out of the two directories
/// <see cref="Hall9k.Domain.Infrastructure.Storage.VoiceSkillLocation"/> looks in.
/// </para>
/// <para>
/// <see cref="None"/> is the honest absence: an owner who never named one, which is every owner
/// until they do. It serializes as the empty string, so an Owner stream written before this type
/// replays into it unchanged.
/// </para>
/// </summary>
[JsonConverter(typeof(VoiceSkillNameJsonConverter))]
public sealed record VoiceSkillName
{
    /// <summary>No voice skill named. Distinct from a name nobody could parse, which is refused.</summary>
    public static readonly VoiceSkillName None = new("");

    /// <summary>
    /// Long enough for any skill directory anyone would actually make and short enough that a
    /// sentence pasted into <c>--voice-skill</c> is refused as the mistake it is.
    /// </summary>
    private const int MaximumLength = 64;

    public string Value { get; }

    private VoiceSkillName(string value) => Value = value;

    /// <summary>True when the owner has actually named a voice skill.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>
    /// The name as a skill directory is named, or a refusal naming the rule. Blank is
    /// <see cref="None"/> rather than an error: clearing the preference is a legitimate thing to
    /// ask for, and it is how an owner says "write in the platform's own voice again".
    /// </summary>
    public static VoiceSkillName Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            return None;
        }

        return IsWellFormed(trimmed)
            ? new VoiceSkillName(trimmed)
            : throw new DomainValidationException(
                $"'{Legible(trimmed)}' is not a skill name. A voice skill is named by its own directory "
                + "name — letters, digits, hyphens and underscores, starting with a letter or a digit "
                + "(my-voice, house_voice) — never a path and never the text itself, which stays in the "
                + "owner's own skill directory and is loaded by name at each prompt seam.");
    }

    /// <summary>The rule itself, so a caller can ask without catching.</summary>
    public static bool IsWellFormed(string value)
    {
        if (value.Length is 0 or > MaximumLength || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What a refused name is safe to be quoted as, on the same whitelist terms
    /// <see cref="JiraProjectKey"/> states in full: the value came off a command line and the
    /// refusal is printed to a terminal, so only what a name could legally have been made of
    /// survives, plus the separators whose presence is the commonest reason a name is refused at
    /// all (a path was passed where a directory name belongs) — echoing that back unrecognisable
    /// would teach nothing.
    /// </summary>
    private static string Legible(string value)
    {
        string visible = new([.. value.Take(MaximumLength).Select(Readable)]);
        return value.Length > MaximumLength ? visible + "…" : visible;
    }

    private static char Readable(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '\\' or '.' or '~' or ' '
            ? character
            : '?';

    public override string ToString() => Value;

    private sealed class VoiceSkillNameJsonConverter : JsonConverter<VoiceSkillName>
    {
        // Reading is deliberately not Parse, the same reason JiraProjectKey's own converter states:
        // a value already on an event stream is a record of what was set, and a rule tightened
        // later must not make an old document unreadable.
        public override VoiceSkillName Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } stored && stored.IsNotBlank() ? new VoiceSkillName(stored) : None;

        public override void Write(Utf8JsonWriter writer, VoiceSkillName value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
