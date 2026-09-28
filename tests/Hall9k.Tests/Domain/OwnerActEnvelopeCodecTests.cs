using FluentAssertions;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The wire shapes for the owner-act envelope pair (idea 6be68ee2, companion 1bb803e1) — pure,
/// database- and transport-free round trips, the same shape <c>ClaimEnvelopeCodecTests</c> would
/// take for the identical claim-request pair.
/// </summary>
public sealed class OwnerActEnvelopeCodecTests
{
    private static readonly Guid InviteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RequesterNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string CandidateOwnerFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset IssuedAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestRecord_RoundTrips()
    {
        OwnerActEnvelopeCodec.OwnerActRequestRecord record = new(
            InviteId, RequesterNodeId, CandidateOwnerFingerprint, ProjectMemberRole.Member, IssuedAt);

        string body = OwnerActEnvelopeCodec.Encode(record);
        OwnerActEnvelopeCodec.OwnerActRequestRecord? decoded = OwnerActEnvelopeCodec.TryDecodeRequest(body);

        decoded.Should().Be(record);
    }

    [Fact]
    public void RequestRecord_RoundTripsTheOwnerRoleToo()
    {
        OwnerActEnvelopeCodec.OwnerActRequestRecord record = new(
            InviteId, RequesterNodeId, CandidateOwnerFingerprint, ProjectMemberRole.Owner, IssuedAt);

        OwnerActEnvelopeCodec.OwnerActRequestRecord? decoded =
            OwnerActEnvelopeCodec.TryDecodeRequest(OwnerActEnvelopeCodec.Encode(record));

        decoded!.Role.Should().Be(ProjectMemberRole.Owner);
    }

    [Fact]
    public void RequestRecord_ADifferentIssuedAtProducesDifferentContent()
    {
        OwnerActEnvelopeCodec.OwnerActRequestRecord first = new(
            InviteId, RequesterNodeId, CandidateOwnerFingerprint, ProjectMemberRole.Member, IssuedAt);
        OwnerActEnvelopeCodec.OwnerActRequestRecord second = first with { IssuedAt = IssuedAt.AddMinutes(1) };

        OwnerActEnvelopeCodec.Encode(first).Should().NotBe(OwnerActEnvelopeCodec.Encode(second),
            "issued_at is what a resend must reproduce byte-identically to make no new commit");
    }

    [Fact]
    public void RequestRecord_TheIdenticalRequestReproducesByteIdenticalContent()
    {
        OwnerActEnvelopeCodec.OwnerActRequestRecord first = new(
            InviteId, RequesterNodeId, CandidateOwnerFingerprint, ProjectMemberRole.Member, IssuedAt);
        OwnerActEnvelopeCodec.OwnerActRequestRecord resend = new(
            InviteId, RequesterNodeId, CandidateOwnerFingerprint, ProjectMemberRole.Member, IssuedAt);

        OwnerActEnvelopeCodec.Encode(first).Should().Be(OwnerActEnvelopeCodec.Encode(resend));
    }

    [Fact]
    public void RequestRecord_MalformedBodyDecodesToNull()
    {
        OwnerActEnvelopeCodec.TryDecodeRequest("not json").Should().BeNull();
    }

    [Theory]
    [InlineData(OwnerActEnvelopeCodec.OwnerActVerdict.Done)]
    [InlineData(OwnerActEnvelopeCodec.OwnerActVerdict.Held)]
    [InlineData(OwnerActEnvelopeCodec.OwnerActVerdict.Refused)]
    [InlineData(OwnerActEnvelopeCodec.OwnerActVerdict.Expired)]
    public void OutcomeRecord_RoundTripsEveryVerdict(string verdict)
    {
        OwnerActEnvelopeCodec.OwnerActOutcomeRecord record = new(InviteId, verdict, CommitId: "abc123", Reason: "because");

        OwnerActEnvelopeCodec.OwnerActOutcomeRecord? decoded =
            OwnerActEnvelopeCodec.TryDecodeOutcome(OwnerActEnvelopeCodec.Encode(record));

        decoded.Should().Be(record);
    }

    [Fact]
    public void OutcomeRecord_MalformedBodyDecodesToNull()
    {
        OwnerActEnvelopeCodec.TryDecodeOutcome("not json").Should().BeNull();
    }
}
