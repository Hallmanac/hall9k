namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>One role's configured model and where that value came from.</summary>
public sealed record RoleModelSetting(string Role, ResolvedSetting<string?> Model);

/// <summary>
/// The true consequence to state alongside a <see cref="ConfigFileProblem.Message"/>, an
/// unpersisted in-process outcome rather than a value object: a document-level failure the
/// daemon already skips gracefully at startup (a syntax error, or valid JSON whose top level is
/// not an object — environment variables and built-in defaults still apply, none of the file's
/// settings take effect); a value-shape failure on the one leaf <c>ConfigurationBinder</c> has no
/// guard for, which crashes the daemon outright; and a value-shape failure on any other leaf,
/// which <c>ConfigurationBinder</c> silently leaves at its default while binding every sibling
/// key normally — so the file is still in force, just not for that one setting.
/// </summary>
public enum ConfigFileProblemConsequence
{
    DaemonSkipsFile,
    DaemonFailsToStart,
    SettingIsIgnored,
}

/// <summary>
/// Why <see cref="PlatformConfigFile.TryReadOperatingSettingsAsync"/> could not read the "hall9k"
/// section (or one setting inside it) cleanly, and the true consequence to state alongside the
/// accurate <paramref name="Message"/> domain layer already built.
/// </summary>
public sealed record ConfigFileProblem(string Message, ConfigFileProblemConsequence Consequence);

/// <summary>The outcome of a non-throwing operating-settings read: the settings, or why not.</summary>
public sealed record ConfigFileReadResult(OperatingSettings Settings, ConfigFileProblem? Problem)
{
    public static ConfigFileReadResult Ok(OperatingSettings settings) => new(settings, null);

    /// <summary>
    /// A document-level failure: nothing in the file can be trusted, so every setting falls back
    /// to the environment variable or built-in default.
    /// </summary>
    public static ConfigFileReadResult Failed(string message) =>
        new(new OperatingSettings(), new ConfigFileProblem(message, ConfigFileProblemConsequence.DaemonSkipsFile));

    /// <summary>
    /// A value-shape failure on the one leaf <c>ConfigurationBinder</c> crashes the daemon on —
    /// the document parsed, but nothing in it can be trusted to be what the daemon will actually
    /// run with, because the daemon will not run at all.
    /// </summary>
    public static ConfigFileReadResult DaemonCrashes(string message) =>
        new(new OperatingSettings(), new ConfigFileProblem(message, ConfigFileProblemConsequence.DaemonFailsToStart));

    /// <summary>
    /// A value-shape failure on any other leaf: <paramref name="settings"/> is the partial
    /// recovery with the malformed leaf left at its default, mirroring what
    /// <c>ConfigurationBinder</c> actually binds for every sibling key.
    /// </summary>
    public static ConfigFileReadResult SettingIgnored(OperatingSettings settings, string message) =>
        new(settings, new ConfigFileProblem(message, ConfigFileProblemConsequence.SettingIsIgnored));
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
