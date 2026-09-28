using FluentAssertions;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon.Messaging;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The cooperative-take request's own owner-verification decision (idea 202383dc, item 5;
/// independent pre-PR review, cycle 6, adversarial lens, medium):
/// <see cref="ClaimRequestWatchLoop.IsRequesterOwnerVerified"/> is the pure logic behind "does the
/// ledger's own trust chain actually vouch the sender for the owner root its claim request body
/// self-declares" — no document store, no ledger, no daemon loop, just the chain lookup itself.
/// </summary>
public sealed class ClaimRequestWatchLoopTests
{
    private const string OwnerRoot = "owner-root-fingerprint";
    private const string OtherOwnerRoot = "other-owner-root-fingerprint";
    private static readonly Guid SenderNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string SenderDeviceFingerprint = "sender-device-fingerprint";

    [Fact]
    public void A_vouched_devices_own_claimed_root_verifies()
    {
        TrustedOwner owner = new(
            OwnerRoot, "root-public-key",
            [new TrustedNode(SenderNodeId.ToString(), "device-public-key", SenderDeviceFingerprint, DateTimeOffset.UtcNow)]);
        TrustChain chain = new(new Dictionary<string, TrustedOwner> { [OwnerRoot] = owner }, []);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, SenderDeviceFingerprint, OwnerRoot, SenderNodeId);

        verified.Should().BeTrue("the ledger's own owner chain vouches this exact device for this exact node id");
    }

    [Fact]
    public void An_owner_roots_own_founding_device_verifies_with_no_separate_vouch_entry()
    {
        // The device that establishes an owner's root never gets its own entry in Nodes (nothing
        // ever writes a owners/<root>/nodes/<id>.yaml vouch for it) — it is trusted purely because
        // its own key self-certified the root, which GitLedgerChainReader records by attaching
        // RootNodeId to that exact node id. TrustedOwner.ContainsForNode's own root special case
        // (idea 6be68ee2, trust-ledger finding 7) binds on RootNodeId now, so the root-establishing
        // node's own genuine cooperative-take requests must still verify here.
        TrustedOwner owner = new(OwnerRoot, "root-public-key", [], RootNodeId: SenderNodeId.ToString());
        TrustChain chain = new(new Dictionary<string, TrustedOwner> { [OwnerRoot] = owner }, []);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, OwnerRoot, OwnerRoot, SenderNodeId);

        verified.Should().BeTrue("the root's own key qualifies for the exact node id the ledger attached as RootNodeId");
    }

    [Fact]
    public void A_different_real_owners_root_never_verifies_for_a_sender_it_never_vouched()
    {
        // The adversarial scenario itself: node B is genuinely vouched under OwnerRoot, but its
        // claim request body names OtherOwnerRoot — a real project member's own root the sender
        // merely knows the fingerprint of from this project's own replicated history, never one
        // that actually vouches this device.
        TrustedOwner senderOwner = new(
            OwnerRoot, "root-public-key",
            [new TrustedNode(SenderNodeId.ToString(), "device-public-key", SenderDeviceFingerprint, DateTimeOffset.UtcNow)]);
        TrustedOwner otherOwner = new(OtherOwnerRoot, "other-root-public-key", []);
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { [OwnerRoot] = senderOwner, [OtherOwnerRoot] = otherOwner }, []);

        bool verified =
            ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, SenderDeviceFingerprint, OtherOwnerRoot, SenderNodeId);

        verified.Should().BeFalse("OtherOwnerRoot's own chain never vouched this sender's own device");
    }

    [Fact]
    public void A_claimed_root_that_is_not_a_current_owner_at_all_never_verifies()
    {
        TrustChain chain = TrustChain.Empty;

        bool verified =
            ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, SenderDeviceFingerprint, "nobody-owns-this-fingerprint", SenderNodeId);

        verified.Should().BeFalse("the claimed root names nobody this ledger's own chain currently recognizes");
    }

    [Fact]
    public void An_unresolvable_sender_fingerprint_never_verifies()
    {
        TrustedOwner owner = new(
            OwnerRoot, "root-public-key",
            [new TrustedNode(SenderNodeId.ToString(), "device-public-key", SenderDeviceFingerprint, DateTimeOffset.UtcNow)]);
        TrustChain chain = new(new Dictionary<string, TrustedOwner> { [OwnerRoot] = owner }, []);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, senderFingerprint: null, OwnerRoot, SenderNodeId);

        verified.Should().BeFalse("a caller that could not resolve the sender's own device key has nothing to verify against");
    }
}

/// <summary>
/// <see cref="ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity"/> alone (idea 6be68ee2,
/// trust-ledger finding 13): pure logic, no document store, no ledger, no daemon loop, the same
/// shape <see cref="ClaimRequestWatchLoopTests"/> already established for
/// <c>IsRequesterOwnerVerified</c>.
/// </summary>
public sealed class ClaimRequestWatchLoopApplyDeclaredTrackerIdentityTests
{
    private static readonly Guid RequesterNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RequesterOwnerId = DomainId.New();
    private static readonly Guid FromNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ClaimEnvelopeCodec.ClaimRequestRecord RequestCarrying(string? trackerIdentity) => new(
        DomainId.New(), RequesterNodeId, RequesterOwnerId, "requester-owner-fingerprint", "give it back",
        trackerIdentity);

    private static TrustChain ChainDeclaring(string login) => TrustChain.Empty with
    {
        NodeDeclarations = new Dictionary<string, NodeGitHubDeclaration>
        {
            [FromNodeId.ToString()] = new(
                FromNodeId.ToString(), "node-device-fingerprint", new DeclaredGitHubAccount(42, login), DateTimeOffset.UtcNow),
        },
    };

    [Fact]
    public void A_github_gated_request_carrying_a_login_other_than_the_declaration_is_replaced_with_the_declared_login()
    {
        TrustChain chain = ChainDeclaring("alice");
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("mallory");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.TrackerAssignee, WorkItemProvider.GitHub, chain, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().Be("alice", "the requesting node's own declared login wins over whatever it self-declared on the request");
        replaced.Should().BeTrue("the carried identity and the declared login genuinely differ");
    }

    [Fact]
    public void A_github_gated_request_already_carrying_the_declared_login_is_not_flagged_as_replaced()
    {
        TrustChain chain = ChainDeclaring("alice");
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("alice");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.TrackerAssignee, WorkItemProvider.GitHub, chain, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().Be("alice");
        replaced.Should().BeFalse("nothing actually changed, so this must never be logged as a replacement");
    }

    [Fact]
    public void A_github_gated_request_from_a_node_with_no_declaration_loses_its_carried_identity_rather_than_keeping_it()
    {
        // idea 6be68ee2, trust-ledger finding 13: the fail-closed rule. Falling back to the carried,
        // self-declared value here would defeat the entire fix — a node with no declaration must
        // never have its own self-declared login trusted either. ClaimRequestEngine's own best-effort
        // tracker move already turns a null identity into the existing hand-assign warning
        // (decision 229), the same state as today's no-identity case.
        TrustChain chainWithNoDeclaration = TrustChain.Empty;
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("mallory");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.TrackerAssignee, WorkItemProvider.GitHub, chainWithNoDeclaration, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().BeNull(
            "a node with no declaration never falls back to its own untrusted, self-declared carried value");
        replaced.Should().BeTrue();
    }

    [Fact]
    public void A_github_pull_request_gated_request_is_also_replaced_never_only_a_plain_issue()
    {
        TrustChain chain = ChainDeclaring("alice");
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("mallory");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.TrackerAssignee, WorkItemProvider.GitHubPullRequest, chain, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().Be("alice");
        replaced.Should().BeTrue();
    }

    [Fact]
    public void A_jira_gated_request_keeps_its_carried_accountId_even_when_a_github_login_is_declared()
    {
        // The declaration carries no Jira identity at all (NodeGitHubDeclaration's own doc) — the
        // carried accountId is already the audit fact TaskTakeRequested records.
        TrustChain chain = ChainDeclaring("alice");
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("jira-account-id-123");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.TrackerAssignee, WorkItemProvider.Jira, chain, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().Be("jira-account-id-123");
        replaced.Should().BeFalse();
    }

    [Fact]
    public void An_ungated_project_never_touches_the_carried_identity()
    {
        TrustChain chain = ChainDeclaring("alice");
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("mallory");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.Off, WorkItemProvider.GitHub, chain, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().Be("mallory");
        replaced.Should().BeFalse();
    }

    [Fact]
    public void A_task_with_no_external_reference_yet_never_touches_the_carried_identity()
    {
        TrustChain chain = ChainDeclaring("alice");
        ClaimEnvelopeCodec.ClaimRequestRecord request = RequestCarrying("mallory");

        (ClaimEnvelopeCodec.ClaimRequestRecord effective, bool replaced) = ClaimRequestWatchLoop.ApplyDeclaredTrackerIdentity(
            ClaimGate.TrackerAssignee, externalReferenceProvider: null, chain, request, FromNodeId);

        effective.RequesterTrackerIdentity.Should().Be("mallory");
        replaced.Should().BeFalse();
    }
}
