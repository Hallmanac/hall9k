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
    /// <summary>
    /// The <c>Hall9k__</c> section-separator convention every concurrency-setting environment
    /// variable name is built from — public so a remedy message naming one of these variables (the
    /// daemon's own startup log, or <c>h9k config show</c>/<c>h9k daemon status</c>) builds the same
    /// name this resolver reads, rather than a copy that can drift from it.
    /// </summary>
    public const string EnvironmentPrefix = "Hall9k__";

    public static async Task<OperatingSettingsReport> ResolveAsync(CancellationToken cancellationToken)
    {
        ConfigFileReadResult read = await PlatformConfigFile.TryReadOperatingSettingsAsync(cancellationToken);
        OperatingSettings configured = read.Settings;

        List<string> unusableEnvironmentVariables = [];

        (ResolvedSetting<int> maxConcurrentTaskRuns, bool convertedFromLegacy, bool shadowsConfigFileValue) =
            ResolveMaxConcurrentTaskRuns(
                configured, read.MaxConcurrentAgentSessionsIsFabricatedZero, unusableEnvironmentVariables);

        ResolvedSetting<int> concurrency = ResolveInt(
            $"{EnvironmentPrefix}MaxConcurrentAgentSessions",
            configured.MaxConcurrentAgentSessions,
            OperatingSettings.DefaultMaxConcurrentAgentSessions,
            unusableEnvironmentVariables,
            convertedFromLegacy ? WarnIfBelowCeilingFloor : null,
            "max-concurrent-agent-sessions",
            unparseableValueFallsBackRatherThanCrashing: true);

        ResolvedSetting<int> sessionCapPerRun =
            ResolveSessionCapPerRun(configured.SessionCapPerRun, unusableEnvironmentVariables);

        ResolvedSetting<string> defaultModel = ResolveString(
            $"{EnvironmentPrefix}DefaultModel",
            configured.DefaultModel,
            AgentModel.PlatformFallback,
            unusableEnvironmentVariables);

        List<RoleModelSetting> roles = [.. configured.ModelByRole.AsPairs().Select(pair =>
            new RoleModelSetting(
                pair.Role,
                ResolveOptionalString(
                    $"{EnvironmentPrefix}ModelByRole__{pair.Role}", pair.Model, pair.Role, unusableEnvironmentVariables)))];

        ResolvedSetting<int> maxComplianceReviewCycles = ResolveInt(
            $"{EnvironmentPrefix}MaxComplianceReviewCycles",
            configured.MaxComplianceReviewCycles,
            OperatingSettings.DefaultMaxComplianceReviewCycles,
            unusableEnvironmentVariables,
            (source, value, unusable) => WarnIfBelowReviewCapFloor(source, value, "max-compliance-review-cycles", unusable),
            "max-compliance-review-cycles",
            unparseableValueFallsBackRatherThanCrashing: false);

        ResolvedSetting<int> maxAdversarialReviewCycles = ResolveInt(
            $"{EnvironmentPrefix}MaxAdversarialReviewCycles",
            configured.MaxAdversarialReviewCycles,
            OperatingSettings.DefaultMaxAdversarialReviewCycles,
            unusableEnvironmentVariables,
            (source, value, unusable) => WarnIfBelowReviewCapFloor(source, value, "max-adversarial-review-cycles", unusable),
            "max-adversarial-review-cycles",
            unparseableValueFallsBackRatherThanCrashing: false);

        ResolvedSetting<int> maxFinalFullPassRounds = ResolveInt(
            $"{EnvironmentPrefix}MaxFinalFullPassRounds",
            configured.MaxFinalFullPassRounds,
            OperatingSettings.DefaultMaxFinalFullPassRounds,
            unusableEnvironmentVariables,
            (source, value, unusable) => WarnIfBelowReviewCapFloor(source, value, "max-final-full-pass-rounds", unusable),
            "max-final-full-pass-rounds",
            unparseableValueFallsBackRatherThanCrashing: false);

        ResolvedSetting<int> lifetimeReviewCycleBudget = ResolveInt(
            $"{EnvironmentPrefix}LifetimeReviewCycleBudget",
            configured.LifetimeReviewCycleBudget,
            OperatingSettings.DefaultLifetimeReviewCycleBudget,
            unusableEnvironmentVariables,
            (source, value, unusable) => WarnIfBelowReviewCapFloor(source, value, "lifetime-review-cycle-budget", unusable),
            "lifetime-review-cycle-budget",
            unparseableValueFallsBackRatherThanCrashing: false);

        ResolvedSetting<long?> spendBudgetTokens =
            ResolveSpendBudgetTokens(configured.SpendBudgetTokens, unusableEnvironmentVariables);
        ResolvedSetting<string> spendPeriod = ResolveSpendPeriod(configured.SpendPeriod, unusableEnvironmentVariables);
        ResolvedSetting<string> reviewStageComposition =
            ResolveReviewStageComposition(configured.ReviewStageComposition, unusableEnvironmentVariables);

        return new OperatingSettingsReport(
            concurrency, read.MaxConcurrentAgentSessionsIsFabricatedZero, maxConcurrentTaskRuns, convertedFromLegacy,
            shadowsConfigFileValue, sessionCapPerRun, defaultModel, roles, read.Problem, unusableEnvironmentVariables,
            maxComplianceReviewCycles, maxAdversarialReviewCycles, maxFinalFullPassRounds, lifetimeReviewCycleBudget,
            spendBudgetTokens, spendPeriod, reviewStageComposition);
    }

    /// <summary>
    /// This node's review stage composition (task: the review pipeline's stage composition
    /// becomes configuration recorded per run) — the same five-recognized-word closed set
    /// <see cref="Features.Run.ReviewStageComposition.FromInput"/> already reads, so an
    /// unrecognized value here is treated as absent rather than ridden through the way
    /// <see cref="ResolveString"/> would for a model name, mirroring <see cref="ResolveSpendPeriod"/>'s
    /// own shape for a closed vocabulary.
    /// </summary>
    private static ResolvedSetting<string> ResolveReviewStageComposition(string? configured, List<string> unusable)
    {
        string environmentVariable = $"{EnvironmentPrefix}ReviewStageComposition";
        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            Features.Run.ReviewStageComposition composition = Features.Run.ReviewStageComposition.FromInput(fromEnvironment);
            if (composition != Features.Run.ReviewStageComposition.Unknown)
            {
                return new ResolvedSetting<string>(composition.Value, SettingOrigin.EnvironmentVariable, environmentVariable);
            }

            unusable.Add(
                $"{environmentVariable} is set to \"{fromEnvironment}\", which is not a recognized review stage "
                + "composition — it is treated as absent, and review-stage-composition falls back to the config "
                + "file or default instead.");
        }

        if (configured is { Length: > 0 } value)
        {
            Features.Run.ReviewStageComposition composition = Features.Run.ReviewStageComposition.FromInput(value);
            if (composition != Features.Run.ReviewStageComposition.Unknown)
            {
                return new ResolvedSetting<string>(composition.Value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);
            }

            unusable.Add(
                $"{Hall9kDatabase.ConfigFile} sets review-stage-composition to \"{value}\", which is not a "
                + "recognized review stage composition — it is treated as absent, and review-stage-composition "
                + "falls back to the default instead.");
        }

        return new ResolvedSetting<string>(
            Features.Run.ReviewStageComposition.FullPipeline.Value, SettingOrigin.Default, null);
    }

    /// <summary>
    /// This node's periodic token-spend budget: nullable end to end, because "no budget" is
    /// itself the fallback rather than a compiled ceiling (unlike every other numeric setting
    /// this resolver reads) — a config-file or environment value must be a non-negative whole
    /// number of tokens, and anything else is treated as absent rather than crashing, the same
    /// shape <see cref="ResolveSessionCapPerRun"/> already gives the other settings
    /// <c>DaemonOptionsBinding.ResolverOwnedKeys</c> excludes from <c>ConfigurationBinder</c>.
    /// </summary>
    private static ResolvedSetting<long?> ResolveSpendBudgetTokens(long? configured, List<string> unusable)
    {
        string environmentVariable = $"{EnvironmentPrefix}SpendBudgetTokens";
        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (long.TryParse(fromEnvironment, out long parsed) && parsed >= 0)
            {
                return new ResolvedSetting<long?>(parsed, SettingOrigin.EnvironmentVariable, environmentVariable);
            }

            unusable.Add(
                $"{environmentVariable} is set to \"{fromEnvironment}\", which is not a non-negative whole number "
                + "of tokens — it is treated as absent, and spend-budget-tokens falls back to the config file or "
                + "no budget instead.");
        }

        if (configured is { } value)
        {
            if (value >= 0)
            {
                return new ResolvedSetting<long?>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);
            }

            unusable.Add(
                $"{Hall9kDatabase.ConfigFile} sets spend-budget-tokens to {value}, which is negative — it is "
                + "treated as absent, and no budget applies.");
        }

        return new ResolvedSetting<long?>(null, SettingOrigin.Default, null);
    }

    /// <summary>
    /// The window <see cref="OperatingSettings.SpendBudgetTokens"/> resets on — "day" or "week"
    /// only, unlike every other string setting this resolver reads, so an unrecognized value is
    /// treated as absent rather than ridden through as itself the way <see cref="ResolveString"/>
    /// would for a model name.
    /// </summary>
    private static ResolvedSetting<string> ResolveSpendPeriod(string? configured, List<string> unusable)
    {
        string environmentVariable = $"{EnvironmentPrefix}SpendPeriod";
        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            SpendPeriod period = SpendPeriod.FromInput(fromEnvironment);
            if (period.IsWellFormed)
            {
                return new ResolvedSetting<string>(period.Value, SettingOrigin.EnvironmentVariable, environmentVariable);
            }

            unusable.Add(
                $"{environmentVariable} is set to \"{fromEnvironment}\", which is neither \"day\" nor \"week\" — "
                + "it is treated as absent, and spend-period falls back to the config file or default instead.");
        }

        if (configured is { Length: > 0 } value)
        {
            SpendPeriod period = SpendPeriod.FromInput(value);
            if (period.IsWellFormed)
            {
                return new ResolvedSetting<string>(period.Value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);
            }

            unusable.Add(
                $"{Hall9kDatabase.ConfigFile} sets spend-period to \"{value}\", which is neither \"day\" nor "
                + "\"week\" — it is treated as absent, and spend-period falls back to the default instead.");
        }

        return new ResolvedSetting<string>(OperatingSettings.DefaultSpendPeriod, SettingOrigin.Default, null);
    }

    /// <summary>
    /// The node ceiling, resolved in its own unit (Decisions Log #111): a
    /// <c>max-concurrent-task-runs</c> key wins wherever it is set, and only where it is absent
    /// does the retired <c>max-concurrent-agent-sessions</c> key convert — checked at each
    /// precedence level independently, exactly as the acceptance criteria demand, rather than as
    /// two globally-merged signals. That distinction matters when the two keys are set at
    /// different levels: an environment variable naming only the legacy key must still outrank a
    /// config-file value for the new key, the same way the legacy key itself would have outranked
    /// it before this decision — "the new key wins when both exist" is a same-level statement, not
    /// a global one. <c>ShadowsConfigFileValue</c> names the one case that statement leaves
    /// dangerous to a naive "set max-concurrent-task-runs to stop relying on the conversion"
    /// remedy: an environment-level legacy conversion winning while the config file already carries
    /// its own <c>max-concurrent-task-runs</c> value, which that remedy would not change at all,
    /// since the environment variable still outranks the file regardless (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    /// <param name="maxConcurrentAgentSessionsIsFabricatedZero">
    /// <see cref="ConfigFileReadResult.MaxConcurrentAgentSessionsIsFabricatedZero"/> — when true,
    /// <paramref name="configured"/>'s <c>MaxConcurrentAgentSessions</c> is a simulated <c>0</c>
    /// for a file leaf that actually held no number at all, so the config-file level treats it as
    /// absent rather than converting a fabricated zero into a run ceiling of one (independent
    /// pre-PR review, cycle 1, adversarial lens).
    /// </param>
    private static (ResolvedSetting<int> Setting, bool ConvertedFromLegacy, bool ShadowsConfigFileValue)
        ResolveMaxConcurrentTaskRuns(
            OperatingSettings configured, bool maxConcurrentAgentSessionsIsFabricatedZero, List<string> unusable)
    {
        if (ResolveRunsAtLevel(
                GetEnvironmentVariable($"{EnvironmentPrefix}MaxConcurrentTaskRuns"),
                GetEnvironmentVariable($"{EnvironmentPrefix}MaxConcurrentAgentSessions"),
                $"{EnvironmentPrefix}MaxConcurrentTaskRuns", $"{EnvironmentPrefix}MaxConcurrentAgentSessions",
                SettingOrigin.EnvironmentVariable, unusable) is { } fromEnvironment)
        {
            bool shadowsConfigFileValue = fromEnvironment.ConvertedFromLegacy && configured.MaxConcurrentTaskRuns is not null;
            return (fromEnvironment.Setting, fromEnvironment.ConvertedFromLegacy, shadowsConfigFileValue);
        }

        string? legacyAtFileLevel = maxConcurrentAgentSessionsIsFabricatedZero
            ? null
            : configured.MaxConcurrentAgentSessions?.ToString();
        if (ResolveRunsAtLevel(
                configured.MaxConcurrentTaskRuns?.ToString(), legacyAtFileLevel,
                Hall9kDatabase.ConfigFile, Hall9kDatabase.ConfigFile,
                SettingOrigin.PlatformConfigFile, unusable) is { } fromFile)
        {
            return (fromFile.Setting, fromFile.ConvertedFromLegacy, false);
        }

        return (new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentTaskRuns, SettingOrigin.Default, null), false, false);
    }

    /// <summary>
    /// One precedence level's own answer to "how many runs": the new key if it parses, else the
    /// legacy key converted, else nothing — <see langword="null"/> here means "this level says
    /// nothing", which is what lets the caller fall through to the next level rather than treating
    /// an unset level as a run ceiling of zero.
    /// </summary>
    private static (ResolvedSetting<int> Setting, bool ConvertedFromLegacy)? ResolveRunsAtLevel(
        string? rawNewKey, string? rawLegacyKey, string newKeySource, string legacySource,
        SettingOrigin origin, List<string> unusable)
    {
        if (rawNewKey is { } newValue)
        {
            if (int.TryParse(newValue, out int runs))
            {
                WarnIfBelowRunFloor(newKeySource, runs, unusable);
                return (new ResolvedSetting<int>(runs, origin, newKeySource), false);
            }

            // Unlike the legacy max-concurrent-agent-sessions key, this one is never bound through
            // ConfigurationBinder: Program.cs excludes it from the section its own Bind() call
            // sees, because the internal setter alone does not stop the binder from attempting the
            // conversion (BindProperty converts before it ever checks whether the property has a
            // public setter to assign through — confirmed directly against ConfigurationBinder).
            // So an unparseable value here does not crash the daemon; it is treated as absent at
            // this level and resolution falls through, same as an unset key would.
            unusable.Add(
                $"{newKeySource} is set to \"{newValue}\", which is not a whole number — it is treated as absent, "
                + "and max-concurrent-task-runs falls back to a lower precedence level, the retired "
                + "max-concurrent-agent-sessions key, or the default instead.");
        }

        // An unparseable legacy value is not reported again here: ResolveInt's own resolution of
        // MaxConcurrentAgentSessions (ResolveAsync, above) already adds the identical unusable-
        // variable message for this exact source, and reporting it a second time under a
        // different wording would read as two mistakes rather than one.
        if (rawLegacyKey is { } legacyValue && int.TryParse(legacyValue, out int sessions))
        {
            int runs = OperatingSettings.ConvertLegacyMaxConcurrentAgentSessions(sessions);
            return (new ResolvedSetting<int>(runs, origin, legacySource), true);
        }

        return null;
    }

    /// <summary>
    /// The run-denominated mirror of <see cref="WarnIfBelowCeilingFloor"/>:
    /// <c>Hall9k.Daemon.Dispatch.NodeLoad.Capacity</c> floors a sub-1 configured ceiling to
    /// exactly one concurrent run rather than dispatching nothing.
    /// </summary>
    private static void WarnIfBelowRunFloor(string source, int value, List<string> unusable)
    {
        if (value < 1)
        {
            unusable.Add(
                $"{source} sets max-concurrent-task-runs to {value}, which is below 1 — the daemon floors this "
                + "to exactly one concurrent run rather than dispatching nothing.");
        }
    }

    /// <summary>
    /// The same lookup <c>EnvironmentVariablesConfigurationProvider</c> performs when the daemon
    /// binds this section: it loads every process environment variable into an
    /// <c>OrdinalIgnoreCase</c> dictionary (after normalizing <c>__</c> to <c>:</c>, a step that
    /// makes no difference to case), so <c>IConfiguration</c> finds <c>Hall9k:MaxConcurrentAgentSessions</c>
    /// regardless of the exported name's casing. <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// is case-sensitive on Linux and macOS, so calling it directly with <paramref name="name"/>'s
    /// exact casing would miss a variable the daemon is in fact running on. Origin: the cycle-6
    /// pre-PR review found <c>export HALL9K__MAXCONCURRENTAGENTSESSIONS=9</c> — the all-caps form
    /// a shell user reaches for — bound and run by the daemon while this resolver reported the
    /// setting as its built-in default, because the exact-case lookup returned null.
    /// </summary>
    private static string? GetEnvironmentVariable(string name)
    {
        string? found = null;
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                // Last one wins, the same as EnvironmentVariablesConfigurationProvider's own
                // Data[key] = value assignment inside its enumeration loop: two raw variable
                // names that only differ by case both land in the daemon's OrdinalIgnoreCase
                // dictionary under one key, so whichever the enumeration visits last is the
                // value the daemon actually binds.
                found = entry.Value as string;
            }
        }

        return found;
    }

    /// <summary>
    /// Shared by <c>max-concurrent-agent-sessions</c> and the four review-cycle caps, whose
    /// unparseable-environment-variable consequence differs by call site and must be named
    /// correctly rather than assumed: <c>max-concurrent-agent-sessions</c> is excluded from
    /// Program.cs's own <c>Bind()</c> call entirely (Decisions Log #111's follow-up) and nothing
    /// else reads the bound <see cref="DaemonOptions.MaxConcurrentAgentSessions"/> property at
    /// dispatch time — the retired-key conversion itself reads the raw environment variable and
    /// config file directly, never this method's result — so an unparseable value there merely
    /// falls back rather than crashing. The four review-cycle caps carry no such exclusion
    /// (<c>DaemonOptionsBinding.ResolverOwnedKeys</c> names only the concurrency settings and the
    /// periodic spend budget, never the review caps), so <see cref="DaemonOptions"/> binds their
    /// equivalent key through <c>ConfigurationBinder</c>,
    /// which throws at options-resolution time on the identical unparseable value instead of
    /// keeping the config-file/default value — reporting that as a graceful fallback would tell an
    /// operator the daemon is fine when it will not start (independent pre-PR review, cycle 8,
    /// adversarial lens). <paramref name="unparseableValueFallsBackRatherThanCrashing"/> is what
    /// call sites use to say which shape applies to them. Unlike <see cref="ResolveString"/>, a
    /// set-but-unparseable value cannot just ride through as itself, or a caller would report an
    /// origin and a value nothing actually runs with — either way, the variable's raw value is
    /// recorded in <paramref name="unusable"/> instead, so a caller can name the mistake rather
    /// than the resolver quietly outranking it.
    /// <paramref name="warnIfOutOfRange"/> has no default: every call site names its own floor
    /// check, or passes <c>null</c> explicitly for a setting that is genuinely unbounded — a
    /// silently-omitted argument would report an out-of-range value as a healthy in-force setting
    /// with no line naming the gap (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    private static ResolvedSetting<int> ResolveInt(
        string environmentVariable, int? configured, int fallback, List<string> unusable,
        Action<string, int, List<string>>? warnIfOutOfRange, string optionName,
        bool unparseableValueFallsBackRatherThanCrashing)
    {
        // Unlike ResolveString, an empty value is not treated as unset here: a shell that expands
        // an unset variable into "" (Hall9k__MaxConcurrentAgentSessions= with nothing after it —
        // the origin incident's own failure shape) still sets the variable, and this value would
        // otherwise misreport as though nothing were set at all.
        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (int.TryParse(fromEnvironment, out int parsed))
            {
                warnIfOutOfRange?.Invoke(environmentVariable, parsed, unusable);
                return new ResolvedSetting<int>(parsed, SettingOrigin.EnvironmentVariable, environmentVariable);
            }

            string consequence = unparseableValueFallsBackRatherThanCrashing
                ? $"it is treated as absent, and {optionName} falls back to the config file or default instead "
                  + "(this retired setting no longer crashes the daemon on a bad value)."
                : $"the daemon binds {optionName} through ConfigurationBinder like any other setting and fails to "
                  + "start converting it, rather than falling back to the config file or default.";
            unusable.Add($"{environmentVariable} is set to \"{fromEnvironment}\", which is not a whole number — {consequence}");
        }

        if (configured is { } value)
        {
            warnIfOutOfRange?.Invoke(Hall9kDatabase.ConfigFile, value, unusable);
            return new ResolvedSetting<int>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);
        }

        return new ResolvedSetting<int>(fallback, SettingOrigin.Default, null);
    }

    /// <summary>
    /// A ceiling below 1 is not refused the way <c>h9k config set</c> refuses it on the write
    /// path (a hand-edited file or an environment variable skips that gate entirely) — instead
    /// <see cref="Hall9k.Daemon.Dispatch.NodeLoad.MaxConcurrentRuns"/> floors it to exactly one
    /// concurrent run, which contradicts the CLI's own "a ceiling of zero would dispatch nothing"
    /// refusal message. Reporting the raw value as a healthy in-force setting with no line naming
    /// that gap would leave an operator believing dispatch has stopped rather than slowed to one.
    /// </summary>
    private static void WarnIfBelowCeilingFloor(string source, int value, List<string> unusable)
    {
        if (value < 1)
        {
            unusable.Add(
                $"{source} sets max-concurrent-agent-sessions to {value}, which is below 1 — the daemon floors "
                + "this to exactly one concurrent run rather than dispatching nothing.");
        }
    }

    /// <summary>
    /// A review-cycle cap below 1 is not refused the way <c>h9k config set</c> refuses it on the
    /// write path (a hand-edited file or an environment variable skips that gate entirely, the same
    /// bypass <see cref="WarnIfBelowCeilingFloor"/> already guards for concurrency) — unlike the
    /// concurrency ceiling, though, nothing floors a cap back up to something usable:
    /// <c>ReviewTrackPolicy</c>/<c>ReviewEngine.FinalFullPassCapReached</c> park the run the moment
    /// the cycle count reaches it, so zero or a negative value parks the very first cycle it is
    /// consulted on, and a lifetime budget of zero or less parks at the first settle point
    /// regardless of how cleanly the run converged. Reporting the raw value as a healthy in-force
    /// setting with no line naming that gap would leave an operator wondering why every run on this
    /// node parks immediately.
    /// </summary>
    private static void WarnIfBelowReviewCapFloor(string source, int value, string optionName, List<string> unusable)
    {
        if (value < 1)
        {
            string consequence = optionName == "lifetime-review-cycle-budget"
                ? "review cycles still run normally, but the task parks for a human at its very first settle "
                  + "point regardless of how cleanly it converged."
                : "every review cycle it is consulted on parks for a human immediately rather than running.";
            unusable.Add($"{source} sets {optionName} to {value}, which is below 1 — {consequence}");
        }
    }

    /// <summary>
    /// The per-run session cap's own resolution (Decisions Log #111) — deliberately not
    /// <see cref="ResolveInt"/>, whose unparseable-value and below-floor messages are both
    /// hardcoded to describe <c>max-concurrent-agent-sessions</c> specifically:
    /// <c>DaemonOptions.SessionCapPerRun</c>, the same as <c>DaemonOptions.MaxConcurrentTaskRuns</c>,
    /// is excluded from the section Program.cs's own <c>Bind()</c> call sees (its
    /// <see langword="internal"/> setter alone would not be enough — see
    /// <c>Hall9k.Daemon.DaemonOptionsBinding</c>'s own doc), so it is never bound through
    /// <c>ConfigurationBinder</c> and an unparseable value here is treated as absent rather than
    /// crashing the daemon — the same as every other concurrency setting this section carries.
    /// </summary>
    private static ResolvedSetting<int> ResolveSessionCapPerRun(int? configured, List<string> unusable)
    {
        string environmentVariable = $"{EnvironmentPrefix}SessionCapPerRun";
        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (int.TryParse(fromEnvironment, out int parsed))
            {
                WarnIfBelowSessionCapFloor(environmentVariable, parsed, unusable);
                return new ResolvedSetting<int>(parsed, SettingOrigin.EnvironmentVariable, environmentVariable);
            }

            unusable.Add(
                $"{environmentVariable} is set to \"{fromEnvironment}\", which is not a whole number — it is "
                + "treated as absent, and session-cap-per-run falls back to the config file or default instead.");
        }

        if (configured is { } value)
        {
            WarnIfBelowSessionCapFloor(Hall9kDatabase.ConfigFile, value, unusable);
            return new ResolvedSetting<int>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile);
        }

        return new ResolvedSetting<int>(OperatingSettings.DefaultSessionCapPerRun, SettingOrigin.Default, null);
    }

    /// <summary>
    /// A cap below 1 is not refused the way <c>h9k config set</c> refuses it on the write path (a
    /// hand-edited file or an environment variable skips that gate entirely) — instead
    /// <c>Hall9k.Daemon.Review.ReviewEngine</c>'s own effective-cap read floors it to exactly
    /// one session per run rather than dispatching nothing.
    /// </summary>
    private static void WarnIfBelowSessionCapFloor(string source, int value, List<string> unusable)
    {
        if (value < 1)
        {
            unusable.Add(
                $"{source} sets session-cap-per-run to {value}, which is below 1 — a run's next session dispatch "
                + "floors this to exactly one session rather than dispatching nothing.");
        }
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
        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (fromEnvironment.Length == 0)
            {
                unusable.Add(
                    $"{environmentVariable} is set to an empty value — AgentModel treats that as unset, so the "
                    + "daemon falls through to the platform default rather than to the config file's value.");
                return new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
            }

            return IsUsableModel(fromEnvironment, environmentVariable, "every agent session on this node", unusable)
                ? new ResolvedSetting<string>(fromEnvironment, SettingOrigin.EnvironmentVariable, environmentVariable)
                : new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
        }

        if (configured is { Length: > 0 } value)
        {
            return IsUsableModel(value, Hall9kDatabase.ConfigFile, "every agent session on this node", unusable)
                ? new ResolvedSetting<string>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
                : new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
        }

        return new ResolvedSetting<string>(fallback, SettingOrigin.Default, null);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a value <c>AgentModel</c> actually resolves to and
    /// runs on — the same <see cref="AgentModel.IsWellFormed"/> gate <c>ConfigSetCommand.ApplyModel</c>
    /// applies on the write path, applied here too because a hand-edited config file or a raw
    /// environment variable never goes through that gate. Unlike an earlier shape of this method,
    /// <see cref="AgentModel.Unknown"/> is not usable either: it is what <c>AgentModel.FromInput</c>
    /// maps the literal word <c>"default"</c> and a blank value to, and <c>AgentModel.Resolve</c>
    /// never returns it — it is a signal to fall through to the next tier, not a spawnable model.
    /// Reporting either shape as the healthy, in-force value the daemon runs on would be wrong in
    /// two different ways: <see cref="AgentModel.IsWellFormed"/> false (garbage, spaces, an
    /// overlong string) means <c>ClaudeExecutor.SpawnAsync</c> throws only for spawns that read
    /// <paramref name="spawnScope"/> (every session on this node for the platform default, only
    /// the owning role's spawns for a per-role override); <see cref="AgentModel.Unknown"/> means
    /// the daemon quietly runs on the fallback while the reported origin points at this
    /// environment variable or config file instead. The named fallback tier also depends on
    /// <paramref name="source"/>: an unusable environment variable still has the config file and
    /// the default underneath it, but an unusable config-file value has only the default left, so
    /// naming the config file there would point the message at itself.
    /// </summary>
    private static bool IsUsableModel(string candidate, string source, string spawnScope, List<string> unusable)
    {
        AgentModel resolved = AgentModel.FromInput(candidate);
        if (resolved.IsWellFormed)
        {
            return true;
        }

        string fallbackTiers = source == Hall9kDatabase.ConfigFile
            ? "the default"
            : "the config file or default";

        string message = resolved == AgentModel.Unknown
            ? $"{source} is set to \"{candidate}\", which AgentModel treats as unset (\"default\", or a blank "
                + "value, clears an override rather than naming one) — the daemon falls through to the next "
                + "tier rather than running on this value, even though it reads back as though it were in force."
            : $"{source} is set to \"{candidate}\", which is not a usable model name — the daemon will fail to "
                + $"spawn {spawnScope} rather than fall back to {fallbackTiers}.";
        unusable.Add(message);
        return false;
    }

    private static ResolvedSetting<string?> ResolveOptionalString(
        string environmentVariable, string? configured, string role, List<string> unusable)
    {
        // ReviewVerify is not an AgentRole (DaemonOptions.ResolveVerifyReviewModel reads it
        // directly, never through DaemonOptions.ResolveModel), so the "using the '{role}' role"
        // phrasing every real role gets below would name a role that does not exist and overstate
        // the blast radius — a bad ReviewVerify value only breaks a Verify-shape review pass, not
        // every session that resolves the Review role.
        string spawnScope = role == nameof(RoleModelSettings.ReviewVerify)
            ? "a Verify-shape review pass"
            : $"agent sessions using the '{role}' role";

        if (GetEnvironmentVariable(environmentVariable) is { } fromEnvironment)
        {
            if (fromEnvironment.Length > 0 && !IsUsableModel(fromEnvironment, environmentVariable, spawnScope, unusable))
            {
                return new ResolvedSetting<string?>(null, SettingOrigin.Default, null);
            }

            return new ResolvedSetting<string?>(fromEnvironment, SettingOrigin.EnvironmentVariable, environmentVariable);
        }

        if (configured is { Length: > 0 } value)
        {
            return IsUsableModel(value, Hall9kDatabase.ConfigFile, spawnScope, unusable)
                ? new ResolvedSetting<string?>(value, SettingOrigin.PlatformConfigFile, Hall9kDatabase.ConfigFile)
                : new ResolvedSetting<string?>(null, SettingOrigin.Default, null);
        }

        return new ResolvedSetting<string?>(null, SettingOrigin.Default, null);
    }
}
