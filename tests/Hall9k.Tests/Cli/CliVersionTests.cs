using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class CliVersionTests
{
    [Fact]
    public void Current_is_a_semantic_version_without_build_metadata()
    {
        CliVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
    }
}
