using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// What has to be true before a linked task may be claimed on this install (idea 64c75e43,
/// Decisions Log #142) — the <see cref="AutoPrReviewSpeed"/> shape, so the same three things are
/// pinned: <c>Parse</c> is the strict form <c>h9k project set --claim-gate</c> goes through, the
/// implicit string conversion is the raw unvalidated wrap that leaves
/// <see cref="ProjectDecider.ChangeSettings"/> the one place the closed set is enforced, and the
/// setting is absent-means-unchanged on the way through the aggregate and the projection.
/// </summary>
public sealed class ClaimGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("tracker-assignee")]
    [InlineData("Tracker-Assignee")]
    [InlineData("TRACKER-ASSIGNEE")]
    [InlineData("TrackerAssignee")]
    public void Parse_reads_tracker_assignee_case_insensitively(string value) =>
        ClaimGate.Parse(value).Should().Be(ClaimGate.TrackerAssignee);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("off")]
    [InlineData("Off")]
    public void Parse_reads_blank_or_off_as_off(string? value) =>
        ClaimGate.Parse(value).Should().Be(ClaimGate.Off);

    [Fact]
    public void Parse_refuses_anything_outside_the_vocabulary()
    {
        Action act = () => ClaimGate.Parse("assignee");

        act.Should().Throw<DomainValidationException>().WithMessage("*off or tracker-assignee*");
    }

    [Fact]
    public void Parse_refuses_a_control_character_without_echoing_it_into_the_refusal()
    {
        Action act = () => ClaimGate.Parse("tracker‮-evil");

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should().NotContain("‮")
            .And.Contain("tracker?-evil");
    }

    [Fact]
    public void Parse_refuses_an_unbounded_argument_without_echoing_it_whole()
    {
        Action act = () => ClaimGate.Parse(new string('x', 500));

        act.Should().Throw<DomainValidationException>()
            .Which.Message.Should().Contain("…")
            .And.NotContain(new string('x', 500));
    }

    [Fact]
    public void The_raw_conversion_wraps_without_validating_so_the_decider_can_be_the_one_gate()
    {
        ClaimGate raw = "not-a-real-gate";

        raw.Value.Should().Be("not-a-real-gate");
        raw.Should().NotBe(ClaimGate.Off);
    }

    [Fact]
    public void Off_round_trips_through_the_string_conversion()
    {
        ((string)ClaimGate.Off).Should().Be("Off");
        ((ClaimGate)"Off").Should().Be(ClaimGate.Off);
    }

    [Fact]
    public void The_decider_refuses_a_gate_outside_the_closed_set()
    {
        ProjectAggregate project = Project();

        Action act = () => Change(project, claimGate: Optional<ClaimGate>.Of("tracker-assignee-ish"));

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*claim gate must be Off or TrackerAssignee*");
    }

    [Fact]
    public void The_decider_records_the_gate_and_the_aggregate_applies_it()
    {
        ProjectAggregate project = Project();
        project.ClaimGate.Should().Be(ClaimGate.Off, "off is the default and the platform's original behaviour");

        ProjectSettingsChanged changed = Change(project, claimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee));
        project.Apply(changed);

        changed.ClaimGate.HasValue.Should().BeTrue();
        project.ClaimGate.Should().Be(ClaimGate.TrackerAssignee);

        project.Apply(Change(project, claimGate: Optional<ClaimGate>.Of(ClaimGate.Off)));
        project.ClaimGate.Should().Be(ClaimGate.Off, "there is no clearing word to invent — off is the value");
    }

    /// <summary>
    /// The absent-means-unchanged contract every other setting on this event carries. It is what
    /// makes "default off, today's behaviour byte-for-byte" true of an existing stream: a
    /// settings change about something else cannot silently turn the gate on or off.
    /// </summary>
    [Fact]
    public void A_change_that_does_not_name_the_gate_leaves_it_alone()
    {
        ProjectAggregate project = Project();
        project.Apply(Change(project, claimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee)));

        project.Apply(Change(project, claimGate: Optional<ClaimGate>.None));

        project.ClaimGate.Should().Be(ClaimGate.TrackerAssignee);
    }

    [Fact]
    public void The_projection_reads_the_gate_back_the_same_way_the_aggregate_does()
    {
        Guid projectId = DomainId.New();
        ProjectDetailsProjection projection = new();
        ProjectDetails view = projection.Create(new FakeEvent<ProjectRegistered>(new ProjectRegistered(
            projectId, DomainId.New(), DomainId.New(), "hall9k", Path.GetFullPath("/repo"), null, "main", Now)));

        view.ClaimGate.Should().Be(ClaimGate.Off);

        projection.Apply(
            new FakeEvent<ProjectSettingsChanged>(Settings(projectId, Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee))),
            view);
        view.ClaimGate.Should().Be(ClaimGate.TrackerAssignee);

        projection.Apply(new FakeEvent<ProjectSettingsChanged>(Settings(projectId, Optional<ClaimGate>.None)), view);
        view.ClaimGate.Should().Be(ClaimGate.TrackerAssignee, "absent means unchanged here too");
    }

    private static ProjectAggregate Project()
    {
        ProjectAggregate project = new();
        project.Apply(ProjectDecider.Register(
            DomainId.New(), DomainId.New(), DomainId.New(), "hall9k", Path.GetFullPath("/repo"), null, "main", Now));
        return project;
    }

    private static ProjectSettingsChanged Change(ProjectAggregate project, Optional<ClaimGate> claimGate) =>
        ProjectDecider.ChangeSettings(
            project,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            claimGate: claimGate);

    private static ProjectSettingsChanged Settings(Guid projectId, Optional<ClaimGate> claimGate) =>
        new(
            projectId,
            Optional<IReadOnlyList<VerifyCommand>>.None,
            Optional<bool>.None,
            Optional<int>.None,
            Optional<IReadOnlyList<ContextLink>>.None,
            Now,
            DomainId.New(),
            ClaimGate: claimGate);
}
