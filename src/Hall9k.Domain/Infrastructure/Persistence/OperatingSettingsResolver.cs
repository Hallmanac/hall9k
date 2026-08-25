using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Resolves the daemon's operating settings the same way <c>DaemonOptions</c> binds them at
/// daemon startup — environment variable, then the platform config file, then the built-in
/// default (backlog 59) — so <c>h9k daemon status</c> and <c>h9k config show</c> can name where
/// each one actually came from without needing a running daemon to ask. The env var name
/// follows .NET configuration's own double-underscore section-separator convention, the same
/// one <c>DaemonOptions.SectionName</c> ("Hall9k") binds against, so this mirrors rather than
/// invents a naming scheme.
/// </summary>
public static class OperatingSettingsResolver
{
    private const string EnvironmentPrefix = "Hall9k__";

    public static async Task<OperatingSettingsReport> ResolveAsync(CancellationToken cancellationToken)
    {
        ConfigFileReadResult read = await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken);
        OperatingSettings configured = read.Settings;

        List<string> unusableEnvironmentVariables = [];

        ResolvedSetting<int> concurrency = ResolveInt(
            $"{EnvironmentPrefix}MaxConcurrentAgentSessions",
            configured.MaxConcurrentAgentSessions,
            OperatingSettings.DefaultMaxConcurrentAgentSessions,
            unusableEnvironmentVariables);

        ResolvedSetting<string> defaultModel = ResolveString(
            $"{EnvironmentPrefix}DefaultModel",
            configured.DefaultModel,
            AgentModel.PlatformFallback,
            unusableEnvironmentVariables);

        List<RoleModelSetting> roles = [.. configured.ModelByRole.AsPairs().Select(pair =>
            new RoleModelSetting(
                pair.Role, ResolveOptionalString($"{EnvironmentPrefix}ModelByRole__{pair.Role}", pair.Model)))];

        return new OperatingSettingsReport(concurrency, defaultModel, roles, read.Problem, unusableEnvironmentVariables);
    }

    /// <summary>
    /// Unlike <see cref="ResolveString"/>, a set-but-unparseable value cannot just ride through as
    /// itself: <see cref="DaemonOptions"/> binds this key through <c>ConfigurationBinder</c>, which
    /// throws at options-resolution time rather than keeping the config-file/default value, so
    /// silently falling through here would report an origin and a value the daemon will never
    /// actually run with. The variable's raw value is recorded in <paramref name="unusable"/>
    /// instead, so a caller can name the mistake rather than the resolver quietly outranking it.
    /// </summary>
    private static ResolvedSetting<int> ResolveInt(
        string environmentVariable, int? configured, int fallback, List<string> unusable)
    {
        // Unlike ResolveString, an empty value is not treated as unset here: a shell that expands
        // an unset variable into "" (Hall9k__MaxConcurrentAgentSessions= with nothing after it —
        // the origin incident's own failure shape) still sets the variable, and the daemon's
        // ConfigurationBinder fails to parse "" as an int exactly the way it fails on "four".
        if (Environment.GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (int.TryParse(fromEnvironment, out int parsed))
            {
                return new ResolvedSetting<int>(parsed, SettingOrigin.EnvironmentVariable, environmentVariable);
            }

            unusable.Add(
                $"{environmentVariable} is set to \"{fromEnvironment}\", which is not a whole number — the "
                + "daemon will fail to start on this value rather than fall back to the config file or default.");
        }

        return configured is { } value
            ? new ResolvedSetting<int>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
            : new ResolvedSetting<int>(fallback, SettingOrigin.Default, null);
    }

    /// <summary>
    /// Unlike <see cref="ResolveOptionalString"/>, a set-but-empty value cannot just be reported
    /// as itself: this is the bottom of <see cref="AgentModel.Resolve"/>'s chain, where
    /// <paramref name="fallback"/> is the only tier left underneath, so a blank value here — which
    /// <c>AgentModel.FromInput</c> maps to <c>Unknown</c> — makes the daemon fall through straight
    /// to <paramref name="fallback"/>, never to <paramref name="configured"/>: ConfigurationBinder
    /// already overwrote the config-file value with the empty one before <c>AgentModel</c> ever
    /// sees it. Reporting the config-file value as still in force here would be exactly the "an
    /// unusable value here breaks every dispatch" mistake <see cref="ResolveInt"/> already guards
    /// against for the integer setting, so the same <paramref name="unusable"/> list records it.
    /// </summary>
    private static ResolvedSetting<string> ResolveString(
        string environmentVariable, string? configured, string fallback, List<string> unusable)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (fromEnvironment.Length == 0)
            {
                unusable.Add(
                    $"{environmentVariable} is set to an empty value — AgentModel treats that as unset, so the "
                    + "daemon falls through to the platform default rather than to the config file's value.");
                return new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
            }

            return IsUsableModel(fromEnvironment, environmentVariable, unusable)
                ? new ResolvedSetting<string>(fromEnvironment, SettingOrigin.EnvironmentVariable, environmentVariable)
                : new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
        }

        if (configured is { Length: > 0 } value)
        {
            return IsUsableModel(value, Hall9kDatabase.ConfigFile, unusable)
                ? new ResolvedSetting<string>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
                : new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
        }

        return new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a value <c>AgentModel</c> can actually spawn on —
    /// the same <see cref="AgentModel.IsWellFormed"/> gate <c>ConfigSetCommand.ApplyModel</c>
    /// applies on the write path, applied here too because a hand-edited config file or a raw
    /// environment variable never goes through that gate. This is the platform-wide bottom of the
    /// resolution chain, so an unusable value here breaks every dispatch on the node
    /// (<c>ClaudeExecutor.SpawnAsync</c> throws for every fresh spawn) rather than one project or
    /// task — exactly the consequence naming the mistake in <paramref name="unusable"/> exists to
    /// surface instead of reporting it as a healthy, in-force setting.
    /// </summary>
    private static bool IsUsableModel(string candidate, string source, List<string> unusable)
    {
        AgentModel resolved = AgentModel.FromInput(candidate);
        if (resolved == AgentModel.Unknown || resolved.IsWellFormed)
        {
            return true;
        }

        unusable.Add(
            $"{source} is set to \"{candidate}\", which is not a usable model name — the daemon will fail to "
            + "spawn every agent session on this node rather than fall back to the config file or default.");
        return false;
    }

    private static ResolvedSetting<string?> ResolveOptionalString(string environmentVariable, string? configured)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            return new ResolvedSetting<string?>(fromEnvironment, SettingOrigin.EnvironmentVariable, environmentVariable);
        }

        return configured is { Length: > 0 } value
            ? new ResolvedSetting<string?>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
            : new ResolvedSetting<string?>(null, SettingOrigin.Default, null);
    }
}
