using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// A Jira project's key — the PROJ in PROJ-123 — as a value object per the house type
/// discipline (TASK-MODEL.md §8) rather than a bare string on a settings event.
/// <para>
/// It carries a format rule because the key is the one part of Jira's vocabulary that is the
/// same shape in every instance, however exotic the issue types are: Jira itself accepts
/// uppercase letters, digits and underscores, starting with a letter. Checking it here means a
/// typo is refused where the human typed it, rather than becoming a card-creation prompt that
/// sends an agent hunting for a board that was never there.
/// </para>
/// <para>
/// Input is upper-cased, which is the one edit this type makes and not a guess about anything:
/// Jira upper-cases a project key when the project is created, so "proj" and "PROJ" are the same
/// key and only one of them is how Jira writes it. <see cref="None"/> is a bound key's honest
/// absence — a project with no board — and serializes as the empty string.
/// </para>
/// </summary>
[JsonConverter(typeof(JiraProjectKeyJsonConverter))]
public sealed record JiraProjectKey
{
    /// <summary>No board bound. Distinct from a key nobody could parse, which is refused outright.</summary>
    public static readonly JiraProjectKey None = new("");

    /// <summary>
    /// Long enough for every key Jira will make and short enough that a paragraph pasted into
    /// --jira is refused as the mistake it is. Jira's own creation form stops at 10; this is
    /// deliberately looser, because a key that already exists in someone's instance is a fact
    /// about their instance and not something to argue with.
    /// </summary>
    private const int MaximumLength = 20;

    public string Value { get; }

    private JiraProjectKey(string value) => Value = value;

    /// <summary>True when a board is actually bound.</summary>
    public bool HasValue => Value.IsNotBlank();

    /// <summary>
    /// The key as Jira writes it, or a refusal naming the rule. Blank is <see cref="None"/>
    /// rather than an error: clearing the binding is a legitimate thing to ask for.
    /// </summary>
    public static JiraProjectKey Parse(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            return None;
        }

        string upper = trimmed.ToUpperInvariant();
        return IsWellFormed(upper)
            ? new JiraProjectKey(upper)
            : throw new DomainValidationException(
                $"'{RelayedProjectKey(trimmed)}' is not a Jira project key. A key starts with a letter "
                + "and continues with letters, digits, or underscores (PROJ, DEV2, SUP_INT) — it is the "
                + "part before the dash in PROJ-123, not the project's name and not a card key.");
    }

    /// <summary>The rule itself, so a caller can ask without catching.</summary>
    public static bool IsWellFormed(string value)
    {
        if (value.Length is 0 or > MaximumLength || !char.IsAsciiLetterUpper(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What a refused key is safe to be quoted as. The value came off a command line and the
    /// refusal is printed to a terminal, and this type cannot reach Hall9k.Connectors' relay
    /// rules from the domain — so it keeps only the characters a key could legally have been
    /// made of, which drops every control character by construction rather than by a list.
    /// </summary>
    private static string RelayedProjectKey(string value)
    {
        string visible = new([.. value.Take(MaximumLength).Select(c => char.IsControl(c) ? '?' : c)]);
        return value.Length > MaximumLength ? visible + "…" : visible;
    }

    public override string ToString() => Value;

    private sealed class JiraProjectKeyJsonConverter : JsonConverter<JiraProjectKey>
    {
        // Reading is deliberately not Parse: a value already on an event stream is a record of
        // what was set, and a rule tightened later must not make an old document unreadable.
        public override JiraProjectKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } stored && stored.IsNotBlank() ? new JiraProjectKey(stored) : None;

        public override void Write(Utf8JsonWriter writer, JiraProjectKey value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
