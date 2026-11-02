using FluentAssertions;
using Hall9k.Daemon;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>The backoff ceiling is settable by environment variable or config file, not by <c>h9k config set</c>.</summary>
public sealed class CourierFailureBackoffCeilingOptionTests
{
    private static DaemonOptions Bind(Dictionary<string, string?> values)
    {
        DaemonOptions options = new();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(DaemonOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void The_ceiling_defaults_to_thirty_minutes()
    {
        new DaemonOptions().CourierFailureBackoffCeiling.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void The_ceiling_binds_from_the_hall9k_section_under_its_own_name()
    {
        // Hall9k__CourierFailureBackoffCeiling in the environment lands here as Hall9k:CourierFailureBackoffCeiling.
        DaemonOptions options = Bind(new() { ["Hall9k:CourierFailureBackoffCeiling"] = "00:45:00" });

        options.CourierFailureBackoffCeiling.Should().Be(TimeSpan.FromMinutes(45));
    }
}
