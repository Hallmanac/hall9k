using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Whether a persona's review may stand the product up (idea b9b09779), DB-free. Two defaults
/// that differ on purpose — the designer drives, QA does not — and the same recorded-versus-
/// default distinction Decisions Log #161 established for auto pr-review, for the same reason:
/// with a default of ON, a stored "off" a reader could take for the platform's own initial state
/// is exactly how a setting goes unnoticed.
/// </summary>
public sealed class ReviewDriveSettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_designer_drives_by_default_and_qa_does_not()
    {
        ReviewDriveSetting.DefaultFor(ReviewPersona.Designer).Should().BeTrue();
        ReviewDriveSetting.DefaultFor(ReviewPersona.Qa).Should().BeFalse();
        ReviewDriveSetting.DefaultFor(ReviewPersona.Engineer).Should().BeFalse(
            "the engineer's review reads a diff and has never stood anything up");
    }

    [Fact]
    public void A_project_that_recorded_nothing_resolves_to_the_personas_own_default()
    {
        ReviewDriveSetting designer = ReviewDriveSetting.From(ReviewPersona.Designer, ProjectSettingsHistory.Empty);

        designer.Enabled.Should().BeTrue();
        designer.Recorded.Should().BeFalse();
        designer.Origin.Should().Be("default");
        designer.OnOff.Should().Be("on");

        ReviewDriveSetting.From(ReviewPersona.Qa, ProjectSettingsHistory.Empty).Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_settings_change_that_touched_something_else_leaves_the_default_in_place()
    {
        ReviewDriveSetting setting = ReviewDriveSetting.From(
            ReviewPersona.Designer,
            ProjectSettingsHistory.FromChanges([Changed(commitStyle: CommitStyle.Narrative)]));

        setting.Should().Be(ReviewDriveSetting.UnrecordedFor(ReviewPersona.Designer),
            "only an event that actually carried the setting makes it explicit");
    }

    [Fact]
    public void An_explicit_off_is_recorded_and_told_apart_from_the_default()
    {
        ReviewDriveSetting setting = ReviewDriveSetting.From(
            ReviewPersona.Designer, ProjectSettingsHistory.FromChanges([Changed(drive: false)]));

        setting.Enabled.Should().BeFalse();
        setting.Recorded.Should().BeTrue();
        setting.Origin.Should().Be("explicit");
        setting.OnOff.Should().Be("off");
    }

    [Fact]
    public void The_last_recorded_choice_wins_over_an_earlier_one()
    {
        ReviewDriveSetting setting = ReviewDriveSetting.From(
            ReviewPersona.Designer,
            ProjectSettingsHistory.FromChanges([Changed(drive: false), Changed(drive: true)]));

        setting.Enabled.Should().BeTrue();
        setting.Recorded.Should().BeTrue();
    }

    /// <summary>
    /// A drive setting is a team field, so on a teammate's own node the only half of the change
    /// that ever arrives is <see cref="ProjectTeamSettingsChanged"/> — the node-scoped half was
    /// never written there at all. Reading only that half would resolve a deliberate "off" back
    /// to the default and drive a product somebody turned driving off for.
    /// </summary>
    [Fact]
    public void A_replicated_change_carrying_only_the_team_half_still_resolves()
    {
        ProjectTeamSettingsChanged? replicated = ProjectTeamSettingsChanged.From(Changed(drive: false));
        replicated.Should().NotBeNull();

        ReviewDriveSetting setting = ReviewDriveSetting.From(
            ReviewPersona.Designer, ProjectSettingsHistory.FromEveryChange([replicated!]));

        setting.Enabled.Should().BeFalse();
        setting.Recorded.Should().BeTrue();
    }

    /// <summary>
    /// One field per persona, so neither answer can be read off the other's: a project that
    /// turned design-review driving on has said nothing whatever about QA's, and a QA review
    /// there still resolves to QA's own default.
    /// </summary>
    [Fact]
    public void One_personas_recorded_choice_is_never_read_as_the_others()
    {
        ReviewDriveSetting setting = ReviewDriveSetting.From(
            ReviewPersona.Qa, ProjectSettingsHistory.FromChanges([Changed(drive: true)]));

        setting.Recorded.Should().BeFalse();
        setting.Enabled.Should().BeFalse();
    }

    /// <summary>
    /// QA's own field, read the same way the designer's is and defaulting the other way: an
    /// explicit "on" is told apart from the default it does not match, and the last choice
    /// recorded is the one that stands.
    /// </summary>
    [Fact]
    public void Qas_own_choice_is_recorded_on_its_own_field_and_the_last_one_wins()
    {
        ReviewDriveSetting on = ReviewDriveSetting.From(
            ReviewPersona.Qa, ProjectSettingsHistory.FromChanges([Changed(qaDrive: true)]));
        on.Enabled.Should().BeTrue();
        on.Recorded.Should().BeTrue();

        ReviewDriveSetting off = ReviewDriveSetting.From(
            ReviewPersona.Qa,
            ProjectSettingsHistory.FromChanges([Changed(qaDrive: true), Changed(qaDrive: false)]));
        off.Enabled.Should().BeFalse();
        off.Recorded.Should().BeTrue(
            "off as a choice somebody typed and off as the untouched default are different facts");

        ReviewDriveSetting designer = ReviewDriveSetting.From(
            ReviewPersona.Designer, ProjectSettingsHistory.FromChanges([Changed(qaDrive: false)]));
        designer.Should().Be(ReviewDriveSetting.UnrecordedFor(ReviewPersona.Designer),
            "qa-review-drive says nothing about the designer's review");
    }

    /// <summary>QA's field replicates like the designer's: the team half alone still resolves it.</summary>
    [Fact]
    public void A_replicated_qa_choice_carrying_only_the_team_half_still_resolves()
    {
        ProjectTeamSettingsChanged? replicated = ProjectTeamSettingsChanged.From(Changed(qaDrive: true));
        replicated.Should().NotBeNull();

        ReviewDriveSetting setting = ReviewDriveSetting.From(
            ReviewPersona.Qa, ProjectSettingsHistory.FromEveryChange([replicated!]));

        setting.Enabled.Should().BeTrue();
        setting.Recorded.Should().BeTrue();
    }

    [Fact]
    public void The_word_a_human_types_is_on_or_off_and_anything_else_is_refused_by_name()
    {
        ReviewDriveSetting.ParseOnOff("on", "--design-review-drive").Should().BeTrue();
        ReviewDriveSetting.ParseOnOff(" OFF ", "--design-review-drive").Should().BeFalse();

        foreach (string? refused in new[] { null, string.Empty, "true", "yes", "default" })
        {
            Action parse = () => ReviewDriveSetting.ParseOnOff(refused, "--design-review-drive");
            parse.Should().Throw<DomainValidationException>().WithMessage("*--design-review-drive*");
        }
    }

    /// <summary>The decider records what it is given, and an untouched setting stays untouched.</summary>
    [Fact]
    public void The_decider_records_the_choice_and_leaves_it_alone_when_nobody_made_one()
    {
        ProjectAggregate project = Registered();

        ProjectSettingsChanged changed = ProjectDecider.ChangeSettings(
            project, default, default, default, Now, Guid.Empty, designReviewDrive: Optional<bool>.Of(false));
        changed.DesignReviewDrive.HasValue.Should().BeTrue();
        changed.DesignReviewDrive.Value.Should().BeFalse();

        ProjectSettingsChanged qa = ProjectDecider.ChangeSettings(
            project, default, default, default, Now, Guid.Empty, qaReviewDrive: Optional<bool>.Of(true));
        qa.QaReviewDrive.HasValue.Should().BeTrue();
        qa.QaReviewDrive.Value.Should().BeTrue();
        qa.DesignReviewDrive.HasValue.Should().BeFalse("one persona's choice never records the other's");

        ProjectSettingsChanged neither =
            ProjectDecider.ChangeSettings(project, default, default, default, Now, Guid.Empty);
        neither.DesignReviewDrive.HasValue.Should().BeFalse();
        neither.QaReviewDrive.HasValue.Should().BeFalse();
    }

    /// <summary>
    /// Both the aggregate and the read model mirror the recorded value, and both start at the
    /// platform default so a project that never chose reads the same on either.
    /// </summary>
    [Fact]
    public void The_aggregate_mirrors_the_recorded_value_and_starts_at_the_default()
    {
        ProjectAggregate project = Registered();
        project.DesignReviewDrive.Should().BeTrue();

        project.Apply(Changed(drive: false));
        project.DesignReviewDrive.Should().BeFalse();

        project.Apply(ProjectTeamSettingsChanged.From(Changed(drive: true))!);
        project.DesignReviewDrive.Should().BeTrue("the replicated half sets the same property");

        project.QaReviewDrive.Should().BeFalse("QA's own default is off");
        project.Apply(Changed(qaDrive: true));
        project.QaReviewDrive.Should().BeTrue();
        project.Apply(ProjectTeamSettingsChanged.From(Changed(qaDrive: false))!);
        project.QaReviewDrive.Should().BeFalse();
    }

    private static ProjectAggregate Registered()
    {
        ProjectAggregate project = new();
        project.Apply(new ProjectRegistered(
            DomainId.New(), DomainId.New(), DomainId.New(), "hall9k", "/repo", null, "main", Now,
            ProjectHome.None, SkipPermissions: false));
        return project;
    }

    private static ProjectSettingsChanged Changed(
        bool? drive = null, CommitStyle? commitStyle = null, bool? qaDrive = null) => new(
        Guid.Empty,
        Optional<IReadOnlyList<VerifyCommand>>.None,
        Optional<bool>.None,
        Optional<int>.None,
        Optional<IReadOnlyList<ContextLink>>.None,
        Now,
        Guid.Empty,
        CommitStyle: commitStyle is null ? Optional<CommitStyle>.None : Optional<CommitStyle>.Of(commitStyle),
        DesignReviewDrive: drive is null ? Optional<bool>.None : Optional<bool>.Of(drive.Value),
        QaReviewDrive: qaDrive is null ? Optional<bool>.None : Optional<bool>.Of(qaDrive.Value));
}
