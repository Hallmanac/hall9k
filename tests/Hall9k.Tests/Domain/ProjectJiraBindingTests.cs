using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The project's binding to a Jira board (backlog 18): a setting like any other, which is the
/// point — it goes through the same Optional discipline, so binding a board does not silently
/// retype the verify gates, and clearing it is a thing that can be said.
/// </summary>
public sealed class ProjectJiraBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 11, 0, 0, TimeSpan.Zero);
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

    private static ProjectSettingsChanged Change(ProjectAggregate project, Optional<JiraProjectKey> key) =>
        ProjectDecider.ChangeSettings(
            project,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<int>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            Owner,
            jiraProjectKey: key);

    [Fact]
    public void A_project_starts_with_no_board_bound()
    {
        (ProjectAggregate aggregate, ProjectDetails view, _) = Registered();

        aggregate.JiraProjectKey.HasValue.Should().BeFalse();
        view.JiraProjectKey.Should().Be(JiraProjectKey.None);
    }

    [Fact]
    public void Binding_a_board_reaches_the_aggregate_and_the_pane()
    {
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();

        ProjectSettingsChanged changed = Change(aggregate, Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("proj")));
        aggregate.Apply(changed);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(changed), view);

        aggregate.JiraProjectKey.Value.Should().Be("PROJ");
        view.JiraProjectKey.Value.Should().Be("PROJ");
    }

    [Fact]
    public void A_settings_change_that_says_nothing_about_the_board_leaves_it_alone()
    {
        // Absent means left alone, which is the whole reason these events carry Optional: changing
        // the model must not also claim somebody retyped the board key.
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();
        ProjectSettingsChanged bound = Change(aggregate, Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ")));
        aggregate.Apply(bound);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(bound), view);

        ProjectSettingsChanged unrelated = ProjectDecider.ChangeSettings(
            aggregate,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.Of(true),
            Optional<int>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            Owner);
        aggregate.Apply(unrelated);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(unrelated), view);

        aggregate.JiraProjectKey.Value.Should().Be("PROJ");
        view.JiraProjectKey.Value.Should().Be("PROJ");
    }

    [Fact]
    public void Clearing_the_binding_is_a_thing_that_can_be_said()
    {
        (ProjectAggregate aggregate, ProjectDetails view, ProjectDetailsProjection projection) = Registered();
        aggregate.Apply(Change(aggregate, Optional<JiraProjectKey>.Of(JiraProjectKey.Parse("PROJ"))));

        ProjectSettingsChanged cleared = Change(aggregate, Optional<JiraProjectKey>.Of(JiraProjectKey.None));
        aggregate.Apply(cleared);
        projection.Apply(new FakeEvent<ProjectSettingsChanged>(cleared), view);

        aggregate.JiraProjectKey.HasValue.Should().BeFalse();
        view.JiraProjectKey.HasValue.Should().BeFalse();
    }
}
