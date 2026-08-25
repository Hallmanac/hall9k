namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Where an operating setting's effective value came from (backlog 59) — printed by
/// <c>h9k daemon status</c> and <c>h9k config show</c> so a wrong-quoting or wrong-file mistake
/// is diagnosable from one command rather than a stale-log hunt. In-process only, never
/// persisted, so an enum is the right shape (AGENTS.md §8).
/// </summary>
public enum SettingOrigin
{
    /// <summary>Nothing set it anywhere; this is <c>DaemonOptions</c>'s own shipped default.</summary>
    Default,

    /// <summary>Read from the "hall9k" section of <see cref="Hall9kDatabase.ConfigFile"/>.</summary>
    PlatformConfigFile,

    /// <summary>An environment variable under the <c>Hall9k__</c> prefix — this shell, this invocation.</summary>
    EnvironmentVariable,
}
