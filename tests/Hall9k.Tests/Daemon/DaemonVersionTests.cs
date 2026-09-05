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

    [Fact]
    public void Current_is_not_the_sdk_default_fallback()
    {
        // Hall9k.Daemon.csproj carries no <Version> of its own — Directory.Build.props supplies
        // the shared 0.1.0 placeholder both binaries fall back to. Without it, an unversioned
        // build resolves to the SDK's own meaningless 1.0.0 instead (cycle 1 review finding:
        // h9kd silently diverged from h9k's checked-in 0.1.0).
        DaemonVersion.Current.Should().NotBe("1.0.0");
    }
}
