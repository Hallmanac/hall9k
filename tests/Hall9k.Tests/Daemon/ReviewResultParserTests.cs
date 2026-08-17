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
}
