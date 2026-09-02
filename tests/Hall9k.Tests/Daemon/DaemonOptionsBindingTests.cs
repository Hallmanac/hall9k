using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="DaemonOptionsBinding.ExcludingKeys"/> is what actually stops
/// <c>ConfigurationBinder</c> from converting a resolver-owned key — an <see langword="internal"/>
/// setter alone does not, because <c>BindProperty</c> converts a section's raw value before it
/// ever checks whether the property has a public setter to assign it through. Origin: the
/// independent pre-PR review of Decisions Log #111 (cycle 1, both lenses) found the internal-setter
/// claim on <c>DaemonOptions.MaxConcurrentTaskRuns</c> and <c>DaemonOptions.SessionCapPerRun</c>
/// false — an unparseable value for either still crashed <c>Bind()</c>.
/// </summary>
public sealed class DaemonOptionsBindingTests
{
    [Fact]
    public void An_unparseable_value_for_a_resolver_owned_key_crashes_the_ordinary_bind()
    {
        // The regression itself, demonstrated directly: this is exactly what Program.cs's own
        // Bind() call would have done before the exclusion existed.
        IConfigurationSection section = Section(("MaxConcurrentTaskRuns", "four"));

        DaemonOptions options = new();
        Action bind = () => section.Bind(options);

        bind.Should().Throw<InvalidOperationException>(
            "an internal setter does not stop ConfigurationBinder from attempting the conversion");
    }

    [Theory]
    [InlineData(nameof(DaemonOptions.MaxConcurrentTaskRuns))]
    [InlineData(nameof(DaemonOptions.SessionCapPerRun))]
    [InlineData(nameof(DaemonOptions.MaxConcurrentAgentSessions))]
    public void Excluding_a_resolver_owned_key_stops_an_unparseable_value_from_crashing_bind(string key)
    {
        IConfigurationSection section = Section((key, "four"));

        IConfiguration filtered = DaemonOptionsBinding.ExcludingKeys(section, DaemonOptionsBinding.ResolverOwnedKeys);

        DaemonOptions options = new();
        Action bind = () => filtered.Bind(options);

        bind.Should().NotThrow("the excluded key must never reach ConfigurationBinder's own conversion");
    }

    [Fact]
    public void Excluding_resolver_owned_keys_leaves_every_sibling_setting_bound()
    {
        IConfigurationSection section = Section(
            ("MaxConcurrentTaskRuns", "four"),
            ("SessionCapPerRun", "four"),
            ("MaxConcurrentAgentSessions", "four"),
            ("MaxAdversarialReviewCycles", "7"));

        IConfiguration filtered = DaemonOptionsBinding.ExcludingKeys(section, DaemonOptionsBinding.ResolverOwnedKeys);

        DaemonOptions options = new();
        filtered.Bind(options);

        options.MaxAdversarialReviewCycles.Should().Be(
            7, "excluding the resolver-owned keys must not stop the rest of the section from binding");
    }

    private static IConfigurationSection Section(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([.. pairs.Select(pair =>
                new KeyValuePair<string, string?>($"Hall9k:{pair.Key}", pair.Value))])
            .Build()
            .GetSection("Hall9k");

    /// <summary>
    /// The regression itself: before Decisions Log #111 excluded these keys from the daemon's own
    /// <c>Bind()</c> call, a value here — appsettings.json, appsettings.Development.json, a
    /// command-line argument, user secrets — bound normally and the daemon ran on it.
    /// <c>PostConfigure</c> now overwrites it with the resolver's own answer unconditionally, with
    /// nothing logged, unless this check names the mismatch (independent pre-PR review, cycle 1,
    /// adversarial lens).
    /// </summary>
    [Fact]
    public void A_value_from_outside_the_resolvers_own_sources_is_named_as_ignored()
    {
        IConfigurationSection section = Section(("MaxConcurrentTaskRuns", "5"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().ContainSingle(message =>
            message.Contains("MaxConcurrentTaskRuns") && message.Contains("5") && message.Contains("1"),
            "the daemon dispatches at the resolver's answer (1), not the merged configuration's own value (5), "
            + "and nothing said so before this check existed");
    }

    [Fact]
    public void A_value_that_agrees_with_the_resolvers_answer_is_silent()
    {
        IConfigurationSection section = Section(("MaxConcurrentTaskRuns", "1"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty(
            "the value an operator would see either way is the one the daemon runs on, so nothing is actually "
            + "being overridden out from under them");
    }

    [Fact]
    public void A_key_absent_from_the_merged_configuration_is_silent()
    {
        IConfigurationSection section = Section(("MaxAdversarialReviewCycles", "7"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty("there is nothing set outside the resolver's own sources to name as ignored");
    }

    /// <summary>
    /// The shadow case: an environment-level legacy conversion outranks a
    /// <c>maxConcurrentTaskRuns</c> value the config file sets directly, so the merged
    /// configuration's raw value for that key reads back as the file's own value even though the
    /// daemon runs on the converted one — a source the resolver does read, just outranked at this
    /// level, not "another configuration source" this check should name (independent pre-PR
    /// review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public void A_config_file_value_shadowed_by_an_environment_level_legacy_conversion_is_silent()
    {
        IConfigurationSection section = Section(("MaxConcurrentTaskRuns", "4"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 3, shadowsConfigFileValue: true);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty(
            "the config file's own value is already explained by the daemon's own shadow-case log line; "
            + "naming it again here as an unread source would contradict that explanation");
    }

    /// <summary>
    /// Named after the daemon's own <c>ConfigurationBinder</c> having no conversion for a nullable
    /// <c>long?</c>, unlike every other resolver-owned setting here: null means "the resolver
    /// answered no budget", which is itself a value <see cref="AddIfIgnored"/>-equivalent checks
    /// must compare against, not an "unset, skip this assertion" sentinel the way it is for the
    /// review-cycle caps below.
    /// </summary>
    [Fact]
    public void A_spend_budget_value_from_outside_the_resolvers_own_sources_is_named_as_ignored()
    {
        IConfigurationSection section = Section(("SpendBudgetTokens", "5000000"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1, spendBudgetTokens: 1000);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().ContainSingle(message =>
            message.Contains("SpendBudgetTokens") && message.Contains("5000000") && message.Contains("1000"),
            "the daemon dispatches at the resolver's answer (1000), not the merged configuration's own value "
            + "(5000000), and nothing said so before this check existed");
    }

    [Fact]
    public void A_spend_budget_value_that_agrees_with_the_resolvers_answer_is_silent()
    {
        IConfigurationSection section = Section(("SpendBudgetTokens", "1000"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1, spendBudgetTokens: 1000);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty();
    }

    [Fact]
    public void A_spend_period_value_from_outside_the_resolvers_own_sources_is_named_as_ignored()
    {
        IConfigurationSection section = Section(("SpendPeriod", "day"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1, spendPeriod: "week");

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().ContainSingle(message =>
            message.Contains("SpendPeriod") && message.Contains("day") && message.Contains("week"));
    }

    [Fact]
    public void A_spend_period_value_that_agrees_with_the_resolvers_answer_is_silent_case_insensitively()
    {
        IConfigurationSection section = Section(("SpendPeriod", "WEEK"));
        OperatingSettingsReport report = ReportWithCeiling(maxConcurrentTaskRuns: 1, spendPeriod: "week");

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty();
    }

    /// <summary>
    /// The self-contradiction the independent pre-PR review caught (cycle 2, adversarial lens): a
    /// parseable-but-unrecognized <c>Hall9k__SpendPeriod</c> value is rejected by
    /// <c>OperatingSettingsResolver.ResolveSpendPeriod</c> and explained in
    /// <see cref="OperatingSettingsReport.UnusableEnvironmentVariables"/>, but the same raw value
    /// still reads back through the merged section's own indexer — the environment-variable
    /// provider never stops carrying it just because the resolver rejected it. Without a guard
    /// against the value already being explained there, this check would additionally claim the
    /// value arrived through "another configuration source the resolver does not read", which is
    /// false: it is the exact same environment variable, already named.
    /// </summary>
    [Fact]
    public void A_spend_period_value_already_explained_as_unusable_is_not_named_again_as_ignored()
    {
        IConfigurationSection section = Section(("SpendPeriod", "weekly"));
        OperatingSettingsReport report = ReportWithCeiling(
            maxConcurrentTaskRuns: 1,
            spendPeriod: "week",
            unusableEnvironmentVariables:
            [
                "Hall9k__SpendPeriod is set to \"weekly\", which is neither \"day\" nor \"week\" — it is treated "
                + "as absent, and spend-period falls back to the config file or default instead.",
            ]);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty(
            "the resolver's own unusable-environment-variable message already explains this value; naming it "
            + "again as coming from an unread source would contradict that explanation on the same value");
    }

    /// <summary>
    /// The same shape as
    /// <see cref="A_spend_period_value_already_explained_as_unusable_is_not_named_again_as_ignored"/>,
    /// but for a hand-edited platform config file rather than an environment variable: a negative
    /// <c>spendBudgetTokens</c> is rejected by <c>ResolveSpendBudgetTokens</c> and explained in
    /// <see cref="OperatingSettingsReport.UnusableEnvironmentVariables"/> without ever naming the
    /// environment variable at all, since the rejected value came from the config file, another of
    /// this check's own two sources.
    /// </summary>
    [Fact]
    public void A_spend_budget_config_file_value_already_explained_as_unusable_is_not_named_again_as_ignored()
    {
        IConfigurationSection section = Section(("SpendBudgetTokens", "-5"));
        OperatingSettingsReport report = ReportWithCeiling(
            maxConcurrentTaskRuns: 1,
            spendBudgetTokens: null,
            unusableEnvironmentVariables:
            [
                "~/.hall9k/config.json sets spend-budget-tokens to -5, which is negative — it is treated as "
                + "absent, and no budget applies.",
            ]);

        IReadOnlyList<string> messages = DaemonOptionsBinding.DescribeConfigurationSourcesTheResolverIgnores(section, report);

        messages.Should().BeEmpty(
            "the resolver's own unusable-environment-variable message already explains this value; naming it "
            + "again as coming from an unread source would contradict that explanation on the same value");
    }

    private static OperatingSettingsReport ReportWithCeiling(
        int maxConcurrentTaskRuns, bool shadowsConfigFileValue = false, long? spendBudgetTokens = null,
        string spendPeriod = OperatingSettings.DefaultSpendPeriod,
        IReadOnlyList<string>? unusableEnvironmentVariables = null) =>
        new(
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentAgentSessions, SettingOrigin.Default, null),
            false,
            new ResolvedSetting<int>(maxConcurrentTaskRuns, SettingOrigin.Default, null),
            false,
            shadowsConfigFileValue,
            new ResolvedSetting<int>(OperatingSettings.DefaultSessionCapPerRun, SettingOrigin.Default, null),
            new ResolvedSetting<string>(AgentModel.PlatformFallback, SettingOrigin.Default, null),
            [],
            null,
            unusableEnvironmentVariables ?? [],
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxComplianceReviewCycles, SettingOrigin.Default, null),
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxAdversarialReviewCycles, SettingOrigin.Default, null),
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxFinalFullPassRounds, SettingOrigin.Default, null),
            new ResolvedSetting<int>(OperatingSettings.DefaultLifetimeReviewCycleBudget, SettingOrigin.Default, null),
            new ResolvedSetting<long?>(spendBudgetTokens, SettingOrigin.Default, null),
            new ResolvedSetting<string>(spendPeriod, SettingOrigin.Default, null));
}
