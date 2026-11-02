using FluentAssertions;
using Hall9k.Daemon.Courier;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Reading a courier session's own terminal result for the delivered marker (idea 89471598,
/// piece 3) — no store, no process, just <see cref="AgentResult"/> the way
/// <see cref="StreamJsonParser"/> would have parsed it off a real stream.
/// </summary>
public sealed class CourierDeliveryOutcomeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static AgentResult Result(string summary) => new(false, 100, 0, 0, 50, null, 3, summary);

    [Fact]
    public void A_summary_carrying_the_delivered_marker_reads_as_delivered()
    {
        (bool delivered, string outcome) = CourierDeliveryOutcome.Parse(
            Result($"Sent it.\n{CourierPromptBuilder.DeliveredMarker}"), timedOut: false, Timeout);

        delivered.Should().BeTrue();
        outcome.Should().Be("Delivered.");
    }

    [Fact]
    public void A_summary_carrying_the_failed_marker_reads_as_not_delivered_and_quotes_it()
    {
        string summary = $"{CourierPromptBuilder.FailedMarkerPrefix} - no session by that name";

        (bool delivered, string outcome) = CourierDeliveryOutcome.Parse(Result(summary), timedOut: false, Timeout);

        delivered.Should().BeFalse();
        outcome.Should().Contain(summary);
    }

    [Fact]
    public void A_summary_that_quotes_the_delivered_marker_before_a_real_failed_line_reads_as_not_delivered()
    {
        string summary = $"I was asked to end with `{CourierPromptBuilder.DeliveredMarker}` if the send "
            + $"succeeded; it did not, so:\n{CourierPromptBuilder.FailedMarkerPrefix} - no session named "
            + "hall9k-orchestrator";

        (bool delivered, string outcome) = CourierDeliveryOutcome.Parse(Result(summary), timedOut: false, Timeout);

        delivered.Should().BeFalse();
        outcome.Should().Contain("without a delivered marker");
    }

    [Fact]
    public void A_result_with_no_marker_at_all_reads_as_not_delivered()
    {
        (bool delivered, string outcome) = CourierDeliveryOutcome.Parse(
            Result("I couldn't find that tool."), timedOut: false, Timeout);

        delivered.Should().BeFalse();
        outcome.Should().Contain("without a delivered marker");
    }

    [Fact]
    public void No_result_at_all_reads_as_not_delivered_and_says_nothing_was_left_to_read()
    {
        (bool delivered, string outcome) = CourierDeliveryOutcome.Parse(null, timedOut: false, Timeout);

        delivered.Should().BeFalse();
        outcome.Should().Contain("left no result to read");
    }

    [Fact]
    public void A_timeout_with_no_marker_names_the_timeout_rather_than_a_missing_result()
    {
        (bool delivered, string outcome) = CourierDeliveryOutcome.Parse(null, timedOut: true, Timeout);

        delivered.Should().BeFalse();
        outcome.Should().Contain(Timeout.ToString());
    }

    [Fact]
    public void The_reason_is_the_text_after_the_failed_marker_when_the_session_gave_one()
    {
        string summary = $"Could not send.\n{CourierPromptBuilder.FailedMarkerPrefix} - hall9k-9f is not reachable";

        CourierDeliveryOutcome.FailureReason(Result(summary), timedOut: false, Timeout)
            .Should().Be("hall9k-9f is not reachable");
    }

    [Theory]
    [InlineData("COURIER-OUTCOME: failed")]
    [InlineData("COURIER-OUTCOME: failed -")]
    [InlineData("COURIER-OUTCOME: failed - ")]
    public void A_failed_marker_with_nothing_after_it_reads_as_no_reason_given(string marker)
    {
        CourierDeliveryOutcome.FailureReason(Result($"Nope.\n{marker}"), timedOut: false, Timeout)
            .Should().Be("no reason given");
    }

    [Fact]
    public void A_timeout_reads_as_timed_out_after_the_timeout()
    {
        CourierDeliveryOutcome.FailureReason(null, timedOut: true, TimeSpan.FromMinutes(3)).Should().Be("timed out after 3m");
        CourierDeliveryOutcome.FailureReason(null, timedOut: true, TimeSpan.FromSeconds(90)).Should().Be("timed out after 90s");
    }

    [Fact]
    public void A_session_with_no_outcome_line_or_no_result_reads_as_ended_without_an_outcome_line()
    {
        CourierDeliveryOutcome.FailureReason(Result("I couldn't find that tool."), timedOut: false, Timeout)
            .Should().Be("ended without an outcome line");
        CourierDeliveryOutcome.FailureReason(null, timedOut: false, Timeout)
            .Should().Be("ended without an outcome line");
        CourierDeliveryOutcome.FailureReason(Result("COURIER-OUTCOME: maybe"), timedOut: false, Timeout)
            .Should().Be("ended without an outcome line", "a marker that is neither delivered nor failed says nothing");
    }

    [Fact]
    public void The_last_outcome_line_wins_over_a_quoted_instruction()
    {
        string summary = $"I was asked to end with `{CourierPromptBuilder.FailedMarkerPrefix} - <reason>`.\n"
            + $"{CourierPromptBuilder.FailedMarkerPrefix} - no session by that name";

        CourierDeliveryOutcome.FailureReason(Result(summary), timedOut: false, Timeout)
            .Should().Be("no session by that name");
    }
}
