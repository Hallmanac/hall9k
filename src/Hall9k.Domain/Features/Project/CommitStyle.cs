using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// How a follow-up run lands its fixes on the pull-request branch (Decisions Log #26).
/// Narrative folds each fix into the branch commit that owns it (fixup, autosquash onto
/// the base, force-with-lease push) so the branch reads as authored history; Append
/// stacks fix commits on top of the existing history. Unknown on a project means "use
/// the platform default" (DaemonOptions.DefaultCommitStyle, itself defaulting to
/// Narrative per the AGENTS.md authored-history rule).
/// </summary>
[JsonConverter(typeof(CommitStyleJsonConverter))]
public sealed record CommitStyle
{
    public static readonly CommitStyle Narrative = new("Narrative");
    public static readonly CommitStyle Append = new("Append");
    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly CommitStyle Unknown = new("");

    public string Value { get; }

    private CommitStyle(string value) => Value = value;

    public static implicit operator string(CommitStyle? style) => style?.Value ?? string.Empty;

    public static implicit operator CommitStyle(string? value) => value.IsBlank() ? Unknown : new CommitStyle(value);

    /// <summary>Maps user or config input to the closed set, case-insensitively; unrecognized input is Unknown.</summary>
    public static CommitStyle FromInput(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "narrative" => Narrative,
        "append" => Append,
        _ => Unknown,
    };

    /// <summary>
    /// The effective style for a run: the project's override when set, else the platform
    /// default, else Narrative (the documented default). Unrecognized values at either
    /// level fall through rather than being guessed at.
    /// </summary>
    public static CommitStyle Resolve(CommitStyle? projectStyle, string? platformDefault)
    {
        CommitStyle project = FromInput(projectStyle);
        if (project != Unknown)
        {
            return project;
        }

        CommitStyle configured = FromInput(platformDefault);
        return configured != Unknown ? configured : Narrative;
    }

    public bool Equals(CommitStyle? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class CommitStyleJsonConverter : JsonConverter<CommitStyle>
    {
        public override CommitStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, CommitStyle value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
