namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>One role's configured model and where that value came from.</summary>
public sealed record RoleModelSetting(string Role, ResolvedSetting<string?> Model);

/// <summary>
/// The true consequence to state alongside a <see cref="ConfigFileProblem.Message"/>, an
/// unpersisted in-process outcome rather than a value object: a document-level failure the
/// daemon already skips gracefully at startup (a syntax error, or valid JSON whose top level is
/// not an object — environment variables and built-in defaults still apply, none of the file's
/// settings take effect); and a value-shape failure on any leaf, which <c>ConfigurationBinder</c>
/// silently leaves at its default while binding every sibling key normally — so the file is
/// still in force, just not for that one setting. The three concurrency keys can no longer crash
/// the daemon outright: <c>Hall9k.Daemon.DaemonOptionsBinding.ResolverOwnedKeys</c> excludes them
/// from the daemon's own <c>ConfigurationBinder</c> call (Decisions Log #108's follow-up), which
/// retired the one leaf (<c>maxConcurrentAgentSessions</c>) that used to; every other
/// <c>DaemonOptions</c> leaf in this section is still bound through <c>ConfigurationBinder</c> and
/// can still crash startup on a bad value (independent pre-PR review, cycle 4, adversarial lens).
/// </summary>
public enum ConfigFileProblemConsequence
{
    DaemonSkipsFile,
    SettingIsIgnored,
}

/// <summary>
/// Why <see cref="PlatformConfigFile.TryReadOperatingSettingsAsync"/> could not read the "hall9k"
/// section (or one setting inside it) cleanly, and the true consequence to state alongside the
/// accurate <paramref name="Message"/> domain layer already built.
/// </summary>
public sealed record ConfigFileProblem(string Message, ConfigFileProblemConsequence Consequence)
{
    /// <summary>
    /// The consequence sentence both <c>h9k config show</c>/<c>h9k daemon status</c>
    /// (<c>Hall9k.Cli.Infrastructure.OperatingSettingsRendering</c>) and the daemon's own startup
    /// log (<c>Hall9k.Daemon.Dispatch.DispatchLoop</c>) print alongside <see cref="Message"/> — kept
    /// here, in Domain, because the reference graph lets both of those projects reach it while
    /// neither can reach the other, and a sentence two callers would otherwise hand-copy is a
    /// sentence that drifts (independent pre-PR review, cycle 2, adversarial lens: the daemon log
    /// used to interpolate the raw <see cref="ConfigFileProblemConsequence"/> member instead).
    /// </summary>
    public string DescribeConsequence() => Consequence switch
    {
        ConfigFileProblemConsequence.SettingIsIgnored =>
            "The daemon's own ConfigurationBinder has no conversion for this value, so this setting does not "
            + "take its value from the file — every other setting in the file, and environment variables and "
            + "built-in defaults, still apply.",
        _ => "The daemon skips the file for this run — environment variables and built-in defaults still apply.",
    };
}

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
    /// A value-shape failure on any leaf: <paramref name="settings"/> is the partial
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
/// <see cref="MaxConcurrentTaskRunsConvertedFromLegacy"/> is true when
/// <see cref="MaxConcurrentTaskRuns"/>'s effective value came from converting the retired
/// <see cref="OperatingSettings.MaxConcurrentAgentSessions"/> key rather than from a
/// <c>max-concurrent-task-runs</c> key read directly (Decisions Log #108) — what lets
/// <c>h9k daemon status</c> and <c>h9k config show</c> name the conversion rather than present a
/// converted number as though it were configured in runs all along.
/// <see cref="MaxConcurrentTaskRunsShadowsConfigFileValue"/> is true only for the one shape that
/// conversion flag alone cannot distinguish: an environment-level legacy conversion winning while
/// the config file already carries its own <c>max-concurrent-task-runs</c> value, which a plain
/// "set max-concurrent-task-runs" remedy would not actually apply, since the environment variable
/// still outranks the file regardless of which key it names (independent pre-PR review, cycle 1,
/// adversarial lens).
/// </summary>
public sealed record OperatingSettingsReport(
    ResolvedSetting<int> MaxConcurrentAgentSessions,
    ResolvedSetting<int> MaxConcurrentTaskRuns,
    bool MaxConcurrentTaskRunsConvertedFromLegacy,
    bool MaxConcurrentTaskRunsShadowsConfigFileValue,
    ResolvedSetting<int> SessionCapPerRun,
    ResolvedSetting<string> DefaultModel,
    IReadOnlyList<RoleModelSetting> ModelByRole,
    ConfigFileProblem? ConfigFileProblem,
    IReadOnlyList<string> UnusableEnvironmentVariables);
