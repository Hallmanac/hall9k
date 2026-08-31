using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// The daemon's durable operating settings that the CLI edits and reports on by name —
/// concurrency and the model-by-role policy (backlog 59, Decisions Log #33's missing bottom
/// layer). Lives in Domain rather than beside <c>Hall9k.Daemon</c>'s own <c>DaemonOptions</c>
/// because both <c>Hall9k.Cli</c> (<c>h9k config set/show</c>) and <c>Hall9k.Daemon</c> (options
/// binding) need the identical shape and the reference graph runs Daemon → Domain, never the
/// other way. <c>DaemonOptions</c> itself binds against the whole "hall9k" section of the
/// platform config file (<see cref="PlatformConfigFile"/>) through the ordinary .NET
/// configuration pipeline, so a sibling member this type does not know about is still
/// bindable by hand-editing the file — this type only names the subset the CLI edits directly.
/// <see cref="Extra"/> is what keeps a hand-edited key like that from being erased the next
/// time the CLI writes: read, mutate the known fields, write back, and everything else round-trips.
/// </summary>
public sealed class OperatingSettings
{
    /// <summary>Mirrors <c>DaemonOptions.MaxConcurrentAgentSessions</c>'s shipped default, so the two never drift apart.</summary>
    public const int DefaultMaxConcurrentAgentSessions = 3;

    /// <summary>
    /// How many days an interactive claim (h9k task work) can sit untouched before h9k status
    /// nudges about it (Decisions Log #103's own follow-up, idea 3ba186b6: "a staleness nudge,
    /// not a timeout"). Read directly by the CLI's attention composer rather than through
    /// <see cref="OperatingSettingsResolver"/> — nothing binds it through <c>DaemonOptions</c>,
    /// since no daemon process acts on it (there is deliberately no reclaim, ever), so it carries
    /// no environment-variable tier and no daemon-startup consequence the way the resolved
    /// settings above do.
    /// </summary>
    public const int DefaultInteractiveClaimStaleAfterDays = 3;

    public int? MaxConcurrentAgentSessions { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? DefaultModel { get; set; }

    public RoleModelSettings ModelByRole { get; set; } = new();

    public int? InteractiveClaimStaleAfterDays { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Per-role model overrides, named rather than held in a dictionary for the same reason
/// <c>DaemonOptions.RoleModelDefaults</c> is: <c>h9k config set --help</c>-shaped discovery
/// states exactly which sessions are configurable, rather than accepting an arbitrary key.
/// </summary>
public sealed class RoleModelSettings
{
    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Build { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Review { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Fix { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Synthesis { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Refinement { get; set; }

    [JsonConverter(typeof(LenientModelStringJsonConverter))]
    public string? Publication { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Every named role and its configured model, in the order <c>h9k config show</c> renders them.</summary>
    public IEnumerable<(string Role, string? Model)> AsPairs()
    {
        yield return (nameof(Build), Build);
        yield return (nameof(Review), Review);
        yield return (nameof(Fix), Fix);
        yield return (nameof(Synthesis), Synthesis);
        yield return (nameof(Refinement), Refinement);
        yield return (nameof(Publication), Publication);
    }
}

/// <summary>
/// Reads a model-name leaf exactly as <c>JsonConfigurationFileParser</c> stringifies it before
/// <c>ConfigurationBinder</c> ever sees it: a JSON string as itself, and a JSON number or boolean
/// the same text <see cref="JsonElement.ToString()"/> renders for it ("3", "True"). Without this,
/// a hand-quoted number or boolean here — the same mistake <see cref="OperatingSettings.MaxConcurrentAgentSessions"/>
/// already tolerates in the other direction — is a value the daemon binds and runs on happily, but
/// this type refused as the wrong shape, so <c>h9k config show</c> would report the setting as
/// ignored (falling back to a healthy default) while every session using it actually fails to
/// spawn. An object or array still throws: <c>JsonConfigurationFileParser</c> routes those into
/// nested keys rather than a leaf value, which <see cref="PlatformConfigFile"/>'s existing
/// shape-mismatch recovery already handles correctly for a leaf that stays this type's
/// responsibility. Origin: the cycle-4 pre-PR review found a hand-quoted number for
/// <c>defaultModel</c> or a role under <c>modelByRole</c> reported as merely ignored when the
/// daemon in fact binds and spawns on the coerced value.
/// </summary>
internal sealed class LenientModelStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.Number => JsonElement.ParseValue(ref reader).ToString(),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => throw new JsonException("The JSON value could not be converted to System.String."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
