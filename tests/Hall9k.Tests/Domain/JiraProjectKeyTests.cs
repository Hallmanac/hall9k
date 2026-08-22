using FluentAssertions;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The board binding a project carries. It is checked where a human types it, because the cost
/// of a typo here is not an error message — it is a card-creation prompt sending an agent to
/// look for a board that was never there.
/// </summary>
public sealed class JiraProjectKeyTests
{
    [Theory]
    [InlineData("PROJ", "PROJ")]
    [InlineData("proj", "PROJ")]
    [InlineData("  dev2  ", "DEV2")]
    [InlineData("SUP_INT", "SUP_INT")]
    public void A_key_is_recorded_the_way_Jira_writes_it(string input, string expected) =>
        JiraProjectKey.Parse(input).Value.Should().Be(expected);

    [Fact]
    public void Blank_is_the_absence_of_a_binding_rather_than_an_error()
    {
        // Clearing a binding is a legitimate thing to ask for, so it cannot be an error to say.
        JiraProjectKey.Parse(null).HasValue.Should().BeFalse();
        JiraProjectKey.Parse("   ").Should().Be(JiraProjectKey.None);
    }

    [Theory]
    [InlineData("1PROJ")]
    [InlineData("my project")]
    [InlineData("PROJ!")]
    [InlineData("THISKEYISFARTOOLONGTOBEONE")]
    public void Anything_that_is_not_a_project_key_is_refused_with_the_rule(string input)
    {
        Action parse = () => JiraProjectKey.Parse(input);

        parse.Should().Throw<DomainValidationException>()
            .WithMessage("*letters, digits, or underscores*");
    }

    [Fact]
    public void A_refused_key_cannot_repaint_the_terminal_that_quotes_it()
    {
        // The value came off a command line and the refusal is printed to a terminal. This type
        // sits in the domain and cannot reach the connectors' relay rules, so it keeps only the
        // characters a key could legally have been made of.
        Action parse = () => JiraProjectKey.Parse("PR\u001b[2JOJ");

        parse.Should().Throw<DomainValidationException>()
            .Which.Message.Should().NotContain("\u001b");
    }

    /// <summary>
    /// The same rule against the character a blacklist of control characters lets through. A
    /// bidirectional override is not a control character; it reverses the display of everything
    /// after it while leaving the stored string alone, so a refusal that echoed one would print
    /// the sentence explaining the refusal backwards. Origin incident (2026-08-21): the pre-PR
    /// review of the Jira branch found this quoting comment describing a whitelist over an
    /// implementation that was a blacklist.
    /// </summary>
    [Theory]
    [InlineData("PROJ\u202Eevil")]
    [InlineData("PROJ\u2066evil")]
    [InlineData("PROJ\ufeffevil")]
    public void A_refused_key_cannot_reverse_the_sentence_that_refuses_it(string input)
    {
        Action parse = () => JiraProjectKey.Parse(input);

        string message = parse.Should().Throw<DomainValidationException>().Which.Message;
        message.Should().NotContain("\u202e").And.NotContain("\u2066").And.NotContain("\ufeff");
    }

    [Fact]
    public void A_card_key_is_not_a_project_key_and_says_so()
    {
        // The commonest mistake by far: PROJ-123 pasted into --jira. The message names the
        // distinction rather than only rejecting the string.
        Action parse = () => JiraProjectKey.Parse("PROJ-123");

        parse.Should().Throw<DomainValidationException>()
            .WithMessage("*part before the dash*")
            // And the key survives the quoting intact: a message that echoed it back as PROJ?123
            // would be teaching the distinction while hiding the evidence for it.
            .Which.Message.Should().Contain("PROJ-123");
    }
}
