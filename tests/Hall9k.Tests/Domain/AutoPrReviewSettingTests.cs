using FluentAssertions;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The default-resolution and no-backfill halves of Decisions Log #161, DB-free: a project that
/// never recorded a setting is on, one that recorded <c>off</c> stays off, and a review request
/// GitHub recorded before a project's own cutoff never starts a task on its own however the
/// setting reads. Origin incident (2026-09-08): the feature sat silent on both nodes for three
/// days because off-as-default and off-as-choice were the same value everywhere, and the first
/// sweep after opting in minted four tasks at once, two of them for August requests.
/// </summary>
public sealed class AutoPrReviewSettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_project_that_recorded_nothing_resolves_to_normal_by_default()
    {
        AutoPrReviewSetting setting = AutoPrReviewSetting.From(ProjectSettingsHistory.Empty);

        setting.Speed.Should().Be(AutoPrReviewSpeed.Normal);
        setting.Recorded.Should().BeFalse();
        setting.IsOn.Should().BeTrue();
        setting.Origin.Should().Be("default");
        setting.OnOff.Should().Be("on");
    }

    [Fact]
    public void A_settings_change_that_touched_something_else_leaves_the_default_in_place()
    {
        AutoPrReviewSetting setting = AutoPrReviewSetting.From(
            ProjectSettingsHistory.FromChanges([Changed(Now, commitStyle: CommitStyle.Narrative)]));

        setting.Should().Be(AutoPrReviewSetting.Unrecorded,
            "only an event that actually carried the setting makes it explicit");
    }

    [Fact]
    public void An_explicit_off_is_recorded_and_honoured()
    {
        AutoPrReviewSetting setting = AutoPrReviewSetting.From(
            ProjectSettingsHistory.FromChanges([Changed(Now, speed: AutoPrReviewSpeed.Off)]));

        setting.Speed.Should().Be(AutoPrReviewSpeed.Off);
        setting.Recorded.Should().BeTrue();
        setting.IsOn.Should().BeFalse();
        setting.Origin.Should().Be("explicit");
        setting.OnOff.Should().Be("off");
    }

    [Fact]
    public void The_last_recorded_choice_wins_over_an_earlier_one()
    {
        AutoPrReviewSetting setting = AutoPrReviewSetting.From(ProjectSettingsHistory.FromChanges(
        [
            Changed(Now, speed: AutoPrReviewSpeed.Off),
            Changed(Now.AddMinutes(1), commitStyle: CommitStyle.Narrative),
            Changed(Now.AddMinutes(2), speed: AutoPrReviewSpeed.Now),
        ]));

        setting.Speed.Should().Be(AutoPrReviewSpeed.Now);
        setting.Recorded.Should().BeTrue();
    }

    [Fact]
    public void A_recorded_choice_read_back_as_null_is_off_and_still_explicit()
    {
        AutoPrReviewSetting setting = AutoPrReviewSetting.From(ProjectSettingsHistory.FromChanges(
            [Changed(Now, speed: Optional<AutoPrReviewSpeed>.Of(null))]));

        setting.Speed.Should().Be(AutoPrReviewSpeed.Off);
        setting.Recorded.Should().BeTrue("present-with-null is a recorded choice like any other");
    }

    [Fact]
    public void The_cutoff_is_the_projects_registration_when_it_postdates_this_installs_adoption()
    {
        DateTimeOffset registered = Now;
        DateTimeOffset adopted = Now.AddDays(-3);

        AutoPrReviewCutoff.For(registered, adopted).Should().Be(registered);
    }

    [Fact]
    public void The_cutoff_is_this_installs_adoption_for_a_project_that_predates_it()
    {
        DateTimeOffset registered = Now.AddMonths(-2);
        DateTimeOffset adopted = Now;

        AutoPrReviewCutoff.For(registered, adopted).Should().Be(adopted);
    }

    [Fact]
    public void A_request_recorded_after_the_cutoff_starts_on_its_own()
    {
        AutoPrReviewCutoff.StartsOnItsOwn(Now.AddMinutes(1), Now).Should().BeTrue();
    }

    [Fact]
    public void A_request_recorded_in_the_same_second_as_the_cutoff_starts_on_its_own()
    {
        AutoPrReviewCutoff.StartsOnItsOwn(Now, Now).Should().BeTrue(
            "GitHub's own timestamps are second-resolution, and a request made as a project was "
            + "registered is a new request, not a stale one");
    }

    [Fact]
    public void A_request_recorded_before_the_cutoff_never_starts_on_its_own()
    {
        AutoPrReviewCutoff.StartsOnItsOwn(Now.AddSeconds(-1), Now).Should().BeFalse();
        AutoPrReviewCutoff.StartsOnItsOwn(Now.AddDays(-14), Now).Should().BeFalse(
            "the two August requests the 16:03 EDT sweep minted are exactly this case");
    }

    [Fact]
    public void A_request_whose_own_time_could_not_be_read_never_starts_on_its_own()
    {
        AutoPrReviewCutoff.StartsOnItsOwn(null, Now).Should().BeFalse(
            "nothing proves it postdates the cutoff, and an unobserved fact is never filled in");
    }

    private static ProjectSettingsChanged Changed(
        DateTimeOffset changedAt,
        Optional<AutoPrReviewSpeed> speed = default,
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
            AutoPrReview: speed);
}
