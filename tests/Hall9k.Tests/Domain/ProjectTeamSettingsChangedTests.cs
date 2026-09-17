using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Idea 202383dc, M2a: the project settings event splits into a team part that travels
/// (<see cref="ProjectTeamSettingsChanged"/>, <see cref="EventScope.ProjectScoped"/>) and a node
/// part that stays (<see cref="ProjectSettingsChanged"/>, <see cref="EventScope.NodeScoped"/>),
/// without changing <see cref="ProjectDecider.ChangeSettings"/>'s own single-event shape or any of
/// its call sites.
/// </summary>
public sealed class ProjectTeamSettingsChangedTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_returns_null_when_the_change_touched_no_team_field()
    {
        ProjectSettingsChanged changed = new(
            DomainId.New(),
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<int>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            Model: Optional<AgentModel>.Of(AgentModel.Sonnet));

        ProjectTeamSettingsChanged.From(changed).Should().BeNull();
    }

    [Fact]
    public void From_derives_the_team_companion_when_a_team_field_changed()
    {
        ProjectSettingsChanged changed = new(
            DomainId.New(),
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<int>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            ClaimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee));

        ProjectTeamSettingsChanged? teamChanged = ProjectTeamSettingsChanged.From(changed);

        teamChanged.Should().NotBeNull();
        teamChanged!.Id.Should().Be(changed.Id);
        teamChanged.ClaimGate.Value.Should().Be(ClaimGate.TrackerAssignee);
    }

    [Fact]
    public void The_aggregate_applies_a_team_event_as_if_it_had_arrived_alone()
    {
        Guid projectId = DomainId.New();
        ProjectTeamSettingsChanged teamChanged = new(
            projectId, Now, DomainId.New(), ClaimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee),
            BranchNameTemplate: Optional<BranchNameTemplate>.Of(BranchNameTemplate.Parse("ARX-{shortid}-{slug}")));

        ProjectAggregate aggregate = new();
        aggregate.Apply(teamChanged);

        aggregate.ClaimGate.Should().Be(ClaimGate.TrackerAssignee);
        aggregate.BranchNameTemplate.Should().Be(BranchNameTemplate.Parse("ARX-{shortid}-{slug}"));
    }

    [Fact]
    public void The_settings_split_is_classified_correctly_for_replication()
    {
        EventScopeRegistry.ClassificationOf(typeof(ProjectSettingsChanged)).Should().Be(EventScope.NodeScoped);
        EventScopeRegistry.ClassificationOf(typeof(ProjectTeamSettingsChanged)).Should().Be(EventScope.ProjectScoped);
    }
}
