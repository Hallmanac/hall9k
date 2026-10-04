using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Independent pre-PR review, cycle 3, adversarial lens: gh's own 300-file diff ceiling
/// (<c>RunLauncher.ParkUnreadableDiffPreflightAsync</c>) parks a task the same way a genuine
/// unsafe pre-flight verdict does, verdict "unreadable". Before this fix the card
/// pointed the owner at "h9k task assign" with nothing distinguishing it from a genuine unsafe
/// park — but assigning here only re-dispatches the identical pre-flight into the identical
/// refusal, since nothing about reassigning shrinks the pull request's own diff. The card must say
/// so, rather than implying the command fixes it.
/// </summary>
public sealed class PreflightParkedAttentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_unreadable_diff_park_names_the_300_file_ceiling_instead_of_pointing_at_a_futile_assign()
    {
        TaskListItem task = new()
        {
            Id = Guid.NewGuid(),
            State = TaskState.Published,
            PrReviewPreflightUnsafe = true,
            PrReviewPreflightParkedVerdict = "unreadable",
            PrReviewPreflightParkedReason =
                "gh could not read this pull request's diff, so the pre-flight has nothing to judge: "
                + "HTTP 406: Sorry, the diff exceeded the maximum number of files (300), so it is not available",
        };

        TaskAttention attention = AttentionComposer.Compose(
            task, run: null, LifecycleState.Published, TaskPhase.None, stalled: false, Now);

        attention.Level.Should().Be(AttentionLevel.NeedsYou);
        attention.Cause.Should().Contain(
            "300-file diff ceiling", "the owner must be told what actually blocks this, not just handed a command");
        attention.Cause.Should().Contain(
            "only re-dispatches the identical pre-flight", "assign is not the fix here, and the card must say so");
        attention.Lever.Should().Be($"h9k task queue {TaskListCommand.ShortId(task.Id)}");
    }

    [Fact]
    public void A_genuine_unsafe_verdict_park_still_names_the_verdict_reason_and_surfaces()
    {
        TaskListItem task = new()
        {
            Id = Guid.NewGuid(),
            State = TaskState.Published,
            PrReviewPreflightUnsafe = true,
            PrReviewPreflightParkedVerdict = "unsafe",
            PrReviewPreflightParkedReason = "the workflow file grants secrets to a forked pull request.",
            PrReviewPreflightParkedSurfaces = [".github/workflows/ci.yml"],
            PrReviewPreflightParkedHeadRefOid = "abc123",
        };

        TaskAttention attention = AttentionComposer.Compose(
            task, run: null, LifecycleState.Published, TaskPhase.None, stalled: false, Now);

        attention.Level.Should().Be(AttentionLevel.NeedsYou);
        attention.Cause.Should().Contain("unsafe");
        attention.Cause.Should().Contain("the workflow file grants secrets to a forked pull request.");
        attention.Cause.Should().Contain(".github/workflows/ci.yml");
        attention.Cause.Should().Contain("abc123");
        attention.Cause.Should().NotContain(
            "300-file diff ceiling", "a genuine verdict must never carry the unreadable-park's own wording");
        attention.Lever.Should().Be($"h9k task queue {TaskListCommand.ShortId(task.Id)}");
    }
}
