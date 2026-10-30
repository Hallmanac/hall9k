using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The one-tracker rule at the doors that link an item to a task. A project with a Linear team bound
/// takes no Jira card and no GitHub issue and needs the team for a Linear issue, a pull request is
/// adopted either way, and a project with no team behaves exactly as it always did.
/// </summary>
public sealed class LinearTrackerDoorsTests
{
    private static ProjectDetails Project(string? team) => new()
    {
        Id = DomainId.New(),
        Name = "hall9k",
        LinearTeam = team is null ? LinearTeamKey.None : LinearTeamKey.Parse(team),
    };

    [Theory]
    [InlineData("h9k task link-jira")]
    [InlineData("h9k task link-issue")]
    [InlineData("h9k task push-to-jira")]
    [InlineData("h9k task write-jira --op create")]
    [InlineData("h9k task add --from-jira")]
    [InlineData("h9k task add --from-issue")]
    public void A_door_that_links_another_trackers_item_is_refused_naming_Linear_as_the_tracker(string door)
    {
        Action refuse = () => ProjectTracker.RefuseOtherTracker(LinearTeamKey.Parse("ENG"), "hall9k", door);

        refuse.Should().Throw<DomainValidationException>()
            .Which.Message.Should().StartWith($"{door} is refused")
            .And.Contain("tracks its work in Linear (team ENG)")
            .And.Contain("h9k project set hall9k --linear none")
            .And.Contain("--from-pr is unaffected");
    }

    [Fact]
    public void Without_a_team_the_other_trackers_doors_are_open()
    {
        Action open = () => ProjectTracker.RefuseOtherTracker(LinearTeamKey.None, "hall9k", "h9k task link-jira");

        open.Should().NotThrow();
    }

    [Fact]
    public void A_Linear_door_without_a_team_is_refused_naming_the_binding_command()
    {
        Action refuse = () => ProjectTracker.RequireLinear(LinearTeamKey.None, "hall9k", "h9k task link-linear");

        refuse.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("needs Linear to be the project's tracker")
            .And.Contain("h9k project set hall9k --linear <TEAM>");
    }

    [Fact]
    public void A_Linear_door_with_a_team_is_open()
    {
        Action open = () => ProjectTracker.RequireLinear(LinearTeamKey.Parse("ENG"), "hall9k", "h9k task link-linear");

        open.Should().NotThrow();
    }

    [Theory]
    [InlineData("jira")]
    [InlineData("issue")]
    public void Adoption_from_Jira_or_a_GitHub_issue_is_refused_on_a_Linear_project(string source)
    {
        TaskAddCommand.Settings settings = source == "jira"
            ? new TaskAddCommand.Settings { FromJira = "PROJ-1" }
            : new TaskAddCommand.Settings { FromIssue = "42" };

        Action adopt = () => TaskAddCommand.RefuseAdoptionAgainstTracker(settings, Project("ENG"));

        adopt.Should().Throw<DomainValidationException>().WithMessage("*tracks its work in Linear*");
    }

    [Fact]
    public void Adopting_both_a_card_and_an_issue_is_refused_too()
    {
        TaskAddCommand.Settings settings = new() { FromJira = "PROJ-1", FromIssue = "42" };

        Action adopt = () => TaskAddCommand.RefuseAdoptionAgainstTracker(settings, Project("ENG"));

        adopt.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void Adopting_a_pull_request_is_unaffected_on_a_Linear_project()
    {
        TaskAddCommand.Settings settings = new() { FromPr = "123" };

        Action adopt = () => TaskAddCommand.RefuseAdoptionAgainstTracker(settings, Project("ENG"));

        adopt.Should().NotThrow();
    }

    [Fact]
    public void Adopting_from_Linear_needs_a_bound_team_and_names_the_command_that_binds_one()
    {
        TaskAddCommand.Settings settings = new() { FromLinear = "ENG-123" };

        Action unbound = () => TaskAddCommand.RefuseAdoptionAgainstTracker(settings, Project(null));
        Action bound = () => TaskAddCommand.RefuseAdoptionAgainstTracker(settings, Project("ENG"));

        unbound.Should().Throw<DomainValidationException>().WithMessage("*h9k project set hall9k --linear <TEAM>*");
        bound.Should().NotThrow();
    }

    [Fact]
    public void A_project_with_no_team_adopts_from_Jira_and_GitHub_exactly_as_before()
    {
        TaskAddCommand.Settings settings = new() { FromJira = "PROJ-1", FromIssue = "42" };

        Action adopt = () => TaskAddCommand.RefuseAdoptionAgainstTracker(settings, Project(null));

        adopt.Should().NotThrow();
    }

    [Fact]
    public void The_connection_list_counts_the_projects_with_a_Linear_team_bound_and_leaves_the_other_providers_counts_alone()
    {
        ProjectDetails[] projects = [Project("ENG"), Project("PLAT"), Project(null)];
        ConnectionDetails linear = new() { Id = DomainId.New(), Provider = WorkItemProvider.Linear };
        ConnectionDetails jira = new() { Id = DomainId.New(), Provider = WorkItemProvider.Jira };

        ConnectionListCommand.BoundProjects(linear, projects).Should().Be(2);
        ConnectionListCommand.BoundProjects(jira, projects).Should().Be(0, "a Linear team is not a Jira board");
    }

    [Fact]
    public void Each_link_is_shown_by_its_own_provider_whatever_the_projects_tracker_is()
    {
        // ExternalMarkup takes the reference alone: a linear: link on a project that never bound a team
        // and a github: link on one bound to Linear render the same way they always did.
        WorkItemImporter importer = new(
            new GitHubWorkItemProvider(RecordingProcessRunner.NeverInvoked()),
            new LinearWorkItemProvider(
                LinearAccount.Registered(
                    new Uri("https://linear.app/hall9k"), "Brian Hall", CredentialReference.EnvironmentVariable("LINEAR_KEY")),
                FakeLinearRequester.NeverInvoked()));

        TaskShowCommand.ExternalMarkup(importer, "linear:ENG-123")
            .Should().Contain("[link=https://linear.app/hall9k/issue/ENG-123]");
        TaskShowCommand.ExternalMarkup(importer, "github:o/r#42")
            .Should().Contain("[link=https://github.com/o/r/issues/42]");
    }

    [Fact]
    public void A_Linear_reference_renders_as_its_identifier_in_a_branch_with_its_case_kept()
    {
        ExternalReference linked = ExternalReference.Parse("linear:ENG-123");
        Guid taskId = Guid.Parse("00000000-0000-0000-0000-00000abcdef1");

        string branch = BranchNameTemplate.LinearConvention.Render(taskId, "Add rate limiting", linked.Key);
        string unlinked = BranchNameTemplate.LinearConvention.Render(taskId, "Add rate limiting", null);

        branch.Should().Be("ENG-123-add-rate-limiting");
        unlinked.Should().Be("no-key-add-rate-limiting", "an unlinked task still renders no-key");
    }

    [Fact]
    public void The_Linear_convention_is_a_template_the_decider_accepts_and_is_not_the_default()
    {
        BranchNameTemplate.Parse(BranchNameTemplate.LinearConvention.Value).Should().Be(BranchNameTemplate.LinearConvention);
        BranchNameTemplate.LinearConvention.Should().NotBe(BranchNameTemplate.Default);
    }
}
