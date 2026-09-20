using FluentAssertions;
using Hall9k.Domain.Features.Courier;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The feed courier's own prompt, pinned as a golden (idea 89471598, piece 3): small, no recipe,
/// no AGENTS.md — just the feed items as the drain prints them, the delivery instruction, and the
/// outcome-marker instruction the daemon reads back.
/// </summary>
public sealed class CourierPromptBuilderTests
{
    [Fact]
    public void The_prompt_carries_the_feed_lines_verbatim_and_the_delivery_instruction()
    {
        const string delivery =
            "Use the SendMessage tool addressed to `hall9k-orchestrator` — the orchestrator's own "
            + "registered session, reached through the cross-session mesh — with the message above "
            + "as the `message` argument, verbatim.";

        string prompt = CourierPromptBuilder.Build(
            "hall9k",
            [
                "37b5ec69  h9k orchestrator feed: cursor plus filter",
                "  2026-09-20 09:00  idea logged: courier batching",
            ],
            delivery);

        // Split on a plain '\n', never Environment.NewLine: the prompt itself is built that way
        // on purpose (this project's CI runs both Windows and Unix), so a test asserting on
        // Environment.NewLine here would pass on one and fail on the other.
        prompt.Split('\n').Should().Equal(
            "# Deliver the hall9k orchestrator feed",
            "",
            "The following items are waiting, undrained, in hall9k's orchestrator feed. They are "
                + "already in the exact order and grouping `h9k orchestrator feed --drain` itself prints, so "
                + "deliver them as one message, verbatim, changing nothing:",
            "",
            "37b5ec69  h9k orchestrator feed: cursor plus filter",
            "  2026-09-20 09:00  idea logged: courier batching",
            "",
            delivery,
            "",
            "End your final message with exactly one line: `COURIER-OUTCOME: delivered` if the send above "
                + "succeeded, or `COURIER-OUTCOME: failed - <one-line reason>` if it did not. Nothing you do "
                + "here drains the feed yourself — the daemon reads this line and drains it on your behalf "
                + "once it sees delivered. Do not run any h9k command. Do not read any file outside this "
                + "prompt. End your turn immediately after that line.",
            "");
    }

    [Fact]
    public void The_outcome_markers_are_the_exact_strings_the_prompt_itself_states()
    {
        CourierPromptBuilder.DeliveredMarker.Should().Be("COURIER-OUTCOME: delivered");
        CourierPromptBuilder.FailedMarkerPrefix.Should().Be("COURIER-OUTCOME: failed");
    }
}
