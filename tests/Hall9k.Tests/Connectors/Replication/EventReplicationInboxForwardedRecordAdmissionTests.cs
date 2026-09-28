using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Idea 6be68ee2, trust-ledger findings 4 and 7:
/// <see cref="EventReplicationInbox.IsForwardedRecordAdmitted"/> is pure, DB-free logic — a
/// teammate can no longer speak as another node unsolicited. A replicated record whose own claimed
/// origin differs from the node that actually delivered it is admitted only inside an answer to a
/// catch-up request THIS node minted, and only from a sender that specific request entitles to
/// answer.
/// </summary>
public sealed class EventReplicationInboxForwardedRecordAdmissionTests
{
    private static readonly Guid OriginNodeId = DomainId.New();
    private static readonly Guid SenderNodeId = DomainId.New();
    private static readonly Guid ProjectId = DomainId.New();

    [Fact]
    public void A_records_own_native_sender_is_always_admitted_whatever_the_chain_says()
    {
        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            originNodeId: SenderNodeId, SenderNodeId, matchedRequest: null, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeTrue("a sender forwarding its own native-origin record needs no catch-up entitlement at all");
    }

    [Fact]
    public void A_forwarded_record_inside_a_legitimate_directed_answer_from_a_listed_candidate_is_admitted()
    {
        Guid otherCandidate = DomainId.New();
        EventCatchUpRequest cascadeAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForOriginNodeId = OriginNodeId,
            Candidates = [otherCandidate, SenderNodeId],
            CandidateIndex = 1,
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, cascadeAsk, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeTrue("the sender is one of this cascade's own ranked candidates");
    }

    [Fact]
    public void A_forwarded_record_outside_any_catch_up_answer_is_dropped()
    {
        // Also the shape a forged record with an inflated OriginSequence takes when it rides an
        // ordinary flush rather than an answer this node actually asked for — matchedRequest is
        // null either way, whether About named nothing this node minted or the envelope carried no
        // About at all (a pre-a56cf16e sender).
        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, matchedRequest: null, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeFalse("nothing this node minted names this sender as entitled to speak for another origin");
    }

    [Fact]
    public void A_directed_answer_from_a_node_not_in_candidates_is_dropped()
    {
        Guid rankedCandidate = DomainId.New();
        EventCatchUpRequest cascadeAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForOriginNodeId = OriginNodeId,
            Candidates = [rankedCandidate],
            CandidateIndex = 0,
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, cascadeAsk, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeFalse("this sender was never one of the cascade's own ranked candidates");
    }

    [Fact]
    public void A_fleet_reconcile_answer_from_its_own_addressed_peer_is_admitted()
    {
        EventCatchUpRequest reconcileAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            SinceGlobalSequence = 0,
            ToNodeId = SenderNodeId,
            Candidates = [],
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, reconcileAsk, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeTrue("the sender is exactly the one peer this reconcile was addressed to");
    }

    [Fact]
    public void A_fleet_reconcile_answer_from_a_different_node_than_the_addressed_peer_is_dropped()
    {
        Guid addressedPeer = DomainId.New();
        EventCatchUpRequest reconcileAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            SinceGlobalSequence = 0,
            ToNodeId = addressedPeer,
            Candidates = [],
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, reconcileAsk, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeFalse("this reconcile was addressed to a different peer entirely");
    }

    [Fact]
    public void A_broadcast_answer_from_a_vouched_member_is_admitted()
    {
        const string root = "owner-root-fingerprint";
        const string senderFingerprint = "sender-node-fingerprint";
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new TrustedOwner(
                    root, "ssh-ed25519 AAAAFAKEROOT root",
                    [new TrustedNode(SenderNodeId.ToString(), "ssh-ed25519 AAAAFAKENODE node", senderFingerprint, DateTimeOffset.UtcNow)]),
            },
            [new ProjectMember(root, MembershipRole.Member, DateTimeOffset.UtcNow)]);
        EventCatchUpRequest broadcastPull = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForStreamId = DomainId.New(),
            Candidates = [],
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, broadcastPull, trustChain, senderFingerprint);

        admitted.Should().BeTrue("a broadcast names no single peer up front, so any vouched project member may answer it");
    }

    [Fact]
    public void A_broadcast_answer_from_an_unvouched_sender_is_dropped()
    {
        EventCatchUpRequest broadcastAdoption = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForStreamId = DomainId.New(),
            Candidates = [],
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, broadcastAdoption, TrustChain.Empty, senderFingerprint: "unvouched-fingerprint");

        admitted.Should().BeFalse("nothing in this project's own trust chain vouches this sender at all");
    }

    [Fact]
    public void A_later_batch_after_the_request_already_closed_is_still_admitted()
    {
        // A split answer's later batches arrive after the first batch already answered, superseded,
        // or exhausted the request — this rule is checked against the matched request regardless of
        // its own outstanding state.
        EventCatchUpRequest closedCascadeAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForOriginNodeId = OriginNodeId,
            Candidates = [SenderNodeId],
            CandidateIndex = 0,
            AnsweredAt = DateTimeOffset.UtcNow,
        };

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            OriginNodeId, SenderNodeId, closedCascadeAsk, TrustChain.Empty, senderFingerprint: null);

        admitted.Should().BeTrue("a request already answered, superseded, or exhausted still names who was entitled to answer it");
    }
}
