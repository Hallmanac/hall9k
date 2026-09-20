using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
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
using Hall9k.Domain.Shared.ValueObjects;
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
            RepositoryPath, nodeA, [beforeLoss.Envelopes[0]], lowWaterMark: beforeLoss.Envelopes[0].Seq, committerA,
            signingKeyA, cts.Token);

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
        FakeLedger ledger = new();
        // Without node A's own node file, InMemoryMessageTransport reports its outbox unvouched and
        // hands back no envelopes at all, so the assertion loop at the end of this test would run
        // over an empty list and check nothing (task a56cf16e, self-review).
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
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
        // A draft, never published — idea 8c5993c5: a published task always reads team scope, and
        // team is one-way, so only a still-fleet draft can legitimately be set private here.
        Guid privateTaskId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                privateTaskId, projectId, "A private draft", ["it ships"], TaskType.Feature, null, null, null,
                Now.AddSeconds(1), ownerId);
            session.Events.StartStream<TaskAggregate>(privateTaskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

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
                Now.AddSeconds(3), trustChain: null, cts.Token);
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

        read.Envelopes.Should().NotBeEmpty("an assertion loop over nothing proves nothing");
    }

    /// <summary>
    /// idea 8c5993c5: "the catch-up responder serves an item only to a requester its scope allows"
    /// — a fleet-scoped task's own events answer a requester of the SAME owner, but never a
    /// requester the trust chain names under a genuinely different owner root, while a team-scoped
    /// task in the same project answers either.
    /// </summary>
    [Fact]
    public async Task A_fleet_scoped_tasks_own_events_answer_only_a_requester_of_the_same_owner()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid requesterSameOwner = DomainId.New();
        Guid requesterDifferentOwner = DomainId.New();

        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeA, Now, cts.Token);
        }

        // A fleet-scoped draft (never published) and a team-scoped published task, side by side.
        Guid fleetTaskId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                fleetTaskId, projectId, "Fleet-only draft", ["it ships"], TaskType.Feature, null, null, null,
                Now.AddSeconds(1), ownerId);
            session.Events.StartStream<TaskAggregate>(fleetTaskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        Guid teamTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(2), cts.Token);

        // The requester's own owner is resolved from the trust chain (idea 8c5993c5) — the SAME
        // owner root vouches for node A and for requesterSameOwner; a genuinely different root
        // vouches for requesterDifferentOwner.
        // The root key matches "owner-a-fingerprint" exactly — the same value passed as
        // AnswerAsync's own myOwnerFingerprint below, since that is what a fleet task's own origin
        // owner fingerprint resolves to here (EventOriginStampingListener stamps no real owner
        // claim in this minimal fixture, so ReplicationEventOriginResolver falls back to it).
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                ["owner-a-fingerprint"] = new TrustedOwner(
                    "owner-a-fingerprint", "ssh-ed25519 AAAAFAKE owner-a-root",
                    [
                        new TrustedNode(nodeA.ToString(), "ssh-ed25519 AAAAFAKE node-a", "node-a-fingerprint", Now),
                        new TrustedNode(requesterSameOwner.ToString(), "ssh-ed25519 AAAAFAKE same-owner", "same-owner-fingerprint", Now),
                    ]),
                ["owner-x-root"] = new TrustedOwner(
                    "owner-x-root", "ssh-ed25519 AAAAFAKE owner-x-root",
                    [new TrustedNode(requesterDifferentOwner.ToString(), "ssh-ed25519 AAAAFAKE different-owner", "different-owner-fingerprint", Now)]),
            },
            [new ProjectMember("owner-a-fingerprint", MembershipRole.Owner, Now), new ProjectMember("owner-x-root", MembershipRole.Member, Now)]);

        EventReplicationCodec.EventsRequestRecord bootstrapRequest =
            new(DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterSameOwner, bootstrapRequest, Now.AddSeconds(3),
                trustChain, cts.Token);
            await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterDifferentOwner, bootstrapRequest, Now.AddSeconds(3),
                trustChain, cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(4), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> envelopes = [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];

        MessageEnvelopeV1 sameOwnerAnswer = envelopes.Single(envelope => envelope.To == MessageAudience.Node(requesterSameOwner));
        EventReplicationCodec.DecodeBatch(sameOwnerAnswer.Body)!.Select(record => record.StreamId).Should()
            .Contain(fleetTaskId, "the same-owner requester is exactly who a fleet item's own fleet reaches")
            .And.Contain(teamTaskId);

        MessageEnvelopeV1 differentOwnerAnswer = envelopes.Single(envelope => envelope.To == MessageAudience.Node(requesterDifferentOwner));
        differentOwnerAnswer.Kind.Should().Be(MessageKind.Events, "the team task still answers this requester");
        EventReplicationCodec.DecodeBatch(differentOwnerAnswer.Body)!.Select(record => record.StreamId).Should()
            .NotContain(fleetTaskId, "a fleet item never answers a requester outside the item's own owner")
            .And.Contain(teamTaskId);
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
        FakeLedger ledger = new();
        // Without node A's own node file, InMemoryMessageTransport reports its outbox unvouched and
        // hands back no envelopes at all, so the assertion loop at the end of this test would run
        // over an empty list and check nothing (task a56cf16e, self-review).
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
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
                Now.AddSeconds(2), trustChain: null, cts.Token);
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

        read.Envelopes.Should().NotBeEmpty("an assertion loop over nothing proves nothing");
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

    /// <summary>
    /// Task a56cf16e's own criterion, and its origin incident: the Mac's own
    /// <c>ReplicationSwitchedOn</c> landed at global sequence 30084 while the task the Windows node
    /// kept asking for sat at 28273 to 30020, so every explicit request for that stream was
    /// answered "nothing held here matches this request" and no re-run could ever have changed it.
    /// An explicit ask — one named stream — now lifts the switch-on exclusion, while the two shapes
    /// a daemon sweep mints on its own, a gap-fill and a bootstrap, still do not. A private task is
    /// refused however explicit the ask, which is the one exclusion that never bends.
    /// </summary>
    [Fact]
    public async Task An_explicit_stream_request_is_answered_from_below_the_switch_on_point_but_a_bootstrap_is_not()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid requesterNodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        FakeLedger ledger = new();
        // Without node A's own node file, InMemoryMessageTransport reports its outbox unvouched and
        // hands back no envelopes at all — an assertion loop over the wire would then pass
        // vacuously, checking nothing.
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Both tasks exist BEFORE this node ever switches replication on, which is exactly the
        // shape that was unservable: every one of their events sits at or below the switch-on
        // sequence recorded below.
        Guid preSwitchOnTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now, cts.Token);
        // Built as a still-draft task rather than through SeedQueuedTaskAsync: publishing now sets
        // team scope unconditionally, and team is one-way, so a published task could never be made
        // private afterward the way this test needs it to be.
        Guid preSwitchOnPrivateTaskId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAdded privateAdded = TaskDecider.Add(
                preSwitchOnPrivateTaskId, projectId, "Keep this quiet", ["stays quiet"], TaskType.Feature, null, null,
                null, Now.AddSeconds(1), ownerId);
            TaskAggregate privateTask = new();
            privateTask.Apply(privateAdded);
            TaskScopeSet madePrivate = TaskDecider.SetPrivate(privateTask, isPrivate: true, Now.AddSeconds(2), ownerId);
            session.Events.StartStream<TaskAggregate>(preSwitchOnPrivateTaskId, privateAdded, madePrivate);
            await session.SaveChangesAsync(cts.Token);
        }

        long switchOnSequence;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            switchOnSequence = await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeA, Now.AddSeconds(3), cts.Token);
        }

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            IReadOnlyList<IEvent> taskEvents = await session.Events.QueryAllRawEvents()
                .Where(e => e.StreamId == preSwitchOnTaskId).ToListAsync(cts.Token);
            taskEvents.Should().NotBeEmpty();
            taskEvents.Should().OnlyContain(
                e => e.Sequence <= switchOnSequence, "this test is only meaningful if the task really is pre-switch-on");
        }

        // A gap-fill (ForOriginNodeId set) and a bootstrap (everything null) are both minted by a
        // daemon sweep with nobody asking, so both still stop at the switch-on point and decline.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            int gapFillEnvelopes = await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterNodeId,
                new EventReplicationCodec.EventsRequestRecord(DomainId.New(), nodeA, SinceOriginSequence: 0, ForStreamId: null),
                Now.AddSeconds(4), trustChain: null, cts.Token);
            gapFillEnvelopes.Should().Be(0, "a gap-fill keeps the switch-on exclusion");

            int bootstrapEnvelopes = await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterNodeId,
                new EventReplicationCodec.EventsRequestRecord(DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, ForStreamId: null),
                Now.AddSeconds(5), trustChain: null, cts.Token);
            bootstrapEnvelopes.Should().Be(0, "a brand-new node's own bootstrap keeps it too");

            // The one named stream, explicitly asked for — served in full, switch-on point and all.
            int explicitEnvelopes = await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterNodeId,
                new EventReplicationCodec.EventsRequestRecord(
                    DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, preSwitchOnTaskId),
                Now.AddSeconds(6), trustChain: null, cts.Token);
            explicitEnvelopes.Should().Be(1, "an explicit stream request lifts the switch-on exclusion");

            int privateEnvelopes = await responder.AnswerAsync(
                session, nodeA, "owner-a-fingerprint", projectId, requesterNodeId,
                new EventReplicationCodec.EventsRequestRecord(
                    DomainId.New(), ForOriginNodeId: null, SinceOriginSequence: 0, preSwitchOnPrivateTaskId),
                Now.AddSeconds(7), trustChain: null, cts.Token);
            privateEnvelopes.Should().Be(0, "a private task is never served, however explicit the ask");

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(8), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<EventReplicationCodec.ReplicatedEventRecord> served = [];
        int declines = 0;
        foreach (TransportEnvelope raw in read.Envelopes)
        {
            MessageEnvelopeV1 envelope = MessageEnvelopeCodec.Decode(raw.Content).Envelope!;
            if (envelope.Kind == MessageKind.EventsUnavailable)
            {
                declines++;
                continue;
            }

            envelope.Kind.Should().Be(MessageKind.Events);
            served.AddRange(EventReplicationCodec.DecodeBatch(envelope.Body)!);
        }

        declines.Should().Be(3, "the gap-fill, the bootstrap, and the private stream all say so rather than going silent");
        served.Should().NotBeEmpty();
        served.Should().OnlyContain(
            record => record.StreamId == preSwitchOnTaskId,
            "only the one explicitly requested, non-private stream travels");
    }

    /// <summary>
    /// Task a56cf16e: <c>h9k project pull --since</c>'s own request shape, answered under the same
    /// explicit-ask rule and applied over streams the asking node already holds. The idempotency
    /// here is the point — a pull is a blunt instrument a human reaches for when something is
    /// missing, and it has to be safe to reach for twice.
    /// <para>
    /// What closes such a pull is the other half (independent pre-PR review, cycle 1, both lenses,
    /// medium): a peer's own answer arriving, never what that answer happened to apply. A pull
    /// answered entirely with events this node already holds applies nothing — the ordinary outcome
    /// of the "am I missing anything?" pull — and one closed on applied content instead would stand
    /// outstanding forever, reported by <c>h9k status</c> and refusing every later pull at the same
    /// or a shallower bound. An unrelated live flush from any member is not that answer, and does
    /// not close it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_project_pull_is_answered_from_below_the_switch_on_point_and_closes_on_the_answer_not_a_live_flush()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeB = OpenStore("event_catchup_project_pull_node_b");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Both tasks predate node A's own switch-on point, so nothing but an explicit ask can ever
        // get them off node A.
        Guid alreadyHeldTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now, cts.Token);
        Guid neverSeenTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeA, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeB, new NodeRegistered(nodeB, ownerId, "node-b", "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeB, Now, cts.Token);
        }

        // Node B pulls the one stream first, the h9k task pull shape, so it genuinely already holds
        // it by the time the project pull answers with it a second time.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await coordinator.RequestStreamBroadcastAsync(
                session, projectId, alreadyHeldTaskId, nodeB, "owner-b-fingerprint", Now.AddSeconds(3), cts.Token))
                .Should().BeTrue();
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeA, "owner-a-fingerprint", Now.AddSeconds(4),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: null, cts.Token);
            read.EventsApplied.Should().BeGreaterThan(0, "the explicit stream request brings the pre-switch-on stream in");
        }

        int heldEventsBeforeThePull;
        await using (IQuerySession session = storeB.QuerySession())
        {
            heldEventsBeforeThePull = (await session.Events.QueryAllRawEvents()
                .Where(e => e.StreamId == alreadyHeldTaskId).ToListAsync(cts.Token)).Count;
            heldEventsBeforeThePull.Should().BeGreaterThan(0);
        }

        // h9k project pull --since all: everything node A holds for this project, including the
        // stream node B just applied.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now.AddSeconds(7), cts.Token))
                .Should().BeTrue();
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(7), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeA, "owner-a-fingerprint", Now.AddSeconds(8),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(9), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(10),
                trustChain: null, cts.Token);
            read.EventsApplied.Should().BeGreaterThan(0, "the never-seen stream arrives on the pull");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            int heldEventsAfterThePull = (await session.Events.QueryAllRawEvents()
                .Where(e => e.StreamId == alreadyHeldTaskId).ToListAsync(cts.Token)).Count;
            heldEventsAfterThePull.Should().Be(
                heldEventsBeforeThePull,
                "a pull that re-serves a stream this node already holds dedupes by origin event id rather than doubling it");

            (await session.Events.QueryAllRawEvents().Where(e => e.StreamId == neverSeenTaskId).ToListAsync(cts.Token))
                .Should().NotBeEmpty("the stream the pull existed to fetch actually landed");

            EventCatchUpRequest pull = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.SinceGlobalSequence != null)
                .FirstOrDefaultAsync(cts.Token))!;
            pull.AnsweredAt.Should().NotBeNull(
                "a project pull closes once an answer applies, or h9k status would report it outstanding forever");
        }

        // A second identical pull finds the answered one closed and asks again — the block is only
        // ever on an OUTSTANDING request reaching at least as far back.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now.AddSeconds(11), cts.Token))
                .Should().BeTrue();
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now.AddSeconds(12), cts.Token))
                .Should().BeFalse("the one just queued is still outstanding");
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(13), cts.Token);
        }

        // Node A's ordinary live flush of a brand-new task reaches node B before any answer to that
        // second pull does. It applies, and the pull must still be standing afterward: a flush
        // addressed to the whole project is not an answer to anything this node asked for.
        Guid taskAddedAfterThePull = await SeedQueuedTaskAsync(
            _postgres.Store, projectId, ownerId, Now.AddSeconds(14), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(14), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(14), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult liveRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(15),
                trustChain: null, cts.Token);
            liveRead.EventsApplied.Should().BeGreaterThan(0, "an ordinary flush of a brand-new task still applies");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Events.QueryAllRawEvents().Where(e => e.StreamId == taskAddedAfterThePull)
                .ToListAsync(cts.Token))
                .Should().NotBeEmpty("the live flush is what brought this one in, not the pull");

            EventCatchUpRequest? stillStanding = await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.SinceGlobalSequence != null && r.AnsweredAt == null)
                .FirstOrDefaultAsync(cts.Token);
            stillStanding.Should().NotBeNull(
                "closing on whatever happened to apply would stop h9k status reporting a pull genuinely still in flight");
        }

        // Node A finally answers the second pull, with everything it holds — every event of which
        // node B has already applied. Nothing applies, and the pull must close anyway.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeA, "owner-a-fingerprint", Now.AddSeconds(16),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(17), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult redundantRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(18),
                trustChain: null, cts.Token);
            redundantRead.EventsApplied.Should().Be(0, "node B already holds every event node A's answer carries");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.SinceGlobalSequence != null && r.AnsweredAt == null)
                .ToListAsync(cts.Token))
                .Should().BeEmpty(
                    "an answer that applied nothing is still an answer; a pull that never closes reports itself "
                    + "outstanding forever and refuses every later pull at the same bound");
        }
    }

    /// <summary>
    /// The other way a whole-project pull ends (independent pre-PR review, cycle 1, both lenses,
    /// medium): a peer holding nothing at or above the bound declines, and a broadcast has no
    /// current candidate for that decline to match, no next candidate to advance to, and no
    /// timeout behind it. The decline has to close the ask itself, recording why — otherwise the
    /// pull stands forever and
    /// <see cref="EventCatchUpCoordinator.RequestProjectHistoryBroadcastAsync"/> refuses every
    /// later pull at the same or a shallower bound for good.
    /// </summary>
    [Fact]
    public async Task A_project_pull_closes_on_a_declining_peer_since_a_broadcast_has_no_next_candidate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeB = DomainId.New();
        Guid nodeC = DomainId.New();
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

        // One store for both node identities, the same shape
        // A_decline_actually_asks_the_next_candidate_rather_than_only_advancing_the_index uses:
        // what is under test is node B's own bookkeeping, and node C holds nothing for this project
        // either way.
        await using DocumentStore storeC = OpenStore("event_catchup_pull_decline_node_c");

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeC, new NodeRegistered(nodeC, DomainId.New(), "node-c", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now, cts.Token))
                .Should().BeTrue();
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now, cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(1),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1, "node C did process the pull, answering with events-unavailable");
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
            EventCatchUpRequest pull = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.SinceGlobalSequence != null).FirstOrDefaultAsync(cts.Token))!;
            pull.IsOutstanding.Should().BeFalse("the only peer said it holds nothing, and nothing else is coming");
            pull.DeclinedReason.Should().NotBeNullOrWhiteSpace("why it ended is recorded rather than guessed at later");
            pull.Exhausted.Should().BeFalse("a broadcast has no candidate cascade to exhaust");
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now.AddSeconds(4), cts.Token))
                .Should().BeTrue("a closed pull blocks nothing, so the human can ask again once a peer has the history");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 4, adversarial lens, medium: the same reasoning holds for
    /// the OTHER broadcast shape. One mistyped character of a task id parses as a GUID, finds no
    /// local stream, and queues a broadcast every member declines — and with the decline-close
    /// restricted to the whole-project shape, that request stood outstanding forever, reported by
    /// <c>h9k status</c> for good, with a corrected re-run for the same wrong id told one was
    /// already on its way and no command able to clear it.
    /// </summary>
    [Fact]
    public async Task A_stream_broadcast_no_member_holds_closes_on_the_decline_rather_than_standing_forever()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeB = DomainId.New();
        Guid nodeC = DomainId.New();
        Guid projectId = DomainId.New();
        Guid streamNobodyHolds = DomainId.New();

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

        // One store for both node identities, the same shape the project-pull decline test above
        // uses: what is under test is node B's own bookkeeping, and node C holds nothing either way.
        await using DocumentStore storeC = OpenStore("event_catchup_stream_decline_node_c");

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeC, new NodeRegistered(nodeC, DomainId.New(), "node-c", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            (await coordinator.RequestStreamBroadcastAsync(
                session, projectId, streamNobodyHolds, nodeB, "owner-b-fingerprint", Now, cts.Token))
                .Should().BeTrue();
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now, cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            (await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeC, "owner-c-fingerprint", Now.AddSeconds(1),
                trustChain: null, cts.Token)).RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeC, projectId, "shared-project-key", adoptUnassigned: false, committerC,
                signingKeyC, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            (await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeC, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: null, cts.Token)).DeclinesObserved.Should().Be(1);
        }

        await using (IQuerySession session = storeC.QuerySession())
        {
            EventCatchUpRequest request = (await session.Query<EventCatchUpRequest>()
                .Where(r => r.ProjectId == projectId && r.ForStreamId == streamNobodyHolds)
                .FirstOrDefaultAsync(cts.Token))!;
            request.IsOutstanding.Should().BeFalse("the only peer said it holds nothing, and nothing else is coming");
            request.DeclinedReason.Should().NotBeNullOrWhiteSpace();
            request.Exhausted.Should().BeFalse("a broadcast has no candidate cascade to exhaust");
        }

        await using (IDocumentSession session = storeC.LightweightSession())
        {
            (await coordinator.RequestStreamBroadcastAsync(
                session, projectId, streamNobodyHolds, nodeB, "owner-b-fingerprint", Now.AddSeconds(4), cts.Token))
                .Should().BeTrue("a closed request blocks nothing, so the human can ask again");
        }
    }

    /// <summary>
    /// Self-review finding, task a56cf16e: a project pull and a brand-new node's bootstrap both
    /// carry a null origin node and a null stream, so <see cref="EventCatchUpCoordinator.RequestBootstrapAsync"/>'s
    /// own "is one already outstanding" query matched the pull as well until it was taught to
    /// require a null <see cref="EventCatchUpRequest.SinceGlobalSequence"/> too. The two shapes are
    /// distinct asks and neither suppresses the other.
    /// </summary>
    [Fact]
    public async Task An_outstanding_project_pull_never_suppresses_a_brand_new_nodes_own_bootstrap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid myNodeId = DomainId.New();
        Guid peerNodeId = DomainId.New();
        Guid projectId = DomainId.New();
        EventCatchUpCoordinator coordinator = new();

        await using IDocumentSession session = _postgres.Store.LightweightSession();

        (await coordinator.RequestProjectHistoryBroadcastAsync(
            session, projectId, sinceGlobalSequence: 500, myNodeId, "owner-fingerprint", Now, cts.Token))
            .Should().BeTrue();

        (await coordinator.RequestBootstrapAsync(
            session, projectId, myNodeId, "owner-fingerprint", [peerNodeId], TimeSpan.FromMinutes(5),
            Now.AddSeconds(1), cts.Token))
            .Should().BeTrue("a pull outstanding for the same project is a different ask, not this bootstrap's own");

        // And the converse: the bootstrap now outstanding does not block a pull either, since a
        // pull only ever matches a request that carries its own sequence bound.
        (await coordinator.RequestProjectHistoryBroadcastAsync(
            session, projectId, sinceGlobalSequence: 0, myNodeId, "owner-fingerprint", Now.AddSeconds(2), cts.Token))
            .Should().BeTrue("asking for everything reaches further back than the outstanding bound, so it is a new ask");

        // What a pull IS blocked by is another pull already reaching at least as far back.
        (await coordinator.RequestProjectHistoryBroadcastAsync(
            session, projectId, sinceGlobalSequence: 900, myNodeId, "owner-fingerprint", Now.AddSeconds(3), cts.Token))
            .Should().BeFalse("one already outstanding covers everything this shallower one would ask for");

        IReadOnlyList<EventCatchUpRequest> all = await session.Query<EventCatchUpRequest>()
            .Where(request => request.ProjectId == projectId).ToListAsync(cts.Token);
        all.Should().HaveCount(3);
        all.Count(request => request.SinceGlobalSequence is null).Should().Be(1, "exactly one of them is the bootstrap");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 4, conformance lens, low: a whole-project pull names no
    /// stream to match and its answer may apply nothing, so "some node-addressed answer went by"
    /// closed it — including an answer to a SIBLING request this node had outstanding at the same
    /// time. <c>h9k status</c> then stopped showing the pull a sweep or more early, and the peer's
    /// real decline for it was ignored on arrival, so no reason was ever recorded.
    /// <see cref="EventCatchUpResponder"/> stamps the answered request's own id into the envelope,
    /// and the pull closes on that id alone.
    /// </summary>
    [Fact]
    public async Task A_sibling_stream_requests_answer_does_not_close_a_project_pull_no_peer_has_answered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeB = OpenStore("event_catchup_sibling_answer_node_b");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now, cts.Token);

        // Node B asks for the one stream, and node A answers it — all before node B's own project
        // pull has reached node A at all.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await coordinator.RequestStreamBroadcastAsync(
                session, projectId, taskId, nodeB, "owner-b-fingerprint", Now.AddSeconds(1), cts.Token))
                .Should().BeTrue();
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(1), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            (await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeA, "owner-a-fingerprint", Now.AddSeconds(2),
                trustChain: null, cts.Token)).RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now.AddSeconds(4), cts.Token))
                .Should().BeTrue();
        }

        // Node B reads the stream request's answer with its own pull still unanswered by anyone.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(5),
                trustChain: null, cts.Token)).EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventCatchUpRequest streamRequest = (await session.Query<EventCatchUpRequest>()
                .Where(request => request.ProjectId == projectId && request.ForStreamId != null)
                .FirstOrDefaultAsync(cts.Token))!;
            streamRequest.AnsweredAt.Should().NotBeNull("this is the request node A actually answered");

            EventCatchUpRequest pull = (await session.Query<EventCatchUpRequest>()
                .Where(request => request.ProjectId == projectId && request.SinceGlobalSequence != null)
                .FirstOrDefaultAsync(cts.Token))!;
            pull.IsOutstanding.Should().BeTrue(
                "no peer has answered the pull, and h9k status must keep saying so until one does");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 4, adversarial lens, high: a pull serves the pre-switch-on
    /// HEAD of a stream whose post-switch-on tail the asking node already applied live, and a
    /// replicated event is appended rather than inserted — so the head would land behind the tail
    /// and the stream would replay backwards, with <c>TaskAggregate.Apply(TaskAdded)</c> running
    /// last resetting the state and clearing the acceptance criteria. Every task open on a node at
    /// its own switch-on moment is in exactly this shape on its peers, so the refusal has to hold
    /// without also refusing the streams a pull genuinely fetches.
    /// </summary>
    [Fact]
    public async Task A_pulled_head_is_refused_rather_than_appended_behind_a_tail_the_asking_node_already_holds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        EventCatchUpResponder responder = new(new ReplicationProjectResolver());
        EventCatchUpInbox catchUpInbox = new(transport, responder);
        EventCatchUpCoordinator coordinator = new();
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeB = OpenStore("event_catchup_partial_stream_node_b");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Both tasks were created before node A ever switched replication on. One of them is then
        // touched AFTER the switch-on point, which is the only half an ordinary flush ships.
        Guid touchedAfterSwitchOnTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now, cts.Token);
        Guid untouchedTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await EventReplicationOutbox.EnsureSwitchedOnAsync(session, nodeA, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(
                touchedAfterSwitchOnTaskId,
                new TaskUnassigned(touchedAfterSwitchOnTaskId, "handed back", Now.AddSeconds(3), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeB, new NodeRegistered(nodeB, ownerId, "node-b", "Windows", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The ordinary live flush: the one post-switch-on event, and nothing under it.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult liveRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(5),
                trustChain: null, cts.Token);
            liveRead.EventsApplied.Should().Be(1, "only the post-switch-on event ever rides an ordinary flush");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            IReadOnlyList<IEvent> tail =
                await session.Events.FetchStreamAsync(touchedAfterSwitchOnTaskId, token: cts.Token);
            tail.Should().ContainSingle("node B holds the tail of this stream and nothing under it");
            tail.Single().EventType.Should().Be(typeof(TaskUnassigned));
        }

        // h9k project pull --since all: node A serves this stream's pre-switch-on head too, now
        // that an explicit ask lifts the exclusion.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await coordinator.RequestProjectHistoryBroadcastAsync(
                session, projectId, sinceGlobalSequence: 0, nodeB, "owner-b-fingerprint", Now.AddSeconds(6), cts.Token))
                .Should().BeTrue();
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventCatchUpInboxReadResult catchUpRead = await catchUpInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeA, "owner-a-fingerprint", Now.AddSeconds(7),
                trustChain: null, cts.Token);
            catchUpRead.RequestsAnswered.Should().Be(1);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(8), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult pullRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(9),
                trustChain: null, cts.Token);
            pullRead.EventsApplied.Should().Be(
                3, "the stream node B holds nothing of arrives in full; the partial one's head is refused");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            IReadOnlyList<IEvent> partial =
                await session.Events.FetchStreamAsync(touchedAfterSwitchOnTaskId, token: cts.Token);
            partial.Should().ContainSingle(
                "the pull's older events belong in front of the one already here, and appending them there "
                + "would replay the stream backwards");
            partial.Should().NotContain(
                candidate => candidate.EventType == typeof(TaskAdded),
                "TaskAdded applied last resets State and clears the acceptance criteria");

            IReadOnlyList<IEvent> whole = await session.Events.FetchStreamAsync(untouchedTaskId, token: cts.Token);
            whole.Should().HaveCount(3, "a stream this node holds nothing of is exactly what a pull does fetch");
            whole[0].EventType.Should().Be(typeof(TaskAdded), "and it arrives in order");

            TaskAggregate fetched =
                (await session.Events.AggregateStreamAsync<TaskAggregate>(untouchedTaskId, token: cts.Token))!;
            fetched.Objective.Should().Be("Ship the thing");
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
