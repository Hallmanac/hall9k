using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="EventOriginStampingListener"/> is the one listener acceptance criterion 1 (idea
/// 202383dc, event stamping) asks for: every event appended through the store carries this
/// node's own id and its owner's root fingerprint as event metadata headers, and no event type's
/// own shape changes to carry them.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class EventOriginStampingTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Every_event_appended_through_the_store_carries_the_node_id_and_owner_root_fingerprint()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        const string fingerprint = "test-root-fingerprint";
        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
                node.OwnerId, token: cts.Token))!;
            session.Events.Append(
                node.OwnerId, OwnerDecider.ClaimRoot(owner, fingerprint, verified: true, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        // NodeBootstrap.EnsureAsync is what re-resolves and re-stamps the listener once the root
        // claim lands — a real node re-runs it at the top of every command, so this mirrors that
        // rather than reaching into the listener directly.
        await using (IDocumentSession bootstrapSession = store.LightweightSession())
        {
            await NodeBootstrap.EnsureAsync(bootstrapSession, cts.Token);
            await bootstrapSession.SaveChangesAsync(cts.Token);
        }

        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Prove the stamping listener", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, DateTimeOffset.UtcNow, node.OwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(taskId, token: cts.Token);
            events.Should().ContainSingle("only TaskAdded was appended to this stream");
            IEvent taskAdded = events[0];

            taskAdded.Headers.Should().NotBeNull("HeadersEnabled makes every event carry its stamped metadata");
            taskAdded.Headers![EventOriginStampingListener.NodeIdHeader].Should().Be(node.NodeId.ToString());
            taskAdded.Headers[EventOriginStampingListener.OwnerRootFingerprintHeader].Should().Be(fingerprint);
        }
    }
}
