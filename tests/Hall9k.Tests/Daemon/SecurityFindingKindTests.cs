using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The <c>security</c> finding kind (idea b2a6f46c), the pattern <see cref="RunSkillDriftQuestionTests"/>
/// set for the run-skill drift kind: reading the tag, recording it on the run stream, routing the
/// finding on its grade and scope alone, and naming it in the merged findings document.
/// </summary>
public sealed class SecurityFindingKindTests
{
    [Fact]
    public void A_security_finding_is_read_off_its_own_kind_tag()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=high; scope=in-scope; kind=security; at=src/Auth.cs:42\n"
            + "Defect: the new endpoint skips the ownership check.\n"
            + "Scenario: any signed-in caller reads another caller's record by id.\n"
            + "\nVERDICT: needs-fixes");

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(ReviewFindingKind.Security);
        findings[0].IsSecurity.Should().BeTrue();
        findings[0].IsRunSkillDrift.Should().BeFalse();
    }

    [Fact]
    public void An_unrecognized_kind_still_reads_as_an_ordinary_finding()
    {
        IReadOnlyList<ReviewFinding> findings = ReviewResultParser.ParseFindings(
            "FINDING: severity=high; scope=in-scope; kind=performance; at=src/Limiter.cs:42\n"
            + "Defect: the retry loop never resets its backoff.\n\nVERDICT: needs-fixes");

        findings.Should().ContainSingle();
        findings[0].Kind.Should().Be(ReviewFindingKind.Unknown);
        findings[0].IsSecurity.Should().BeFalse();
        findings[0].Location.Should().Be("src/Limiter.cs:42");
    }

    [Fact]
    public void A_security_record_survives_a_round_trip_through_the_streams_json()
    {
        ReviewFindingRecord record = new(
            ReviewSeverity.High, ReviewFindingScope.InScope, "src/Auth.cs:42", ReviewFindingDisposition.Fix,
            Track: null, Kind: ReviewFindingKind.Security);

        string json = JsonSerializer.Serialize(record);
        ReviewFindingRecord? read = JsonSerializer.Deserialize<ReviewFindingRecord>(json);

        json.Should().Contain("security");
        read.Should().NotBeNull();
        read!.Kind.Should().Be(ReviewFindingKind.Security);
        read.Should().Be(record);
    }

    /// <summary>A record written before kinds existed has no kind property at all, and replays with none.</summary>
    [Fact]
    public void An_old_payload_without_a_kind_still_replays_unchanged()
    {
        ReviewFindingRecord original = new(
            ReviewSeverity.High, ReviewFindingScope.InScope, "src/A.cs:1", ReviewFindingDisposition.Fix);
        JsonObject payload = JsonNode.Parse(JsonSerializer.Serialize(original))!.AsObject();
        payload.Remove("kind");
        payload.Remove("Kind");
        payload.Remove("track");
        payload.Remove("Track");

        ReviewFindingRecord? read = JsonSerializer.Deserialize<ReviewFindingRecord>(payload.ToJsonString());

        read.Should().NotBeNull();
        read!.Kind.Should().BeNull();
        read.Location.Should().Be("src/A.cs:1");
        read.Disposition.Should().Be(ReviewFindingDisposition.Fix);
    }

    [Fact]
    public void The_kind_is_carried_onto_the_streams_own_record()
    {
        ReviewFinding security = new(
            ReviewSeverity.High, ReviewFindingScope.InScope, "src/Auth.cs:42", "text",
            Track: null, Kind: ReviewFindingKind.Security);

        security.ToRecord(ReviewMode.Discovery).Kind.Should().Be(ReviewFindingKind.Security);
    }

    /// <summary>
    /// "Fixed, routed or carried exactly as an untagged finding of the same grade and scope would
    /// be": the kind says what sort of finding it is, never what the platform does with it.
    /// </summary>
    [Theory]
    [InlineData("out-of-scope", "medium", nameof(ReviewMode.Discovery), nameof(ReviewFindingDisposition.Route))]
    [InlineData("out-of-scope", "high", nameof(ReviewMode.Discovery), nameof(ReviewFindingDisposition.Fix))]
    [InlineData("in-scope", "medium", nameof(ReviewMode.Discovery), nameof(ReviewFindingDisposition.Fix))]
    [InlineData("in-scope", "low", nameof(ReviewMode.Discovery), nameof(ReviewFindingDisposition.RideAlong))]
    [InlineData("in-scope", "medium", nameof(ReviewMode.FinalFullPass), nameof(ReviewFindingDisposition.RideAlong))]
    [InlineData("in-scope", "high", nameof(ReviewMode.FinalFullPass), nameof(ReviewFindingDisposition.Fix))]
    public void A_security_finding_routes_on_exactly_its_grade_and_scope(
        string scope, string severity, string modeName, string expected)
    {
        ReviewMode mode = modeName == nameof(ReviewMode.Discovery) ? ReviewMode.Discovery : ReviewMode.FinalFullPass;
        ReviewFinding security = new(
            ReviewSeverity.Parse(severity), ReviewFindingScope.Parse(scope), "src/Auth.cs:42",
            "FINDING: the check is missing.", Track: null, Kind: ReviewFindingKind.Security);
        ReviewFinding ordinary = security with { Kind = ReviewFindingKind.Unknown };

        security.Disposition(mode).Value.Should().Be(expected);
        security.Disposition(mode).Should().Be(
            ordinary.Disposition(mode),
            "the kind says what sort of finding it is, never what the platform does with it");
    }

    [Fact]
    public void The_merged_findings_document_labels_a_security_finding_as_one()
    {
        ReviewFinding security = new(
            ReviewSeverity.High, ReviewFindingScope.InScope, "src/Auth.cs:42", "text",
            Track: null, Kind: ReviewFindingKind.Security);

        ReviewEngine.FindingLabel(ReviewLens.Adversarial, security).Should().Contain(", security");
        ReviewEngine.FindingLabel(ReviewLens.Adversarial, security with { Kind = ReviewFindingKind.Unknown })
            .Should().NotContain("security");
        ReviewEngine.FindingLabel(ReviewLens.Adversarial, security with { Kind = ReviewFindingKind.RunSkillDrift })
            .Should().Contain(", run-skill drift").And.NotContain("security");
    }
}
