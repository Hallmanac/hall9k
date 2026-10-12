using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A holder release that reaches a node after another node has claimed the task leaves the new
/// holder in place (origin incident, 2026-10-12: Windows' release replicated after the Mac's
/// claim and wiped the Mac's holder, so two tasks closed out with the ledger record still naming
/// the Mac). Two real stores exchange events through the replication seam, nothing else.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LateHolderReleaseReplicationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private Task<ReplicatedFleet> StartFleetAsync(CancellationToken cancellationToken) =>
        ReplicatedFleet.StartAsync(postgres, "late_holder_release_node_b", cleanPrimary: true, cancellationToken);

    [Fact]
    public async Task A_release_replicated_after_another_nodes_claim_leaves_the_new_holder_in_place()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        Guid taskId = DomainId.New();
        await fleet.SeedReviewAsync(fleet.A, taskId, Now, cts.Token, autoCreated: false);
        await fleet.ExchangeAsync(fleet.A, fleet.B, cts.Token);

        // A claims and requeues, as a failure leaves it, without giving the holder back yet.
        await fleet.ClaimAsync(fleet.A, taskId, cts.Token);
        await AppendAsync(fleet.A, taskId, task => TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now), cts.Token);
        await fleet.ExchangeAsync(fleet.A, fleet.B, cts.Token);
        (await TaskOnAsync(fleet.B, taskId, cts.Token)).HolderNodeId.Should().Be(fleet.A.Node.NodeId);

        // B claims the queued task, then A's release finally arrives.
        await fleet.ClaimAsync(fleet.B, taskId, cts.Token);
        await AppendAsync(fleet.A, taskId, task => TaskDecider.ReleaseHolder(task, Now), cts.Token);
        await fleet.ExchangeAsync(fleet.A, fleet.B, cts.Token);

        TaskAggregate onB = await TaskOnAsync(fleet.B, taskId, cts.Token);
        onB.HolderNodeId.Should().Be(fleet.B.Node.NodeId, "A's release was written for A's own holder, not B's");
        onB.State.Should().Be(TaskState.Claimed);
    }

    [Fact]
    public async Task A_release_from_the_current_holder_still_clears_it_on_a_peer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        await using ReplicatedFleet fleet = await StartFleetAsync(cts.Token);
        Guid taskId = DomainId.New();
        await fleet.SeedReviewAsync(fleet.A, taskId, Now, cts.Token, autoCreated: false);
        await fleet.ExchangeAsync(fleet.A, fleet.B, cts.Token);

        await fleet.ClaimAsync(fleet.A, taskId, cts.Token);
        await AppendAsync(fleet.A, taskId, task => TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now), cts.Token);
        await AppendAsync(fleet.A, taskId, task => TaskDecider.ReleaseHolder(task, Now), cts.Token);
        await fleet.ExchangeAsync(fleet.A, fleet.B, cts.Token);

        (await TaskOnAsync(fleet.B, taskId, cts.Token)).HolderNodeId.Should().BeNull(
            "the node that held the task gave it back, which is the ordinary release");
    }

    private static async Task AppendAsync(
        ReplicatedPeer peer, Guid taskId, Func<TaskAggregate, object> decide, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = peer.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, decide(task));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<TaskAggregate> TaskOnAsync(ReplicatedPeer peer, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = peer.Store.QuerySession();
        return (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
    }
}
