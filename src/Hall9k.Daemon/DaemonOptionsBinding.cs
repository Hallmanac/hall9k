using System.Globalization;
using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Hall9k.Daemon;

/// <summary>
/// What actually keeps <c>ConfigurationBinder</c> away from a <see cref="DaemonOptions"/> property
/// that resolves through <see cref="Hall9k.Domain.Infrastructure.Persistence.OperatingSettingsResolver"/>
/// instead: an <see langword="internal"/> setter alone does not do it, because
/// <c>ConfigurationBinder.BindProperty</c> converts a section's raw value before it ever checks
/// whether the property has a public setter to assign the result through — confirmed directly
/// against <c>ConfigurationBinder</c>: it throws converting <c>"four"</c> to <see cref="int"/>
/// regardless of the target property's setter visibility. Removing the key from the section a
/// generic <c>Bind()</c> call sees is the only way to actually stop the attempt (independent
/// pre-PR review, cycle 1, both lenses — the internal-setter claim on
/// <see cref="DaemonOptions.MaxConcurrentTaskRuns"/> and <see cref="DaemonOptions.SessionCapPerRun"/>
/// was wrong, and the still-public-setter <see cref="DaemonOptions.MaxConcurrentAgentSessions"/>
/// paid the same crash for a setting nothing reads any more).
/// </summary>
internal static class DaemonOptionsBinding
{
    /// <summary>
    /// Every setting <see cref="Hall9k.Domain.Infrastructure.Persistence.OperatingSettingsResolver"/>
    /// resolves on its own precedence walk, so a generic <c>Bind()</c> must never see them: four —
    /// <see cref="DaemonOptions.MaxConcurrentTaskRuns"/>, <see cref="DaemonOptions.SessionCapPerRun"/>,
    /// <see cref="DaemonOptions.SpendBudgetTokens"/>, <see cref="DaemonOptions.SpendPeriod"/> — are
    /// then set by <c>PostConfigure</c> from that resolver's report, and the fifth
    /// (<see cref="DaemonOptions.MaxConcurrentAgentSessions"/>) is retired and read by nothing at
    /// all, so it is simply excluded rather than set again.
    /// </summary>
    internal static readonly string[] ResolverOwnedKeys =
    [
        nameof(DaemonOptions.MaxConcurrentTaskRuns),
        nameof(DaemonOptions.SessionCapPerRun),
        nameof(DaemonOptions.MaxConcurrentAgentSessions),
        nameof(DaemonOptions.SpendBudgetTokens),
        nameof(DaemonOptions.SpendPeriod),
    ];

    /// <summary>
    /// A copy of <paramref name="section"/> with <paramref name="excludedKeys"/> removed, so a
    /// generic <c>Bind()</c> against the result never asks <c>ConfigurationBinder</c> to convert
    /// them.
    /// </summary>
    internal static IConfiguration ExcludingKeys(IConfigurationSection section, params string[] excludedKeys) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([.. section.AsEnumerable(makePathsRelative: true)
                .Where(pair => !excludedKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))])
            .Build();

    /// <summary>
    /// Names every configuration source outside <c>OperatingSettingsResolver</c>'s own env-then-
    /// config-file-then-default walk (<c>appsettings.json</c>, <c>appsettings.{Environment}.json</c>,
    /// a command-line argument, user secrets) that sets <see cref="DaemonOptions.MaxConcurrentTaskRuns"/>,
    /// <see cref="DaemonOptions.SessionCapPerRun"/>, <see cref="DaemonOptions.SpendBudgetTokens"/> or
    /// <see cref="DaemonOptions.SpendPeriod"/> to a value the daemon does not actually run
    /// on. Before Decisions Log #111 excluded these keys from Program.cs's own <c>Bind()</c> call,
    /// they bound off the whole merged <see cref="IConfiguration"/> like any other
    /// <see cref="DaemonOptions"/> member, so a value in <c>appsettings.Development.json</c> or on
    /// the command line took effect; today <c>PostConfigure</c> overwrites both with
    /// <paramref name="report"/>'s own answer unconditionally, and until this check existed nothing
    /// said so (independent pre-PR review, cycle 1, adversarial lens). <paramref name="section"/>
    /// must be the <em>un</em>-excluded section — <c>builder.Configuration.GetSection(DaemonOptions.SectionName)</c>
    /// — so its indexer still sees every source, not just <see cref="ExcludingKeys"/>'s filtered copy.
    /// A key absent from <paramref name="section"/>, or whose raw value already equals what the
    /// resolver decided, is silent by design: the value an operator would see either way is the one
    /// the daemon runs on, so nothing is actually being overridden out from under them.
    /// <c>MaxConcurrentTaskRuns</c> is skipped outright when
    /// <see cref="OperatingSettingsReport.MaxConcurrentTaskRunsShadowsConfigFileValue"/> is set: the
    /// platform config file is itself one of <paramref name="section"/>'s own providers
    /// (<c>PlatformConfigFileSource.Insert</c> sits right ahead of the environment-variable source),
    /// so whenever an environment-level legacy conversion outranks a <c>maxConcurrentTaskRuns</c>
    /// value the file sets directly, <paramref name="section"/>'s raw indexer reads that same
    /// file value back — a source the resolver does read, just outranked at this precedence level —
    /// and this method would otherwise misreport it as coming from "another configuration source
    /// the resolver does not read" while contradicting the daemon's own shadow-case log line for
    /// the identical situation (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    internal static IReadOnlyList<string> DescribeConfigurationSourcesTheResolverIgnores(
        IConfigurationSection section, OperatingSettingsReport report)
    {
        List<string> messages = [];
        if (!report.MaxConcurrentTaskRunsShadowsConfigFileValue)
        {
            AddIfIgnored(
                section, nameof(DaemonOptions.MaxConcurrentTaskRuns), "max-concurrent-task-runs",
                "--max-concurrent-task-runs", report.MaxConcurrentTaskRuns.Value, messages);
        }

        AddIfIgnored(
            section, nameof(DaemonOptions.SessionCapPerRun), "session-cap-per-run",
            "--session-cap-per-run", report.SessionCapPerRun.Value, messages);

        AddIfIgnored(
            section, nameof(DaemonOptions.SpendBudgetTokens), "spend-budget-tokens",
            "--spend-budget", report.SpendBudgetTokens.Value, report.UnusableEnvironmentVariables,
            report.ConfigFileProblem, messages);

        AddIfIgnored(
            section, nameof(DaemonOptions.SpendPeriod), "spend-period",
            "--spend-period", report.SpendPeriod.Value, report.UnusableEnvironmentVariables,
            report.ConfigFileProblem, messages);
        return messages;
    }

    /// <summary>
    /// True when this setting's rejection is already explained elsewhere — the resolver read a
    /// value for it (from the environment variable or the platform config file, the only two
    /// sources <see cref="OperatingSettingsResolver.ResolveSpendBudgetTokens"/> and
    /// <see cref="OperatingSettingsResolver.ResolveSpendPeriod"/> ever read) and rejected it as
    /// malformed for this setting, which is a different situation from a value arriving through a
    /// source the resolver never reads at all. The two long?/string <c>AddIfIgnored</c> overloads
    /// below (unlike the pre-existing int one, which cannot disagree with the resolver this way)
    /// can otherwise contradict that message on the exact same value: the raw section indexer still
    /// reads the rejected value — whether it came from the environment variable or from the config
    /// file, which <c>PlatformConfigFileSource</c> also inserts into the same merged section — even
    /// though <c>OperatingSettingsResolver</c> fell back past it, so without this guard the two
    /// lines would both deny and assert that the value is read (independent pre-PR review, cycle 2,
    /// adversarial lens). <paramref name="unusableEnvironmentVariables"/> is matched on
    /// <paramref name="flagLabel"/> — the text both of the resolver's own rejection messages name —
    /// rather than on the environment variable name alone, since a config-file rejection message
    /// never names the environment variable at all.
    /// <para>
    /// A config-file leaf that fails STJ's stricter deserialize (a JSON number where
    /// <see cref="OperatingSettings.SpendPeriod"/> wants a string, say) never reaches that
    /// resolver rejection at all: <c>PlatformConfigFile.TryReadOperatingSettingsAsync</c> drops the
    /// leaf during recovery before <c>OperatingSettingsResolver</c> ever sees it, so
    /// <c>configured</c> reads as absent and no <paramref name="unusableEnvironmentVariables"/>
    /// message is ever written — the file value read back through this method's own raw
    /// <paramref name="section"/> indexer still disagrees with the resolved default, though, and
    /// without also checking <paramref name="configFileProblem"/> here this method would report
    /// that disagreement as coming from a source the resolver does not read at all, when it in fact
    /// came from the platform config file and the resolver's own startup log already names the real
    /// reason (<see cref="ConfigFileProblem.DescribeConsequence"/>, printed by
    /// <c>Hall9k.Daemon.Dispatch.DispatchLoop</c>). Matched on <paramref name="key"/> — the
    /// property name the raw <see cref="System.Text.Json.JsonException.Path"/> a malformed leaf
    /// throws carries — rather than on <paramref name="flagLabel"/>, since the exception path names
    /// the JSON property, never the kebab-case flag (independent pre-PR review, cycle 1, adversarial
    /// lens).
    /// </para>
    /// </summary>
    private static bool AlreadyExplainedAsUnusable(
        string key, string flagLabel, IReadOnlyList<string> unusableEnvironmentVariables,
        ConfigFileProblem? configFileProblem) =>
        unusableEnvironmentVariables.Any(message => message.Contains(flagLabel, StringComparison.Ordinal))
        || (configFileProblem is { AffectsResolverOwnedKey: true } problem
            && problem.Message.Contains(key, StringComparison.OrdinalIgnoreCase));

    private static void AddIfIgnored(
        IConfigurationSection section, string key, string flagLabel, string flag, int effectiveValue,
        List<string> messages)
    {
        string? raw = section[key];
        if (raw is null || !int.TryParse(raw, out int rawValue) || rawValue == effectiveValue)
        {
            return;
        }

        messages.Add(
            $"{section.Path}:{key} resolves to {rawValue} through appsettings.json, a command-line argument, or "
            + "another configuration source the daemon's operating-settings resolver does not read — the daemon "
            + $"dispatches at {effectiveValue.ToString(CultureInfo.InvariantCulture)} instead, since {flagLabel} is "
            + $"resolved only from the {OperatingSettingsResolver.EnvironmentPrefix}{key} environment variable and "
            + $"the platform config file (Decisions Log #111). Set it through one of those instead: "
            + $"h9k config set {flag} <n>, or export {OperatingSettingsResolver.EnvironmentPrefix}{key}=<n>.");
    }

    /// <summary>
    /// <see cref="AddIfIgnored(IConfigurationSection, string, string, string, int, List{string})"/>'s
    /// own check, widened for <see cref="DaemonOptions.SpendBudgetTokens"/> (Decisions Log #120):
    /// nullable, since "no budget" is itself a meaningful resolved value here rather than a
    /// ceiling nothing ever resolves to.
    /// </summary>
    private static void AddIfIgnored(
        IConfigurationSection section, string key, string flagLabel, string flag, long? effectiveValue,
        IReadOnlyList<string> unusableEnvironmentVariables, ConfigFileProblem? configFileProblem,
        List<string> messages)
    {
        string? raw = section[key];
        if (raw is null || !long.TryParse(raw, out long rawValue) || rawValue == effectiveValue)
        {
            return;
        }

        if (AlreadyExplainedAsUnusable(key, flagLabel, unusableEnvironmentVariables, configFileProblem))
        {
            return;
        }

        string effectiveDescription = effectiveValue is { } value
            ? $"a budget of {value.ToString(CultureInfo.InvariantCulture)} tokens"
            : "unbudgeted";
        messages.Add(
            $"{section.Path}:{key} resolves to {rawValue} through appsettings.json, a command-line argument, or "
            + "another configuration source the daemon's operating-settings resolver does not read — the daemon "
            + $"dispatches {effectiveDescription} instead, since {flagLabel} is "
            + $"resolved only from the {OperatingSettingsResolver.EnvironmentPrefix}{key} environment variable and "
            + $"the platform config file (Decisions Log #120). Set it through one of those instead: "
            + $"h9k config set {flag} <n>, or export {OperatingSettingsResolver.EnvironmentPrefix}{key}=<n>.");
    }

    /// <summary>
    /// <see cref="AddIfIgnored(IConfigurationSection, string, string, string, int, List{string})"/>'s
    /// own check, widened for <see cref="DaemonOptions.SpendPeriod"/> (Decisions Log #120): a
    /// string setting rather than a number, compared the same case-insensitive way
    /// <c>SpendPeriod.FromInput</c> itself normalizes it — trimmed the same way too, so a
    /// whitespace-padded value <c>SpendPeriod.FromInput</c> accepts as-is is not reported as
    /// coming from a source the resolver never reads (independent pre-PR review, cycle 3,
    /// conformance lens).
    /// </summary>
    private static void AddIfIgnored(
        IConfigurationSection section, string key, string flagLabel, string flag, string effectiveValue,
        IReadOnlyList<string> unusableEnvironmentVariables, ConfigFileProblem? configFileProblem,
        List<string> messages)
    {
        string? raw = section[key];
        if (raw is null || string.Equals(raw.Trim(), effectiveValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (AlreadyExplainedAsUnusable(key, flagLabel, unusableEnvironmentVariables, configFileProblem))
        {
            return;
        }

        messages.Add(
            $"{section.Path}:{key} resolves to \"{raw}\" through appsettings.json, a command-line argument, or "
            + "another configuration source the daemon's operating-settings resolver does not read — the daemon "
            + $"dispatches on \"{effectiveValue}\" instead, since {flagLabel} is "
            + $"resolved only from the {OperatingSettingsResolver.EnvironmentPrefix}{key} environment variable and "
            + $"the platform config file (Decisions Log #120). Set it through one of those instead: "
            + $"h9k config set {flag} <value>, or export {OperatingSettingsResolver.EnvironmentPrefix}{key}=<value>.");
    }
}
