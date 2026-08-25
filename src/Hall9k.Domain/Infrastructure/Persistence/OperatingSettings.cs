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

    public int? MaxConcurrentAgentSessions { get; set; }

    public string? DefaultModel { get; set; }

    public RoleModelSettings ModelByRole { get; set; } = new();

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
    public string? Build { get; set; }

    public string? Review { get; set; }

    public string? Fix { get; set; }

    public string? Synthesis { get; set; }

    public string? Refinement { get; set; }

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
