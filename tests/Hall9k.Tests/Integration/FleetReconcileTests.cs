using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Weasel.Core;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Task 252bc5cf: the nodes of one owner's fleet reconcile a project's whole history with each
/// other on their own, in both directions and from genesis. Driven through the seam the catch-up
/// suite already established — a shared <see cref="InMemoryMessageTransport"/> and a
/// <see cref="FakeLedger"/>, never a real repository or remote (Brian's 2026-09-13 testing rule) —
/// with two genuinely separate Marten stores standing in for two nodes of the same owner on one
/// Postgres container.
/// <para>
/// The invariant under test: every node in an owner's fleet holds every fleet- and team-scoped
/// event of each project it registers, so any one of them is a sufficient answerer for a new
/// teammate's bootstrap. Origin (Brian, 2026-09-21): the Mac held almost none of arx-platform's
/// history while the Windows node held all of it.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class FleetReconcileTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "/repo-shared-fleet-reconcile";
    private const string OurOwnerRoot = "our-owner-root-fingerprint";
    private const string TeammateOwnerRoot = "teammate-owner-root-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    private readonly PostgresFixture _postgres;

    public FleetReconcileTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentStore OpenStore(string schemaName) => DocumentStore.For(opts =>
    {
        opts.Connection(_postgres.ConnectionString);
        opts.DatabaseSchemaName = schemaName;
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// The criterion this whole task exists for, end to end: the sweep's own rule asks each fleet
    /// sibling once, the answering sibling asks back without a human typing anything, the answer
    /// arrives from the start of the answering node's log, and the peer's own terminal envelope is
    /// what marks the reconcile complete. Then the two guards: a second sweep queues nothing, and
    /// the reverse ask never becomes a third ask.
    /// </summary>
    [Fact]
    public async Task Two_fleet_siblings_reconcile_in_both_directions_once_each_and_complete_on_the_terminal_envelope()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeHolding = DomainId.New();
        Guid nodeThin = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeHolding, cts.Token);
        await SeedNodeFileAsync(ledger, nodeThin, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter holdingCommitter, LedgerSigningKey holdingKey) = Signing("holding");
        (LedgerCommitter thinCommitter, LedgerSigningKey thinKey) = Signing("thin");
        TrustChain chain = OurFleet(nodeHolding, nodeThin);

        await using DocumentStore thinStore = OpenStore("fleet_reconcile_thin");

        // The holding node: registered, replication switched on only AFTER its history exists, so
        // every one of those events sits at or below its own switch-on point — the exact shape a
        // gap-fill, the one automatic ask still held above that point, could never be answered
        // with, and the reason a reconcile carries the explicit bound rather than a bootstrap's
        // empty one, which is minted once in a node's life and so is no sweep-time rule at all.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeHolding, new NodeRegistered(nodeHolding, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid teamTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);
        Guid fleetIdeaId = await SeedIdeaAsync(
            _postgres.Store, projectId, ownerId, "A fleet-scoped note", ReplicationScope.Fleet, Now.AddSeconds(2), cts.Token);

        long switchOnSequence;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            switchOnSequence = await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeHolding, Now.AddSeconds(3), cts.Token);
        }

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            IReadOnlyList<JasperFx.Events.IEvent> history = await session.Events.QueryAllRawEvents()
                .Where(candidate => candidate.StreamId == teamTaskId || candidate.StreamId == fleetIdeaId)
                .ToListAsync(cts.Token);
            history.Should().NotBeEmpty();
            history.Should().OnlyContain(
                candidate => candidate.Sequence <= switchOnSequence,
                "this test only means anything if the history really does sit below the switch-on point — that is "
                + "the whole reason a reconcile carries a bound its peer serves from the start of its own log");
        }

        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeThin, new NodeRegistered(nodeThin, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // One sweep of the thin node: one ask per fleet sibling with no reconcile record.
        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            int asked = await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeThin, OurOwnerRoot, chain, Retention, Now.AddSeconds(4), cts.Token);
            asked.Should().Be(1, "one sibling, one ask");
        }

        // A second sweep, same tick's worth of state: the record's own existence is the guard.
        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            int askedAgain = await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeThin, OurOwnerRoot, chain, Retention, Now.AddSeconds(5), cts.Token);
            askedAgain.Should().Be(0, "a fleet peer with a record is never asked again by the sweep");
        }

        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeThin, projectId, "shared-project-key", adoptUnassigned: false,
                thinCommitter, thinKey, Now.AddSeconds(6), cts.Token);
        }

        IReadOnlyList<MessageEnvelopeV1> thinOutbox = await OutboxAsync(transport, nodeThin, cts.Token);
        MessageEnvelopeV1 ask = thinOutbox.Single(envelope => envelope.Kind == MessageKind.EventsRequest);
        ask.To.Should().Be(
            MessageAudience.Node(nodeHolding),
            "a reconcile goes to the one sibling, never broadcast to every project member");
        EventReplicationCodec.EventsRequestRecord decodedAsk = EventReplicationCodec.DecodeRequest(ask.Body)!;
        decodedAsk.SinceGlobalSequence.Should().Be(0, "the existing explicit shape, which lifts the switch-on bound");
        decodedAsk.ForStreamId.Should().BeNull();
        decodedAsk.ForOriginNodeId.Should().BeNull();
        decodedAsk.IsExplicitAsk.Should().BeTrue();

        // The holding node reads the ask: it queues the reverse ask back, then answers in full.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeThin, projectId, nodeHolding, OurOwnerRoot, Now.AddSeconds(7), chain,
                cts.Token);
            read.RequestsAnswered.Should().Be(1);
            read.ReverseAsksQueued.Should().Be(1, "reconciling is symmetric, so the answering sibling asks back");
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(8), cts.Token);
        }

        IReadOnlyList<MessageEnvelopeV1> holdingOutbox = await OutboxAsync(transport, nodeHolding, cts.Token);
        holdingOutbox.Count(envelope => envelope.Kind == MessageKind.EventsAnswerComplete).Should().Be(
            1, "one answer, one terminal envelope");
        MessageEnvelopeV1 terminal = holdingOutbox.Single(envelope => envelope.Kind == MessageKind.EventsAnswerComplete);
        terminal.To.Should().Be(MessageAudience.Node(nodeThin));
        EventReplicationCodec.EventsAnswerCompleteRecord complete =
            EventReplicationCodec.DecodeAnswerComplete(terminal.Body)!;
        complete.RequestId.Should().Be(decodedAsk.RequestId);
        complete.EnvelopeCount.Should().Be(
            holdingOutbox.Count(envelope => envelope.Kind == MessageKind.Events),
            "the count it claims is the number of batches it actually queued");

        // The thin node applies the answer, then reads the protocol envelopes behind it.
        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            EventReplicationReadResult applied = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeThin, OurOwnerRoot, Now.AddSeconds(9), chain,
                cts.Token);
            applied.EventsApplied.Should().BeGreaterThan(
                0, "the answer serves from the start of the answering node's log, switch-on point and all");
        }

        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeThin, OurOwnerRoot, Now.AddSeconds(10), chain,
                cts.Token);
            read.ReconcilesCompleted.Should().Be(1, "the terminal envelope is what completes it");
            read.ReverseAsksQueued.Should().Be(
                0, "the thin node already holds a record for this sibling, so the reverse ask is never a third ask");
        }

        await using (IQuerySession session = thinStore.QuerySession())
        {
            FleetProjectReconcile record = await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeHolding, projectId), cts.Token)
                ?? throw new InvalidOperationException("the reconcile record is missing");

            record.PeerNodeId.Should().Be(nodeHolding);
            record.ProjectId.Should().Be(projectId);
            record.AskedAt.Should().Be(Now.AddSeconds(4));
            record.FirstAnswerAt.Should().Be(Now.AddSeconds(9));
            record.CompletedAt.Should().Be(Now.AddSeconds(10));
            record.EnvelopesRead.Should().Be(record.AnswerEnvelopeCount, "every batch the peer sent was read here");
            record.RecordsApplied.Should().BeGreaterThan(0);
            record.UnavailableReason.Should().BeNull();
            FleetReconcileRules.NeedsReAsk(record, Retention, Now.AddDays(30)).Should().BeFalse(
                "a completed reconcile is never re-asked");

            // Both a team-scoped task and a fleet-scoped idea crossed, which is the point: a fleet
            // sibling holds everything, drafts and ideas included.
            (await session.Events.FetchStreamStateAsync(teamTaskId, cts.Token)).Should().NotBeNull();
            (await session.Events.FetchStreamStateAsync(fleetIdeaId, cts.Token)).Should().NotBeNull(
                "a fleet-scoped idea answers a requester of the same owner");
        }

        // The other direction, from the holding node's own record of the reverse ask it queued.
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            FleetProjectReconcile reverse = await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeThin, projectId), cts.Token)
                ?? throw new InvalidOperationException("the reverse reconcile record is missing");
            reverse.PeerNodeId.Should().Be(nodeThin);
            reverse.AskedAt.Should().Be(Now.AddSeconds(7));
        }

        // And the ask count over the whole exchange, from the wire itself: exactly one each way.
        (await OutboxAsync(transport, nodeThin, cts.Token))
            .Count(envelope => envelope.Kind == MessageKind.EventsRequest).Should().Be(1);
        (await OutboxAsync(transport, nodeHolding, cts.Token))
            .Count(envelope => envelope.Kind == MessageKind.EventsRequest).Should().Be(
                1, "two nodes settle into one exchange each way rather than asking each other forever");
    }

    /// <summary>
    /// A project-history request from a teammate's node is answered and nothing more. It is not this
    /// owner's business to hold everything a teammate holds, and a reverse ask there would fetch a
    /// whole project from somebody whose fleet this node is not in.
    /// </summary>
    [Fact]
    public async Task A_request_from_a_node_outside_the_fleet_queues_no_reverse_ask()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeHolding = DomainId.New();
        Guid teammateNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeHolding, cts.Token);
        await SeedNodeFileAsync(ledger, teammateNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter teammateCommitter, LedgerSigningKey teammateKey) = Signing("teammate");
        (LedgerCommitter holdingCommitter, LedgerSigningKey holdingKey) = Signing("holding");

        // Our fleet is the holding node alone; the teammate's node sits under a different root.
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner>
            {
                [OurOwnerRoot] = new TrustedOwner(
                    OurOwnerRoot, "ssh-ed25519 AAAAFAKEOURS ours", [], RootNodeId: nodeHolding.ToString()),
                [TeammateOwnerRoot] = new TrustedOwner(
                    TeammateOwnerRoot, "ssh-ed25519 AAAAFAKETEAM teammate",
                    [new TrustedNode(teammateNode.ToString(), $"ssh-ed25519 AAAAFAKE{teammateNode:N} test", SeededFingerprintOf(teammateNode), Now)]),
            },
            [
                new ProjectMember(OurOwnerRoot, MembershipRole.Owner, Now),
                new ProjectMember(TeammateOwnerRoot, MembershipRole.Member, Now),
            ]);

        await using DocumentStore teammateStore = OpenStore("fleet_reconcile_teammate");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeHolding, new NodeRegistered(nodeHolding, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        // The teammate asks the ordinary way a human does: h9k project pull --since all.
        await using (IDocumentSession session = teammateStore.LightweightSession())
        {
            (await new EventCatchUpCoordinator().RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, teammateNode, TeammateOwnerRoot, Now.AddSeconds(2),
                cts.Token)).Should().BeTrue();
            await messageOutbox.FlushAsync(
                session, RepositoryPath, teammateNode, projectId, "shared-project-key", adoptUnassigned: false,
                teammateCommitter, teammateKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, teammateNode, projectId, nodeHolding, OurOwnerRoot, Now.AddSeconds(3), chain,
                cts.Token);
            read.RequestsAnswered.Should().Be(1, "a teammate's pull is still answered in full");
            read.ReverseAsksQueued.Should().Be(0, "a node outside this owner's fleet earns no reverse ask");
        }

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            (await session.Query<FleetProjectReconcile>().ToListAsync(cts.Token)).Should().BeEmpty(
                "no reconcile record is written for a node that is not a fleet sibling");
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(4), cts.Token);
        }

        (await OutboxAsync(transport, nodeHolding, cts.Token))
            .Should().NotContain(envelope => envelope.Kind == MessageKind.EventsRequest);
    }

    /// <summary>
    /// The exclusion that never bends, proven against the existing responder rather than by new
    /// responder code (this task's own criterion): a private idea never travels, however explicit
    /// the ask, while a fleet-scoped one beside it does answer a sibling of the same owner.
    /// </summary>
    [Fact]
    public async Task A_private_idea_is_never_served_while_a_fleet_scoped_one_beside_it_is()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeHolding = DomainId.New();
        Guid nodeThin = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeHolding, cts.Token);
        await SeedNodeFileAsync(ledger, nodeThin, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter holdingCommitter, LedgerSigningKey holdingKey) = Signing("holding");
        TrustChain chain = OurFleet(nodeHolding, nodeThin);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeHolding, new NodeRegistered(nodeHolding, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid fleetIdeaId = await SeedIdeaAsync(
            _postgres.Store, projectId, ownerId, "A fleet note", ReplicationScope.Fleet, Now.AddSeconds(1), cts.Token);
        Guid privateIdeaId = await SeedIdeaAsync(
            _postgres.Store, projectId, ownerId, "Keep this on one machine", ReplicationScope.Private,
            Now.AddSeconds(2), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await responder.AnswerAsync(
                session, RepositoryPath, nodeHolding, OurOwnerRoot, projectId, nodeThin,
                new EventReplicationCodec.EventsRequestRecord(
                    DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null,
                    SinceGlobalSequence: 0),
                Now.AddSeconds(3), chain, cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(4), cts.Token);
        }

        List<Guid> served = [];
        foreach (MessageEnvelopeV1 envelope in await OutboxAsync(transport, nodeHolding, cts.Token))
        {
            if (envelope.Kind == MessageKind.Events)
            {
                served.AddRange(EventReplicationCodec.DecodeBatch(envelope.Body)!.Select(record => record.StreamId));
            }
        }

        served.Should().Contain(fleetIdeaId, "a fleet-scoped idea is exactly what a fleet sibling is owed");
        served.Should().NotContain(privateIdeaId, "a private item never travels, however explicit the ask");
    }

    /// <summary>
    /// A stream this node holds only the tail of is the one shape a whole-project answer cannot
    /// repair: a replicated event is appended, and the head cannot be put in front of a tail already
    /// here. The reconcile counts it and <c>h9k status</c> names it, rather than the reconcile
    /// reading as a clean completion over a stream that is still broken.
    /// </summary>
    [Fact]
    public async Task A_stream_held_tail_only_is_counted_on_the_record_rather_than_silently_skipped()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeHolding = DomainId.New();
        Guid nodeThin = DomainId.New();
        Guid otherOriginNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeHolding, cts.Token);
        await SeedNodeFileAsync(ledger, nodeThin, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter holdingCommitter, LedgerSigningKey holdingKey) = Signing("holding");
        (LedgerCommitter thinCommitter, LedgerSigningKey thinKey) = Signing("thin");
        TrustChain chain = OurFleet(nodeHolding, nodeThin);

        await using DocumentStore thinStore = OpenStore("fleet_reconcile_held_tail");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeHolding, new NodeRegistered(nodeHolding, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid servedTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        // The thin node already holds a genesis-less tail for a stream NOBODY in this exchange will
        // ever supply the head of: the origin is a third node with no outbox here at all, which is
        // exactly what a squash on the far side leaves behind.
        Guid headlessStreamId = DomainId.New();
        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeThin, new NodeRegistered(nodeThin, ownerId, "the-mac", "macOS", Now));
            session.Store(new HeldReplicatedEventRecord
            {
                Id = DomainId.New(),
                StreamId = headlessStreamId,
                ProjectId = projectId,
                SenderNodeId = otherOriginNode,
                OriginProjectKey = "shared-project-key",
                RecordJson = "{}",
                OriginSequence = 42,
                OriginNodeId = otherOriginNode,
                HeldAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeThin, OurOwnerRoot, chain, Retention, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeThin, projectId, "shared-project-key", adoptUnassigned: false,
                thinCommitter, thinKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeThin, projectId, nodeHolding, OurOwnerRoot, Now.AddSeconds(4), chain,
                cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = thinStore.LightweightSession())
        {
            EventReplicationReadResult applied = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeThin, OurOwnerRoot, Now.AddSeconds(6), chain,
                cts.Token);
            applied.EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = thinStore.QuerySession())
        {
            FleetProjectReconcile record = await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeHolding, projectId), cts.Token)
                ?? throw new InvalidOperationException("the reconcile record is missing");

            record.RecordsApplied.Should().BeGreaterThan(0, "the served stream did arrive");
            record.HeldTailOnlyStreams.Should().Be(
                1, "the stream whose genesis no answer carried is counted, never silently skipped");

            (await session.Events.FetchStreamStateAsync(servedTaskId, cts.Token)).Should().NotBeNull();
            (await session.Events.FetchStreamStateAsync(headlessStreamId, cts.Token)).Should().BeNull(
                "a held tail is still held — a reconcile cannot put history in front of it");
        }
    }

    /// <summary>
    /// A brand-new node's automatic bootstrap and its reconcile with the same peer are never in
    /// flight at once: the reconcile waits while the bootstrap is outstanding. It is a wait and
    /// never a substitution, and this test is about why. A bootstrap is addressed to one ranked
    /// candidate and closes on the first envelope from that candidate that applies anything, which
    /// is no statement about whether the rest of the answer arrived — so treating an answered
    /// bootstrap as that pair's reconcile would mark the pair settled on a partial answer, on the
    /// most common path a new fleet node takes: bootstrapping off the one sibling that holds all
    /// the history (independent pre-PR review, cycle 1, adversarial lens, medium). The reconcile
    /// is the exchange that ends on its peer's own terminal envelope and leaves a record saying
    /// when the pair came to hold the same thing.
    /// <para>
    /// How far each shape reaches below an answering node's switch-on point is Decisions Log
    /// #260's own coverage in <c>EventCatchUpTests</c> rather than this test's. A bootstrap is
    /// served from genesis there too now, which narrows what the second ask buys without removing
    /// it, and is why this test says nothing about that bound.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_bootstrap_is_followed_by_that_pairs_own_reconcile_rather_than_standing_in_for_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeHolding = DomainId.New();
        Guid nodeNew = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeHolding, cts.Token);
        await SeedNodeFileAsync(ledger, nodeNew, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        EventCatchUpCoordinator coordinator = new();
        (LedgerCommitter holdingCommitter, LedgerSigningKey holdingKey) = Signing("holding");
        (LedgerCommitter newCommitter, LedgerSigningKey newKey) = Signing("new");
        TrustChain chain = OurFleet(nodeHolding, nodeNew);

        await using DocumentStore newStore = OpenStore("fleet_reconcile_bootstrap");

        // The holding node, and the one piece of history the new node has to end up holding.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeHolding, new NodeRegistered(nodeHolding, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid heldTaskId = await SeedQueuedTaskAsync(
            _postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeNew, new NodeRegistered(nodeNew, ownerId, "the-new-laptop", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The brand-new node's own bootstrap, ranked to the one sibling it knows.
        await using (IDocumentSession session = newStore.LightweightSession())
        {
            (await coordinator.RequestBootstrapAsync(
                session, projectId, nodeNew, OurOwnerRoot, [nodeHolding], TimeSpan.FromMinutes(5), Now.AddSeconds(2),
                cts.Token)).Should().BeTrue();
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            int asked = await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeNew, OurOwnerRoot, chain, Retention, Now.AddSeconds(3), cts.Token);
            asked.Should().Be(
                0, "while a bootstrap addressed to that peer is outstanding the reconcile for that peer waits");
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeNew, projectId, "shared-project-key", adoptUnassigned: false,
                newCommitter, newKey, Now.AddSeconds(4), cts.Token);
        }

        (await OutboxAsync(transport, nodeNew, cts.Token))
            .Count(envelope => envelope.Kind == MessageKind.EventsRequest).Should().Be(
                1, "one ask went out, not two asks for the same whole project");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeNew, projectId, nodeHolding, OurOwnerRoot, Now.AddSeconds(5), chain,
                cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            EventReplicationReadResult applied = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeNew, OurOwnerRoot, Now.AddSeconds(7), chain,
                cts.Token);
            applied.EventsApplied.Should().BeGreaterThan(0, "the bootstrap is answered from the sibling");
        }

        await using (IQuerySession session = newStore.QuerySession())
        {
            (await session.Events.FetchStreamStateAsync(heldTaskId, cts.Token)).Should().NotBeNull(
                "the one candidate the bootstrap ranked answered it");
            (await session.Query<FleetProjectReconcile>().ToListAsync(cts.Token)).Should().BeEmpty(
                "an answered bootstrap is not this pair's reconcile — it closed on the first envelope that applied "
                + "anything, and nothing about that says the rest of the answer arrived");
        }

        // The bootstrap is closed, so the wait is over and the sweep asks that peer in full.
        await using (IDocumentSession session = newStore.LightweightSession())
        {
            int asked = await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeNew, OurOwnerRoot, chain, Retention, Now.AddSeconds(8), cts.Token);
            asked.Should().Be(1, "once the bootstrap closes the reconcile it was waiting behind goes out");
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeNew, projectId, "shared-project-key", adoptUnassigned: false,
                newCommitter, newKey, Now.AddSeconds(9), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeNew, projectId, nodeHolding, OurOwnerRoot, Now.AddSeconds(10), chain,
                cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(11), cts.Token);
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeNew, OurOwnerRoot, Now.AddSeconds(12), chain,
                cts.Token);
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeNew, OurOwnerRoot, Now.AddSeconds(13), chain,
                cts.Token);
            read.ReconcilesCompleted.Should().Be(1, "the terminal envelope is what completes it");
        }

        await using (IQuerySession session = newStore.QuerySession())
        {
            FleetProjectReconcile record = await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeHolding, projectId), cts.Token)
                ?? throw new InvalidOperationException("the reconcile record is missing");
            record.AskedAt.Should().Be(Now.AddSeconds(8), "the record dates from the reconcile's own ask");
            record.CompletedAt.Should().Be(Now.AddSeconds(13));
        }

        // And the whole exchange cost the new node two asks to that peer, never two at once.
        (await OutboxAsync(transport, nodeNew, cts.Token))
            .Count(envelope => envelope.Kind == MessageKind.EventsRequest).Should().Be(
                2, "the bootstrap, and then the reconcile it waited behind");
    }

    /// <summary>
    /// The reverse ask honours the same wait the sweep does. A sibling's own first ask reaches a
    /// brand-new node while that node's bootstrap to the very same sibling is still outstanding,
    /// which is the ordinary order of events when a node joins a fleet; guarding only the sweep
    /// left this path free to put the whole project in flight from one peer twice at once
    /// (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task A_reverse_ask_waits_while_a_bootstrap_to_that_same_peer_is_outstanding()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeHolding = DomainId.New();
        Guid nodeNew = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeHolding, cts.Token);
        await SeedNodeFileAsync(ledger, nodeNew, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        MessageOutbox messageOutbox = new(transport);
        EventCatchUpCoordinator coordinator = new();
        (LedgerCommitter holdingCommitter, LedgerSigningKey holdingKey) = Signing("holding");
        TrustChain chain = OurFleet(nodeHolding, nodeNew);

        await using DocumentStore newStore = OpenStore("fleet_reconcile_reverse_wait");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeHolding, new NodeRegistered(nodeHolding, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeNew, new NodeRegistered(nodeNew, ownerId, "the-new-laptop", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            (await coordinator.RequestBootstrapAsync(
                session, projectId, nodeNew, OurOwnerRoot, [nodeHolding], TimeSpan.FromMinutes(5), Now.AddSeconds(2),
                cts.Token)).Should().BeTrue();
        }

        // The sibling's own sweep asks first, and its ask reaches the new node before any answer to
        // that bootstrap does.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeHolding, OurOwnerRoot, chain, Retention, Now.AddSeconds(3), cts.Token))
                .Should().Be(1);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeHolding, projectId, "shared-project-key", adoptUnassigned: false,
                holdingCommitter, holdingKey, Now.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeHolding, projectId, nodeNew, OurOwnerRoot, Now.AddSeconds(5), chain,
                cts.Token);
            read.RequestsAnswered.Should().Be(1, "the sibling's ask is still answered, with whatever this node holds");
            read.ReverseAsksQueued.Should().Be(
                0, "the bootstrap to that same sibling is still outstanding, so the reverse ask waits for it");
        }

        await using (IQuerySession session = newStore.QuerySession())
        {
            (await session.Query<FleetProjectReconcile>().ToListAsync(cts.Token)).Should().BeEmpty(
                "no record is written for an ask that was not queued");
            (await session.Query<EventCatchUpRequest>().Where(request => request.ProjectId == projectId)
                .ToListAsync(cts.Token)).Should().ContainSingle()
                .Which.SinceGlobalSequence.Should().BeNull("the bootstrap, and nothing beside it");
        }
    }

    /// <summary>
    /// A sibling that holds nothing for the project gives a final answer rather than going quiet,
    /// and the record keeps that answer in the peer's own words. Without this the retention clock
    /// would re-ask a peer that already said it has nothing, every two days, forever.
    /// </summary>
    [Fact]
    public async Task A_sibling_holding_nothing_declines_and_the_record_says_so_rather_than_being_re_asked()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeEmpty = DomainId.New();
        Guid nodeAsking = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeEmpty, cts.Token);
        await SeedNodeFileAsync(ledger, nodeAsking, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter emptyCommitter, LedgerSigningKey emptyKey) = Signing("empty");
        (LedgerCommitter askingCommitter, LedgerSigningKey askingKey) = Signing("asking");
        TrustChain chain = OurFleet(nodeEmpty, nodeAsking);

        await using DocumentStore askingStore = OpenStore("fleet_reconcile_declined");

        // The empty node is registered and switched on, and holds nothing for this project at all.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeEmpty, new NodeRegistered(nodeEmpty, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeEmpty, Now, cts.Token);
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeAsking, new NodeRegistered(nodeAsking, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now.AddSeconds(1), cts.Token);
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeAsking, projectId, "shared-project-key", adoptUnassigned: false,
                askingCommitter, askingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeAsking, projectId, nodeEmpty, OurOwnerRoot, Now.AddSeconds(3), chain,
                cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeEmpty, projectId, "shared-project-key", adoptUnassigned: false,
                emptyCommitter, emptyKey, Now.AddSeconds(4), cts.Token);
        }

        (await OutboxAsync(transport, nodeEmpty, cts.Token))
            .Should().Contain(envelope => envelope.Kind == MessageKind.EventsUnavailable)
            .And.NotContain(
                envelope => envelope.Kind == MessageKind.EventsAnswerComplete,
                "a decline is already final on its own, so it earns no terminal envelope");

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeEmpty, projectId, nodeAsking, OurOwnerRoot, Now.AddSeconds(5), chain,
                cts.Token);
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeEmpty, projectId), cts.Token)
                ?? throw new InvalidOperationException("the reconcile record is missing");

            record.UnavailableReason.Should().Be("nothing held here matches this request");
            record.CompletedAt.Should().Be(Now.AddSeconds(5));
            FleetReconcileRules.NeedsReAsk(record, Retention, Now.AddDays(30)).Should().BeFalse();
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            int asked = await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now.AddDays(30), cts.Token);
            asked.Should().Be(0, "a peer that already said it holds nothing is not asked again on the clock");
        }
    }

    /// <summary>
    /// The outbox squash window can prune an answer this node's daemon never read, and the request
    /// is already closed on its first envelope, so nothing else would ever ask again. One automatic
    /// re-ask covers that, and the second silence is reported rather than turned into a third ask.
    /// </summary>
    [Fact]
    public async Task An_unanswered_reconcile_is_re_asked_once_and_then_reported_stalled()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeSilent = DomainId.New();
        Guid nodeAsking = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        TrustChain chain = OurFleet(nodeSilent, nodeAsking);

        await using DocumentStore askingStore = OpenStore("fleet_reconcile_stalled");

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeAsking, new NodeRegistered(nodeAsking, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now, cts.Token)).Should().Be(1);
        }

        Guid firstRequestId;
        await using (IQuerySession session = askingStore.QuerySession())
        {
            firstRequestId = (await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeSilent, projectId), cts.Token))!.RequestId;
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now.AddHours(47), cts.Token))
                .Should().Be(0, "inside the squash window the answer may still be on its way");

            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now + Retention, cts.Token))
                .Should().Be(1, "past it the answer has been pruned and nothing else would ever ask again");
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeSilent, projectId), cts.Token))!;
            record.ReAskedAt.Should().Be(Now + Retention);
            record.RequestId.Should().NotBe(firstRequestId, "the re-ask is its own request, with its own id");
            record.StalledAt.Should().BeNull();

            IReadOnlyList<EventCatchUpRequest> asks = await session.Query<EventCatchUpRequest>()
                .Where(request => request.ProjectId == projectId).ToListAsync(cts.Token);
            asks.Should().HaveCount(2, "one ask, then one re-ask, and no more");
            asks.Should().ContainSingle(request => request.IsOutstanding)
                .Which.Id.Should().Be(record.RequestId, "the re-ask is the one live ask");
            asks.Single(request => request.Id == firstRequestId).Should().Match<EventCatchUpRequest>(
                superseded => superseded.SupersededAt == Now + Retention
                    && superseded.SupersededByRequestId == record.RequestId && superseded.AnsweredAt == null,
                "the ask the re-ask replaced is closed rather than left standing outstanding forever — it has no "
                + "candidates, so no cascade would ever clear it — and closed as superseded by the ask that took "
                + "its place rather than as answered, since nothing answered it");
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now + Retention + Retention, cts.Token))
                .Should().Be(0, "once, not on a loop");
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(
                EventReplicationStreamId.ForFleetReconcile(nodeSilent, projectId), cts.Token))!;
            record.StalledAt.Should().Be(
                Now + Retention + Retention, "the second silence is reported so a human can act on it");
            StatusCommand.FleetReconcileLine(record, "arx-platform").Should()
                .Contain("STALLED").And.Contain("h9k project reconcile arx-platform");
        }
    }

    /// <summary>
    /// Only the record's own peer can end its exchange. A reconcile's request id travels in the
    /// clear past every project member — each member's sweep reads every seq above its cursor on the
    /// shared outbox ref and decodes each envelope before it checks the audience — so a teammate's
    /// node can address this node a terminal or a declining envelope carrying that id. Neither is
    /// the peer's last word, and neither may close the record: an unavailable reason would also
    /// spend the one automatic re-ask, ending the exchange on a sentence its peer never said.
    /// </summary>
    [Fact]
    public async Task Only_the_records_own_peer_can_complete_or_decline_its_reconcile()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeAsking = DomainId.New();
        Guid nodeSibling = DomainId.New();
        Guid teammateNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeAsking, cts.Token);
        await SeedNodeFileAsync(ledger, nodeSibling, cts.Token);
        await SeedNodeFileAsync(ledger, teammateNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter teammateCommitter, LedgerSigningKey teammateKey) = Signing("teammate");
        (LedgerCommitter siblingCommitter, LedgerSigningKey siblingKey) = Signing("sibling");
        TrustChain chain = OurFleetBesideATeammateNode(nodeAsking, nodeSibling, teammateNode);
        Guid recordId = EventReplicationStreamId.ForFleetReconcile(nodeSibling, projectId);

        await using DocumentStore askingStore = OpenStore("fleet_reconcile_impostor_asking");
        await using DocumentStore teammateStore = OpenStore("fleet_reconcile_impostor_teammate");

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeAsking, new NodeRegistered(nodeAsking, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, chain, Retention, Now, cts.Token)).Should().Be(1);
        }

        Guid requestId;
        await using (IQuerySession session = askingStore.QuerySession())
        {
            requestId = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!.RequestId;
        }

        // The teammate answers the sibling's exchange as though it were the sibling, twice over.
        await using (IDocumentSession session = teammateStore.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, teammateNode, projectId, TeammateOwnerRoot, MessageAudience.Node(nodeAsking),
                about: requestId.ToString(), MessageKind.EventsAnswerComplete,
                EventReplicationCodec.EncodeAnswerComplete(
                    new EventReplicationCodec.EventsAnswerCompleteRecord(requestId, 41)),
                Now.AddSeconds(1), cts.Token);
            await MessageOutbox.QueueAsync(
                session, teammateNode, projectId, TeammateOwnerRoot, MessageAudience.Node(nodeAsking),
                about: requestId.ToString(), MessageKind.EventsUnavailable,
                EventReplicationCodec.EncodeUnavailable(
                    new EventReplicationCodec.EventsUnavailableRecord(requestId, "nothing held here matches this request")),
                Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, teammateNode, projectId, "shared-project-key", adoptUnassigned: false,
                teammateCommitter, teammateKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, teammateNode, projectId, nodeAsking, OurOwnerRoot, Now.AddSeconds(4), chain,
                cts.Token);
            read.ReconcilesCompleted.Should().Be(0, "a reconcile is completed by its own peer or by nobody");
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            record.CompletedAt.Should().BeNull("another member's envelope is not this peer's last word");
            record.AnswerEnvelopeCount.Should().BeNull("nor is its claimed count this peer's claim");
            record.UnavailableReason.Should().BeNull(
                "and a reason recorded from the wrong node would spend the one automatic re-ask too");
            FleetReconcileRules.NeedsReAsk(record, Retention, Now + Retention).Should().BeTrue(
                "the exchange is still waiting on the peer that was actually asked");
        }

        // The peer that was actually asked still closes it, on the identical envelope.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeSibling, projectId, OurOwnerRoot, MessageAudience.Node(nodeAsking),
                about: requestId.ToString(), MessageKind.EventsAnswerComplete,
                EventReplicationCodec.EncodeAnswerComplete(
                    new EventReplicationCodec.EventsAnswerCompleteRecord(requestId, 41)),
                Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeSibling, projectId, "shared-project-key", adoptUnassigned: false,
                siblingCommitter, siblingKey, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeSibling, projectId, nodeAsking, OurOwnerRoot, Now.AddSeconds(7), chain,
                cts.Token)).ReconcilesCompleted.Should().Be(1);
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            record.CompletedAt.Should().Be(Now.AddSeconds(7));
            record.AnswerEnvelopeCount.Should().Be(41);
        }
    }

    /// <summary>
    /// A sibling revoked mid-exchange is not re-asked and not reported as a stall: nothing sent to a
    /// node outside this owner's fleet will ever be answered, and the stall line names
    /// <c>h9k project reconcile</c>, which walks the CURRENT fleet and so could never clear the
    /// record. Vouching that node back in is what starts the exchange over, on its own.
    /// </summary>
    [Fact]
    public async Task A_record_whose_peer_left_the_fleet_is_closed_unanswered_and_revives_on_a_re_vouch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeAsking = DomainId.New();
        Guid nodeRevoked = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        TrustChain withSibling = OurFleet(nodeAsking, nodeRevoked);
        TrustChain afterRevoke = OurFleetAlone(nodeAsking);
        Guid recordId = EventReplicationStreamId.ForFleetReconcile(nodeRevoked, projectId);

        await using DocumentStore askingStore = OpenStore("fleet_reconcile_revoked");

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeAsking, new NodeRegistered(nodeAsking, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, withSibling, Retention, Now, cts.Token)).Should().Be(1);
        }

        Guid firstRequestId;
        await using (IQuerySession session = askingStore.QuerySession())
        {
            firstRequestId = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!.RequestId;
        }

        // The case that must NOT retire anything: a chain this read could not see names no peers for
        // the identical reason a genuinely solo fleet does, and reading the two the same way would
        // close every record on this node the first time a ledger read came back thin.
        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, "an-owner-root-nobody-vouched", withSibling, Retention,
                Now + Retention, cts.Token)).Should().Be(0);
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!.PeerLeftFleetAt.Should().BeNull(
                "an unreadable chain is unknown, never a fleet that lost its members");
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, afterRevoke, Retention, Now + Retention, cts.Token))
                .Should().Be(0, "a node that is not a sibling any more earns no re-ask, on the clock or otherwise");
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            record.PeerLeftFleetAt.Should().Be(Now + Retention);
            record.CompletedAt.Should().BeNull("no answer was ever observed, so nothing here claims one");
            record.UnavailableReason.Should().BeNull("that field holds the peer's own words, and it said nothing");
            record.StalledAt.Should().BeNull();

            EventCatchUpRequest abandoned = (await session.LoadAsync<EventCatchUpRequest>(firstRequestId, cts.Token))!;
            abandoned.Should().Match<EventCatchUpRequest>(
                ask => ask.Exhausted && ask.AnsweredAt == null,
                "the ask it was waiting on is closed rather than left outstanding forever — it has no "
                + "candidates, so no cascade would ever clear it");

            // What h9k status then says about such a record — that it is closed rather than stalled,
            // and that it names no command a human could run — is FleetReconcileRulesTests's own
            // A_record_whose_peer_left_the_fleet_is_closed_rather_than_re_asked_and_stalled, through
            // the same StatusCommand.FleetReconcileLine seam. This test's job is the pass that
            // produces the record.
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, afterRevoke, Retention, Now + Retention + Retention,
                cts.Token)).Should().Be(0, "retired once, and never re-reported after that");
        }

        await using (IDocumentSession session = askingStore.LightweightSession())
        {
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeAsking, OurOwnerRoot, withSibling, Retention, Now.AddDays(10), cts.Token))
                .Should().Be(1, "vouched back in, the pair reconciles again — the record alone would never ask");
        }

        await using (IQuerySession session = askingStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            record.PeerLeftFleetAt.Should().BeNull();
            record.AskedAt.Should().Be(Now.AddDays(10));
            record.RequestId.Should().NotBe(firstRequestId, "the restart is its own ask, with its own id");
            record.ReAskedAt.Should().BeNull(
                "a peer's return is a new exchange, not a second strike against the one the revoke killed");
        }
    }

    /// <summary>
    /// A stalled direction reopens on the sibling's own ask arriving, which is proof that sibling
    /// came back — the one piece of news the stall was waiting on. Without it, a pair whose asks both
    /// died inside one squash window stayed stalled in this direction for good: the sibling asks and
    /// is answered in full, while this node's own half waits on a human running the hand command.
    /// </summary>
    [Fact]
    public async Task A_stalled_direction_reopens_when_the_sibling_itself_asks_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid nodeStalled = DomainId.New();
        Guid nodeReturning = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeStalled, cts.Token);
        await SeedNodeFileAsync(ledger, nodeReturning, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver(), ledger);
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter returningCommitter, LedgerSigningKey returningKey) = Signing("returning");
        (LedgerCommitter stalledCommitter, LedgerSigningKey stalledKey) = Signing("stalled");
        TrustChain chain = OurFleet(nodeStalled, nodeReturning);
        Guid recordId = EventReplicationStreamId.ForFleetReconcile(nodeReturning, projectId);
        DateTimeOffset stalledAt = Now + Retention + Retention;

        await using DocumentStore stalledStore = OpenStore("fleet_reconcile_reopened");

        // This node asks, re-asks 48 hours later, and is reported stalled 48 hours after that.
        await using (IDocumentSession session = stalledStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeStalled, new NodeRegistered(nodeStalled, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeStalled, OurOwnerRoot, chain, Retention, Now, cts.Token);
            await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeStalled, OurOwnerRoot, chain, Retention, Now + Retention, cts.Token);
            await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeStalled, OurOwnerRoot, chain, Retention, stalledAt, cts.Token);
        }

        Guid stalledRequestId;
        await using (IQuerySession session = stalledStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            record.StalledAt.Should().Be(stalledAt);
            stalledRequestId = record.RequestId;
        }

        // The sibling comes back and asks for its own half of the reconcile.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeReturning, new NodeRegistered(nodeReturning, ownerId, Environment.MachineName, "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeReturning, OurOwnerRoot, chain, Retention, stalledAt.AddSeconds(1), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeReturning, projectId, "shared-project-key", adoptUnassigned: false,
                returningCommitter, returningKey, stalledAt.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = stalledStore.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeReturning, projectId, nodeStalled, OurOwnerRoot, stalledAt.AddSeconds(3),
                chain, cts.Token);
            read.ReverseAsksQueued.Should().Be(1, "a stalled record is the one record a sibling's own ask reopens");
        }

        await using (IQuerySession session = stalledStore.QuerySession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            record.StalledAt.Should().BeNull("the sibling asking is proof it came back");
            record.AskedAt.Should().Be(stalledAt.AddSeconds(3));
            record.RequestId.Should().NotBe(stalledRequestId, "the reopened exchange is its own ask");
            record.ReAskedAt.Should().BeNull("restarting the ladder rather than spending a re-ask already spent");
        }

        // And it cannot ping-pong: the sibling reading this ask finds its own record live, not
        // stalled, so it queues nothing back.
        await using (IDocumentSession session = stalledStore.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeStalled, projectId, "shared-project-key", adoptUnassigned: false,
                stalledCommitter, stalledKey, stalledAt.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult read = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeStalled, projectId, nodeReturning, OurOwnerRoot, stalledAt.AddSeconds(5),
                chain, cts.Token);
            read.ReverseAsksQueued.Should().Be(0, "two nodes settle into one exchange each way, never a third ask");
        }
    }

    /// <summary>
    /// A bootstrap is never addressed to a peer a reconcile is already in flight with. The
    /// reconcile reads the ledger rather than probed outbox tips, so a brand-new node's very first
    /// sweep can queue one before it has any tip to rank a bootstrap candidate from — and the
    /// bootstrap's own wait, which only looks for an outstanding bootstrap, had nothing to wait on a
    /// tick later. Both asks would then have the whole project in flight from that one peer.
    /// </summary>
    [Fact]
    public async Task A_bootstrap_is_never_addressed_to_a_peer_a_reconcile_is_already_in_flight_with()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeNew = DomainId.New();
        Guid nodeSibling = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        TrustChain chain = OurFleet(nodeSibling, nodeNew);
        EventCatchUpCoordinator coordinator = new();
        Guid recordId = EventReplicationStreamId.ForFleetReconcile(nodeSibling, projectId);

        await using DocumentStore newStore = OpenStore("fleet_reconcile_bootstrap_order");

        // Tick one: no outbox tip has moved yet, so the bootstrap has no candidate to rank at all,
        // while the reconcile — which reads the ledger, not tips — asks the sibling.
        await using (IDocumentSession session = newStore.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodeNew, new NodeRegistered(nodeNew, ownerId, "the-mac", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            (await coordinator.RequestBootstrapAsync(
                session, projectId, nodeNew, OurOwnerRoot, candidates: [], Retention, Now, cts.Token))
                .Should().BeFalse("no probed tip, no candidate, no bootstrap");
            (await EventCatchUpCoordinator.RequestFleetReconcilesAsync(
                session, projectId, nodeNew, OurOwnerRoot, chain, Retention, Now, cts.Token)).Should().Be(1);
        }

        // Tick two: the sibling's tip has appeared and this node still has no history of its own.
        IReadOnlyList<Guid> candidates = EventCatchUpCoordinator.RankCandidates(
            [nodeSibling], nodeNew, voucherNodeId: null, chain);
        candidates.Should().BeEquivalentTo(new[] { nodeSibling });

        await using (IDocumentSession session = newStore.LightweightSession())
        {
            (await coordinator.RequestBootstrapAsync(
                session, projectId, nodeNew, OurOwnerRoot, candidates, Retention, Now.AddMinutes(1), cts.Token))
                .Should().BeFalse(
                    "a reconcile with that peer is already fetching the whole project from below its switch-on "
                    + "point, which is strictly more than a bootstrap could answer");
        }

        await using (IQuerySession session = newStore.QuerySession())
        {
            (await session.Query<EventCatchUpRequest>()
                .Where(request => request.ProjectId == projectId && request.SinceGlobalSequence == null)
                .ToListAsync(cts.Token))
                .Should().BeEmpty("the whole project is never in flight from one peer twice at once");
        }

        // Once that reconcile closes, the peer ranks like any other.
        await using (IDocumentSession session = newStore.LightweightSession())
        {
            FleetProjectReconcile record = (await session.LoadAsync<FleetProjectReconcile>(recordId, cts.Token))!;
            FleetReconcileRules.NoteComplete(record, envelopeCount: 0, Now.AddMinutes(2));
            session.Store(record);
            await session.SaveChangesAsync(cts.Token);

            (await coordinator.RequestBootstrapAsync(
                session, projectId, nodeNew, OurOwnerRoot, candidates, Retention, Now.AddMinutes(3), cts.Token))
                .Should().BeTrue();
        }
    }

    /// <summary>Our owner's fleet, as this project's ledger would name it: the root's own node plus
    /// one vouched sibling, with a teammate root present so a "same owner" match cannot pass by
    /// there being only one owner in the chain.</summary>
    private static TrustChain OurFleet(Guid rootNodeId, Guid vouchedNodeId) => new(
        new Dictionary<string, TrustedOwner>
        {
            [OurOwnerRoot] = new TrustedOwner(
                OurOwnerRoot, "ssh-ed25519 AAAAFAKEOURS ours",
                [
                    new TrustedNode(
                        vouchedNodeId.ToString(), $"ssh-ed25519 AAAAFAKE{vouchedNodeId:N} test",
                        SeededFingerprintOf(vouchedNodeId), Now),
                ],
                RootNodeId: rootNodeId.ToString()),
            [TeammateOwnerRoot] = new TrustedOwner(
                TeammateOwnerRoot, "ssh-ed25519 AAAAFAKETEAM teammate", []),
        },
        [
            new ProjectMember(OurOwnerRoot, MembershipRole.Owner, Now),
            new ProjectMember(TeammateOwnerRoot, MembershipRole.Member, Now),
        ]);

    /// <summary>Our owner's fleet after a revoke: this node's own root entry with nothing vouched
    /// into it. A fleet of one that this read genuinely SAW, which is the only reading that may be
    /// taken as "a recorded peer is no longer a sibling" — an unreadable chain names no peers
    /// either, and means nothing at all.</summary>
    private static TrustChain OurFleetAlone(Guid rootNodeId) => new(
        new Dictionary<string, TrustedOwner>
        {
            [OurOwnerRoot] = new TrustedOwner(
                OurOwnerRoot, "ssh-ed25519 AAAAFAKEOURS ours", [], RootNodeId: rootNodeId.ToString()),
            [TeammateOwnerRoot] = new TrustedOwner(
                TeammateOwnerRoot, "ssh-ed25519 AAAAFAKETEAM teammate", []),
        },
        [
            new ProjectMember(OurOwnerRoot, MembershipRole.Owner, Now),
            new ProjectMember(TeammateOwnerRoot, MembershipRole.Member, Now),
        ]);

    /// <summary><see cref="OurFleet"/> with the teammate's own node actually named in the chain, so
    /// an envelope from it arrives from a node this project genuinely recognizes as a member of
    /// somebody else's fleet rather than from one nothing here has heard of.</summary>
    private static TrustChain OurFleetBesideATeammateNode(
        Guid rootNodeId, Guid vouchedNodeId, Guid teammateNodeId) => new(
        new Dictionary<string, TrustedOwner>
        {
            [OurOwnerRoot] = new TrustedOwner(
                OurOwnerRoot, "ssh-ed25519 AAAAFAKEOURS ours",
                [
                    new TrustedNode(
                        vouchedNodeId.ToString(), $"ssh-ed25519 AAAAFAKE{vouchedNodeId:N} test",
                        SeededFingerprintOf(vouchedNodeId), Now),
                ],
                RootNodeId: rootNodeId.ToString()),
            [TeammateOwnerRoot] = new TrustedOwner(
                TeammateOwnerRoot, "ssh-ed25519 AAAAFAKETEAM teammate",
                [
                    new TrustedNode(
                        teammateNodeId.ToString(), $"ssh-ed25519 AAAAFAKE{teammateNodeId:N} test",
                        SeededFingerprintOf(teammateNodeId), Now),
                ]),
        },
        [
            new ProjectMember(OurOwnerRoot, MembershipRole.Owner, Now),
            new ProjectMember(TeammateOwnerRoot, MembershipRole.Member, Now),
        ]);

    private static async Task<IReadOnlyList<MessageEnvelopeV1>> OutboxAsync(
        InMemoryMessageTransport transport, Guid nodeId, CancellationToken cancellationToken)
    {
        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeId, sinceSeq: 0, cancellationToken);
        return [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];
    }

    private static async Task<Guid> SeedQueuedTaskAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, now, ownerId);
        session.Events.StartStream<TaskAggregate>(taskId, added);
        session.Events.Append(taskId, new TaskPublished(taskId, now, ownerId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>An idea at a named scope. A fresh capture is <see cref="ReplicationScope.Fleet"/>,
    /// and narrowing to <see cref="ReplicationScope.Private"/> is a second event on the same stream
    /// — the shape a draft nobody has shared yet actually takes.</summary>
    private static async Task<Guid> SeedIdeaAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, string text, ReplicationScope scope, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Guid ideaId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        IdeaCaptured captured = IdeaDecider.Capture(ideaId, ownerId, text, projectId, now, ProjectHome.None);
        session.Events.StartStream<IdeaAggregate>(ideaId, captured);
        if (scope != ReplicationScope.Fleet)
        {
            IdeaAggregate idea = new();
            idea.Apply(captured);
            session.Events.Append(ideaId, IdeaDecider.SetScope(idea, scope, now, ownerId));
        }

        await session.SaveChangesAsync(cancellationToken);
        return ideaId;
    }

    /// <summary>The fingerprint <c>NodeSelfAnnouncedKeyResolver.ResolveFingerprintAsync</c> resolves
    /// for a node <see cref="SeedNodeFileAsync"/> seeded — the same public key line through the
    /// identical <c>NodeKeyStore.Fingerprint</c> call that resolver itself makes.</summary>
    private static string SeededFingerprintOf(Guid nodeId) => NodeKeyStore.Fingerprint($"ssh-ed25519 AAAAFAKE{nodeId:N} test");

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, CancellationToken cancellationToken)
    {
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("seed");
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeId:N} test\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml", content,
                ExpectedBlobId: null, "seed node file", committer, signingKey),
            cancellationToken);
    }

    private static (LedgerCommitter Committer, LedgerSigningKey SigningKey) Signing(string name) =>
        (new LedgerCommitter(name, $"{name}@hall9k.local"), new LedgerSigningKey($"/dev/null/{name}"));
}
