namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>One role's configured model and where that value came from.</summary>
public sealed record RoleModelSetting(string Role, ResolvedSetting<string?> Model);

/// <summary>
/// The true consequence to state alongside a <see cref="ConfigFileProblem.Message"/>, an
/// unpersisted in-process outcome rather than a value object: a document-level failure the
/// daemon already skips gracefully at startup (a syntax error, or valid JSON whose top level is
/// not an object — environment variables and built-in defaults still apply, none of the file's
/// settings take effect); a value-shape failure on the four review-cycle-cap leaves
/// <c>ConfigurationBinder</c> has no guard for, which crashes the daemon outright; and a
/// value-shape failure on any other leaf, which <c>ConfigurationBinder</c> silently leaves at its
/// default while binding every sibling key normally — so the file is still in force, just not for
/// that one setting. The resolver-owned keys — the three concurrency settings (Decisions Log
/// #111's follow-up) plus the periodic spend budget and its period (Decisions Log #120) — can no
/// longer crash the daemon this second way: <c>Hall9k.Daemon.DaemonOptionsBinding.ResolverOwnedKeys</c>
/// excludes them from the daemon's own <c>ConfigurationBinder</c> call; every other
/// <c>DaemonOptions</c> leaf in this section — the four review-cycle caps included — is still
/// bound through <c>ConfigurationBinder</c> and can still crash startup on a bad value
/// (independent pre-PR review, cycle 4, adversarial lens).
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
/// <param name="AffectsResolverOwnedKey">
/// Whether the malformed leaf named by <paramref name="Message"/> is one of the keys
/// <c>Hall9k.Daemon.DaemonOptionsBinding.ResolverOwnedKeys</c> excludes from the daemon's own
/// <c>ConfigurationBinder</c> call: the three concurrency settings (Decisions Log #111's
/// follow-up) — <c>maxConcurrentTaskRuns</c>, <c>sessionCapPerRun</c>, <c>maxConcurrentAgentSessions</c>
/// — or the periodic spend budget and its period (Decisions Log #120) — <c>spendBudgetTokens</c>,
/// <c>spendPeriod</c>. <see cref="DescribeConsequence"/> names the mechanism that actually
/// ignored the leaf, and for these five it is <see cref="OperatingSettingsResolver"/> treating a
/// malformed value as absent, not <c>ConfigurationBinder</c> declining a conversion — the binder
/// never sees these leaves at all (independent pre-PR review, cycle 3, adversarial lens). False
/// for every other leaf (<c>defaultModel</c>, <c>modelByRole</c>), where the binder is still the
/// accurate mechanism, and for the whole-section recovery paths that could not identify which
/// leaf failed.
/// </param>
public sealed record ConfigFileProblem(
    string Message, ConfigFileProblemConsequence Consequence, bool AffectsResolverOwnedKey = false)
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
        ConfigFileProblemConsequence.SettingIsIgnored when AffectsResolverOwnedKey =>
            "This setting no longer binds through the daemon's ConfigurationBinder at all — it is one of the "
            + "settings OperatingSettingsResolver reads directly (the node's concurrency settings or its "
            + "periodic spend budget), and a malformed value there is treated as absent rather than converted — "
            + "so this setting does not take its value from the file, and every other setting in the file, and "
            + "environment variables and built-in defaults, still apply.",
        ConfigFileProblemConsequence.SettingIsIgnored =>
            "The daemon's own ConfigurationBinder has no conversion for this value, so this setting does not "
            + "take its value from the file — every other setting in the file, and environment variables and "
            + "built-in defaults, still apply.",
        ConfigFileProblemConsequence.DaemonFailsToStart =>
            "The daemon's own ConfigurationBinder crashes at startup on this value — nothing in the file, "
            + "environment variables, or built-in defaults takes effect until it is fixed.",
        _ => "The daemon skips the file for this run — environment variables and built-in defaults still apply.",
    };
}

/// <summary>The outcome of a non-throwing operating-settings read: the settings, or why not.</summary>
/// <param name="MaxConcurrentAgentSessionsIsFabricatedZero">
/// Whether <see cref="OperatingSettings.MaxConcurrentAgentSessions"/>'s <c>0</c> on
/// <paramref name="Settings"/> is <c>PlatformConfigFile.ApplyIntBinderQuirks</c>'s
/// own simulation of what <c>ConfigurationBinder</c> would have bound a JSON <c>null</c> or
/// <c>{}</c> leaf to, rather than a real configured <c>0</c> read from the file. That simulation
/// exists only for <c>h9k config show</c>'s own accuracy about the retired key's JSON shape;
/// <see cref="OperatingSettingsResolver.ResolveMaxConcurrentTaskRuns"/>'s legacy-conversion walk
/// reads this flag separately so it can treat the leaf as genuinely absent at this level — falling
/// through rather than converting a fabricated <c>0</c> into a run ceiling of one and reporting
/// that as a real config-file-driven conversion (independent pre-PR review, cycle 1, adversarial
/// lens: a key holding no number at all was reported as a configured <c>0</c> that got converted).
/// </param>
public sealed record ConfigFileReadResult(
    OperatingSettings Settings, ConfigFileProblem? Problem, bool MaxConcurrentAgentSessionsIsFabricatedZero)
{
    public static ConfigFileReadResult Ok(OperatingSettings settings, bool maxConcurrentAgentSessionsIsFabricatedZero) =>
        new(settings, null, maxConcurrentAgentSessionsIsFabricatedZero);

    /// <summary>
    /// A document-level failure: nothing in the file can be trusted, so every setting falls back
    /// to the environment variable or built-in default.
    /// </summary>
    public static ConfigFileReadResult Failed(string message) =>
        new(new OperatingSettings(), new ConfigFileProblem(message, ConfigFileProblemConsequence.DaemonSkipsFile), false);

    /// <summary>
    /// A value-shape failure on one of the four review-cycle-cap leaves <c>ConfigurationBinder</c>
    /// crashes the daemon on — the document parsed, but nothing in it can be trusted to be what
    /// the daemon will actually run with, because the daemon will not run at all.
    /// </summary>
    public static ConfigFileReadResult DaemonCrashes(string message) =>
        new(new OperatingSettings(), new ConfigFileProblem(message, ConfigFileProblemConsequence.DaemonFailsToStart), false);

    /// <summary>
    /// A value-shape failure on any leaf: <paramref name="settings"/> is the partial
    /// recovery with the malformed leaf left at its default, mirroring what
    /// <c>ConfigurationBinder</c> actually binds for every sibling key.
    /// </summary>
    public static ConfigFileReadResult SettingIgnored(
        OperatingSettings settings, string message, bool maxConcurrentAgentSessionsIsFabricatedZero,
        bool affectsResolverOwnedKey = false) =>
        new(settings,
            new ConfigFileProblem(message, ConfigFileProblemConsequence.SettingIsIgnored, affectsResolverOwnedKey),
            maxConcurrentAgentSessionsIsFabricatedZero);
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
/// <c>max-concurrent-task-runs</c> key read directly (Decisions Log #111) — what lets
/// <c>h9k daemon status</c> and <c>h9k config show</c> name the conversion rather than present a
/// converted number as though it were configured in runs all along.
/// <see cref="MaxConcurrentTaskRunsShadowsConfigFileValue"/> is true only for the one shape that
/// conversion flag alone cannot distinguish: an environment-level legacy conversion winning while
/// the config file already carries its own <c>max-concurrent-task-runs</c> value, which a plain
/// "set max-concurrent-task-runs" remedy would not actually apply, since the environment variable
/// still outranks the file regardless of which key it names (independent pre-PR review, cycle 1,
/// adversarial lens).
/// <see cref="MaxConcurrentAgentSessionsIsFabricatedZero"/> mirrors
/// <see cref="ConfigFileReadResult.MaxConcurrentAgentSessionsIsFabricatedZero"/> forward into the
/// report so a renderer can tell the difference between a genuinely configured zero (which really
/// is consulted as a fallback wherever <c>max-concurrent-task-runs</c> is absent at that level) and
/// this binder-quirk simulation of a <c>null</c> or <c>{}</c> leaf, which
/// <see cref="OperatingSettingsResolver.ResolveMaxConcurrentTaskRuns"/> deliberately treats as
/// absent rather than falling back to (independent pre-PR review, cycle 1, both lenses).
/// </summary>
/// <param name="SpendBudgetTokens">
/// The node's periodic token-spend budget (backlog: spend-governor step three), or null when
/// nothing sets one — the resolver's own fallback for this setting is "no budget", never a
/// compiled number, so <see cref="ResolvedSetting{T}.Origin"/> of <see cref="SettingOrigin.Default"/>
/// here means dispatch is unbudgeted rather than budgeted at some built-in ceiling.
/// </param>
/// <param name="SpendPeriod">
/// The window that budget resets on ("day" or "week"), always resolved even with no budget set —
/// <see cref="OperatingSettings.DefaultSpendPeriod"/> underneath it — so a later
/// <c>h9k config set --spend-budget</c> with no <c>--spend-period</c> has an answer already.
/// </param>
public sealed record OperatingSettingsReport(
    ResolvedSetting<int> MaxConcurrentAgentSessions,
    bool MaxConcurrentAgentSessionsIsFabricatedZero,
    ResolvedSetting<int> MaxConcurrentTaskRuns,
    bool MaxConcurrentTaskRunsConvertedFromLegacy,
    bool MaxConcurrentTaskRunsShadowsConfigFileValue,
    ResolvedSetting<int> SessionCapPerRun,
    ResolvedSetting<string> DefaultModel,
    IReadOnlyList<RoleModelSetting> ModelByRole,
    ConfigFileProblem? ConfigFileProblem,
    IReadOnlyList<string> UnusableEnvironmentVariables,
    ResolvedSetting<int> MaxComplianceReviewCycles,
    ResolvedSetting<int> MaxAdversarialReviewCycles,
    ResolvedSetting<int> MaxFinalFullPassRounds,
    ResolvedSetting<int> LifetimeReviewCycleBudget,
    ResolvedSetting<long?> SpendBudgetTokens,
    ResolvedSetting<string> SpendPeriod);
