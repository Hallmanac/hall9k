using FluentAssertions;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="ProjectMemberLabelsDecider.Observe"/> (task b7d8222e): the change-only rule that
/// keeps a project with a settled team from appending an identical labels event tick after tick,
/// the same shape <c>ProjectDecider.ObserveGitHubCollaborators</c> already applies to its own
/// roster.
/// </summary>
public sealed class ProjectMemberLabelsDeciderTests
{
    private static readonly Guid ProjectId = Guid.Parse("01a0bc05-a960-7657-b708-1aed4a1b2c3d");
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset At = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);

    private static ProjectMemberLabel Label(DisplayName? displayName = null, string? login = "octocat") =>
        new("root-fingerprint", [NodeId], displayName ?? DisplayName.None, login);

    [Fact]
    public void The_first_observation_always_appends()
    {
        ProjectMemberLabelsObserved? observed = ProjectMemberLabelsDecider.Observe(null, ProjectId, [Label()], At);

        observed.Should().NotBeNull();
        observed!.ProjectId.Should().Be(ProjectId);
        observed.Labels.Should().BeEquivalentTo([Label()]);
    }

    [Fact]
    public void An_identical_repeat_appends_nothing()
    {
        ProjectMemberLabels existing = new() { Id = ProjectId, Labels = [Label()] };

        ProjectMemberLabelsDecider.Observe(existing, ProjectId, [Label()], At).Should().BeNull(
            "a project with a settled team must not append an identical labels event every sweep");
    }

    [Fact]
    public void A_changed_display_name_appends_again()
    {
        ProjectMemberLabels existing = new() { Id = ProjectId, Labels = [Label()] };

        ProjectMemberLabelsObserved? observed = ProjectMemberLabelsDecider.Observe(
            existing, ProjectId, [Label(DisplayName.Parse("Brian"))], At);

        observed.Should().NotBeNull();
    }

    [Fact]
    public void A_fleet_reordered_with_the_identical_node_ids_appends_nothing()
    {
        ProjectMemberLabel before = new("root-fingerprint", [NodeId, Guid.Parse("22222222-2222-2222-2222-222222222222")], DisplayName.None, "octocat");
        ProjectMemberLabel reordered = before with { FleetNodeIds = [.. before.FleetNodeIds.Reverse()] };
        ProjectMemberLabels existing = new() { Id = ProjectId, Labels = [before] };

        ProjectMemberLabelsDecider.Observe(existing, ProjectId, [reordered], At).Should().BeNull(
            "the identical fleet in a different order must never read as a change");
    }

    [Fact]
    public void A_member_who_dropped_out_of_the_project_appends_again()
    {
        ProjectMemberLabels existing = new() { Id = ProjectId, Labels = [Label()] };

        ProjectMemberLabelsDecider.Observe(existing, ProjectId, [], At).Should().NotBeNull();
    }
}
