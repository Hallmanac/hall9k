using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>The forms a person has to hand for a Linear issue, and the one URL that must be refused.</summary>
public sealed class LinearIssueIdentifierTests
{
    private static readonly Uri Workspace = new("https://linear.app/hall9k");

    [Theory]
    [InlineData("ENG-123", "ENG-123")]
    [InlineData("eng-123", "ENG-123")]
    [InlineData("linear:ENG-123", "ENG-123")]
    [InlineData("  ENG-7  ", "ENG-7")]
    [InlineData("https://linear.app/hall9k/issue/ENG-123", "ENG-123")]
    [InlineData("https://linear.app/hall9k/issue/ENG-123/add-rate-limiting", "ENG-123")]
    [InlineData("https://linear.app/Hall9k/issue/eng-123/add-rate-limiting", "ENG-123")]
    public void Every_form_a_person_has_to_hand_reads_as_one_identifier(string typed, string expected)
    {
        LinearIssueIdentifier.Parse(typed, Workspace).Value.Should().Be(expected);
    }

    [Fact]
    public void An_issue_url_from_another_workspace_is_refused_because_a_stored_reference_records_no_workspace()
    {
        Action parse = () => LinearIssueIdentifier.Parse("https://linear.app/other-co/issue/ENG-123", Workspace);

        parse.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("other-co").And.Contain("records the identifier with no workspace");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ENG")]
    [InlineData("123-4")]
    [InlineData("ENG-0")]
    [InlineData("ENG-x")]
    [InlineData("https://example.com/hall9k/issue/ENG-123")]
    [InlineData("https://linear.app/hall9k/team/ENG")]
    public void Something_that_is_not_an_issue_is_refused_with_the_forms_that_work(string typed)
    {
        Action parse = () => LinearIssueIdentifier.Parse(typed, Workspace);

        parse.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void The_canonical_reference_is_the_linear_provider_and_the_identifier()
    {
        LinearIssueIdentifier.Parse("eng-5", Workspace).Reference.ToString().Should().Be("linear:ENG-5");
    }
}
