using FluentAssertions;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The default-resolution half of the Security persona's own project setting (idea 6be68ee2,
/// phase two), resolved the <see cref="AutoPrReviewSetting"/> way (Decisions Log #161): a project
/// that never recorded a choice is on, one that recorded off stays off, and the last recorded
/// choice wins over an earlier one.
/// </summary>
public sealed class SecurityReviewSettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_project_that_recorded_nothing_resolves_to_on_by_default()
    {
        SecurityReviewSetting setting = SecurityReviewSetting.From(ProjectSettingsHistory.Empty);

        setting.IsOn.Should().BeTrue();
        setting.Recorded.Should().BeFalse();
        setting.Origin.Should().Be("default");
        setting.OnOff.Should().Be("on");
    }

    [Fact]
    public void A_settings_change_that_touched_something_else_leaves_the_default_in_place()
    {
        SecurityReviewSetting setting = SecurityReviewSetting.From(
            ProjectSettingsHistory.FromChanges([Changed(Now, commitStyle: CommitStyle.Narrative)]));

        setting.Should().Be(SecurityReviewSetting.Unrecorded,
            "only an event that actually carried the setting makes it explicit");
    }

    [Fact]
    public void An_explicit_off_is_recorded_and_honoured()
    {
        SecurityReviewSetting setting = SecurityReviewSetting.From(
            ProjectSettingsHistory.FromChanges([Changed(Now, securityReview: false)]));

        setting.IsOn.Should().BeFalse();
        setting.Recorded.Should().BeTrue();
        setting.Origin.Should().Be("explicit");
        setting.OnOff.Should().Be("off");
    }

    [Fact]
    public void The_last_recorded_choice_wins_over_an_earlier_one()
    {
        SecurityReviewSetting setting = SecurityReviewSetting.From(ProjectSettingsHistory.FromChanges(
        [
            Changed(Now, securityReview: false),
            Changed(Now.AddMinutes(1), commitStyle: CommitStyle.Narrative),
            Changed(Now.AddMinutes(2), securityReview: true),
        ]));

        setting.IsOn.Should().BeTrue();
        setting.Recorded.Should().BeTrue();
    }

    /// <summary>
    /// A teammate's node holds only the team half of a change, and that half can sit behind a
    /// newer one when a catch-up answer delivers a pre-switch-off head after the tail. The daemon
    /// reads the newest stamp either way.
    /// </summary>
    [Fact]
    public void A_replicated_team_change_resolves_and_the_newest_stamp_wins_whatever_order_they_sit_in()
    {
        ProjectTeamSettingsChanged newer = new(
            DomainId.New(), Now.AddDays(5), DomainId.New(), SecurityReview: Optional<bool>.Of(false));
        ProjectTeamSettingsChanged olderAppendedLater = new(
            DomainId.New(), Now, DomainId.New(), SecurityReview: Optional<bool>.Of(true));

        SecurityReviewSetting.From(ProjectSettingsHistory.FromEveryChange([newer])).IsOn.Should().BeFalse();

        SecurityReviewSetting setting = SecurityReviewSetting.From(
            ProjectSettingsHistory.FromEveryChange([newer, olderAppendedLater]));

        setting.IsOn.Should().BeFalse();
        setting.Recorded.Should().BeTrue();
    }

    [Fact]
    public void ParseOnOff_accepts_on_and_off_and_refuses_anything_else()
    {
        SecurityReviewSetting.ParseOnOff("on", "--security-review").Should().BeTrue();
        SecurityReviewSetting.ParseOnOff("off", "--security-review").Should().BeFalse();

        FluentActions.Invoking(() => SecurityReviewSetting.ParseOnOff("maybe", "--security-review"))
            .Should().Throw<DomainValidationException>().WithMessage("*--security-review*on*off*");
    }

    private static ProjectSettingsChanged Changed(
        DateTimeOffset changedAt,
        Optional<bool> securityReview = default,
        Optional<CommitStyle> commitStyle = default) =>
        new(
            DomainId.New(),
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
            SkipPermissions: Optional<bool>.None,
            MaxParallelAgents: Optional<int>.None,
            ContextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            ChangedAt: changedAt,
            ChangedByOwnerId: DomainId.New(),
            CommitStyle: commitStyle,
            SecurityReview: securityReview);
}
