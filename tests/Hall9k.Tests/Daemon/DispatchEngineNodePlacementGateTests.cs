using FluentAssertions;
using Hall9k.Daemon.Dispatch;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// <see cref="DispatchEngine.IsPlacedOnThisNode"/> is the pure decision behind the daemon's own
/// node-placement gate (idea 202383dc: an owner can place a task on one of their own nodes) —
/// whether a task's advisory <c>PlacedOnNodeId</c> admits a claim from this node, narrower than
/// (and orthogonal to) the owner match <see cref="DispatchEngineClaimGateTests"/> covers.
/// </summary>
public sealed class DispatchEngineNodePlacementGateTests
{
    private static readonly Guid PlacedNodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherNodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void An_unplaced_task_is_unchanged_and_claimable_by_any_node_of_the_granted_owner()
    {
        bool admittedHere = DispatchEngine.IsPlacedOnThisNode(placedOnNodeId: null, thisNodeId: PlacedNodeId);
        bool admittedElsewhere = DispatchEngine.IsPlacedOnThisNode(placedOnNodeId: null, thisNodeId: OtherNodeId);

        admittedHere.Should().BeTrue("no placement was ever recorded, so dispatch behaves exactly as it did before this feature existed");
        admittedElsewhere.Should().BeTrue("an unplaced task admits every node of the granted owner, not just one");
    }

    [Fact]
    public void A_placed_task_is_claimed_by_the_placed_node()
    {
        bool admitted = DispatchEngine.IsPlacedOnThisNode(placedOnNodeId: PlacedNodeId, thisNodeId: PlacedNodeId);

        admitted.Should().BeTrue("the placement names this exact node");
    }

    [Fact]
    public void A_placed_task_is_skipped_by_a_non_placed_node()
    {
        bool admitted = DispatchEngine.IsPlacedOnThisNode(placedOnNodeId: PlacedNodeId, thisNodeId: OtherNodeId);

        admitted.Should().BeFalse("the placement names a different node of the same owner's own fleet — this node stands down without a forced take");
    }
}
