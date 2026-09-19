using FluentAssertions;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon.Messaging;
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
        // its own key self-certified the root. TrustedOwner.ContainsForNode already special-cases
        // this (its own doc), so the root-establishing node's own genuine cooperative-take requests
        // must still verify here.
        TrustedOwner owner = new(OwnerRoot, "root-public-key", []);
        TrustChain chain = new(new Dictionary<string, TrustedOwner> { [OwnerRoot] = owner }, []);

        bool verified = ClaimRequestWatchLoop.IsRequesterOwnerVerified(chain, OwnerRoot, OwnerRoot, SenderNodeId);

        verified.Should().BeTrue("the root's own key always qualifies for any node id, per TrustedOwner.ContainsForNode");
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
