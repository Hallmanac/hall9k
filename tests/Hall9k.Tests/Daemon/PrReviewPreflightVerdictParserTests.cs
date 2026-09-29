using FluentAssertions;
using Hall9k.Daemon.Review;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="PrReviewPreflightVerdictParser"/> is a pure function over a session's own final
/// text — no store, no I/O — the same tier <see cref="ReviewResultParser"/>'s own parse methods
/// are tested at.
/// </summary>
public sealed class PrReviewPreflightVerdictParserTests
{
    [Fact]
    public void A_safe_marker_parses_as_safe_with_its_reason()
    {
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse(
            "Some narration.\nPREFLIGHT: safe - nothing here touches an executable surface.");

        verdict.Safe.Should().BeTrue();
        verdict.Verdict.Should().Be("safe");
        verdict.Reason.Should().Be("nothing here touches an executable surface.");
    }

    [Fact]
    public void An_unsafe_marker_parses_as_unsafe_with_its_reason()
    {
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse(
            "PREFLIGHT: unsafe - the workflow file grants secrets to a forked pull request.");

        verdict.Safe.Should().BeFalse();
        verdict.Verdict.Should().Be("unsafe");
        verdict.Reason.Should().Be("the workflow file grants secrets to a forked pull request.");
    }

    [Fact]
    public void A_bare_unsafe_marker_with_no_reason_still_parses_as_unsafe_not_safe()
    {
        // The hazard this parser exists to avoid: "unsafe" contains "safe" as a substring, so a
        // naive .Contains("safe") check would misread this as safe.
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse("PREFLIGHT: unsafe");

        verdict.Safe.Should().BeFalse("'unsafe' must never be read as containing a safe verdict");
        verdict.Verdict.Should().Be("unsafe");
    }

    [Fact]
    public void A_missing_marker_is_unparseable_and_unsafe()
    {
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse("Some narration with no marker at all.");

        verdict.Safe.Should().BeFalse();
    }

    [Fact]
    public void A_null_summary_is_unparseable_and_unsafe()
    {
        PrReviewPreflightVerdictParser.Parse(null).Safe.Should().BeFalse();
    }

    [Fact]
    public void A_malformed_marker_value_is_unparseable_and_unsafe()
    {
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse("PREFLIGHT: maybe - not sure honestly");

        verdict.Safe.Should().BeFalse();
        verdict.Reason.Should().Contain("unparseable");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, both lenses: the verdict token is the marker's own first
    /// word, never a split on the first '-' — a hedge that starts with "safe-" must not read as
    /// exactly "safe" just because a naive split on '-' would carve "safe" off the front of it.
    /// </summary>
    [Theory]
    [InlineData("PREFLIGHT: safe-ish - only a workflow comment changed")]
    [InlineData("PREFLIGHT: safe-but-unsure")]
    public void A_hedge_starting_with_safe_dash_is_unparseable_and_unsafe_not_safe(string summary)
    {
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse(summary);

        verdict.Safe.Should().BeFalse("the first word is 'safe-ish' or 'safe-but-unsure', neither of which is exactly 'safe'");
    }

    [Fact]
    public void The_last_marker_line_wins_when_more_than_one_is_present()
    {
        PrReviewPreflightVerdict verdict = PrReviewPreflightVerdictParser.Parse(
            "PREFLIGHT: unsafe - first guess\nmore narration\nPREFLIGHT: safe - final answer");

        verdict.Safe.Should().BeTrue();
        verdict.Reason.Should().Be("final answer");
    }
}
