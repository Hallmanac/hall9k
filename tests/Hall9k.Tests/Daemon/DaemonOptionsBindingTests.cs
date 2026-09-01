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
/// independent pre-PR review of Decisions Log #109 (cycle 1, both lenses) found the internal-setter
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
    /// The regression itself: before Decisions Log #109 excluded these keys from the daemon's own
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

    private static OperatingSettingsReport ReportWithCeiling(int maxConcurrentTaskRuns) =>
        new(
            new ResolvedSetting<int>(OperatingSettings.DefaultMaxConcurrentAgentSessions, SettingOrigin.Default, null),
            new ResolvedSetting<int>(maxConcurrentTaskRuns, SettingOrigin.Default, null),
            false,
            false,
            new ResolvedSetting<int>(OperatingSettings.DefaultSessionCapPerRun, SettingOrigin.Default, null),
            new ResolvedSetting<string>(AgentModel.PlatformFallback, SettingOrigin.Default, null),
            [],
            null,
            []);
}
