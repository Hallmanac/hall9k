using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Weasel.Core;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A node that joins an existing project by invite receives the project's pre-switch-on
/// membership and team-settings history, so <c>h9k project show</c> and <c>h9k status</c> read it
/// as joined rather than "not joined yet" forever.
/// <para>
/// The Project aggregate's team-facing events are merged onto the receiver's own local Project
/// stream, where the ordinary outbox lands the post-switch-on events first and the catch-up answer
/// serves the older head afterward. The per-origin high-water guard in
/// <see cref="EventReplicationInbox"/>, keyed on that receiver-local stream, used to refuse the head
/// as out of order on every reconcile, and nothing could repair it. Two genuinely separate Marten
/// stores stand in for the two nodes, driven through the seam only: an in-memory transport and a
/// fake ledger, never a real repository.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class ProjectTeamHistoryCatchUpTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "/repo-shared-team-history";
    private const string MemberFingerprint = "SHA256:the-invited-member";
    private const string OwnerAFingerprint = "owner-a-fingerprint";
    private const string OwnerBFingerprint = "owner-b-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public ProjectTeamHistoryCatchUpTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentStore OpenStore(string schemaName) => DocumentStore.For(opts =>
    {
        opts.Connection(_postgres.ConnectionString);
        opts.DatabaseSchemaName = schemaName;
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// The issue's shape, end to end. Origin A holds a <see cref="MemberVouched"/> and a
    /// <see cref="ProjectTeamSettingsChanged"/> before its switch-on point and another team change
    /// after it. Receiver B takes the ordinary post-switch-on flush first, then the bootstrap
    /// catch-up answer, which is the path a fresh join runs.
    /// </summary>
    [Fact]
    public async Task A_bootstrap_after_the_ordinary_flush_brings_the_pre_switch_on_membership_and_settings_and_the_newest_value_stands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await using Nodes nodes = await SeedNodesAsync("team_history_bootstrap_node_b", cts.Token);
        await nodes.ReceiveOrdinaryFlushAsync(Now.AddSeconds(20), cts.Token);

        await using (IQuerySession session = nodes.StoreB.QuerySession())
        {
            ProjectDetails stuck = (await session.LoadAsync<ProjectDetails>(nodes.ProjectIdB, cts.Token))!;
            stuck.Members.Should().BeEmpty("only the post-switch-on tail arrived, which is the state the issue starts from");
            ProjectJoinStatus.NotJoined(stuck, MemberFingerprint).Should().BeTrue();
        }

        await using (IDocumentSession session = nodes.StoreB.LightweightSession())
        {
            (await nodes.Coordinator.RequestBootstrapAsync(
                session, nodes.ProjectIdB, nodes.NodeB, OwnerBFingerprint, [nodes.NodeA], TimeSpan.FromMinutes(5),
                Now.AddSeconds(21), cts.Token)).Should().BeTrue();
        }

        int applied = await nodes.RunAnswerAsync(Now.AddSeconds(22), cts.Token);

        applied.Should().Be(2, "the vouch and the pre-switch-on settings change apply; the tail is a duplicate");
        await AssertJoinedWithNewestSettingsAsync(nodes, cts.Token);
    }

    /// <summary>
    /// A node already stuck on v0.10.50 holds the refused tail and no head, with a reconcile record
    /// that is already complete. One explicit <c>h9k project reconcile</c> is the by-hand ask, and its
    /// answer repairs it with nothing else typed.
    /// </summary>
    [Fact]
    public async Task A_node_already_stuck_with_the_tail_and_no_head_is_repaired_by_one_by_hand_reconcile()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await using Nodes nodes = await SeedNodesAsync("team_history_reconcile_node_b", cts.Token);
        await nodes.ReceiveOrdinaryFlushAsync(Now.AddSeconds(20), cts.Token);

        await using (IDocumentSession session = nodes.StoreB.LightweightSession())
        {
            session.Store(new FleetProjectReconcile
            {
                Id = EventReplicationStreamId.ForFleetReconcile(nodes.NodeA, nodes.ProjectIdB),
                PeerNodeId = nodes.NodeA,
                ProjectId = nodes.ProjectIdB,
                RequestId = DomainId.New(),
                AskedAt = Now.AddSeconds(15),
                FirstAnswerAt = Now.AddSeconds(16),
                CompletedAt = Now.AddSeconds(17),
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = nodes.StoreB.QuerySession())
        {
            ProjectDetails stuck = (await session.LoadAsync<ProjectDetails>(nodes.ProjectIdB, cts.Token))!;
            ProjectJoinStatus.NotJoined(stuck, MemberFingerprint).Should().BeTrue();
        }

        await using (IDocumentSession session = nodes.StoreB.LightweightSession())
        {
            await EventCatchUpCoordinator.RequestFleetReconcileByHandAsync(
                session, nodes.ProjectIdB, nodes.NodeA, nodes.NodeB, OwnerBFingerprint, Now.AddSeconds(21), cts.Token);
        }

        int applied = await nodes.RunAnswerAsync(Now.AddSeconds(22), cts.Token);

        applied.Should().Be(2, "the head applies now, and the tail it already held is a duplicate");
        await AssertJoinedWithNewestSettingsAsync(nodes, cts.Token);
    }

    /// <summary>
    /// The guard is unchanged for every other stream: a Task stream this node already holds a
    /// later event of, from the same origin, still refuses an earlier record rather than appending
    /// it and replaying the stream backwards.
    /// </summary>
    [Fact]
    public async Task The_origin_high_water_guard_still_refuses_an_out_of_order_record_on_a_task_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        await using Nodes nodes = await SeedNodesAsync("team_history_task_guard_node_b", cts.Token);
        Guid ownerId = DomainId.New();
        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, nodes.ProjectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = nodes.StoreB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, nodes.ProjectIdB, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null,
                Now.AddSeconds(2), ownerId);
            StreamAction start = session.Events.StartStream<TaskAggregate>(taskId, added);
            IEvent appended = start.Events[^1];
            // A test store stamps no node id on its own events, so node A's answer names the empty id as origin.
            appended.SetHeader(ReplicationEventHeaders.OriginNodeId, Guid.Empty.ToString());
            appended.SetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint, OwnerAFingerprint);
            appended.SetHeader(ReplicationEventHeaders.OriginEventId, DomainId.New().ToString());
            appended.SetHeader(ReplicationEventHeaders.OriginSequence, "1000000");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = nodes.StoreB.LightweightSession())
        {
            (await nodes.Coordinator.RequestBootstrapAsync(
                session, nodes.ProjectIdB, nodes.NodeB, OwnerBFingerprint, [nodes.NodeA], TimeSpan.FromMinutes(5),
                Now.AddSeconds(21), cts.Token)).Should().BeTrue();
        }

        int applied = await nodes.RunAnswerAsync(Now.AddSeconds(22), cts.Token);

        applied.Should().Be(3, "the project's vouch and two settings changes apply, and no task record does");
        nodes.Log.Lines.Count(line => line.Contains("refused rather than appended out of order")).Should().Be(
            1, "the published event is refused by the guard; the second genesis is discarded by its own rule");

        await using IQuerySession query = nodes.StoreB.QuerySession();
        IReadOnlyList<IEvent> taskStream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
        taskStream.Should().ContainSingle("nothing was appended behind the later origin event already held");
    }

    /// <summary>What both repair tests assert: the answer left B reading joined, with A's newest settings.</summary>
    private async Task AssertJoinedWithNewestSettingsAsync(Nodes nodes, CancellationToken cancellationToken)
    {
        await using IQuerySession sessionA = _postgres.Store.QuerySession();
        ProjectDetails origin = (await sessionA.LoadAsync<ProjectDetails>(nodes.ProjectIdA, cancellationToken))!;

        await using IQuerySession sessionB = nodes.StoreB.QuerySession();
        ProjectDetails receiver = (await sessionB.LoadAsync<ProjectDetails>(nodes.ProjectIdB, cancellationToken))!;

        receiver.Members.Should().ContainKey(MemberFingerprint).WhoseValue.Should().Be(ProjectMemberRole.Owner);
        ProjectJoinStatus.NotJoined(receiver, MemberFingerprint).Should().BeFalse(
            "the vouch is on the receiver's own Project stream now");
        receiver.ClaimGate.Should().Be(ClaimGate.Off, "the post-switch-on change is A's newest value for this field");
        receiver.ClaimGate.Should().Be(origin.ClaimGate);
        receiver.MaxComplianceReviewCycles.Should().Be(5);
        receiver.MaxComplianceReviewCycles.Should().Be(origin.MaxComplianceReviewCycles);
        receiver.DesignReviewDrive.Should().BeFalse("only the pre-switch-on head ever set it");
        receiver.DesignReviewDrive.Should().Be(origin.DesignReviewDrive);

        nodes.Log.Lines.Should().NotContain(
            line => line.Contains("refused rather than appended out of order"),
            "a Project-aggregate event never trips the origin high-water guard");
    }

    /// <summary>
    /// Node A holds the head before its switch-on point and a newer change after it; node B has
    /// registered the same project under its own id and has read only the ordinary flush's post-switch-on
    /// tail. Nothing here has run the catch-up yet: each test does that itself.
    /// </summary>
    private async Task<Nodes> SeedNodesAsync(string schemaB, CancellationToken cancellationToken)
    {
        Nodes nodes = new(OpenStore(schemaB), _postgres.Store);
        await SeedNodeFileAsync(nodes.Ledger, nodes.NodeA, cancellationToken);
        await SeedNodeFileAsync(nodes.Ledger, nodes.NodeB, cancellationToken);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodes.NodeA, new NodeRegistered(nodes.NodeA, nodes.OwnerId, "node-a", "macOS", Now));
            session.Events.StartStream<ProjectAggregate>(
                nodes.ProjectIdA,
                new ProjectRegistered(nodes.ProjectIdA, nodes.OwnerId, DomainId.New(), "Shared Project", "/repo-a", null, "main", Now));
            session.Events.Append(
                nodes.ProjectIdA, new MemberVouched(nodes.ProjectIdA, MemberFingerprint, ProjectMemberRole.Owner, Now.AddSeconds(1)));
            session.Events.Append(
                nodes.ProjectIdA,
                new ProjectTeamSettingsChanged(
                    nodes.ProjectIdA, Now.AddSeconds(2), nodes.OwnerId,
                    ClaimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee),
                    MaxComplianceReviewCycles: Optional<int?>.Of(2),
                    DesignReviewDrive: Optional<bool>.Of(false)));
            await session.SaveChangesAsync(cancellationToken);
        }

        await using (IDocumentSession session = nodes.StoreB.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(
                nodes.NodeB, new NodeRegistered(nodes.NodeB, nodes.OwnerId, "node-b", "Windows", Now));
            session.Events.StartStream<ProjectAggregate>(
                nodes.ProjectIdB,
                new ProjectRegistered(nodes.ProjectIdB, nodes.OwnerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cancellationToken);
        }

        // The first queue pass switches replication on, which is what puts the vouch and the first
        // settings change below the point the ordinary flush ships from.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await nodes.Outbox.QueuePendingAsync(
                session, nodes.NodeA, nodes.ProjectIdA, OwnerAFingerprint, Now.AddSeconds(5), cancellationToken);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(
                nodes.ProjectIdA,
                new ProjectTeamSettingsChanged(
                    nodes.ProjectIdA, Now.AddSeconds(10), nodes.OwnerId,
                    ClaimGate: Optional<ClaimGate>.Of(ClaimGate.Off),
                    MaxComplianceReviewCycles: Optional<int?>.Of(5)));
            await session.SaveChangesAsync(cancellationToken);
        }

        return nodes;
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

    /// <summary>The two nodes' stores and the seam between them, with the message choreography both
    /// repair paths share written once.</summary>
    private sealed class Nodes(DocumentStore storeB, DocumentStore storeA) : IAsyncDisposable
    {
        public Guid NodeA { get; } = DomainId.New();
        public Guid NodeB { get; } = DomainId.New();
        public Guid OwnerId { get; } = DomainId.New();
        public Guid ProjectIdA { get; } = DomainId.New();
        public Guid ProjectIdB { get; } = DomainId.New();
        public DocumentStore StoreB { get; } = storeB;
        public FakeLedger Ledger { get; } = new();
        public ListLogger<EventReplicationInbox> Log { get; } = new();
        public EventReplicationOutbox Outbox { get; } = new(new ReplicationProjectResolver());
        public EventCatchUpCoordinator Coordinator { get; } = new();
        private InMemoryMessageTransport? _transport;
        private readonly (LedgerCommitter Committer, LedgerSigningKey SigningKey) _signingA = Signing("node-a");
        private readonly (LedgerCommitter Committer, LedgerSigningKey SigningKey) _signingB = Signing("node-b");
        private InMemoryMessageTransport Transport => _transport ??= new InMemoryMessageTransport(Ledger);

        public ValueTask DisposeAsync() => StoreB.DisposeAsync();

        /// <summary>Node A queues and flushes what follows its switch-on point; node B reads it. The tail is one event.</summary>
        public async Task ReceiveOrdinaryFlushAsync(DateTimeOffset at, CancellationToken cancellationToken)
        {
            await using (IDocumentSession session = storeA.LightweightSession())
            {
                await Outbox.QueuePendingAsync(session, NodeA, ProjectIdA, OwnerAFingerprint, at, cancellationToken);
                await FlushAsync(session, NodeA, ProjectIdA, _signingA, at, cancellationToken);
            }

            await using IDocumentSession receiver = StoreB.LightweightSession();
            EventReplicationReadResult read = await Inbox.ReadFromAsync(
                receiver, RepositoryPath, NodeA, ProjectIdB, NodeB, OwnerBFingerprint, at.AddSeconds(1), trustChain: null,
                cancellationToken);
            read.EventsApplied.Should().Be(1, "the ordinary flush ships only what follows the switch-on point");
        }

        /// <summary>
        /// B flushes its ask, A answers it and flushes the answer, and B applies it. Returns how many
        /// events applied on B.
        /// </summary>
        public async Task<int> RunAnswerAsync(DateTimeOffset at, CancellationToken cancellationToken)
        {
            await using (IDocumentSession session = StoreB.LightweightSession())
            {
                await FlushAsync(session, NodeB, ProjectIdB, _signingB, at, cancellationToken);
            }

            await using (IDocumentSession session = storeA.LightweightSession())
            {
                EventCatchUpInboxReadResult catchUp = await CatchUpInbox.ReadFromAsync(
                    session, RepositoryPath, NodeB, ProjectIdA, NodeA, OwnerAFingerprint, at.AddSeconds(1), trustChain: null,
                    cancellationToken);
                catchUp.RequestsAnswered.Should().Be(1);
                await FlushAsync(session, NodeA, ProjectIdA, _signingA, at.AddSeconds(2), cancellationToken);
            }

            await using IDocumentSession receiver = StoreB.LightweightSession();
            EventReplicationReadResult read = await Inbox.ReadFromAsync(
                receiver, RepositoryPath, NodeA, ProjectIdB, NodeB, OwnerBFingerprint, at.AddSeconds(3), trustChain: null,
                cancellationToken);
            return read.EventsApplied;
        }

        private EventReplicationInbox Inbox => new(Transport, Log);

        private EventCatchUpInbox CatchUpInbox => new(Transport, new EventCatchUpResponder(new ReplicationProjectResolver(), Ledger));

        private Task FlushAsync(
            IDocumentSession session, Guid nodeId, Guid projectId,
            (LedgerCommitter Committer, LedgerSigningKey SigningKey) signing, DateTimeOffset at,
            CancellationToken cancellationToken) =>
            new MessageOutbox(Transport).FlushAsync(
                session, RepositoryPath, nodeId, projectId, "shared-project-key", adoptUnassigned: false,
                signing.Committer, signing.SigningKey, at, cancellationToken);
    }
}
