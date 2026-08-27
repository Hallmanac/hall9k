using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Where a published task's work becomes visible outside Hall9k. Parse is the strict form a
/// human's own <c>--backlog</c> input goes through; the implicit string conversion is the raw,
/// unvalidated wrap every closed-vocabulary value object in this repo uses (CommitStyle,
/// ReviewRerequestPolicy) so that <see cref="Handlers.ProjectDecider"/> stays the one place the
/// closed set is actually enforced.
/// </summary>
public sealed class BacklogPolicyTests
{
    [Theory]
    [InlineData("github-issues")]
    [InlineData("GitHub-Issues")]
    [InlineData("github")]
    public void Parse_reads_github_issues_case_insensitively(string value) =>
        BacklogPolicy.Parse(value).Should().Be(BacklogPolicy.GitHubIssues);

    [Theory]
    [InlineData("jira")]
    [InlineData("JIRA")]
    public void Parse_reads_jira_case_insensitively(string value) =>
        BacklogPolicy.Parse(value).Should().Be(BacklogPolicy.Jira);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("none")]
    [InlineData("None")]
    public void Parse_reads_blank_or_none_as_none(string? value) =>
        BacklogPolicy.Parse(value).Should().Be(BacklogPolicy.None);

    [Fact]
    public void Parse_refuses_anything_outside_the_vocabulary()
    {
        Action act = () => BacklogPolicy.Parse("trello");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*none, github-issues, or jira*");
    }

    [Fact]
    public void The_raw_conversion_wraps_without_validating_so_the_decider_can_be_the_one_gate()
    {
        // The CommitStyle/ReviewRerequestPolicy convention: an implicit conversion from a bare
        // string is deliberately permissive, because ProjectDecider.ChangeSettings is where the
        // closed set is actually enforced, not the value object's constructor.
        BacklogPolicy raw = "not-a-real-policy";

        raw.Value.Should().Be("not-a-real-policy");
        raw.Should().NotBe(BacklogPolicy.None);
    }

    [Fact]
    public void None_round_trips_through_the_string_conversion()
    {
        ((string)BacklogPolicy.None).Should().Be("None");
        ((BacklogPolicy)"None").Should().Be(BacklogPolicy.None);
    }
}
