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
    public void Compose_drops_the_discovery_workspace_pointer_a_promoted_idea_appends_and_keeps_the_rest_of_the_context()
    {
        // The shape IdeaPromoteCommand.AgentContext writes: the note's context, then the heading, then a path
        // on the promoting machine, which means nothing to an issue's reader and names a home directory.
        string context = "Why this matters.\n\n" + GitHubIssueBody.DiscoveryWorkspaceHeading
            + "\n/Users/someone/.hall9k/projects/x/ideas/y/workspace";

        string body = GitHubIssueBody.Compose(context, ["One criterion"]);

        body.Should().Be("Why this matters.\n\n## Acceptance criteria\n\n- [ ] One criterion")
            .And.NotContain("Discovery workspace").And.NotContain("/Users/");
    }

    [Fact]
    public void Compose_with_a_context_that_is_only_the_workspace_pointer_carries_no_context_section()
    {
        string context = GitHubIssueBody.DiscoveryWorkspaceHeading + "\n/Users/someone/.hall9k/ideas/y/workspace";

        GitHubIssueBody.Compose(context, ["One criterion"]).Should().Be("## Acceptance criteria\n\n- [ ] One criterion");
    }

    [Fact]
    public void Compose_keeps_a_mention_of_the_heading_that_does_not_start_a_line()
    {
        string context = "See the note: " + GitHubIssueBody.DiscoveryWorkspaceHeading + " is where research went.";

        GitHubIssueBody.Compose(context, []).Should().Be(context);
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
