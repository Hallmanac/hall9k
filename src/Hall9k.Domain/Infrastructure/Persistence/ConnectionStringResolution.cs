using System.Diagnostics.CodeAnalysis;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// What <see cref="Hall9kDatabase.Resolve"/> found and where it found it. Never guesses:
/// an unconfigured install resolves to <see cref="NotConfigured"/> rather than a
/// plausible-looking default (AGENTS.md, the never-guess rule; Decisions Log #58).
/// </summary>
public sealed record ConnectionStringResolution(string? Value, ConnectionStringOrigin Origin, string? Source)
{
    public static ConnectionStringResolution NotConfigured { get; } = new(null, ConnectionStringOrigin.None, null);

    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsConfigured => Value is not null;

    /// <summary>A human-readable name for where this came from, for the doctor's teaching messages.</summary>
    public string Description => Origin switch
    {
        ConnectionStringOrigin.Configured => "explicit configuration",
        ConnectionStringOrigin.EnvironmentVariable => $"the {Source} environment variable",
        ConnectionStringOrigin.PlatformConfigFile => $"the platform config file ({Source})",
        ConnectionStringOrigin.PlatformConfigFileMalformed => $"the platform config file ({Source}) — but it isn't valid JSON",
        ConnectionStringOrigin.ProjectOverride => $"the project override file ({Source})",
        _ => "nowhere — nothing is configured",
    };
}
