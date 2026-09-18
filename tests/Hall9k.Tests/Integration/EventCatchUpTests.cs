using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Weasel.Core;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Idea 202383dc, M2b, task 9408d525: a node that finds a numeric gap in a sender's own outbox
/// sequence, or has no local history for a project at all, asks a peer for the events it lacks, and
/// a peer answers from what it holds — forwarding another node's own events with origin and event
/// id intact, never fabricating them. Driven entirely through the seam — a shared
/// <see cref="InMemoryMessageTransport"/> and <see cref="FakeLedger"/>, never a real repository
/// (Brian's 2026-09-13 testing rule) — with up to three genuinely separate Marten stores standing in
/// for three nodes on the identical Postgres container, the pattern <see cref="EventReplicationTests"/>
/// already established.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class EventCatchUpTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "/repo-shared-catchup";
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public EventCatchUpTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentStore OpenStore(string schemaName) => DocumentStore.For(opts =>
    {
        opts.Connection(_postgres.ConnectionString);
        opts.DatabaseSchemaName = schemaName;
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// The round trip criterion M2b's own acceptance list names first: a numeric gap in the sender's
    /// own outbox sequence is detected (<see cref="EventReplicationReadResult.StalledAtSeq"/>), a
    /// gap-fill request goes to a ranked peer, and that peer — who happened to read the missing
    /// content directly from the origin before it was lost — answers from what it holds, with the
    /// origin node's own identity preserved rather than the answering peer's.
    /// </summary>
    [Fact]
    public async Task A_gap_is_detected_and_filled_from_a_peer_who_already_held_the_missing_events()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeC, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerC, LedgerSigningKey signingKeyC) = Signing("node-c");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeC = OpenStore("event_catchup_gap_node_c");
        await using DocumentStore storeB = OpenStore("event_catchup_gap_node_b");

        // Node C answers the gap-fill request below, so its own store needs its own Node stream
        // registered first, and its own switch-on point established EARLY — before it ever applies
        // anything — so everything it later applies by replication counts as post-switch-on:
        // EventCatchUpResponder.AnswerAsync now reads that same point (independent pre-PR review,
        // cycle 1, conformance lens, high) the identical way an ordinary outbox flush already
        // requires it, and establishing it late (only once the responder itself first runs, well
        // after content already arrived) would wrongly exclude everything node C is about to forward.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeC, new NodeRegistered(nodeC, ownerId, "node-c", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeC, Now, cts.Token);
        }

        // Node A: register, switch replication on, then publish task1 — landing at seq 1 on its own outbox.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            // MachineName must be this real process's own Environment.MachineName: EventOriginStampingListener
            // (the origin stamp every appended event carries) resolves "which node produced this" by
            // matching NodeDetails.MachineName against the live process's own machine name, never the
            // node's own id or a caller-supplied label.
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, Environment.MachineName, "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid task1Id = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(2), cts.Token);
        }

        // Node C reads node A's outbox in full, before anything is lost, and holds a real copy of
        // task1's events with node A's own origin preserved.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventReplicationReadResult firstRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            firstRead.EventsApplied.Should().BeGreaterThan(0);
        }

        // Node A then publishes task2 — a second envelope, at seq 2.
        Guid task2Id = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(4), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(5), cts.Token);
        }

        // Node C also reads task2 directly from node A, while node A's own outbox is still intact.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(6), trustChain: null, cts.Token);
            secondRead.EventsApplied.Should().BeGreaterThan(0);
        }

        // Node A's own outbox ref then loses task2's own envelope (seq 2) — a lost push, simulated
        // here by squashing down to seq 1's survivor alone — and node A sends a further, unrelated
        // envelope afterward, so the hole at seq 2 becomes detectable as a numeric gap rather than
        // merely "nothing sent yet".
        TransportReadResult beforeLoss = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        await transport.SquashAsync(
            RepositoryPath, nodeA, [beforeLoss.Envelopes[0]], committerA, signingKeyA, cts.Token);

        Guid task3Id = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(7), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(8), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(8), cts.Token);
        }

        // Node B reads node A's outbox for the very first time: task1's own envelope (seq 1) still
        // applies cleanly, but task2's own envelope is gone and task3's own envelope (seq 3) sits
        // past the hole — a numeric gap this read cannot cross.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(9), trustChain: null, cts.Token);
            read.EventsApplied.Should().BeGreaterThan(0, "task1's own envelope at seq 1 still applies cleanly");
            read.StalledAtSeq.Should().Be(3, "task2's own envelope is gone, so the read stops short of task3's own, later one");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskDetails>(task1Id, cts.Token)).Should().NotBeNull();
            (await session.LoadAsync<TaskDetails>(task2Id, cts.Token)).Should().BeNull("task2's own events never reached node B directly");
        }

        // Node B asks node C — a peer who already held the missing events — to fill the gap.
        bool started;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            started = await coordinator.RequestGapFillAsync(
                session, projectId, forOriginNodeId: nodeA, myNodeId: nodeB, myOwnerFingerprint: "owner-b-fingerprint",
                candidates: [nodeC], TimeSpan.FromMinutes(5), Now.AddSeconds(10), cts.Token);
        }

        started.Should().BeTrue();

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(10), cts.Token);
        }

        // Node C reads node B's outbox, finds the events-request addressed to it, and answers from
        // what it holds — including task2, which it read directly from node A earlier.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(11),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeC, projectId, "shared-project-key", adoptUnassigned: false, committerC,
                signingKeyC, Now.AddSeconds(12), cts.Token);
        }

        // Node B reads node C's own outbox — an ordinary events envelope, not the gapped sender's
        // own ref — and the missing task lands, with node A's own origin preserved rather than node
        // C's.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult fillRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeC, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(13), trustChain: null, cts.Token);
            fillRead.EventsApplied.Should().BeGreaterThan(0, "task2's own events, forwarded by node C, now apply");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails? filled = await session.LoadAsync<TaskDetails>(task2Id, cts.Token);
            filled.Should().NotBeNull("the gap-fill answer from node C brought task2's own stream in");
            filled!.ProjectId.Should().Be(projectId);

            IReadOnlyList<IEvent> streamEvents = await session.Events.FetchStreamAsync(task2Id, token: cts.Token);
            streamEvents.Should().NotBeEmpty();
            streamEvents.Should().OnlyContain(
                e => (e.GetHeader(ReplicationEventHeaders.OriginNodeId) as string) == nodeA.ToString(),
                "task2 originated on node A, and node C's own forwarding must never overwrite that with its own identity");

            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForOriginNodeId == nodeA).FirstOrDefaultAsync(cts.Token))!;
            request.AnsweredAt.Should().NotBeNull("applying new content from the currently-asked candidate marks the request answered");

            // task3 remains unreachable — the numeric hole at seq 2 in node A's own ref is still
            // there, so nothing past it was ever inspected. Only a peer's own separate outbox (node
            // C's, above) can carry content past a permanent hole like this one.
            (await session.LoadAsync<TaskDetails>(task3Id, cts.Token)).Should().BeNull();
        }
    }

    /// <summary>
    /// The second round trip M2b's own acceptance list names: a brand-new node with no local
    /// history for a project at all asks for everything, and bootstraps from a peer's answer —
    /// reaching the identical projection the origin itself has, under its own local project id, and
    /// with the origin's own identity preserved even though the peer that actually answered (node C)
    /// is neither the origin nor the requester.
    /// </summary>
    [Fact]
    public async Task A_new_store_bootstraps_a_project_from_one_peer_and_matches_its_projections()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeC, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerC, LedgerSigningKey signingKeyC) = Signing("node-c");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeC = OpenStore("event_catchup_bootstrap_node_c");
        await using DocumentStore storeB = OpenStore("event_catchup_bootstrap_node_b");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, Environment.MachineName, "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        // Node C answers the bootstrap request below, so its own store needs its own Node stream
        // registered first, and its own switch-on point established EARLY — before it ever applies
        // anything — so everything it later applies by replication counts as post-switch-on:
        // EventCatchUpResponder.AnswerAsync now reads that same point (independent pre-PR review,
        // cycle 1, conformance lens, high) the identical way an ordinary outbox flush already
        // requires it, and establishing it late (only once the responder itself first runs, well
        // after content already arrived) would wrongly exclude everything node C is about to forward.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeC, new NodeRegistered(nodeC, ownerId, "node-c", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeC, Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(2), cts.Token);
        }

        // Node C already has this project's full history — a peer other than the origin, so the
        // bootstrap answer below genuinely forwards someone else's events.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdA, nodeC, "owner-c-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        // Node B is brand new: no local history for this project at all. It asks node C for
        // everything — ForOriginNodeId and ForStreamId both null.
        bool started;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            started = await coordinator.RequestBootstrapAsync(
                session, projectIdB, nodeB, "owner-b-fingerprint", [nodeC], TimeSpan.FromMinutes(5), Now.AddSeconds(4), cts.Token);
        }

        started.Should().BeTrue();

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectIdB, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectIdA, nodeC, "owner-c-fingerprint", Now.AddSeconds(5),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeC, projectIdA, "shared-project-key", adoptUnassigned: false, committerC,
                signingKeyC, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult bootstrapRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeC, projectIdB, nodeB, "owner-b-fingerprint", Now.AddSeconds(7), trustChain: null, cts.Token);
            bootstrapRead.EventsApplied.Should().BeGreaterThan(0, "node C's answer bootstraps node B's own, empty store");
        }

        await using (IQuerySession bSession = storeB.QuerySession())
        await using (IQuerySession aSession = _postgres.Store.QuerySession())
        {
            TaskDetails? bootstrapped = await bSession.LoadAsync<TaskDetails>(taskId, cts.Token);
            TaskDetails original = (await aSession.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            bootstrapped.Should().NotBeNull();
            bootstrapped!.ProjectId.Should().Be(projectIdB, "node B's own local project id, never node A's or node C's foreign one");
            bootstrapped.Objective.Should().Be(original.Objective);
            bootstrapped.State.Should().Be(original.State);

            IReadOnlyList<IEvent> streamEvents = await bSession.Events.FetchStreamAsync(taskId, token: cts.Token);
            streamEvents.Should().NotBeEmpty();
            streamEvents.Should().OnlyContain(
                e => (e.GetHeader(ReplicationEventHeaders.OriginNodeId) as string) == nodeA.ToString(),
                "node C forwarded node A's own events, and must never overwrite their true origin with its own identity");
        }
    }

    /// <summary>
    /// idea 202383dc, M2b: a ranked, candidate-cascading request whose currently-asked peer sits
    /// silent past the per-candidate timeout advances to the next ranked candidate — the original
    /// ask is never retracted, only superseded — and once every candidate has timed out the request
    /// is exhausted. A request still within its own timeout is left standing untouched.
    /// </summary>
    [Fact]
    public async Task A_request_that_times_out_moves_to_the_next_candidate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid nodeD = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeB = OpenStore("event_catchup_timeout_node_b");

        bool started;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            started = await coordinator.RequestGapFillAsync(
                session, projectId, forOriginNodeId: nodeA, myNodeId: nodeB, myOwnerFingerprint: "owner-b-fingerprint",
                candidates: [nodeC, nodeD], TimeSpan.FromMinutes(5), Now, cts.Token);
        }

        started.Should().BeTrue();

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForOriginNodeId == nodeA).FirstOrDefaultAsync(cts.Token))!;
            request.CurrentCandidateNodeId.Should().Be(nodeC, "the first-ranked candidate is asked first");
        }

        // Node C, the first-asked candidate, never answers within the timeout — the request
        // cascades to the next ranked candidate (node D), with the original ask never retracted.
        int advanced;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            advanced = await coordinator.AdvanceOverdueRequestsAsync(
                session, projectId, nodeB, "owner-b-fingerprint", TimeSpan.FromMinutes(5), Now.AddMinutes(6), cts.Token);
        }

        advanced.Should().Be(1);

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForOriginNodeId == nodeA).FirstOrDefaultAsync(cts.Token))!;
            request.CandidateIndex.Should().Be(1);
            request.CurrentCandidateNodeId.Should().Be(nodeD, "node C timed out, so the cascade moves to the next ranked candidate");
            request.Exhausted.Should().BeFalse("a candidate remains");
            request.IsOutstanding.Should().BeTrue();
        }

        // A request still well within its own timeout is left standing — only the one whose timeout
        // actually elapsed advances.
        int notYetOverdue;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            notYetOverdue = await coordinator.AdvanceOverdueRequestsAsync(
                session, projectId, nodeB, "owner-b-fingerprint", TimeSpan.FromMinutes(5), Now.AddMinutes(6).AddSeconds(1), cts.Token);
        }

        notYetOverdue.Should().Be(0, "node D's own clock only just started; nothing is overdue yet");

        // Node D also times out — every ranked candidate is now exhausted.
        int exhausted;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            exhausted = await coordinator.AdvanceOverdueRequestsAsync(
                session, projectId, nodeB, "owner-b-fingerprint", TimeSpan.FromMinutes(5), Now.AddMinutes(12), cts.Token);
        }

        exhausted.Should().Be(1);

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForOriginNodeId == nodeA).FirstOrDefaultAsync(cts.Token))!;
            request.Exhausted.Should().BeTrue("every ranked candidate has now timed out");
            request.IsOutstanding.Should().BeFalse();
        }

        // Every events-request envelope this cascade ever queued is still there for the daemon's own
        // flush — the original ask to node C is never retracted, only superseded.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddMinutes(12), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeB, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().HaveCount(2, "one events-request was queued for node C, then a second for node D");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses, high: a catch-up answer must honor the
    /// identical hold-back <see cref="EventReplicationOutbox.QueuePendingAsync"/> already applies to
    /// a currently-private task's own events — a bootstrap request's own <c>_ =&gt; true</c> match
    /// arm would otherwise match every project-scoped event this node holds, private draft included.
    /// </summary>
    [Fact]
    public async Task A_private_tasks_own_events_are_never_forwarded_by_a_catch_up_answer()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid requesterNodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        InMemoryMessageTransport transport = new(new FakeLedger());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        // Node A answers below, so its own store needs its own Node stream registered first, and its
        // own switch-on point established EARLY — before either task exists — so both tasks count as
        // post-switch-on: EventCatchUpResponder.AnswerAsync now reads that same point (independent
        // pre-PR review, cycle 1, conformance lens, high) the identical way an ordinary outbox flush
        // already requires it, and establishing it late (only once the responder itself first runs,
        // well after both tasks already exist) would wrongly exclude both of them.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeA, Now, cts.Token);
        }

        Guid publicTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now, cts.Token);
        Guid privateTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate privateTask =
                (await session.Events.AggregateStreamAsync<TaskAggregate>(privateTaskId, token: cts.Token))!;
            session.Events.Append(privateTaskId, TaskDecider.SetPrivate(privateTask, isPrivate: true, Now.AddSeconds(2), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        // A brand-new requester's own bootstrap request — ForOriginNodeId and ForStreamId both
        // null, the shape that otherwise matches every project-scoped event node A holds.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int envelopesQueued = await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterNodeId,
                new EventReplicationCodec.EventsRequestRecord(DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null),
                Now.AddSeconds(3), cts.Token);
            envelopesQueued.Should().BeGreaterThan(0, "the public task's own events still answer");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(4), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        foreach (TransportEnvelope raw in read.Envelopes)
        {
            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            decoded.Envelope!.Kind.Should().Be(MessageKind.Events);
            IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord>? batch =
                EventReplicationCodec.DecodeBatch(decoded.Envelope.Body);
            batch.Should().NotBeNull();
            batch!.Should().OnlyContain(
                record => record.StreamId == publicTaskId,
                "the private task's own stream must never ride a catch-up answer");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance lens, high: a bootstrap answer must never
    /// hand a node its own history back — the requester's own dedupe
    /// (<see cref="EventReplicationInbox.ApplyAsync"/>) only ever recognises an event it received BY
    /// REPLICATION, never one it produced natively, so an echo of its own event would apply as a
    /// second, un-deduped copy onto its own stream.
    /// </summary>
    [Fact]
    public async Task A_catch_up_answer_never_echoes_the_requesters_own_events_back_to_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid requesterNodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        InMemoryMessageTransport transport = new(new FakeLedger());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        // Node A answers below, so its own store needs its own Node stream registered first, and its
        // own switch-on point established EARLY — before either task exists — so both tasks count as
        // post-switch-on: EventCatchUpResponder.AnswerAsync now reads that same point (independent
        // pre-PR review, cycle 1, conformance lens, high) the identical way an ordinary outbox flush
        // already requires it, and establishing it late (only once the responder itself first runs,
        // well after both tasks already exist) would wrongly exclude both of them.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeA, Now, cts.Token);
        }

        // ownTaskId's own origin is the REQUESTER itself — node A holds it as though it arrived by
        // replication earlier (the identical way EventReplicationInbox.ApplyAsync stamps a freshly
        // applied record's origin, before that append is ever committed). A bootstrap request from
        // that same node must never get it handed back.
        Guid ownTaskId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                ownTaskId, projectId, "Already known to the requester", ["it ships"], TaskType.Feature, null, null, null,
                Now, ownerId);
            StreamAction action = session.Events.StartStream<TaskAggregate>(ownTaskId, added);
            foreach (IEvent appended in action.Events)
            {
                appended.SetHeader(ReplicationEventHeaders.OriginNodeId, requesterNodeId.ToString());
            }

            await session.SaveChangesAsync(cts.Token);
        }

        Guid otherTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int envelopesQueued = await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterNodeId,
                new EventReplicationCodec.EventsRequestRecord(DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null),
                Now.AddSeconds(2), cts.Token);
            envelopesQueued.Should().BeGreaterThan(0, "the other task's own events still answer");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(3), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        foreach (TransportEnvelope raw in read.Envelopes)
        {
            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord>? batch =
                EventReplicationCodec.DecodeBatch(decoded.Envelope!.Body);
            batch.Should().NotBeNull();
            batch!.Should().OnlyContain(
                record => record.StreamId == otherTaskId,
                "the requester's own originated task must never be handed back to it");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens, high: an explicit decline
    /// (<c>events-unavailable</c>) must actually ask the next candidate, not merely record that the
    /// cascade should have moved — an earlier build here advanced <see cref="EventCatchUpRequest.CandidateIndex"/>
    /// without ever queuing a fresh <see cref="MessageKind.EventsRequest"/> to the newly-current
    /// candidate, silently skipping it until a LATER decline or timeout finally asked the one after it.
    /// </summary>
    [Fact]
    public async Task A_decline_actually_asks_the_next_candidate_rather_than_only_advancing_the_index()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid nodeD = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        await SeedNodeFileAsync(ledger, nodeC, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");
        (LedgerCommitter committerC, LedgerSigningKey signingKeyC) = Signing("node-c");

        await using DocumentStore storeC = OpenStore("event_catchup_decline_node_c");

        // Node C answers (declines) the request below, so its own store needs its own Node stream
        // registered first — EventCatchUpResponder.AnswerAsync now reads its own switch-on point
        // (independent pre-PR review, cycle 1, conformance lens, high) the identical way an ordinary
        // outbox flush already requires it.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeC, new NodeRegistered(nodeC, DomainId.New(), "node-c", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        bool started;
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            started = await coordinator.RequestGapFillAsync(
                session, projectId, forOriginNodeId: nodeA, myNodeId: nodeB, myOwnerFingerprint: "owner-b-fingerprint",
                candidates: [nodeC, nodeD], TimeSpan.FromMinutes(5), Now, cts.Token);
        }

        started.Should().BeTrue();

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now, cts.Token);
        }

        // Node C holds nothing matching this request, so it declines — node B's own cascade must
        // move straight to node D, actually queuing a fresh events-request to it, not merely
        // recording that it should.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(1),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1, "node C did process the request, answering with events-unavailable");
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeC, projectId, "shared-project-key", adoptUnassigned: false, committerC,
                signingKeyC, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventCatchUpInboxReadResult declineRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeC, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: null, cts.Token);
            declineRead.DeclinesObserved.Should().Be(1);
        }

        await using (IQuerySession session = storeC.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForOriginNodeId == nodeA).FirstOrDefaultAsync(cts.Token))!;
            request.CandidateIndex.Should().Be(1);
            request.CurrentCandidateNodeId.Should().Be(nodeD, "node C declined, so the cascade moves to node D");
        }

        // The decline itself must have queued a fresh events-request to node D — not merely
        // recorded that the cascade moved, leaving node D never actually asked until a later
        // timeout.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(4), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeB, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().HaveCount(2, "one events-request was queued for node C, then a second, actually sent, for node D");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, conformance lens, medium: a genuinely empty project (no
    /// peer holds anything to answer with) must not re-mint a fresh bootstrap the instant the
    /// previous one exhausts — every candidate declining immediately (idea 202383dc: "a peer that
    /// cannot answer says so") would otherwise re-arm the cascade every single sweep tick, forever.
    /// A re-mint is still allowed once the cooldown has actually elapsed.
    /// </summary>
    [Fact]
    public async Task A_bootstrap_request_is_not_re_minted_until_its_own_cooldown_elapses()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeB = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid projectId = DomainId.New();
        TimeSpan cooldown = TimeSpan.FromMinutes(5);

        EventCatchUpCoordinator coordinator = new();
        await using DocumentStore storeB = OpenStore("event_catchup_bootstrap_cooldown_node_b");

        bool started;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            started = await coordinator.RequestBootstrapAsync(
                session, projectId, nodeB, "owner-b-fingerprint", [nodeC], cooldown, Now, cts.Token);
        }

        started.Should().BeTrue();

        // Node C is the only candidate and immediately declines (simulated directly, the same
        // outcome an EventsUnavailable answer produces) — the cascade exhausts right away.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            int exhausted = await coordinator.AdvanceOverdueRequestsAsync(
                session, projectId, nodeB, "owner-b-fingerprint", TimeSpan.Zero, Now.AddSeconds(1), cts.Token);
            exhausted.Should().Be(1);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForOriginNodeId == null && r.ForStreamId == null)
                .FirstOrDefaultAsync(cts.Token))!;
            request.Exhausted.Should().BeTrue();
        }

        // The project is still empty this very next tick — without a cooldown this would re-mint
        // immediately and loop forever.
        bool reMintedTooSoon;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            reMintedTooSoon = await coordinator.RequestBootstrapAsync(
                session, projectId, nodeB, "owner-b-fingerprint", [nodeC], cooldown, Now.AddSeconds(2), cts.Token);
        }

        reMintedTooSoon.Should().BeFalse("the previous cascade only just exhausted; re-minting immediately would loop forever");

        // Once the cooldown has genuinely elapsed, a fresh bootstrap is allowed again.
        bool reMintedAfterCooldown;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            reMintedAfterCooldown = await coordinator.RequestBootstrapAsync(
                session, projectId, nodeB, "owner-b-fingerprint", [nodeC], cooldown, Now.Add(cooldown).AddSeconds(1), cts.Token);
        }

        reMintedAfterCooldown.Should().BeTrue("the cooldown has elapsed, so the project may be asked again");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses, medium: a broadcast catch-up request (the
    /// ledger-record adoption path, <see cref="EventCatchUpRequest.Candidates"/> empty) has no single
    /// current candidate to match a sender against, so it must still close once ANY project member's
    /// own answer actually brings the requested stream in — otherwise it sits <c>IsOutstanding</c>
    /// forever, reported by <c>h9k status</c> long after the stream genuinely arrived.
    /// </summary>
    [Fact]
    public async Task A_broadcast_request_closes_once_its_own_stream_arrives()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        await SeedNodeFileAsync(ledger, nodeC, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerC, LedgerSigningKey signingKeyC) = Signing("node-c");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeC = OpenStore("event_catchup_broadcast_node_c");
        await using DocumentStore storeB = OpenStore("event_catchup_broadcast_node_b");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, Environment.MachineName, "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        // Node C answers the broadcast request below, so its own store needs its own Node stream
        // registered first, and its own switch-on point established EARLY — before it ever applies
        // anything — so everything it later applies by replication counts as post-switch-on:
        // EventCatchUpResponder.AnswerAsync now reads that same point (independent pre-PR review,
        // cycle 1, conformance lens, high) the identical way an ordinary outbox flush already
        // requires it, and establishing it late (only once the responder itself first runs, well
        // after content already arrived) would wrongly exclude everything node C is about to forward.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeC, new NodeRegistered(nodeC, ownerId, "node-c", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeC, Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(2), cts.Token);
        }

        // Node C already holds the task directly from node A.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        // Node B adopted a ledger record for this exact stream and broadcasts for it — the
        // TaskAddCommand.RefuseIfRecordedElsewhereAsync path, addressed to the whole project rather
        // than one ranked peer.
        bool started;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            started = await coordinator.RequestStreamBroadcastAsync(
                session, projectId, taskId, nodeB, "owner-b-fingerprint", Now.AddSeconds(4), cts.Token);
        }

        started.Should().BeTrue();

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(4), cts.Token);
        }

        // Node C reads the project-wide broadcast and answers it — anyone's own daemon sweep can.
        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(5),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeC, projectId, "shared-project-key", adoptUnassigned: false, committerC,
                signingKeyC, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult fillRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeC, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(7), trustChain: null, cts.Token);
            fillRead.EventsApplied.Should().BeGreaterThan(0, "node C's answer brings the broadcast stream in");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForStreamId == taskId).FirstOrDefaultAsync(cts.Token))!;
            request.AnsweredAt.Should().NotBeNull(
                "the broadcast's own requested stream actually arrived, so it must close rather than stay outstanding forever");
            request.IsOutstanding.Should().BeFalse();
        }
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
        session.Events.Append(taskId, new TaskAssigned(taskId, ownerId, [], now, ownerId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

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
