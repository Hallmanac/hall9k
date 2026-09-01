using FluentAssertions;
using Hall9k.Daemon;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="DaemonOptionsBinding.ExcludingKeys"/> is what actually stops
/// <c>ConfigurationBinder</c> from converting a resolver-owned key — an <see langword="internal"/>
/// setter alone does not, because <c>BindProperty</c> converts a section's raw value before it
/// ever checks whether the property has a public setter to assign it through. Origin: the
/// independent pre-PR review of Decisions Log #108 (cycle 1, both lenses) found the internal-setter
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
}
