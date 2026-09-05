using FluentAssertions;
using Hall9k.Daemon;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class DaemonVersionTests
{
    [Fact]
    public void Current_is_a_semantic_version_without_build_metadata()
    {
        // Mirrors CliVersionTests: both read the identical shared
        // AssemblyInformationalVersion.Resolve, just off this assembly instead of h9k's.
        DaemonVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$");
    }

    [Fact]
    public void Current_is_not_the_missing_attribute_fallback()
    {
        DaemonVersion.Current.Should().NotBe("0.0.0");
    }
}
