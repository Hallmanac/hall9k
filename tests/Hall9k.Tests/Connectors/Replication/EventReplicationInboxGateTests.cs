using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Idea 6be68ee2, trust-ledger findings 1 and 6: <see cref="EventReplicationInbox.EvaluateGatedEvent"/>
/// is pure, DB-free logic — a project-settings-shaped event (one of
/// <see cref="Hall9k.Domain.Features.Replication.ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/>'s
/// own six) applies only when the verified sender key belongs to an Owner-role project member's own
/// chain for the sender's own node id, and a refused verdict burns the origin event id only when the
/// sender is that record's own claimed origin.
/// </summary>
public sealed class EventReplicationInboxGateTests
{
    [Fact]
    public void A_member_roles_sender_signing_its_own_event_is_dropped_and_burned()
    {
        Guid senderNodeId = DomainId.New();
        const string root = "member-root-fingerprint";
        const string senderFingerprint = "member-node-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new TrustedOwner(
                    root, "ssh-ed25519 AAAAFAKEROOT root",
                    [new TrustedNode(senderNodeId.ToString(), "ssh-ed25519 AAAAFAKENODE node", senderFingerprint, DateTimeOffset.UtcNow)]),
            },
            [new ProjectMember(root, MembershipRole.Member, DateTimeOffset.UtcNow)]);

        EventReplicationInbox.GatedEventVerdict verdict = EventReplicationInbox.EvaluateGatedEvent(
            trustChain, senderFingerprint, senderNodeId, originNodeId: senderNodeId, typeof(ProjectTeamSettingsChanged));

        verdict.Should().Be(EventReplicationInbox.GatedEventVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void An_owner_roles_sender_signing_its_own_event_is_allowed()
    {
        Guid senderNodeId = DomainId.New();
        const string root = "owner-root-fingerprint";
        const string senderFingerprint = "owner-node-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new TrustedOwner(
                    root, "ssh-ed25519 AAAAFAKEROOT root",
                    [new TrustedNode(senderNodeId.ToString(), "ssh-ed25519 AAAAFAKENODE node", senderFingerprint, DateTimeOffset.UtcNow)]),
            },
            [new ProjectMember(root, MembershipRole.Owner, DateTimeOffset.UtcNow)]);

        EventReplicationInbox.GatedEventVerdict verdict = EventReplicationInbox.EvaluateGatedEvent(
            trustChain, senderFingerprint, senderNodeId, originNodeId: senderNodeId, typeof(ProjectTeamSettingsChanged));

        verdict.Should().Be(EventReplicationInbox.GatedEventVerdict.Allowed);
    }

    /// <summary>
    /// The catch-up shape: a member-role peer forwards an event a real owner authored elsewhere
    /// (originNodeId names that owner, never this forwarding sender). Dropped, since the peer
    /// carrying it here is not itself vouched as an owner, but the origin event id is never burned —
    /// the legitimate owner event may still arrive, or be forwarded again, through a sender this
    /// gate actually allows.
    /// </summary>
    [Fact]
    public void A_member_signed_event_forwarded_from_an_owner_origin_is_dropped_without_recording_the_origin_id()
    {
        Guid forwardingSenderNodeId = DomainId.New();
        Guid trueOwnerOriginNodeId = DomainId.New();
        const string root = "member-root-fingerprint";
        const string forwarderFingerprint = "forwarder-node-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new TrustedOwner(
                    root, "ssh-ed25519 AAAAFAKEROOT root",
                    [
                        new TrustedNode(
                            forwardingSenderNodeId.ToString(), "ssh-ed25519 AAAAFAKENODE node", forwarderFingerprint,
                            DateTimeOffset.UtcNow),
                    ]),
            },
            [new ProjectMember(root, MembershipRole.Member, DateTimeOffset.UtcNow)]);

        EventReplicationInbox.GatedEventVerdict verdict = EventReplicationInbox.EvaluateGatedEvent(
            trustChain, forwarderFingerprint, forwardingSenderNodeId, trueOwnerOriginNodeId, typeof(ProjectTeamSettingsChanged));

        verdict.Should().Be(EventReplicationInbox.GatedEventVerdict.DroppedWithoutRecording);
    }

    /// <summary>
    /// A member overwrote this node id's own self-announced node file with a key an owner's chain
    /// vouched for a DIFFERENT node id — <see cref="TrustedOwner.ContainsForNode"/>'s own binding
    /// refuses reusing one vouched node's key to speak for another node id, so this must never read
    /// as allowed just because the fingerprint appears somewhere in the owner's chain.
    /// </summary>
    [Fact]
    public void A_key_vouched_for_a_different_node_id_than_the_envelopes_sender_is_dropped()
    {
        Guid senderNodeId = DomainId.New();
        Guid vouchedForADifferentNodeId = DomainId.New();
        const string root = "owner-root-fingerprint";
        const string reusedFingerprint = "vouched-elsewhere-fingerprint";

        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [root] = new TrustedOwner(
                    root, "ssh-ed25519 AAAAFAKEROOT root",
                    [
                        new TrustedNode(
                            vouchedForADifferentNodeId.ToString(), "ssh-ed25519 AAAAFAKENODE node", reusedFingerprint,
                            DateTimeOffset.UtcNow),
                    ]),
            },
            [new ProjectMember(root, MembershipRole.Owner, DateTimeOffset.UtcNow)]);

        EventReplicationInbox.GatedEventVerdict verdict = EventReplicationInbox.EvaluateGatedEvent(
            trustChain, reusedFingerprint, senderNodeId, originNodeId: senderNodeId, typeof(ProjectTeamSettingsChanged));

        verdict.Should().Be(EventReplicationInbox.GatedEventVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void An_ungated_event_type_is_always_allowed_whatever_the_chain_says()
    {
        Guid senderNodeId = DomainId.New();

        EventReplicationInbox.GatedEventVerdict verdict = EventReplicationInbox.EvaluateGatedEvent(
            TrustChain.Empty, senderFingerprint: null, senderNodeId, originNodeId: senderNodeId, typeof(TaskAddedStandIn));

        verdict.Should().Be(EventReplicationInbox.GatedEventVerdict.Allowed);
    }

    /// <summary>
    /// <see cref="EventReplicationInbox.EvaluateCrossProjectIdeaEvent"/>'s catch-up shape: a peer
    /// the source project's own ledger does not vouch forwards somebody else's event on an idea that
    /// still resolves to that source project. Dropped, but never burned, so the true origin can still
    /// deliver it. The native-sender outcomes (admitted, refused and burned) are proven end to end in
    /// <c>EventReplicationTests</c>.
    /// </summary>
    [Fact]
    public void An_idea_event_forwarded_by_a_peer_the_source_project_does_not_vouch_is_dropped_without_recording()
    {
        Guid forwardingSenderNodeId = DomainId.New();
        Guid trueOriginNodeId = DomainId.New();

        EventReplicationInbox.GatedEventVerdict verdict = EventReplicationInbox.EvaluateCrossProjectIdeaEvent(
            TrustChain.Empty, "forwarder-node-fingerprint", forwardingSenderNodeId, trueOriginNodeId);

        verdict.Should().Be(EventReplicationInbox.GatedEventVerdict.DroppedWithoutRecording);
    }

    /// <summary>A stand-in type — any type outside <c>IsProjectAggregateStreamEvent</c>'s own six
    /// answers identically, so a real domain event type is not needed to prove the branch.</summary>
    private sealed record TaskAddedStandIn;
}
