using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The one standing question every review carries (idea b9b09779, piece 1): did this change alter
/// how the application runs locally? Here: reading the answer, reading the finding a yes comes
/// with, and the rule that a drift finding routes exactly like any other finding of its grade and
/// scope.
/// <para>
/// That the question actually reaches every review prompt is proved by
/// <see cref="AgentPromptBuilderGoldenTests"/>'s own byte-for-byte fixtures plus its
/// drift-coverage test, not here — this file is the parser and the routing.
/// </para>
/// </summary>
public sealed class RunSkillDriftQuestionTests
{
    [Fact]
    public void A_no_is_read_as_a_real_answer_rather_than_as_silence() =>
        ReviewResultParser.ParseRunSkillDrift("Nothing found.\n\nRUN-SKILL DRIFT: no\n\nVERDICT: merge-ready")
            .Should().Be(RunSkillDriftAnswer.No);

    [Fact]
    public void A_yes_is_read_as_a_yes() =>
        ReviewResultParser.ParseRunSkillDrift("RUN-SKILL DRIFT: yes\n\nVERDICT: needs-fixes")
            .Should().Be(RunSkillDriftAnswer.Yes);

    /// <summary>
    /// "Nobody answered" and "answered no" are different facts, and only the second is evidence
    /// the question was considered. A report printing the second when only the first happened
    /// would be asserting an observation nobody made.
    /// </summary>
    [Fact]
    public void A_pass_that_never_answered_is_unstated_rather_than_a_no()
    {
        ReviewResultParser.ParseRunSkillDrift("Nothing found.\n\nVERDICT: merge-ready")
            .Should().Be(RunSkillDriftAnswer.Unstated);
        RunSkillDriftAnswer.Unstated.HasValue.Should().BeFalse();
    }

    /// <summary>
    /// Last marker wins, exactly as VERDICT does and for the same observed reason: a pass that
    /// quotes its instructions before answering writes the marker twice.
    /// </summary>
    [Fact]
    public void The_last_answer_wins_so_a_quoted_instruction_never_stands_as_the_answer() =>
        ReviewResultParser.ParseRunSkillDrift(
            "The contract says to end with RUN-SKILL DRIFT: no\n\nRUN-SKILL DRIFT: yes\n\nVERDICT: needs-fixes")
            .Should().Be(RunSkillDriftAnswer.Yes);

    [Fact]
    public void A_drift_finding_is_read_off_its_own_kind_tag()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=medium; scope=in-scope; kind=run-skill-drift; at=docs/running.md:12\n"
            + "Defect: the app now needs REDIS_URL set before it will start.\n"
            + "Scenario: following today's instructions gets a connection refused at boot.\n"
            + "\nRUN-SKILL DRIFT: yes\n\nVERDICT: needs-fixes");

        findings.Should().HaveCount(1);
        findings[0].Kind.Should().Be(ReviewFindingKind.RunSkillDrift);
        findings[0].IsRunSkillDrift.Should().BeTrue();
        findings[0].Text.Should().NotContain(ReviewResultParser.RunSkillDriftMarker);
    }

    /// <summary>
    /// The contract asks for the drift line as the last thing before the verdict, which puts it
    /// directly after the last finding block. A finding's text is carried whole into a routed
    /// draft bug task and compared by exact equality across sweep cycles, so the marker must
    /// close the block rather than ride along inside it.
    /// </summary>
    [Fact]
    public void The_answer_closes_the_last_finding_rather_than_being_absorbed_into_its_text()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=medium; scope=out-of-scope; at=src/Old.cs:10\n"
            + "Defect: the retry loop never resets its backoff.\n"
            + "Scenario: a second failure waits the first one's delay again.\n"
            + "RUN-SKILL DRIFT: no\n"
            + "VERDICT: needs-fixes");

        findings.Should().HaveCount(1);
        findings[0].Text.Should().EndWith("Scenario: a second failure waits the first one's delay again.");
    }

    /// <summary>
    /// Unlike the verdict, the drift marker closes the open block without ending the parse: a
    /// pass that quotes its instructions before answering writes the marker early — the same
    /// observed habit the last-marker-wins rule above tolerates — and treating that as a
    /// terminator would drop every finding it actually reported.
    /// </summary>
    [Fact]
    public void An_answer_written_early_does_not_swallow_the_findings_after_it()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "The contract says to end with RUN-SKILL DRIFT: no\n\n"
            + "FINDING: severity=high; scope=in-scope; at=src/Limiter.cs:42\n"
            + "Defect: the retry loop never resets its backoff.\n\n"
            + "RUN-SKILL DRIFT: no\n\nVERDICT: needs-fixes");

        findings.Should().ContainSingle().Which.Location.Should().Be("src/Limiter.cs:42");
    }

    [Fact]
    public void An_ordinary_finding_carries_no_kind()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=high; scope=in-scope; at=src/Limiter.cs:42\n"
            + "Defect: the retry loop never resets its backoff.\n\nVERDICT: needs-fixes");

        findings.Should().HaveCount(1);
        findings[0].Kind.Should().Be(ReviewFindingKind.Unknown);
        findings[0].IsRunSkillDrift.Should().BeFalse();
    }

    /// <summary>
    /// "Routed like any finding": the kind never overrides severity and scope. The same three
    /// dispositions an ordinary finding of each grade and scope gets, a drift finding gets too.
    /// </summary>
    [Theory]
    [InlineData("out-of-scope", "medium", nameof(ReviewFindingDisposition.Route))]
    [InlineData("out-of-scope", "high", nameof(ReviewFindingDisposition.Fix))]
    [InlineData("in-scope", "medium", nameof(ReviewFindingDisposition.Fix))]
    [InlineData("in-scope", "low", nameof(ReviewFindingDisposition.RideAlong))]
    public void A_drift_finding_routes_on_exactly_its_grade_and_scope(string scope, string severity, string expected)
    {
        ReviewFinding drift = new(
            ReviewSeverity.Parse(severity), ReviewFindingScope.Parse(scope), "docs/running.md:12",
            "FINDING: the run instructions are stale.", Track: null, Kind: ReviewFindingKind.RunSkillDrift);
        ReviewFinding ordinary = drift with { Kind = ReviewFindingKind.Unknown };

        drift.Disposition(ReviewMode.Discovery).Value.Should().Be(expected);
        drift.Disposition(ReviewMode.Discovery).Should().Be(
            ordinary.Disposition(ReviewMode.Discovery),
            "the kind says what sort of finding it is, never what the platform does with it");
    }

    /// <summary>
    /// The kind reaches the run stream on the finding's own record, so the review history can
    /// answer how often a change altered how the application runs locally.
    /// </summary>
    [Fact]
    public void The_kind_is_carried_onto_the_streams_own_record()
    {
        ReviewFinding drift = new(
            ReviewSeverity.Medium, ReviewFindingScope.InScope, "docs/running.md:12", "text",
            Track: null, Kind: ReviewFindingKind.RunSkillDrift);

        drift.ToRecord(ReviewMode.Discovery).Kind.Should().Be(ReviewFindingKind.RunSkillDrift);
    }

    /// <summary>
    /// A record written before kinds existed replays with none rather than with a guess.
    /// </summary>
    [Fact]
    public void A_record_written_before_kinds_existed_carries_none() =>
        new ReviewFindingRecord(
            ReviewSeverity.High, ReviewFindingScope.InScope, "src/A.cs:1", ReviewFindingDisposition.Fix)
            .Kind.Should().BeNull();
}
