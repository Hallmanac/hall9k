using FluentAssertions;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The team binding a project carries for Linear. It is checked where a human types it, so a typo
/// is refused with the rule rather than becoming a lookup that cannot find anything.
/// </summary>
public sealed class LinearTeamKeyTests
{
    [Theory]
    [InlineData("ENG", "ENG")]
    [InlineData("eng", "ENG")]
    [InlineData("  plat2  ", "PLAT2")]
    public void A_key_is_recorded_the_way_Linear_writes_it(string input, string expected) =>
        LinearTeamKey.Parse(input).Value.Should().Be(expected);

    [Fact]
    public void Blank_is_the_absence_of_a_binding_rather_than_an_error()
    {
        LinearTeamKey.Parse(null).HasValue.Should().BeFalse();
        LinearTeamKey.Parse("  ").Should().Be(LinearTeamKey.None);
    }

    [Theory]
    [InlineData("1ENG")]
    [InlineData("my team")]
    [InlineData("ENG-123")]
    [InlineData("ENG_X")]
    [InlineData("THISKEYISFARTOOLONGTOBEONE")]
    public void Anything_that_is_not_a_team_key_is_refused_with_the_rule(string input)
    {
        Action parse = () => LinearTeamKey.Parse(input);

        parse.Should().Throw<DomainValidationException>().WithMessage("*letters or digits*");
    }

    [Fact]
    public void A_refused_key_cannot_repaint_the_terminal_that_quotes_it()
    {
        Action parse = () => LinearTeamKey.Parse("EN\u001b[2JG‮");

        parse.Should().Throw<DomainValidationException>()
            .Which.Message.Should().NotContain("\u001b").And.NotContain("‮");
    }

    [Fact]
    public void An_issue_identifier_is_quoted_back_recognisably_because_it_is_the_commonest_mistake() =>
        ((Action)(() => LinearTeamKey.Parse("ENG-123"))).Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("'ENG-123'");
}
