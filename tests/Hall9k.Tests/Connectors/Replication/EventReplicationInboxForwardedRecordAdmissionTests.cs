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
/// catch-up request THIS node minted, only from a sender that specific request entitles to answer,
/// and only when the record itself falls inside what that request actually asked for (independent
/// pre-PR review, cycle 1, conformance lens, medium: a stale request id read once off this node's
/// own past outbox must never entitle an unrelated forged record on an unrelated stream or origin).
/// </summary>
public sealed class EventReplicationInboxForwardedRecordAdmissionTests
{
    private static readonly Guid OriginNodeId = DomainId.New();
    private static readonly Guid SenderNodeId = DomainId.New();
    private static readonly Guid ProjectId = DomainId.New();
    private static readonly Guid StreamId = DomainId.New();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static EventReplicationCodec.ReplicatedEventRecord ForeignRecord(
        Guid originNodeId, Guid? streamId = null, long originSequence = 1) => new(
        streamId ?? StreamId, "Some.EventType", "{}", DomainId.New(), originSequence, originNodeId,
        "origin-owner-fingerprint", Now, ProjectId);

    [Fact]
    public void A_records_own_native_sender_is_always_admitted_whatever_the_chain_says()
    {
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(SenderNodeId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, matchedRequest: null, TrustChain.Empty, senderFingerprint: null,
            requestedStreamIds: [], Now);

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
            SinceOriginSequence = 0,
            Candidates = [otherCandidate, SenderNodeId],
            CandidateIndex = 1,
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, originSequence: 1);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, cascadeAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

        admitted.Should().BeTrue("the sender is one of this cascade's own ranked candidates, and the record matches the origin and sequence it asked for");
    }

    [Fact]
    public void A_forwarded_record_outside_any_catch_up_answer_is_dropped()
    {
        // Also the shape a forged record with an inflated OriginSequence takes when it rides an
        // ordinary flush rather than an answer this node actually asked for — matchedRequest is
        // null either way, whether About named nothing this node minted or the envelope carried no
        // About at all (a pre-a56cf16e sender).
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, originSequence: long.MaxValue);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, matchedRequest: null, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

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
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, originSequence: 1);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, cascadeAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

        admitted.Should().BeFalse("this sender was never one of the cascade's own ranked candidates");
    }

    [Fact]
    public void A_gap_fill_answer_claiming_an_origin_other_than_the_one_asked_for_is_dropped()
    {
        // The cascade entitles SenderNodeId to answer, but only for OriginNodeId's own gap — a
        // record forwarded under some OTHER origin must never ride the same entitlement.
        Guid unrelatedOriginNodeId = DomainId.New();
        EventCatchUpRequest gapFillAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForOriginNodeId = OriginNodeId,
            SinceOriginSequence = 0,
            Candidates = [SenderNodeId],
            CandidateIndex = 0,
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(unrelatedOriginNodeId, originSequence: 1);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, gapFillAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

        admitted.Should().BeFalse("this gap-fill asked for a different origin's own missing history, never this one");
    }

    [Fact]
    public void A_gap_fill_answer_at_or_below_the_since_sequence_it_already_holds_is_dropped()
    {
        // The freezing attack's own shape: a huge OriginSequence forwarded under the gap-fill's own
        // entitled origin and sender must still be refused when it does not actually advance past
        // what this node already told the cascade it was missing from.
        EventCatchUpRequest gapFillAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForOriginNodeId = OriginNodeId,
            SinceOriginSequence = 10,
            Candidates = [SenderNodeId],
            CandidateIndex = 0,
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, originSequence: 10);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, gapFillAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

        admitted.Should().BeFalse("this gap-fill asked for history above sequence 10, and this record does not clear that bound");
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
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, reconcileAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

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
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, reconcileAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

        admitted.Should().BeFalse("this reconcile was addressed to a different peer entirely");
    }

    [Fact]
    public void A_broadcast_answer_from_a_vouched_member_carrying_the_requested_stream_is_admitted()
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
        Guid requestedStreamId = DomainId.New();
        EventCatchUpRequest broadcastPull = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForStreamId = requestedStreamId,
            Candidates = [],
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, streamId: requestedStreamId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, broadcastPull, trustChain, senderFingerprint, requestedStreamIds: [requestedStreamId], Now);

        admitted.Should().BeTrue("a broadcast names no single peer up front, so any vouched project member may answer it for the exact stream it asked for");
    }

    [Fact]
    public void A_broadcast_answer_carrying_a_stream_the_request_never_asked_for_is_dropped()
    {
        // The narrowing this rule adds: entitlement to answer a stream broadcast is not entitlement
        // to forward ANY stream — only the one requested, or one of that task's own run streams,
        // resolved into requestedStreamIds the identical way EventCatchUpResponder itself would.
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
        Guid requestedStreamId = DomainId.New();
        Guid unrelatedStreamId = DomainId.New();
        EventCatchUpRequest broadcastPull = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForStreamId = requestedStreamId,
            Candidates = [],
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, streamId: unrelatedStreamId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, broadcastPull, trustChain, senderFingerprint, requestedStreamIds: [requestedStreamId], Now);

        admitted.Should().BeFalse(
            "this broadcast asked for one specific stream (and its own runs), never a forged record riding in on an unrelated one");
    }

    [Fact]
    public void A_broadcast_answer_from_an_unvouched_sender_is_dropped()
    {
        Guid requestedStreamId = DomainId.New();
        EventCatchUpRequest broadcastAdoption = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForStreamId = requestedStreamId,
            Candidates = [],
            SentAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, streamId: requestedStreamId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, broadcastAdoption, TrustChain.Empty, senderFingerprint: "unvouched-fingerprint",
            requestedStreamIds: [requestedStreamId], Now);

        admitted.Should().BeFalse("nothing in this project's own trust chain vouches this sender at all");
    }

    [Fact]
    public void A_later_batch_after_the_request_already_closed_is_still_admitted()
    {
        // A split answer's later batches arrive after the first batch already answered, superseded,
        // or exhausted the request — this rule is checked against the matched request regardless of
        // its own outstanding state, as long as it is still inside the admission window.
        EventCatchUpRequest closedCascadeAsk = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForOriginNodeId = OriginNodeId,
            SinceOriginSequence = 0,
            Candidates = [SenderNodeId],
            CandidateIndex = 0,
            SentAt = Now,
            AnsweredAt = Now,
        };
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, originSequence: 1);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, closedCascadeAsk, TrustChain.Empty, senderFingerprint: null, requestedStreamIds: [], Now);

        admitted.Should().BeTrue("a request already answered, superseded, or exhausted still names who was entitled to answer it");
    }

    [Fact]
    public void A_request_minted_long_enough_ago_no_longer_entitles_a_forwarded_record()
    {
        // independent pre-PR review, cycle 1, conformance lens, medium: none of a request's own
        // closing marks is ever pruned, and a broadcast's own id travels in the clear on this node's
        // past outbox forever — reusing an ancient one must not entitle a forwarded record weeks or
        // months later, whatever it claims to answer.
        Guid requestedStreamId = DomainId.New();
        EventCatchUpRequest staleBroadcast = new()
        {
            Id = DomainId.New(),
            ProjectId = ProjectId,
            ForStreamId = requestedStreamId,
            Candidates = [],
            SentAt = Now - EventReplicationInbox.ForwardedRecordAdmissionWindow - TimeSpan.FromSeconds(1),
        };
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
        EventReplicationCodec.ReplicatedEventRecord record = ForeignRecord(OriginNodeId, streamId: requestedStreamId);

        bool admitted = EventReplicationInbox.IsForwardedRecordAdmitted(
            record, SenderNodeId, staleBroadcast, trustChain, senderFingerprint, requestedStreamIds: [requestedStreamId], Now);

        admitted.Should().BeFalse("a request this old no longer entitles anyone to forward a record on its say-so");
    }
}
