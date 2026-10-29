using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>Spectre refuses a lone dash as an option value, so <c>--invite -</c> is rewritten to the form it accepts.</summary>
public sealed class StdinOptionValueTests
{
    [Fact]
    public void An_option_followed_by_a_lone_dash_becomes_the_equals_form()
    {
        StdinOptionValue.Normalise(["solution", "join", "--invite", "-"]).Should().Equal("solution", "join", "--invite=-");
        StdinOptionValue.Normalise(["project", "join", "hall9k", "--invite", "-", "--owner", "abc"])
            .Should().Equal("project", "join", "hall9k", "--invite=-", "--owner", "abc");
    }

    [Fact]
    public void Everything_else_is_left_exactly_as_it_was()
    {
        string[] untouched = ["project", "join", "hall9k", "--invite=-", "--owner", "abc", "-h", "--", "-"];

        StdinOptionValue.Normalise(untouched).Should().Equal(untouched);
        StdinOptionValue.Normalise(["-", "--invite", "x"]).Should().Equal("-", "--invite", "x");
        StdinOptionValue.Normalise([]).Should().BeEmpty();
    }
}
