using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The forms a human or an agent actually has to hand for a Jira card, and the one form that is
/// refused on purpose: a card from a site this install did not register.
/// </summary>
public sealed class JiraIssueKeyTests
{
    private static readonly Uri Site = new("https://hall9k.atlassian.net");

    [Theory]
    [InlineData("PROJ-123")]
    [InlineData("  PROJ-123  ")]
    [InlineData("proj-123")]
    [InlineData("jira:PROJ-123")]
    [InlineData("https://hall9k.atlassian.net/browse/PROJ-123")]
    [InlineData("https://hall9k.atlassian.net/jira/software/projects/PROJ/boards/1?selectedIssue=PROJ-123")]
    public void Every_form_a_human_has_to_hand_names_the_same_card(string reference) =>
        JiraIssueKey.Parse(reference, Site).Value.Should().Be("PROJ-123");

    [Fact]
    public void The_canonical_reference_round_trips_through_the_stored_form()
    {
        JiraIssueKey key = JiraIssueKey.Parse("PROJ-123", Site);

        key.Reference.ToString().Should().Be("jira:PROJ-123");
        JiraIssueKey.Parse(key.Reference.ToString(), Site).Should().Be(key);
    }

    [Fact]
    public void A_card_on_another_tenant_is_refused_rather_than_having_its_key_taken()
    {
        // A stored reference records the key and no site at all, so adopting someone else's
        // PROJ-123 would file it as this tenant's PROJ-123 — a guessed identity, and one that
        // would later render as a link to the wrong card.
        Action parse = () => JiraIssueKey.Parse("https://other-org.atlassian.net/browse/PROJ-123", Site);

        parse.Should().Throw<DomainValidationException>()
            .WithMessage("*other-org.atlassian.net*")
            .WithMessage("*hall9k.atlassian.net*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("PROJ")]
    [InlineData("PROJ-")]
    [InlineData("PROJ-abc")]
    [InlineData("PROJ-0")]
    [InlineData("1PROJ-2")]
    [InlineData("https://hall9k.atlassian.net/secure/Dashboard.jspa")]
    public void Anything_that_is_not_a_card_key_is_refused_with_the_forms_that_are(string reference)
    {
        Action parse = () => JiraIssueKey.Parse(reference, Site);

        parse.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void A_key_whose_project_carries_a_digit_or_an_underscore_is_still_a_key()
    {
        // Jira allows both, and a rule tighter than Jira's would refuse keys that already exist
        // in somebody's instance — which is a fact about their instance, not an error.
        JiraIssueKey.Parse("DEV2_INT-9", Site).Value.Should().Be("DEV2_INT-9");
    }
}
