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
            $"{EnvironmentPrefix}DefaultModel", configured.DefaultModel, AgentModel.PlatformFallback);

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

    private static ResolvedSetting<string> ResolveString(string environmentVariable, string? configured, string fallback)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return new ResolvedSetting<string>(fromEnvironment, SettingOrigin.EnvironmentVariable, environmentVariable);
        }

        return configured is { Length: > 0 } value
            ? new ResolvedSetting<string>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
            : new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
    }

    private static ResolvedSetting<string?> ResolveOptionalString(string environmentVariable, string? configured)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return new ResolvedSetting<string?>(fromEnvironment, SettingOrigin.EnvironmentVariable, environmentVariable);
        }

        return configured is { Length: > 0 } value
            ? new ResolvedSetting<string?>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
            : new ResolvedSetting<string?>(null, SettingOrigin.Default, null);
    }
}
