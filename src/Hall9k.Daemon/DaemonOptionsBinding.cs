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
    /// a command-line argument, user secrets) that sets <see cref="DaemonOptions.MaxConcurrentTaskRuns"/>
    /// or <see cref="DaemonOptions.SessionCapPerRun"/> to a value the daemon does not actually run
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
        return messages;
    }

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
}
