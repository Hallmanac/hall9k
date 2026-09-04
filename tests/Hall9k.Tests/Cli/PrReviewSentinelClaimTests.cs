using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="PrReviewSentinelClaim.Refuse"/> — the refusal h9k task release, h9k task handback
/// and h9k task verify all owe an auto-pr-review Now-speed sentinel claim. The point of the
/// shared composer is that the sentence never claims a run state nobody observed: a parked run is
/// not "already running headlessly", which is exactly what the three hand-written copies said
/// before (independent pre-PR review, cycles 7 and 8).
/// </summary>
public sealed class PrReviewSentinelClaimTests
{
    [Theory]
    [InlineData("Dispatched")]
    [InlineData("Running")]
    [InlineData("Verifying")]
    [InlineData("UnderReview")]
    public void Every_live_state_reads_as_running_headlessly(string state)
    {
        Guid taskId = Guid.NewGuid();

        Exception refusal = PrReviewSentinelClaim.Refuse(taskId, state, "release");

        refusal.Message.Should().Contain("already running headlessly under the daemon's own supervision");
        refusal.Message.Should().Contain("not an interactive claim to release");
        refusal.Message.Should().Contain($"h9k task show {taskId}");
    }

    [Fact]
    public void A_review_park_names_the_lever_that_actually_closes_it()
    {
        Guid taskId = Guid.NewGuid();

        Exception refusal = PrReviewSentinelClaim.Refuse(taskId, RunState.ReviewParked, "hand back");

        refusal.Message.Should().NotContain(
            "already running", "a parked run is waiting on a human, not running");
        refusal.Message.Should().Contain("parked for you with its findings report");
        refusal.Message.Should().Contain(
            $"h9k review resolve {taskId} --merge-ready",
            "a pr-review park takes only --merge-ready, once the report has been walked (Decisions Log #99)");
    }

    [Fact]
    public void A_budget_park_says_so_without_promising_a_sweep_that_never_sees_it()
    {
        Guid taskId = Guid.NewGuid();

        Exception refusal = PrReviewSentinelClaim.Refuse(taskId, RunState.BudgetParked, "verify");

        refusal.Message.Should().NotContain("already running");
        refusal.Message.Should().Contain("parked on an exhausted token budget");
        // TokenBudgetRetryEngine filters on RunDetails.NodeId == this node's id, and a sentinel
        // run carries Guid.Empty there, so the hourly retry sweep does not in fact reach this run
        // — the message must not offer it as the way out.
        refusal.Message.Should().NotContain("retry sweep");
    }

    [Fact]
    public void An_unrecorded_state_is_said_out_loud_rather_than_guessed_at()
    {
        Exception refusal = PrReviewSentinelClaim.Refuse(Guid.NewGuid(), RunState.Unknown, "release");

        refusal.Message.Should().Contain("state is not recorded");
    }

    [Fact]
    public void Every_other_state_is_named_as_itself()
    {
        Exception refusal = PrReviewSentinelClaim.Refuse(Guid.NewGuid(), RunState.Completed, "verify");

        refusal.Message.Should().Contain("its headless run is Completed");
        refusal.Message.Should().Contain("not an interactive claim to verify");
    }
}
