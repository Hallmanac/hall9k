using FluentAssertions;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class NodeDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RegisterKey_produces_an_event_carrying_the_public_key_and_its_fingerprint()
    {
        NodeAggregate node = Registered();

        NodeKeyRegistered @event = NodeDecider.RegisterKey(node, "ssh-ed25519 AAAA test", "fingerprint-1", Now);

        @event.Id.Should().Be(node.Id);
        @event.PublicKey.Should().Be("ssh-ed25519 AAAA test");
        @event.KeyFingerprint.Should().Be("fingerprint-1");
    }

    [Fact]
    public void RegisterKey_refuses_a_node_that_already_has_a_key()
    {
        NodeAggregate node = Registered();
        node.Apply(NodeDecider.RegisterKey(node, "ssh-ed25519 AAAA test", "fingerprint-1", Now));

        Action act = () => NodeDecider.RegisterKey(node, "ssh-ed25519 BBBB other", "fingerprint-2", Now);

        act.Should().Throw<DomainValidationException>("a node's key is generated once and never replaced");
    }

    [Fact]
    public void ClaimOwner_refuses_a_blank_fingerprint()
    {
        NodeAggregate node = Registered();

        Action act = () => NodeDecider.ClaimOwner(node, string.Empty, Now);

        act.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void NodeAggregate_applies_the_key_and_the_claim_onto_its_own_fields()
    {
        NodeAggregate node = Registered();
        node.Apply(NodeDecider.RegisterKey(node, "ssh-ed25519 AAAA test", "fingerprint-1", Now));
        node.Apply(NodeDecider.ClaimOwner(node, "fingerprint-1", Now));

        node.PublicKey.Should().Be("ssh-ed25519 AAAA test");
        node.KeyFingerprint.Should().Be("fingerprint-1");
        node.KeyRegisteredAt.Should().Be(Now);
        node.ClaimedOwnerFingerprint.Should().Be("fingerprint-1");
        node.ClaimedOwnerAt.Should().Be(Now);
    }

    [Fact]
    public void A_later_claim_replaces_the_earlier_one_rather_than_merging_with_it()
    {
        NodeAggregate node = Registered();
        node.Apply(NodeDecider.ClaimOwner(node, "fingerprint-1", Now));
        node.Apply(NodeDecider.ClaimOwner(node, "fingerprint-2", Now.AddMinutes(5)));

        node.ClaimedOwnerFingerprint.Should().Be("fingerprint-2");
    }

    private static NodeAggregate Registered()
    {
        NodeAggregate node = new();
        node.Apply(NodeDecider.Register(DomainId.New(), DomainId.New(), "test-machine", "macos", Now));
        return node;
    }
}
