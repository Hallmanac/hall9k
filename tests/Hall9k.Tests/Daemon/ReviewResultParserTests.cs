using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class ReviewResultParserTests
{
    [Theory]
    [InlineData("All clear.\n\nVERDICT: merge-ready")]
    [InlineData("findings...\nverdict: MERGE-READY")]
    [InlineData("  VERDICT:   merge-ready  ")]
    public void Merge_ready_verdicts_parse_regardless_of_case_and_whitespace(string summary) =>
        ReviewResultParser.ParseVerdict(summary).Should().Be(ReviewVerdict.MergeReady);

    [Fact]
    public void Needs_fixes_verdict_parses()
    {
        ReviewResultParser.ParseVerdict("1. Foo.cs:12 — off-by-one.\n\nVERDICT: needs-fixes")
            .Should().Be(ReviewVerdict.NeedsFixes);
    }

    [Fact]
    public void The_last_verdict_line_wins_when_the_reviewer_quotes_the_instructions()
    {
        string summary =
            "The instructions said to end with VERDICT: merge-ready or VERDICT: needs-fixes.\n" +
            "I found a real defect.\n" +
            "VERDICT: needs-fixes";

        ReviewResultParser.ParseVerdict(summary).Should().Be(ReviewVerdict.NeedsFixes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Looks fine to me, ship it.")]
    [InlineData("VERDICT: looks great")]
    public void A_missing_or_unrecognized_verdict_maps_to_the_unknown_sentinel(string? summary) =>
        ReviewResultParser.ParseVerdict(summary).Should().Be(ReviewVerdict.Unknown);

    [Theory]
    [InlineData("Fixed both findings.\nRESOLUTION: fixed", "Fixed")]
    [InlineData("Finding 2 is a deliberate design choice.\nresolution: DISPUTED", "Disputed")]
    [InlineData("Finding 1 is fixed.\nRESOLUTION: fixed, except finding 2 which I judge disputed", "Disputed")]
    [InlineData("Done, all good.", "")]
    public void Fix_resolutions_parse_with_disputes_taking_precedence(string summary, string expected) =>
        ReviewResultParser.ParseFixOutcome(summary).Value.Should().Be(expected);

    /// <summary>
    /// The block a changes-requested fix lap parks with (task: a changes-requested pull-request
    /// review from a human becomes a fix lap) — three positions and the two tags that say where a
    /// reply would go and which review it answers.
    /// </summary>
    [Fact]
    public void A_disagreement_block_parses_its_three_positions_and_its_tags()
    {
        string summary = """
            Fixed the two findings I agreed with and replied in their threads.

            DISAGREEMENT: at=src/Limiter.cs:42; thread=PRRT_abc; review=https://x/y/pull/7#r1
            REVIEWER ASKED: reset the limiter on every request rather than per window.
            MY REASONING: per-window is the documented contract (PLAN.md 12.3), and
            per-request would let a single client starve the pool.
            PROPOSED REPLY:
            Good catch on the naming — the reset really is per window, deliberately:
            PLAN.md 12.3 sets the contract. Happy to rename it if that reads better.

            RESOLUTION: disputed

            HANDOFF:
            nothing surprising here
            """;

        ReviewDisagreement disagreement =
            ReviewResultParser.ParseDisagreements(summary).Should().ContainSingle().Subject;

        disagreement.Location.Should().Be("src/Limiter.cs:42");
        disagreement.ThreadId.Should().Be("PRRT_abc");
        disagreement.ReviewUrl.Should().Be("https://x/y/pull/7#r1");
        disagreement.Finding.Should().Be("reset the limiter on every request rather than per window.");
        disagreement.Reasoning.Should().StartWith("per-window is the documented contract");
        disagreement.Reasoning.Should().Contain("starve the pool");
        disagreement.ProposedReply.Should().StartWith("Good catch on the naming");
        disagreement.ProposedReply.Should().NotContain(
            "RESOLUTION", "the marker line ends the block rather than becoming part of the reply");
        disagreement.ProposedReply.Should().NotContain(
            "HANDOFF", "the handoff is for the next task, not for the reviewer");
    }

    /// <summary>
    /// A body with no sub-markers is read whole as the session's reasoning rather than dropped: it
    /// plainly said something, and the only thing unrecoverable is which half of it was which —
    /// which the human resolving the park can see for themselves.
    /// </summary>
    [Fact]
    public void A_disagreement_with_no_sub_markers_keeps_its_prose_as_the_reasoning()
    {
        ReviewDisagreement disagreement = ReviewResultParser.ParseDisagreements(
            "DISAGREEMENT: at=src/A.cs:1\nI think the existing behaviour is right.\nRESOLUTION: disputed")
            .Should().ContainSingle().Subject;

        disagreement.Reasoning.Should().Be("I think the existing behaviour is right.");
        disagreement.Finding.Should().BeEmpty("nothing said what the reviewer asked for; it is not invented");
        disagreement.ProposedReply.Should().BeEmpty("no reply was drafted, so there is none to post as written");
    }

    /// <summary>A review body disputed has no thread and no location — both absent rather than filled in.</summary>
    [Fact]
    public void A_disagreement_naming_no_thread_or_location_carries_neither()
    {
        ReviewDisagreement disagreement = ReviewResultParser.ParseDisagreements(
            "DISAGREEMENT: review=https://x/y/pull/7#r1\nMY REASONING: no.\nPROPOSED REPLY:\nRespectfully, no.")
            .Should().ContainSingle().Subject;

        disagreement.Location.Should().BeNull();
        disagreement.ThreadId.Should().BeNull();
        disagreement.ReviewUrl.Should().Be("https://x/y/pull/7#r1");
        disagreement.ProposedReply.Should().Be("Respectfully, no.");
    }

    /// <summary>
    /// Two blocks are two disagreements, each ending where the next begins — the parser stays
    /// general even though the prompt asks for one, so a lap that parks two is recorded honestly
    /// rather than collapsed.
    /// </summary>
    [Fact]
    public void Each_disagreement_block_ends_where_the_next_begins()
    {
        IReadOnlyList<ReviewDisagreement> disagreements = ReviewResultParser.ParseDisagreements(
            """
            DISAGREEMENT: at=src/A.cs:1; thread=t1
            PROPOSED REPLY:
            first reply
            DISAGREEMENT: at=src/B.cs:2; thread=t2
            PROPOSED REPLY:
            second reply
            RESOLUTION: disputed
            """);

        disagreements.Should().HaveCount(2);
        disagreements[0].ProposedReply.Should().Be("first reply");
        disagreements[1].ProposedReply.Should().Be("second reply");
    }

    /// <summary>
    /// No block is not "the session agreed with everything" — the RESOLUTION marker is what says a
    /// disagreement exists, and the park is recorded on that whether or not a block parses.
    /// </summary>
    [Fact]
    public void A_summary_with_no_disagreement_block_parses_to_none()
    {
        ReviewResultParser.ParseDisagreements("Fixed everything.\nRESOLUTION: disputed").Should().BeEmpty();
        ReviewResultParser.ParseDisagreements(null).Should().BeEmpty();
    }
}
