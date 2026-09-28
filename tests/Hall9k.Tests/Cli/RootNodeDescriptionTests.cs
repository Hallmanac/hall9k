using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Trust;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="RootNodeDescription"/>: a root-only refusal names the node behind a root key when
/// the chain knows it (independent pre-PR review, cycle 1, conformance lens, low) — pure
/// composition against a hand-built <see cref="TrustChain"/>, no ledger or Postgres involved.
/// </summary>
public sealed class RootNodeDescriptionTests
{
    private const string Root = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RootPublicKeyLine = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleRootKeyLine";
    private const string RootNodeId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Names_the_node_id_when_the_chain_knows_it_but_declares_no_display_name()
    {
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner> { [Root] = new(Root, RootPublicKeyLine, [], RootNodeId: RootNodeId) }, []);

        RootNodeDescription.Of(chain, Root).Should().Be($" (the root node, node {RootNodeId})");
    }

    [Fact]
    public void Is_empty_when_the_chain_cannot_name_a_node_for_this_root()
    {
        TrustChain chain = new(new Dictionary<string, TrustedOwner> { [Root] = new(Root, RootPublicKeyLine, []) }, []);

        RootNodeDescription.Of(chain, Root).Should().BeEmpty("an older ledger's own TrustedOwner.RootNodeId is null");
    }

    [Fact]
    public void Is_empty_when_the_chain_has_no_owner_at_all_for_this_root()
    {
        RootNodeDescription.Of(TrustChain.Empty, Root).Should().BeEmpty();
    }
}
