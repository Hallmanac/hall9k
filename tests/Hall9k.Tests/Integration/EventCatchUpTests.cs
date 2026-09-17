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
                session, RepositoryPath, nodeA, projectId, Now.AddSeconds(3), trustChain: null, cts.Token);
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
                session, RepositoryPath, nodeA, projectId, Now.AddSeconds(6), trustChain: null, cts.Token);
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
                session, RepositoryPath, nodeA, projectId, Now.AddSeconds(9), trustChain: null, cts.Token);
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
                candidates: [nodeC], Now.AddSeconds(10), cts.Token);
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
                session, RepositoryPath, nodeC, projectId, Now.AddSeconds(13), trustChain: null, cts.Token);
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
                session, RepositoryPath, nodeA, projectIdA, Now.AddSeconds(3), trustChain: null, cts.Token);
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        // Node B is brand new: no local history for this project at all. It asks node C for
        // everything — ForOriginNodeId and ForStreamId both null.
        bool started;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            started = await coordinator.RequestBootstrapAsync(
                session, projectIdB, nodeB, "owner-b-fingerprint", [nodeC], Now.AddSeconds(4), cts.Token);
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
                session, RepositoryPath, nodeC, projectIdB, Now.AddSeconds(7), trustChain: null, cts.Token);
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
                candidates: [nodeC, nodeD], Now, cts.Token);
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
