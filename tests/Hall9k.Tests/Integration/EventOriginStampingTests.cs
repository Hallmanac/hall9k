using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Node;
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

        // No re-bootstrap in between: the listener resolves the owner's root fingerprint fresh
        // from the store on every save, so a long-lived session (the daemon's own shape) sees the
        // claim that just landed without NodeBootstrap.EnsureAsync ever running again.
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

    /// <summary>
    /// A fresh install's first command: <c>NodeBootstrap.EnsureAsync</c> starts the Owner and Node
    /// streams and the command appends its own events to the same session, all flushed by one
    /// save. The listener runs before that save's SQL executes, so neither projection row is
    /// queryable yet; only the batch's own pending events can say who this node and owner are
    /// (cycle-2 pre-PR review, conformance lens).
    /// </summary>
    [Fact]
    public async Task Events_saved_in_the_same_batch_that_registers_the_node_and_owner_carry_their_origin()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        await NodeBootstrapSeed.SeedGitHubConnectionAsync(store, cts.Token);

        const string fingerprint = "fresh-install-root-fingerprint";
        Guid taskId = DomainId.New();
        BootstrapContext context;
        await using (IDocumentSession session = store.LightweightSession())
        {
            context = await NodeBootstrap.EnsureAsync(session, cts.Token);

            OwnerRegistered registered = session.PendingChanges.Streams()
                .SelectMany(s => s.Events).Select(e => e.Data).OfType<OwnerRegistered>().Single();
            OwnerAggregate owner = new();
            owner.Apply(registered);
            session.Events.Append(
                context.OwnerId, OwnerDecider.ClaimRoot(owner, fingerprint, verified: true, DateTimeOffset.UtcNow));

            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Prove the first save is stamped", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, DateTimeOffset.UtcNow, context.OwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            List<IEvent> events =
            [
                .. await session.Events.FetchStreamAsync(context.OwnerId, token: cts.Token),
                .. await session.Events.FetchStreamAsync(context.NodeId, token: cts.Token),
                .. await session.Events.FetchStreamAsync(taskId, token: cts.Token),
            ];
            Type[] expected = [typeof(OwnerRegistered), typeof(OwnerRootClaimed), typeof(NodeRegistered), typeof(TaskAdded)];
            events.Select(e => e.Data.GetType()).Should().Equal(expected);

            foreach (IEvent @event in events)
            {
                @event.Headers.Should().NotBeNull();
                @event.Headers![EventOriginStampingListener.NodeIdHeader].Should().Be(
                    context.NodeId.ToString(), "{0} was saved with its own node's registration", @event.EventTypeName);
                @event.Headers[EventOriginStampingListener.OwnerRootFingerprintHeader].Should().Be(
                    fingerprint, "{0} was saved with its owner's root claim", @event.EventTypeName);
            }
        }
    }

    /// <summary>
    /// <c>h9k project join</c>'s own shape: an owner already on file claims a new root and another
    /// event (join's own <see cref="NodeOwnerClaimed"/>, a task here) lands in the same save, so the
    /// stored <see cref="OwnerDetails"/> row still names the previous root while the listener runs.
    /// </summary>
    [Fact]
    public async Task Events_saved_in_the_same_batch_as_a_root_claim_carry_the_claimed_root()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        const string previousFingerprint = "previous-root-fingerprint";
        const string claimedFingerprint = "claimed-root-fingerprint";
        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
                node.OwnerId, token: cts.Token))!;
            session.Events.Append(
                node.OwnerId, OwnerDecider.ClaimRoot(owner, previousFingerprint, verified: true, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
                node.OwnerId, token: cts.Token))!;
            session.Events.Append(
                node.OwnerId, OwnerDecider.ClaimRoot(owner, claimedFingerprint, verified: false, DateTimeOffset.UtcNow));

            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Prove a same-batch claim is stamped", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, DateTimeOffset.UtcNow, node.OwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IEvent claim = (await session.Events.FetchStreamAsync(node.OwnerId, token: cts.Token))[^1];
            claim.Data.Should().BeOfType<OwnerRootClaimed>()
                .Which.RootFingerprint.Should().Be(claimedFingerprint, "the claim is the owner stream's newest event");
            IEvent taskAdded = (await session.Events.FetchStreamAsync(taskId, token: cts.Token)).Single();

            IEvent[] stamped = [claim, taskAdded];
            foreach (IEvent @event in stamped)
            {
                @event.Headers![EventOriginStampingListener.NodeIdHeader].Should().Be(node.NodeId.ToString());
                @event.Headers[EventOriginStampingListener.OwnerRootFingerprintHeader].Should().Be(
                    claimedFingerprint, "{0} was saved with the claim, not before it", @event.EventTypeName);
            }
        }
    }

    /// <summary>
    /// The real ordering <c>h9k project add</c> and <c>h9k project join</c> both produce, neither
    /// artificially folded into one save the way the fixture above is: bootstrap's own save (here,
    /// <see cref="NodeBootstrapSeed.NewNodeAsync"/>'s call to <c>NodeContext.InitializeAsync</c>)
    /// commits and returns before any root is ever claimed, because Marten can only
    /// <c>AggregateStreamAsync</c> the Owner stream a root claim needs once that stream is actually
    /// committed — the same constraint <c>ProjectJoinCommand</c>'s own doc names for flushing
    /// bootstrap first. <see cref="EventOriginStampingListener.UnclaimedOwnerRootFingerprint"/>
    /// is what those bootstrap events carry, and it stays that way permanently: the store is
    /// append-only, so a root claimed moments later in a second save never reaches back to rewrite
    /// a header already committed.
    /// </summary>
    [Fact]
    public async Task Bootstrap_events_saved_before_any_root_is_claimed_keep_the_unclaimed_sentinel_forever()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerAggregate owner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
                node.OwnerId, token: cts.Token))!;
            session.Events.Append(
                node.OwnerId, OwnerDecider.ClaimRoot(owner, "claimed-after-bootstrap", verified: true, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IReadOnlyList<IEvent> ownerStream = await session.Events.FetchStreamAsync(node.OwnerId, token: cts.Token);
            IEvent ownerRegistered = ownerStream.Single(e => e.Data is OwnerRegistered);
            IEvent nodeRegistered = (await session.Events.FetchStreamAsync(node.NodeId, token: cts.Token))
                .Single(e => e.Data is NodeRegistered);

            foreach (IEvent bootstrapEvent in new[] { ownerRegistered, nodeRegistered })
            {
                bootstrapEvent.Headers![EventOriginStampingListener.OwnerRootFingerprintHeader].Should().Be(
                    EventOriginStampingListener.UnclaimedOwnerRootFingerprint,
                    "{0} was saved before any root was claimed, and nothing ever rewrites a committed header",
                    bootstrapEvent.EventTypeName);
            }

            IEvent claim = ownerStream.Single(e => e.Data is OwnerRootClaimed);
            claim.Headers![EventOriginStampingListener.OwnerRootFingerprintHeader].Should().Be("claimed-after-bootstrap");
        }
    }

    /// <summary>
    /// A platform genuinely built to support more than one registered owner
    /// (<c>OwnerResolver</c>'s own "more than one owner is registered, so name the one you mean")
    /// means an unfiltered <c>Take(1)</c> over every <see cref="OwnerDetails"/> row can resolve to
    /// a different owner than the one this machine's own node actually belongs to — every event
    /// stamped with a confidently wrong root fingerprint rather than an honestly unknown one
    /// (follow-up review finding, PR #370). The listener must resolve the owner through
    /// <see cref="NodeDetails.OwnerId"/>, not whichever <see cref="OwnerDetails"/> row a bare
    /// query happens to return first.
    /// </summary>
    [Fact]
    public async Task An_events_stamped_fingerprint_is_this_nodes_own_owner_even_when_another_owner_exists()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);

        // Registered, and its root claimed, before this machine's own node ever bootstraps — the
        // shape that lets an unfiltered "first owner returned" resolve to the wrong one.
        Guid otherOwnerId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerRegistered otherOwnerRegistered = OwnerDecider.Register(
                otherOwnerId, "someone-elses-machine", null, DateTimeOffset.UtcNow);
            session.Events.StartStream<OwnerAggregate>(otherOwnerId, otherOwnerRegistered);
            NodeRegistered otherNodeRegistered = NodeDecider.Register(
                DomainId.New(), otherOwnerId, "someone-elses-machine", "linux", DateTimeOffset.UtcNow);
            session.Events.StartStream<NodeAggregate>(otherNodeRegistered.Id, otherNodeRegistered);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerAggregate otherOwner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
                otherOwnerId, token: cts.Token))!;
            session.Events.Append(
                otherOwnerId, OwnerDecider.ClaimRoot(otherOwner, "someone-elses-fingerprint", verified: true, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        const string thisOwnersFingerprint = "this-nodes-own-fingerprint";
        await using (IDocumentSession session = store.LightweightSession())
        {
            OwnerAggregate thisOwner = (await session.Events.AggregateStreamAsync<OwnerAggregate>(
                node.OwnerId, token: cts.Token))!;
            session.Events.Append(
                node.OwnerId, OwnerDecider.ClaimRoot(thisOwner, thisOwnersFingerprint, verified: true, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid taskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Prove the owner resolved is this node's own", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, DateTimeOffset.UtcNow, node.OwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            IEvent taskAdded = (await session.Events.FetchStreamAsync(taskId, token: cts.Token)).Single();
            taskAdded.Headers![EventOriginStampingListener.NodeIdHeader].Should().Be(node.NodeId.ToString());
            taskAdded.Headers[EventOriginStampingListener.OwnerRootFingerprintHeader].Should().Be(
                thisOwnersFingerprint, "the event belongs to this node's own owner, never the other registered owner");
        }
    }
}
