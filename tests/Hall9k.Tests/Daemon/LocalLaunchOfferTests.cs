using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The identity a review report's launch offer carries (idea b9b09779, piece 5). The criterion it
/// exists for is that a yes in the orchestrator window resolves to exactly one branch and worktree
/// without the human naming either, so what this pins is that the block is there when an offer was
/// made, absent when none was, and complete when present.
/// </summary>
public sealed class LocalLaunchOfferTests
{
    private static readonly Guid TaskId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();
    private const string Worktree = "/home/me/.hall9k/projects/demo/repo/wt-review";
    private const string Branch = "task/28b19893-a-change";

    [Fact]
    public void A_report_whose_project_has_a_run_skill_carries_the_command_and_the_identity()
    {
        string block = Compose(ReviewPersona.Qa, settingOn: false, hasRunSkill: true);

        block.Should().Contain("## Running this branch locally");
        block.Should().Contain($"h9k task run-local {TaskId}");
        block.Should().Contain($"task `{TaskId}`");
        block.Should().Contain($"run `{RunId}`");
        block.Should().Contain($"branch `{Branch}`");
        block.Should().Contain($"worktree `{Worktree}`");
    }

    /// <summary>
    /// The offer is gated on the run skill alone, never on whether the session was allowed to
    /// drive: a project that will not let a review start its product can still have a reviewer who
    /// can.
    /// </summary>
    [Fact]
    public void A_static_review_still_carries_the_identity()
    {
        Compose(ReviewPersona.Designer, settingOn: false, hasRunSkill: true)
            .Should().Contain($"h9k task run-local {TaskId}");
    }

    /// <summary>
    /// No run skill means no offer anywhere in the report, so an identity block would answer a
    /// question nobody asked — and would read as an offer the platform made on the review's behalf.
    /// </summary>
    [Fact]
    public void A_project_with_no_run_skill_gets_no_identity_block()
    {
        Compose(ReviewPersona.Qa, settingOn: true, hasRunSkill: false).Should().BeEmpty();
    }

    /// <summary>
    /// The engineer's review neither drives nor offers, and records no drive decision at all, so
    /// an engineer-only report carries nothing here however the project is configured.
    /// </summary>
    [Fact]
    public void An_engineer_only_report_gets_no_identity_block()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan([ReviewPersona.Engineer]);

        LocalLaunchOffer.Compose(plan, TaskId, RunId, Worktree, Branch).Should().BeEmpty();
    }

    /// <summary>
    /// A review opened with no checkout has no worktree, so the command would refuse. The block
    /// says the offer cannot be taken up rather than printing a command it already knows will not
    /// run, and still names the worktree as missing rather than as an empty pair of backticks a
    /// reader cannot tell from a field the platform failed to fill in.
    /// </summary>
    [Fact]
    public void A_review_with_no_checkout_says_the_offer_cannot_be_taken_up()
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan(
            [ReviewPersona.Qa], [new ReviewDriveDecision(ReviewPersona.Qa, false, true)]);

        string block = LocalLaunchOffer.Compose(plan, TaskId, RunId, string.Empty, Branch);

        block.Should().Contain("cannot be taken up");
        block.Should().Contain("worktree `not recorded`");
        block.Should().NotContain($"h9k task run-local {TaskId}");
    }

    private static string Compose(ReviewPersona persona, bool settingOn, bool hasRunSkill)
    {
        ReviewPersonaPlan plan = ReviewPersonaRegistry.Plan(
            [persona], [new ReviewDriveDecision(persona, settingOn, hasRunSkill)]);
        return LocalLaunchOffer.Compose(plan, TaskId, RunId, Worktree, Branch);
    }
}
