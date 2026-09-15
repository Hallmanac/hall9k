using FluentAssertions;
using Hall9k.Connectors.Trust;
using Xunit;

namespace Hall9k.Tests.Connectors.Trust;

/// <summary>
/// <see cref="TrustChain"/>'s own pure logic — no git, no repository, since every method here is a
/// fold over data a caller already built by hand. The node-id-bound overload of
/// <see cref="TrustChain.IsAllowedSigner(string, Guid)"/> is what
/// <see cref="Hall9k.Connectors.Messaging.GitLedgerMessageTransport"/> now checks a message sender
/// against (independent pre-PR review, cycle 1, conformance and adversarial lenses, medium): a
/// vouched node's key must answer only for the exact node id it was vouched under, never for some
/// other node the same owner happens to have vouched.
/// </summary>
public sealed class TrustChainTests
{
    private static readonly Guid VouchedNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TrustChain BuildChain()
    {
        TrustedNode node = new(VouchedNodeId.ToString(), "ssh-ed25519 AAAAnode node", "node-fingerprint", DateTimeOffset.UnixEpoch);
        TrustedOwner owner = new("root-fingerprint", "ssh-ed25519 AAAAroot root", [node]);
        ProjectMember member = new("root-fingerprint", MembershipRole.Owner, DateTimeOffset.UnixEpoch);
        return new TrustChain(new Dictionary<string, TrustedOwner> { ["root-fingerprint"] = owner }, [member]);
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_AcceptsTheNodeItWasVouchedUnder()
    {
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("node-fingerprint", VouchedNodeId).Should().BeTrue();
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_RejectsTheIdenticalKeyClaimedForADifferentNodeId()
    {
        // The shape of the attack the review reproduced: a member overwrites some other node's own
        // self-announced node.yaml with a key that IS genuinely vouched — just not for that node id.
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("node-fingerprint", OtherNodeId).Should().BeFalse(
            "the key was vouched for VouchedNodeId, never for a different node id claiming the same key");
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_AcceptsTheRootsOwnKeyRegardlessOfNodeId()
    {
        // The root has no separate vouched-node entry of its own to bind a node id to, so its own
        // key answers for any node id — unlike a vouched node's key, which is always bound.
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("root-fingerprint", OtherNodeId).Should().BeTrue();
    }

    [Fact]
    public void IsAllowedSigner_WithNodeId_RejectsAKeyNoOwnerChainContainsAtAll()
    {
        TrustChain chain = BuildChain();

        chain.IsAllowedSigner("stranger-fingerprint", VouchedNodeId).Should().BeFalse();
    }
}
