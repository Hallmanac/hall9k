using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="StackAssessmentResultParser"/>'s trailer contract, string-in verdict-out with no
/// I/O — the same style <see cref="ReviewVerdictValidationTests"/> already keeps for
/// <c>ReviewVerdictValidation</c>. A missing or malformed trailer is undecidable by design; these
/// tests prove both halves of that promise: a well-formed trailer parses into exactly what it
/// named, and every way a trailer can go wrong collapses to the same honest "could not read this."
/// </summary>
public sealed class StackAssessmentResultParserTests
{
    private const string Boundary = "1111111111111111111111111111111111aaaa";
    private const string Onto = "2222222222222222222222222222222222bbbb";

    [Fact]
    public void A_well_formed_aligned_trailer_parses()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            "Investigated the boundary and found this branch already current.\n\n"
            + $"STACK ASSESSMENT VERDICT: aligned\nBOUNDARY: {Boundary}\nONTO: {Onto}\n"
            + "EVIDENCE:\ngit merge-base --is-ancestor confirmed containment.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Aligned);
        verdict.BoundaryCommit.Should().Be(Boundary);
        verdict.OntoCommit.Should().Be(Onto);
        verdict.Evidence.Should().Contain("git merge-base --is-ancestor confirmed containment.");
    }

    [Fact]
    public void A_well_formed_replay_trailer_parses()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: replay\nBOUNDARY: {Boundary}\nONTO: {Onto}\n"
            + "EVIDENCE:\nThe parent's pull request retargeted onto main; replaying from the recorded fork point lands cleanly there.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Replay);
        verdict.BoundaryCommit.Should().Be(Boundary);
        verdict.OntoCommit.Should().Be(Onto);
        verdict.Evidence.Should().Contain("retargeted onto main");
    }

    [Fact]
    public void A_well_formed_undecidable_trailer_parses_with_its_own_evidence()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            "STACK ASSESSMENT VERDICT: undecidable\nBOUNDARY: none\nONTO: none\n"
            + "EVIDENCE:\nTwo candidate boundaries disagree and the history was rewritten between them.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.BoundaryCommit.Should().BeEmpty();
        verdict.OntoCommit.Should().BeEmpty();
        verdict.Evidence.Should().Contain("Two candidate boundaries disagree");
    }

    [Fact]
    public void A_missing_summary_is_undecidable()
    {
        StackAssessmentResultParser.Parse(null).Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        StackAssessmentResultParser.Parse(string.Empty).Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        StackAssessmentResultParser.Parse("   ").Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
    }

    [Fact]
    public void A_summary_with_no_verdict_line_at_all_is_undecidable()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            "Looked at the branch, nothing conclusive to report.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.Evidence.Should().Contain("carried no STACK ASSESSMENT VERDICT");
    }

    [Fact]
    public void A_replay_verdict_missing_its_boundary_is_undecidable()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: replay\nONTO: {Onto}\nEVIDENCE:\nSome evidence.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.Evidence.Should().Contain("malformed");
        verdict.Evidence.Should().Contain("Some evidence.", "evidence the trailer did carry is preserved, not discarded");
    }

    [Fact]
    public void A_replay_verdict_missing_its_onto_is_undecidable()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: replay\nBOUNDARY: {Boundary}\nEVIDENCE:\nSome evidence.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
    }

    [Fact]
    public void An_aligned_verdict_missing_its_evidence_block_is_undecidable()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: aligned\nBOUNDARY: {Boundary}\nONTO: {Onto}");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.Evidence.Should().Contain("malformed");
    }

    /// <summary>
    /// The template shows <c>BOUNDARY: none</c> only as the value for an undecidable verdict — an
    /// aligned or replay verdict that names it anyway (or any other non-SHA value: a branch name,
    /// a short mnemonic, an option-looking string) is not trusted as a commit, the same as a
    /// missing BOUNDARY: line (independent pre-PR review, cycle 1, conformance and adversarial
    /// lenses: an unverified, non-SHA value reaching `git rebase --onto` as a bare revision
    /// argument is exactly the shape this guards against).
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("main")]
    [InlineData("-x")]
    [InlineData("--upload-pack=evil")]
    [InlineData("not-a-sha")]
    public void A_replay_verdict_with_a_non_sha_boundary_is_undecidable(string nonShaBoundary)
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: replay\nBOUNDARY: {nonShaBoundary}\nONTO: {Onto}\nEVIDENCE:\nSome evidence.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.Evidence.Should().Contain("malformed");
    }

    [Fact]
    public void An_aligned_verdict_with_a_non_sha_onto_is_undecidable()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: aligned\nBOUNDARY: {Boundary}\nONTO: none\nEVIDENCE:\nSome evidence.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.Evidence.Should().Contain("malformed");
    }

    [Fact]
    public void An_unrecognized_verdict_value_is_undecidable()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            "STACK ASSESSMENT VERDICT: maybe\nEVIDENCE:\nNot sure.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Undecidable);
        verdict.Evidence.Should().Contain("unrecognized verdict");
    }

    [Fact]
    public void The_last_verdict_line_wins_when_the_summary_quotes_the_contract_before_answering()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            "Your trailer should read: STACK ASSESSMENT VERDICT: aligned | replay | undecidable\n\n"
            + $"STACK ASSESSMENT VERDICT: replay\nBOUNDARY: {Boundary}\nONTO: {Onto}\nEVIDENCE:\nReal evidence.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Replay);
        verdict.BoundaryCommit.Should().Be(Boundary);
    }

    [Fact]
    public void The_marker_match_is_case_insensitive()
    {
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"stack assessment verdict: aligned\nboundary: {Boundary}\nonto: {Onto}\nevidence:\nChecked.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Aligned);
        verdict.BoundaryCommit.Should().Be(Boundary);
    }

    [Fact]
    public void A_marker_shaped_line_inside_the_evidence_block_never_overrides_the_real_trailer_value()
    {
        string rejectedOnto = "3333333333333333333333333333333333cccc";
        StackAssessmentVerdict verdict = StackAssessmentResultParser.Parse(
            $"STACK ASSESSMENT VERDICT: replay\nBOUNDARY: {Boundary}\nONTO: {Onto}\n"
            + $"EVIDENCE:\nChecked candidate boundaries. Onto: {rejectedOnto} was ruled out because it "
            + "belongs to an unrelated branch; the real target is the one named above.");

        verdict.Kind.Should().Be(StackAssessmentVerdictKind.Replay);
        verdict.OntoCommit.Should().Be(Onto);
    }
}
