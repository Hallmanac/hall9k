using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The model an agent session runs on, stored as the exact string <c>claude -p --model</c>
/// accepts (Decisions Log #33). The named statics are the tier aliases; any other value
/// rides through as itself so an exact model id (<c>claude-opus-5</c>, or a
/// context-variant like <c>claude-opus-5[1m]</c>) is equally expressible: the set is
/// defined once, not enforced. Unknown means "not set at this level", which is how every
/// optional link in the resolution chain says "ask the next one down".
/// </summary>
[JsonConverter(typeof(AgentModelJsonConverter))]
public sealed record AgentModel
{
    /// <summary>Alias tiers. An alias is remapped by Claude Code as new models ship; an exact id is not.</summary>
    public static readonly AgentModel Fable = new("fable");
    public static readonly AgentModel Opus = new("opus");
    public static readonly AgentModel Sonnet = new("sonnet");
    public static readonly AgentModel Haiku = new("haiku");

    /// <summary>
    /// The platform's bottom-of-the-chain default: an exact model id rather than an alias,
    /// because an alias silently re-points as new models ship, which is the same drift this
    /// whole value object exists to stop. The 1M-context variant specifically, because that
    /// is what dispatched sessions were observed running on when this default was written
    /// (Decisions Log #33): shipping the standard-context id would itself have been a silent,
    /// unrecorded narrowing of every build, review, and fix session, which is the exact
    /// failure this value object exists to prevent. Fable is the human-interactive tier, not
    /// a silent-agent default. Overridable through DaemonOptions.DefaultModel.
    /// </summary>
    public const string PlatformFallback = "claude-opus-5[1m]";

    /// <summary>Not recognized or not yet set. Serializes as an empty string.</summary>
    public static readonly AgentModel Unknown = new("");

    public string Value { get; }

    private AgentModel(string value) => Value = value;

    public static implicit operator string(AgentModel? model) => model?.Value ?? string.Empty;

    public static implicit operator AgentModel(string? value) => value.IsBlank() ? Unknown : new AgentModel(value);

    /// <summary>
    /// Maps user or config input to a model value: aliases are lower-cased into their
    /// canonical form, everything else is trimmed and kept verbatim (an exact model id is a
    /// legitimate answer, and its casing is the provider's business, not ours). Blank is
    /// Unknown, meaning "not set at this level", and never a guessed model.
    /// <para>
    /// <c>default</c> is Unknown for the same reason, at every level: Claude Code reads
    /// <c>--model default</c> as "use whatever this machine's configured default is", so
    /// letting the word ride through as an exact id would spawn the session on the human's
    /// personal setting, which is precisely the silent inheritance Decisions Log #33 closes,
    /// and would then record <c>default</c> as the model the run used — a provenance no
    /// session ever ran on. Read as "no opinion here, ask the next level down", it is also
    /// the one idiom that clears an override, which is how <c>h9k project set --model</c>
    /// already documents it.
    /// </para>
    /// </summary>
    public static AgentModel FromInput(string? value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "" or "default" => Unknown,
            "fable" => Fable,
            "opus" => Opus,
            "sonnet" => Sonnet,
            "haiku" => Haiku,
            _ => new AgentModel(trimmed),
        };
    }

    /// <summary>
    /// Whether the value is safe to hand to the executor's shell. The spawn command is
    /// assembled as a /bin/sh string, so a model value carrying shell metacharacters would
    /// be a command-injection seam; model ids are plain identifiers, so the safe set is
    /// letters, digits, and <c>. _ - : / @ [ ]</c>. Unknown is not well-formed: an absent
    /// model is never spawnable.
    /// </summary>
    public bool IsWellFormed =>
        Value.Length is > 0 and <= 128
        && Value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/' or '@' or '[' or ']');

    /// <summary>
    /// The resolution chain, most specific wins (Decisions Log #33): a task-level override
    /// beats the per-role default, which beats the project default, which beats the platform
    /// default. Every level is optional (Unknown at a level means "not set here", so the
    /// chain falls through) and it bottoms out at an explicit value, never at inheritance
    /// from whatever the human's personal settings happen to say that day.
    /// </summary>
    public static AgentModel Resolve(
        AgentModel? taskOverride, AgentModel? roleDefault, AgentModel? projectDefault, string? platformDefault)
    {
        foreach (AgentModel candidate in new[]
                 {
                     FromInput(taskOverride),
                     FromInput(roleDefault),
                     FromInput(projectDefault),
                     FromInput(platformDefault),
                 })
        {
            if (candidate != Unknown)
            {
                return candidate;
            }
        }

        return FromInput(PlatformFallback);
    }

    public bool Equals(AgentModel? other) => other is not null && Value == other.Value;

    public bool Equals(string? other) => other is not null && Value == other;

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value;

    private sealed class AgentModelJsonConverter : JsonConverter<AgentModel>
    {
        /// <summary>
        /// Reads through FromInput rather than straight into the value, unlike the sibling
        /// value objects: a stored document is an input like any other, so a hand-edited or
        /// older payload carrying <c>default</c> must land on Unknown here too, and an alias
        /// must arrive canonicalized. Deserializing verbatim would let the one word this type
        /// exists to intercept re-enter the chain as a spawnable-looking model name.
        /// </summary>
        public override AgentModel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            FromInput(reader.GetString());

        public override void Write(Utf8JsonWriter writer, AgentModel value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
