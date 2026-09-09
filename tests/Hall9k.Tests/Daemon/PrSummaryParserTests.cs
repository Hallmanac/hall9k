using FluentAssertions;
using Hall9k.Connectors.Prompts;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The pull request the build session composed for itself, read off the same terminal result the
/// handoff comes from (Decisions Log #163). Every rule here exists because a
/// session's final message is prose written by a model, not a form it filled in.
/// </summary>
public sealed class PrSummaryParserTests
{
    [Fact]
    public void A_result_with_no_marker_carries_no_summary()
    {
        PrSummaryParser.Parse("I did the work and here is what I found.").Should().BeNull();
        PrSummaryParser.Parse(null).Should().BeNull();
        PrSummaryParser.Parse("   ").Should().BeNull();
    }

    [Fact]
    public void The_title_and_the_body_come_off_the_block()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            """
            Here is what I did.

            PR SUMMARY:
            Title: Resolve Key Vault references in every host

            Every host now resolves references through the shared provider.

            HANDOFF:
            Nothing surprising here.
            """);

        summary.Should().NotBeNull();
        summary!.Title.Should().Be("Resolve Key Vault references in every host");
        summary.Body.Should().Be("Every host now resolves references through the shared provider.");
    }

    /// <summary>
    /// The same last-marker-wins rule <see cref="HandoffParser"/> uses, for the same reason: a
    /// session that quotes its own instructions before answering writes the marker twice, and the
    /// second one is the one it meant.
    /// </summary>
    [Fact]
    public void The_last_marker_wins_when_the_session_quoted_the_instruction_first()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            """
            The prompt asked me to write a block under PR SUMMARY: before the handoff.

            PR SUMMARY:
            Title: The real one

            The real body.
            """);

        summary!.Title.Should().Be("The real one");
        summary.Body.Should().Be("The real body.");
    }

    /// <summary>
    /// A HANDOFF: line ABOVE the block never closes it. A session that quoted the handoff
    /// instruction on its way past would otherwise leave the parser reading an empty block.
    /// </summary>
    [Fact]
    public void A_handoff_marker_before_the_block_does_not_end_it()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            """
            I will end with HANDOFF: as asked.

            PR SUMMARY:
            Title: A title

            A body.
            """);

        summary!.Body.Should().Be("A body.");
    }

    [Fact]
    public void The_block_stops_at_the_handoff_rather_than_swallowing_it()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            """
            PR SUMMARY:
            Title: A title

            A body.

            HANDOFF:
            What someone building on this needs to know.
            """);

        summary!.Body.Should().Be("A body.")
            .And.NotContain("What someone building on this needs to know.");
    }

    /// <summary>
    /// A review-fix session ends with a resolution line and never a handoff, so the resolution is
    /// what closes its block. Without this the fix session's refreshed summary would carry
    /// "RESOLUTION: fixed" into the pull request body.
    /// </summary>
    [Fact]
    public void The_block_stops_at_a_review_fix_sessions_resolution_line()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            """
            PR SUMMARY:
            Title: A refreshed title

            The refreshed body.

            RESOLUTION: fixed
            """);

        summary!.Body.Should().Be("The refreshed body.");
        summary.Title.Should().Be("A refreshed title");
    }

    /// <summary>
    /// The skill's own process step tells the agent to output its result in a fenced code block,
    /// so a session that follows both instructions faithfully hands one over.
    /// </summary>
    [Fact]
    public void A_surrounding_fence_is_tolerated()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            "PR SUMMARY:\n```\nTitle: A fenced title\n\nA fenced body.\n```\n\nHANDOFF:\nnothing surprising");

        summary!.Title.Should().Be("A fenced title");
        summary.Body.Should().Be("A fenced body.");
    }

    /// <summary>
    /// A fence the body opens and never closes is not a wrapper, and neither is an example the
    /// body carries in the middle of itself; only a fence that both opens and closes the whole
    /// block is stripped.
    /// </summary>
    [Fact]
    public void A_fenced_example_inside_the_body_is_left_alone()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            "PR SUMMARY:\nTitle: A title\n\nRun this:\n\n```\nh9k status\n```\n\nAnd then read it.");

        summary!.Body.Should().Contain("```").And.Contain("h9k status").And.Contain("And then read it.");
    }

    /// <summary>
    /// A block with no Title: line is recorded title-null with the body kept, never with a title
    /// guessed off the opening sentence (AGENTS.md: never guess at unobserved facts). The opener's
    /// own fallback rule then names the title.
    /// </summary>
    [Fact]
    public void A_block_with_no_title_line_keeps_its_body_and_admits_it_has_no_title()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse(
            "PR SUMMARY:\nEvery host now resolves references through the shared provider.\n\nHANDOFF:\nnothing");

        summary!.Title.Should().BeNull();
        summary.Body.Should().Be("Every host now resolves references through the shared provider.");
    }

    [Fact]
    public void A_block_with_a_title_and_no_body_records_the_body_as_absent()
    {
        PrSummaryParser.PrSummary? summary = PrSummaryParser.Parse("PR SUMMARY:\nTitle: A title\n\nHANDOFF:\nnothing");

        summary!.Title.Should().Be("A title");
        summary.Body.Should().BeEmpty();
    }

    [Fact]
    public void A_marker_with_nothing_under_it_at_all_is_no_summary()
    {
        PrSummaryParser.Parse("PR SUMMARY:\n\nHANDOFF:\nnothing").Should().BeNull();
    }

    [Fact]
    public void The_marker_is_matched_however_the_session_cased_it()
    {
        PrSummaryParser.Parse("pr summary:\ntitle: A title\n\nA body.")
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary("A title", "A body."));
    }

    [Fact]
    public void A_title_written_on_the_marker_line_itself_is_still_read()
    {
        PrSummaryParser.Parse("PR SUMMARY: Title: A title\n\nA body.")
            .Should().BeEquivalentTo(new PrSummaryParser.PrSummary("A title", "A body."));
    }

    /// <summary>
    /// The artifact round-trips: what <c>pr-summary.md</c> holds is read back by the same rules the
    /// session was asked to write it under, so the opener never sees a shape the parser did not.
    /// </summary>
    [Theory]
    [InlineData("A title", "A body.")]
    [InlineData(null, "A body with no title.")]
    [InlineData("A title", "")]
    public void What_the_run_directory_keeps_reads_back_as_what_was_captured(string? title, string body)
    {
        PrSummaryParser.PrSummary captured = new(title, body);

        PrSummaryParser.ParseBlock(PrSummaryParser.Render(captured)).Should().BeEquivalentTo(captured);
    }
}
