using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// What a deterministic issue author (backlog policy github-issues) renders from a task, since
/// there is no agent in the loop here to write anything more considered.
/// </summary>
public sealed class GitHubIssueBodyTests
{
    [Fact]
    public void Compose_carries_the_agent_context_and_the_criteria_as_a_checklist()
    {
        string body = GitHubIssueBody.Compose("Read the migration doc first.", ["Requests over the limit get 429"]);

        body.Should().Contain("Read the migration doc first.")
            .And.Contain("## Acceptance criteria")
            .And.Contain("- [ ] Requests over the limit get 429");
    }

    [Fact]
    public void Compose_folds_a_newline_inside_a_criterion_so_it_cannot_break_out_of_its_checklist_item()
    {
        string body = GitHubIssueBody.Compose(null, ["Requests over the limit get 429\n\n## Notes\n- something else"]);

        body.Should().Contain("- [ ] Requests over the limit get 429").And.NotContain("\n## Notes");
    }

    [Fact]
    public void Compose_with_neither_half_present_is_blank()
    {
        GitHubIssueBody.Compose(null, []).Should().BeEmpty();
    }

    [Fact]
    public void Compose_with_only_criteria_carries_no_empty_context_section()
    {
        string body = GitHubIssueBody.Compose(null, ["One criterion"]);

        body.Should().NotContain("## Context").And.Contain("- [ ] One criterion");
    }

    [Fact]
    public void Compose_with_a_truncated_objective_puts_the_full_text_in_a_leading_section()
    {
        string body = GitHubIssueBody.Compose(
            "Read the migration doc first.", ["One criterion"], "The full objective, unabridged.");

        body.Should().Contain("## Objective")
            .And.Contain("The full objective, unabridged.")
            .And.MatchRegex("(?s)^## Objective.*## Acceptance criteria");
    }

    [Fact]
    public void Compose_with_no_truncated_objective_carries_no_objective_section()
    {
        string body = GitHubIssueBody.Compose("Read the migration doc first.", ["One criterion"], truncatedObjective: null);

        body.Should().NotContain("## Objective");
    }

    [Fact]
    public void Labels_reads_a_comma_separated_list_and_trims_each_entry()
    {
        GitHubIssueBody.Labels("bug, needs-triage ,  platform").Should().Equal("bug", "needs-triage", "platform");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Labels_with_nothing_configured_is_empty(string? routingGuidance)
    {
        GitHubIssueBody.Labels(routingGuidance).Should().BeEmpty();
    }

    [Fact]
    public void Labels_ignores_prose_that_is_not_comma_separated_beyond_treating_it_as_one_label()
    {
        // v1's honest limit: a deterministic author cannot follow "file under the platform epic"
        // as an instruction, so it becomes a (probably nonexistent, and therefore refused by gh)
        // label rather than being silently dropped or misread as routing.
        GitHubIssueBody.Labels("file under the platform epic").Should().Equal("file under the platform epic");
    }
}
