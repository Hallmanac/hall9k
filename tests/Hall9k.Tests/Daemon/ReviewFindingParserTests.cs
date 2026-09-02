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
    /// Two readings that no longer agree the way they once did (Decisions Log #87): an ungraded
    /// finding still forces the adversarial track's own multi-cycle gate once it applies (it has
    /// not been shown safe to wave through that one), but it no longer earns a fix session of
    /// its own this cycle — the platform cannot tell a lazy omission from genuine polish once
    /// both lenses are told to grade everything, so it rides along exactly as a stated Low would.
    /// </summary>
    [Fact]
    public void An_untagged_finding_rides_along_but_still_forces_the_adversarial_gate()
    {
        ReviewFinding finding = ReviewResultParser.ParseFindings("FINDING: at=A.cs:1\nDefect: something.")
            .Should().ContainSingle().Subject;

        finding.Disposition(ReviewMode.Discovery).Should().Be(ReviewFindingDisposition.RideAlong);
        finding.Severity.ForcesAnotherCycle.Should().BeTrue();
    }

    /// <summary>An out-of-scope high is cleanup-as-you-touch: fixed here, not routed away.</summary>
    [Fact]
    public void An_out_of_scope_high_is_fixed_here_while_an_out_of_scope_low_routes()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=high; scope=out-of-scope; at=Old.cs:1\nDefect: unbounded read.\n" +
            "FINDING: severity=low; scope=out-of-scope; at=Old.cs:2\nDefect: a typo.");

        findings[0].Disposition(ReviewMode.Discovery).Should().Be(ReviewFindingDisposition.Fix);
        findings[1].Disposition(ReviewMode.Discovery).Should().Be(ReviewFindingDisposition.Route);
    }

    /// <summary>
    /// A Verify pass's finding carries an extra tag naming which track it belongs to (task:
    /// review cycles after the first): recognized, it is the real lens; unrecognized or absent,
    /// it stays null — the same conservative "applies to every active track" reading an ungraded
    /// severity or an untagged scope already gets, never guessed at.
    /// </summary>
    [Theory]
    [InlineData("conformance", "Conformance")]
    [InlineData("adversarial", "Adversarial")]
    [InlineData("Adversarial", "Adversarial")]
    public void A_recognized_track_tag_parses_to_its_real_lens(string tag, string expectedLens)
    {
        ReviewFinding finding = ReviewResultParser.ParseFindings(
            $"FINDING: severity=high; scope=in-scope; track={tag}; at=A.cs:1\nDefect: something.")
            .Should().ContainSingle().Subject;

        finding.Track.Should().Be((ReviewLens)expectedLens);
    }

    [Theory]
    [InlineData("FINDING: severity=high; scope=in-scope; at=A.cs:1\nDefect: something.")]
    [InlineData("FINDING: severity=high; scope=in-scope; track=both; at=A.cs:1\nDefect: something.")]
    public void An_absent_or_unrecognized_track_tag_stays_null(string summary)
    {
        ReviewFinding finding = ReviewResultParser.ParseFindings(summary).Should().ContainSingle().Subject;
        finding.Track.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1. `A.cs:1` — broken. Scenario: boom.\n\nVERDICT: needs-fixes")]
    public void Prose_without_headers_reads_as_no_readable_findings(string? summary) =>
        ReviewResultParser.ParseFindings(summary).Should().BeEmpty(
            "that is 'nothing this parser can read', and only the caller knows whether the verdict said there were any");

    /// <summary>
    /// A pass that quotes the finding contract's own worked example before answering — the same
    /// habit already tolerated for VERDICT lines — must not have that quoted example counted as
    /// a real finding. Unlike a verdict, a finding block has no "last one wins" rule, so an
    /// echoed example would otherwise stand alongside the pass's actual finding.
    /// </summary>
    [Fact]
    public void An_echoed_example_header_is_not_read_as_a_real_finding()
    {
        string summary =
            "The contract says to open each finding like this:\n" +
            "    FINDING: severity=high; scope=in-scope; at=src/Some/File.cs:123\n" +
            "    Defect: one sentence saying what is wrong.\n" +
            "    Scenario: the input or state that makes it misbehave, and what goes wrong.\n\n" +
            "FINDING: severity=high; scope=in-scope; at=src/Auth.cs:42\n" +
            "Defect: the limiter never resets.\n" +
            "Scenario: the second request always 429s.\n\n" +
            "VERDICT: needs-fixes";

        ReviewFinding finding = ReviewResultParser.ParseFindings(summary).Should().ContainSingle().Subject;
        finding.Location.Should().Be("src/Auth.cs:42");
    }

    /// <summary>
    /// The placeholder screen matches on path, not the full literal (cycle-3 adversarial
    /// finding): a pass that drops or adapts the example's line number while echoing it, or
    /// echoes the mechanics bullet's own <c>path/to/file.cs</c> placeholder instead, still points
    /// at a path no repository has, and an exact-literal comparison against
    /// <see cref="ReviewResultParser.ExampleLocationPlaceholder"/> alone let those through as a
    /// fabricated finding.
    /// </summary>
    [Theory]
    [InlineData("src/Some/File.cs:45")]
    [InlineData("src/Some/File.cs")]
    [InlineData("path/to/file.cs:123")]
    [InlineData("path/to/file.cs")]
    public void A_placeholder_path_is_screened_even_with_an_adapted_line_number_or_no_line_at_all(string location)
    {
        string summary =
            $"FINDING: severity=high; scope=in-scope; at={location}\n" +
            "Defect: one sentence saying what is wrong.\n" +
            "Scenario: the input or state that makes it misbehave, and what goes wrong.\n\n" +
            "VERDICT: merge-ready";

        ReviewResultParser.ParseFindings(summary).Should().BeEmpty();
    }
}
