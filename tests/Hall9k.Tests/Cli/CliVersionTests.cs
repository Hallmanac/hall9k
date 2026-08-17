using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class CliVersionTests
{
    [Fact]
    public void Current_is_a_semantic_version_without_build_metadata()
    {
        // Prerelease must be dot-separated, non-empty identifiers (rejects "1.2.3-.").
        CliVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$");
    }

    [Fact]
    public void Current_is_not_the_missing_attribute_fallback()
    {
        // Guards the deliberate runtime fallback: a build that loses the informational
        // version attribute fails here instead of shipping "0.0.0" silently.
        CliVersion.Current.Should().NotBe("0.0.0");
    }
}
