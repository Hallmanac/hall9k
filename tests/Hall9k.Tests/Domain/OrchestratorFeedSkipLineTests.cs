using FluentAssertions;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Trust;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>The orchestrator feed's sentence for a manual override, pinned as a golden.</summary>
public sealed class OrchestratorFeedSkipLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid RunId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");
    private const string Fingerprint = "owner-a-fingerprint-0123456789";
    private static readonly string[] Skipped = [ReviewSkipPlan.Gates, ReviewSkipPlan.SettlingGate];

    private static MemberLabelLookup Labels() => new(new ProjectMemberLabels
    {
        Id = DomainId.New(),
        Labels = [new ProjectMemberLabel(Fingerprint, [], DisplayName.Parse("Brian"), null)],
    });

    [Fact]
    public void An_override_names_who_what_and_the_humans_own_reason()
    {
        string? line = OrchestratorFeedDescription.Of(
            new ReviewSkippedToPullRequest(RunId, DomainId.New(), Now, "docs only", false, Skipped, Fingerprint), Labels());

        line.Should().Be(
            "Brian skipped the rest of review and sent the run straight to its pull request, bypassing "
            + "the verification gates, the settling gate; reason: docs only");
    }

    [Fact]
    public void An_agent_written_reason_is_marked_as_the_driving_sessions()
    {
        string? line = OrchestratorFeedDescription.Of(
            new ReviewSkippedToPullRequest(RunId, DomainId.New(), Now, "You said it was a known flake", true, Skipped, Fingerprint),
            Labels());

        line.Should().Contain("reason (written by the driving session): You said it was a known flake");
    }

    [Fact]
    public void No_reason_reads_no_reason_given_and_never_the_unrecorded_wording()
    {
        string? line = OrchestratorFeedDescription.Of(
            new ReviewSkippedToPullRequest(RunId, DomainId.New(), Now, null, false, Skipped, Fingerprint), Labels());

        line.Should().EndWith("; no reason given").And.NotContain("no reason recorded");
    }

    [Fact]
    public void The_event_sits_in_the_everything_band_with_the_other_review_movement()
    {
        OrchestratorFeedInterest.BandOf(typeof(ReviewSkippedToPullRequest)).Should().Be(OrchestratorFeedLevel.Everything);
    }
}
