using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="TaskAssignCommand.ResolveFleet"/> is <c>h9k task assign --node</c>'s own fleet
/// composition, pure and I/O-free: the owner's root node — the one whose key established the
/// root, carried on <see cref="TrustedOwner.RootNodeId"/> — counts as part of the owner's fleet
/// even though the root never has an <c>owners/&lt;root&gt;/nodes/&lt;id&gt;.yaml</c> vouch entry
/// of its own (a root never vouches itself). Paired with <see cref="NodePlacementResolver.Resolve"/>
/// the same way <see cref="NodePlacementResolverTests"/> already exercises it, so these tests read
/// as "placement on X succeeds/is refused" without a project document or a real ledger.
/// </summary>
public sealed class TaskAssignCommandFleetTests
{
    private const string Root = "root-fingerprint";
    private static readonly Guid RootNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VouchedNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StrangerNodeId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static TrustChain BuildChain(bool includeVouchedNode = true)
    {
        TrustedOwner owner = new(
            Root, "ssh-ed25519 AAAAFAKEROOT root",
            includeVouchedNode
                ? [new TrustedNode(VouchedNodeId.ToString(), "ssh-ed25519 AAAAFAKEVOUCHED vouched", "vouched-fingerprint", DateTimeOffset.UnixEpoch)]
                : [],
            RootNodeId: RootNodeId.ToString());
        return new TrustChain(new Dictionary<string, TrustedOwner> { [Root] = owner }, [new ProjectMember(Root, MembershipRole.Owner, DateTimeOffset.UnixEpoch)]);
    }

    [Fact]
    public void Placement_on_the_root_node_succeeds_without_a_self_vouch()
    {
        TrustChain chain = BuildChain(includeVouchedNode: false);

        HashSet<Guid> fleet = TaskAssignCommand.ResolveFleet(chain, Root, ownerIsThisInstall: false, thisNodeId: Guid.Empty);
        Guid resolved = NodePlacementResolver.Resolve(RootNodeId.ToString(), fleet);

        resolved.Should().Be(RootNodeId, "the root's own key established it, so it never needs h9k node vouch on itself");
    }

    [Fact]
    public void Placement_on_a_vouched_node_is_unchanged()
    {
        TrustChain chain = BuildChain();

        HashSet<Guid> fleet = TaskAssignCommand.ResolveFleet(chain, Root, ownerIsThisInstall: false, thisNodeId: Guid.Empty);
        Guid resolved = NodePlacementResolver.Resolve(VouchedNodeId.ToString(), fleet);

        resolved.Should().Be(VouchedNodeId);
    }

    [Fact]
    public void A_node_in_neither_the_root_nor_the_vouched_set_is_refused()
    {
        TrustChain chain = BuildChain();

        HashSet<Guid> fleet = TaskAssignCommand.ResolveFleet(chain, Root, ownerIsThisInstall: false, thisNodeId: Guid.Empty);
        Action act = () => NodePlacementResolver.Resolve(StrangerNodeId.ToString(), fleet);

        act.Should().Throw<DomainValidationException>().WithMessage("*not vouched*");
    }

    [Fact]
    public void The_root_and_a_vouched_node_are_both_in_the_fleet_together()
    {
        TrustChain chain = BuildChain();

        HashSet<Guid> fleet = TaskAssignCommand.ResolveFleet(chain, Root, ownerIsThisInstall: false, thisNodeId: Guid.Empty);

        fleet.Should().BeEquivalentTo([RootNodeId, VouchedNodeId]);
    }

    [Fact]
    public void An_owner_with_no_ledger_chain_entry_falls_back_to_this_installs_own_node_id_when_it_is_that_owners_work()
    {
        // No chain at all (TrustChain.Empty) — the CLI's own local shortcut for a fresh,
        // still-unvouched single-node project (TaskAssignCommand.ResolveNodeIdAsync's own doc).
        HashSet<Guid> fleet = TaskAssignCommand.ResolveFleet(TrustChain.Empty, Root, ownerIsThisInstall: true, thisNodeId: RootNodeId);

        fleet.Should().BeEquivalentTo([RootNodeId]);
    }
}
