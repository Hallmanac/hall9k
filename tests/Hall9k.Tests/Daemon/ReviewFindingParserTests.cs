using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Reading the adversarial pass's structured findings (Decisions Log #63). Every declared tag
/// is the reviewer's own observation, and every tag the parser cannot read stays Unknown — the
/// gate decides what an ungraded finding costs, never this parser.
/// </summary>
public sealed class ReviewFindingParserTests
{
    [Fact]
    public void A_finding_block_carries_its_grade_scope_pointer_and_text()
    {
        const string summary =
            "I read the diff and one thing stands.\n\n" +
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n" +
            "Defect: the limiter never resets.\n" +
            "Scenario: the second request always 429s.\n\n" +
            "VERDICT: needs-fixes";

        ReviewFinding finding = ReviewResultParser.ParseFindings(summary).Should().ContainSingle().Subject;
        finding.Severity.Should().Be(ReviewSeverity.High);
        finding.Scope.Should().Be(ReviewFindingScope.InScope);
        finding.Location.Should().Be("src/Auth.cs:42");
        finding.Text.Should().Contain("the limiter never resets").And.Contain("always 429s")
            .And.NotContain("VERDICT", "the verdict line closes the last block rather than joining it");
    }

    [Fact]
    public void Several_findings_split_at_their_headers_and_keep_their_order()
    {
        const string summary =
            "FINDING: severity=medium, scope=out-of-scope, at=Legacy.cs:9\nDefect: a stale comment.\n" +
            "FINDING: severity=low; scope=in-scope; at=New.cs:3\nDefect: a name that reads badly.\n" +
            "VERDICT: needs-fixes";

        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(summary);

        findings.Select(finding => (finding.Severity, finding.Scope, finding.Location)).Should().Equal(
        [
            (ReviewSeverity.Medium, ReviewFindingScope.OutOfScope, "Legacy.cs:9"),
            (ReviewSeverity.Low, ReviewFindingScope.InScope, "New.cs:3"),
        ], "commas and semicolons are both what agents actually write, and a file:line contains neither");
        findings[0].Text.Should().Contain("a stale comment").And.NotContain("reads badly");
    }

    [Theory]
    [InlineData("FINDING: at=A.cs:1\nDefect: something.")]
    [InlineData("FINDING: severity=critical; scope=partly; at=A.cs:1\nDefect: something.")]
    public void An_absent_or_unrecognized_tag_stays_unknown_rather_than_being_guessed(string summary)
    {
        ReviewFinding finding = ReviewResultParser.ParseFindings(summary).Should().ContainSingle().Subject;
        finding.Severity.Should().Be(ReviewSeverity.Unknown);
        finding.Scope.Should().Be(ReviewFindingScope.Unknown);
    }

    /// <summary>
    /// The two conservative readings, stated as behaviour rather than left implicit: an ungraded
    /// finding still forces another cycle (it has not been shown safe to wave through), and an
    /// untagged one is fixed here rather than routed away (routing is the irreversible half).
    /// </summary>
    [Fact]
    public void An_untagged_finding_is_fixed_here_and_still_forces_a_cycle()
    {
        ReviewFinding finding = ReviewResultParser.ParseFindings("FINDING: at=A.cs:1\nDefect: something.")
            .Should().ContainSingle().Subject;

        finding.IsFixedHere.Should().BeTrue();
        finding.Disposition.Should().Be(ReviewFindingDisposition.Fix);
        finding.Severity.ForcesAnotherCycle.Should().BeTrue();
    }

    /// <summary>An out-of-scope high is cleanup-as-you-touch: fixed here, not routed away.</summary>
    [Fact]
    public void An_out_of_scope_high_is_fixed_here_while_an_out_of_scope_low_routes()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=high; scope=out-of-scope; at=Old.cs:1\nDefect: unbounded read.\n" +
            "FINDING: severity=low; scope=out-of-scope; at=Old.cs:2\nDefect: a typo.");

        findings[0].Disposition.Should().Be(ReviewFindingDisposition.Fix);
        findings[1].Disposition.Should().Be(ReviewFindingDisposition.Route);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1. `A.cs:1` — broken. Scenario: boom.\n\nVERDICT: needs-fixes")]
    public void Prose_without_headers_reads_as_no_readable_findings(string? summary) =>
        ReviewResultParser.ParseFindings(summary).Should().BeEmpty(
            "that is 'nothing this parser can read', and only the caller knows whether the verdict said there were any");
}
