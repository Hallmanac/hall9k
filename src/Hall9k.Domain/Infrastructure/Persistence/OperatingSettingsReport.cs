namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>One role's configured model and where that value came from.</summary>
public sealed record RoleModelSetting(string Role, ResolvedSetting<string?> Model);

/// <summary>
/// Why <see cref="PlatformConfigFile.TryReadOperatingSettingsAsync"/> could not read the "hall9k"
/// section, and the true consequence to state alongside the accurate <paramref name="Message"/>
/// domain layer already built: <see cref="DaemonFailsToStart"/> distinguishes a document-level
/// failure the daemon already skips gracefully at startup (a syntax error, or valid JSON whose
/// top level is not an object — environment variables and built-in defaults still apply) from a
/// value-shape failure inside an otherwise well-formed document, which <c>ConfigurationBinder</c>
/// has no guard for and crashes the daemon on outright.
/// </summary>
public sealed record ConfigFileProblem(string Message, bool DaemonFailsToStart);

/// <summary>The outcome of a non-throwing operating-settings read: the settings, or why not.</summary>
public sealed record ConfigFileReadResult(OperatingSettings Settings, ConfigFileProblem? Problem)
{
    public static ConfigFileReadResult Ok(OperatingSettings settings) => new(settings, null);

    public static ConfigFileReadResult Failed(string message, bool daemonFailsToStart) =>
        new(new OperatingSettings(), new ConfigFileProblem(message, daemonFailsToStart));
}

/// <summary>
/// Every daemon operating setting the CLI names directly, resolved the same way
/// <c>DaemonOptions</c> binds them at daemon startup: environment variable, then the platform
/// config file, then the built-in default (backlog 59). <see cref="ConfigFileProblem"/> is
/// carried separately from "not configured", the same distinction
/// <see cref="ConnectionStringOrigin.PlatformConfigFileMalformed"/> makes for the connection
/// string — the fix is repairing the file, not the "nothing configured" guidance.
/// <see cref="UnusableEnvironmentVariables"/> is the same idea for a variable that is set but
/// fails to parse: the resolver falls through to a lower tier for the *value* it reports, but the
/// mistake itself has to survive into the report or an operator is told a healthy default is in
/// effect while the daemon dies at startup on the very variable that was silently discarded.
/// </summary>
public sealed record OperatingSettingsReport(
    ResolvedSetting<int> MaxConcurrentAgentSessions,
    ResolvedSetting<string> DefaultModel,
    IReadOnlyList<RoleModelSetting> ModelByRole,
    ConfigFileProblem? ConfigFileProblem,
    IReadOnlyList<string> UnusableEnvironmentVariables);
