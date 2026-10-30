using System.Text.Json;
using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// A project's Linear team, bound the way its Jira board is, and the rule that follows from binding
/// it: Linear is then the project's one tracker. The binding is a team setting, so it has to reach
/// the aggregate, both projection paths, and the companion event that replicates it; a field missing
/// from any one of them works on the node that set it and nowhere else.
/// </summary>
public sealed class ProjectLinearBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    private static (ProjectAggregate Aggregate, ProjectDetails View, ProjectDetailsProjection Projection) Registered()
    {
        ProjectRegistered registered = ProjectDecider.Register(
            DomainId.New(), Owner, DomainId.New(), "hall9k", "/code/hall9k", null, "main", Now);
        ProjectAggregate aggregate = new();
        aggregate.Apply(registered);

        ProjectDetailsProjection projection = new();
        return (aggregate, projection.Create(new FakeEvent<ProjectRegistered>(registered)), projection);
    }

    private static ProjectSettingsChanged Change(
        ProjectAggregate project,
        Optional<LinearTeamKey> team = default,
        Optional<JiraProjectKey> jira = default,
        Optional<BacklogPolicy> backlog = default,
        Optional<WorkItemProvider> primary = default,
        DateTimeOffset? at = null) =>
        ProjectDecider.ChangeSettings(
            project,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            at ?? Now,
            Owner,
            jiraProjectKey: jira,
            backlogPolicy: backlog,
            primaryTracker: primary,
            linearTeam: team);

    private static Optional<LinearTeamKey> Team(string key) => Optional<LinearTeamKey>.Of(LinearTeamKey.Parse(key));

    private static ProjectAggregate Bound(string key = "ENG")
    {
        (ProjectAggregate aggregate, _, _) = Registered();
        aggregate.Apply(Change(aggregate, Team(key)));
        return aggregate;
    }

    [Fact]
    public void A_project_starts_with_no_team_bound()
    {
        (ProjectAggregate aggregate, ProjectDetails view, _) = Registered();

        aggregate.LinearTeam.HasValue.Should().BeFalse();
        view.LinearTeam.Should().Be(LinearTeamKey.None);
    }

    [Fact]
    public void Binding_a_team_reaches_the_aggregate_and_the_pane_through_the_node_event()
    {
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();

        ProjectSettingsChanged changed = Change(aggregate, Team("eng"));
        aggregate.Apply(changed);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(changed), view);

        aggregate.LinearTeam.Value.Should().Be("ENG");
        view.LinearTeam.Value.Should().Be("ENG");
    }

    [Fact]
    public void The_binding_travels_on_the_team_companion_event_so_every_node_of_the_project_reads_it()
    {
        // The companion event is all a teammate's node receives, so applying it alone to a fresh
        // aggregate and pane must reproduce the binding.
        (ProjectAggregate origin, _, _) = Registered();
        ProjectSettingsChanged changed = Change(origin, Team("ENG"));

        ProjectTeamSettingsChanged? team = ProjectTeamSettingsChanged.From(changed);

        team.Should().NotBeNull("a Linear team is a team field, so a change carrying only it still replicates");
        team!.LinearTeam.Value.Should().Be(LinearTeamKey.Parse("ENG"));

        (ProjectAggregate receiver, ProjectDetails view, ProjectDetailsProjection projection) = Registered();
        receiver.Apply(team);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(team), view);

        receiver.LinearTeam.Value.Should().Be("ENG");
        view.LinearTeam.Value.Should().Be("ENG");
    }

    [Fact]
    public void A_team_event_older_than_the_one_applied_does_not_overwrite_it()
    {
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();
        ProjectTeamSettingsChanged newer = ProjectTeamSettingsChanged.From(Change(aggregate, Team("NEW"), at: Now.AddHours(1)))!;
        ProjectTeamSettingsChanged older = ProjectTeamSettingsChanged.From(Change(aggregate, Team("OLD"), at: Now))!;

        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(newer), view);
        projection.Apply(new FakeEvent<ProjectTeamSettingsChanged>(older), view);

        view.LinearTeam.Value.Should().Be("NEW");
    }

    [Fact]
    public void A_settings_change_that_says_nothing_about_the_team_leaves_it_alone_and_appends_no_team_event()
    {
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();
        ProjectSettingsChanged bound = Change(aggregate, Team("ENG"));
        aggregate.Apply(bound);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(bound), view);

        ProjectSettingsChanged unrelated = ProjectDecider.ChangeSettings(
            aggregate,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.Of(true),
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            Owner);
        aggregate.Apply(unrelated);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(unrelated), view);

        aggregate.LinearTeam.Value.Should().Be("ENG");
        view.LinearTeam.Value.Should().Be("ENG");
        ProjectTeamSettingsChanged.From(unrelated).Should().BeNull();
    }

    [Fact]
    public void Clearing_the_binding_is_a_thing_that_can_be_said()
    {
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();
        aggregate.Apply(Change(aggregate, Team("ENG")));

        ProjectSettingsChanged cleared = Change(aggregate, Optional<LinearTeamKey>.Of(LinearTeamKey.None));
        aggregate.Apply(cleared);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(cleared), view);

        aggregate.LinearTeam.HasValue.Should().BeFalse();
        view.LinearTeam.HasValue.Should().BeFalse();
    }

    [Fact]
    public void Both_events_round_trip_the_team_through_the_stored_json_and_an_old_event_reads_as_absent()
    {
        // A node on an older version drops the field it does not know; a stream written before this
        // setting existed has none, and both must read as "nothing said" rather than as a clearing.
        JsonSerializerOptions stored = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        ProjectTeamSettingsChanged team = new(DomainId.New(), Now, Owner, LinearTeam: Team("ENG"));
        ProjectSettingsChanged node = Change(new ProjectAggregate(), Team("ENG"));

        JsonSerializer.Deserialize<ProjectTeamSettingsChanged>(JsonSerializer.Serialize(team, stored), stored)!
            .LinearTeam.Value!.Value.Should().Be("ENG");
        JsonSerializer.Deserialize<ProjectSettingsChanged>(JsonSerializer.Serialize(node, stored), stored)!
            .LinearTeam.Value!.Value.Should().Be("ENG");

        ProjectTeamSettingsChanged before = JsonSerializer.Deserialize<ProjectTeamSettingsChanged>(
            """{"id":"0191d3b0-0000-7000-8000-000000000001","changedAt":"2026-09-16T12:00:00+00:00","changedByOwnerId":"0191d3b0-0000-7000-8000-000000000002"}""",
            stored)!;
        before.LinearTeam.HasValue.Should().BeFalse();
    }

    [Fact]
    public void Linear_is_the_projects_tracker_exactly_when_a_team_is_bound()
    {
        ProjectTracker.IsLinear(LinearTeamKey.None).Should().BeFalse();
        ProjectTracker.IsLinear(LinearTeamKey.Parse("ENG")).Should().BeTrue();
    }

    [Fact]
    public void Binding_is_refused_while_the_project_has_a_Jira_board()
    {
        (ProjectAggregate aggregate, _, _) = Registered();
        aggregate.Apply(Change(aggregate, jira: Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ"))));

        Action bind = () => Change(aggregate, Team("ENG"));

        bind.Should().Throw<DomainValidationException>()
            .WithMessage("*Jira board (PROJ)*").And.Message.Should().Contain("--jira none");
    }

    [Theory]
    [InlineData("GitHubIssues", "--backlog none")]
    [InlineData("Jira", "--backlog none")]
    public void Binding_is_refused_while_the_backlog_policy_is_github_issues_or_jira(string policy, string remedy)
    {
        (ProjectAggregate aggregate, _, _) = Registered();
        aggregate.Apply(Change(aggregate, backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.FromInput(policy))));

        Action bind = () => Change(aggregate, Team("ENG"));

        bind.Should().Throw<DomainValidationException>()
            .WithMessage($"*backlog policy of {policy}*").And.Message.Should().Contain(remedy);
    }

    [Fact]
    public void Binding_is_refused_while_a_primary_tracker_is_set()
    {
        (ProjectAggregate aggregate, _, _) = Registered();
        aggregate.Apply(Change(aggregate, primary: Optional<WorkItemProvider>.Of(WorkItemProvider.GitHub)));

        Action bind = () => Change(aggregate, Team("ENG"));

        bind.Should().Throw<DomainValidationException>()
            .WithMessage("*primary tracker*").And.Message.Should().Contain("--primary-tracker none");
    }

    [Fact]
    public void A_backlog_policy_of_none_does_not_stand_in_the_way()
    {
        (ProjectAggregate aggregate, _, _) = Registered();
        aggregate.Apply(Change(aggregate, backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.GitHubIssues)));
        aggregate.Apply(Change(aggregate, backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.None)));

        Action bind = () => Change(aggregate, Team("ENG"));

        bind.Should().NotThrow();
    }

    [Fact]
    public void One_command_can_switch_from_Jira_to_Linear_because_the_settings_it_leaves_are_judged()
    {
        (ProjectAggregate aggregate, _, _) = Registered();
        aggregate.Apply(Change(aggregate, jira: Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ"))));
        aggregate.Apply(Change(aggregate, backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.Jira)));

        Action switchTrackers = () => Change(
            aggregate,
            Team("ENG"),
            Optional<JiraProjectKey>.Of(JiraProjectKey.None),
            Optional<BacklogPolicy>.Of(BacklogPolicy.None));

        switchTrackers.Should().NotThrow();
    }

    [Fact]
    public void One_command_can_switch_from_Linear_to_Jira()
    {
        ProjectAggregate aggregate = Bound();

        Action switchTrackers = () => Change(
            aggregate,
            Optional<LinearTeamKey>.Of(LinearTeamKey.None),
            Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ")),
            Optional<BacklogPolicy>.Of(BacklogPolicy.Jira));

        switchTrackers.Should().NotThrow();
    }

    [Fact]
    public void Binding_and_setting_a_Jira_board_in_one_command_is_refused_because_the_board_is_left_in_place()
    {
        (ProjectAggregate aggregate, _, _) = Registered();

        Action both = () => Change(aggregate, Team("ENG"), Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ")));

        both.Should().Throw<DomainValidationException>().WithMessage("*Jira board (PROJ)*");
    }

    [Fact]
    public void A_Jira_board_is_refused_on_a_project_bound_to_Linear_naming_Linear_as_the_tracker()
    {
        ProjectAggregate aggregate = Bound("ENG");

        Action jira = () => Change(aggregate, jira: Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ")));

        DomainValidationException refusal = jira.Should().Throw<DomainValidationException>().Which;
        refusal.Message.Should().Contain("--jira is refused").And.Contain("tracks its work in Linear (team ENG)")
            .And.Contain("--linear none");
    }

    [Theory]
    [InlineData("GitHubIssues")]
    [InlineData("Jira")]
    public void A_tracking_backlog_policy_is_refused_on_a_project_bound_to_Linear(string policy)
    {
        ProjectAggregate aggregate = Bound();

        Action backlog = () => Change(aggregate, backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.FromInput(policy)));

        backlog.Should().Throw<DomainValidationException>()
            .WithMessage("*--backlog is refused*tracks its work in Linear*");
    }

    [Fact]
    public void A_primary_tracker_is_refused_on_a_project_bound_to_Linear()
    {
        ProjectAggregate aggregate = Bound();

        Action primary = () => Change(aggregate, primary: Optional<WorkItemProvider>.Of(WorkItemProvider.Jira));

        primary.Should().Throw<DomainValidationException>()
            .WithMessage("*--primary-tracker is refused*tracks its work in Linear*");
    }

    [Fact]
    public void Clearing_a_Jira_board_or_a_policy_on_a_Linear_project_is_not_a_conflict()
    {
        ProjectAggregate aggregate = Bound();

        Action clear = () => Change(
            aggregate,
            jira: Optional<JiraProjectKey>.Of(JiraProjectKey.None),
            backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.None),
            primary: Optional<WorkItemProvider>.Of(WorkItemProvider.Unknown));

        clear.Should().NotThrow();
    }

    [Fact]
    public void Changing_the_team_on_a_bound_project_is_allowed()
    {
        ProjectAggregate aggregate = Bound("ENG");

        Action change = () => Change(aggregate, Team("PLAT"));

        change.Should().NotThrow();
    }

    [Fact]
    public void A_project_with_no_Linear_team_behaves_exactly_as_it_did_before()
    {
        (ProjectAggregate aggregate, _, _) = Registered();

        Action everything = () => Change(
            aggregate,
            jira: Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ")),
            backlog: Optional<BacklogPolicy>.Of(BacklogPolicy.Jira),
            primary: Optional<WorkItemProvider>.Of(WorkItemProvider.GitHub));

        everything.Should().NotThrow();
    }

    [Fact]
    public void An_unrelated_change_is_not_refused_over_a_combination_a_teammates_node_left_behind()
    {
        // Replication can deliver a Jira board beside a Linear team. Only what a command touches is
        // judged, so changing the model must not be refused for it.
        ProjectAggregate aggregate = Bound();
        aggregate.Apply(ProjectTeamSettingsChanged.From(
            Change(new ProjectAggregate(), jira: Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ"))))!);

        Action unrelated = () => ProjectDecider.ChangeSettings(
            aggregate,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.Of(true),
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            Owner);

        unrelated.Should().NotThrow();
    }
}
