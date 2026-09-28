using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Integration;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hall9k.Tests.Fakes;

/// <summary>One node of a <see cref="ReplicatedFleet"/>: its own store, its own identity, its own convergence pass.</summary>
internal sealed record ReplicatedPeer(
    string Name, DocumentStore Store, NodeContext Node, PullRequestReviewDuplicateConvergence Convergence,
    LedgerCommitter Committer, LedgerSigningKey SigningKey, string Fingerprint);

/// <summary>
/// Two nodes of one owner, each with a genuinely separate Marten store, exchanging project-scoped
/// events through a shared in-memory transport and fake ledger, never a real repository: the
/// shape <c>EventReplicationTests</c> established, packaged so a test about what two nodes do
/// with each other's tasks reads as a story rather than as plumbing. The first peer runs on the
/// fixture's own store (cleaned when asked), the second in a schema of its own on the same
/// container. Both are nodes of the same owner, which is what makes their tasks rivals.
/// </summary>
internal sealed class ReplicatedFleet : IAsyncDisposable
{
    public const string RepositoryPath = "/repo-shared";

    /// <summary>Genuinely 26-character ULID-shaped, unlike a hand-picked short literal, since a
    /// caller wiring a live <c>TrustChain.ProjectKey</c> matching this fixture's own flushed key
    /// needs the two to survive <see cref="Hall9k.Connectors.Messaging.ProjectKeyMismatch"/>'s own
    /// malformed-key refusal (idea 6be68ee2, trust-ledger finding 13) rather than being refused for
    /// looking nothing like a real project key once that caller's own project has one.</summary>
    public const string ProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FEC";

    private readonly EventReplicationOutbox _eventOutbox = new(new ReplicationProjectResolver());
    private readonly EventReplicationInbox _eventInbox;
    private readonly MessageOutbox _messageOutbox;
    private DateTimeOffset _clock = new(2026, 9, 24, 16, 0, 0, TimeSpan.Zero);

    private ReplicatedFleet(ReplicatedPeer a, ReplicatedPeer b, Guid projectId, InMemoryMessageTransport transport)
    {
        A = a;
        B = b;
        ProjectId = projectId;
        Transport = transport;
        _eventInbox = new EventReplicationInbox(transport);
        _messageOutbox = new MessageOutbox(transport);
    }

    public ReplicatedPeer A { get; }

    public ReplicatedPeer B { get; }

    public Guid ProjectId { get; }

    public static async Task<ReplicatedFleet> StartAsync(
        PostgresFixture postgres, string secondSchema, bool cleanPrimary, CancellationToken cancellationToken)
    {
        if (cleanPrimary)
        {
            await postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();
        }

        DocumentStore second = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = secondSchema;
            options.ConfigureHall9k(AutoCreate.All);
        });
        await second.Advanced.Clean.CompletelyRemoveAllAsync();

        NodeContext nodeA = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cancellationToken);
        await SeedOwnerAsync(second, nodeA.OwnerId, cancellationToken);
        NodeContext nodeB = await NodeBootstrapSeed.NewNodeAsync(second, cancellationToken);

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA.NodeId, cancellationToken);
        await SeedNodeFileAsync(ledger, nodeB.NodeId, cancellationToken);

        ReplicatedPeer peerA = Peer("node-a", postgres.Store, nodeA);
        ReplicatedPeer peerB = Peer("node-b", second, nodeB);
        ReplicatedFleet fleet = new(peerA, peerB, DomainId.New(), new InMemoryMessageTransport(ledger));

        // Replication switches on at a point in time; only what is appended after it travels.
        foreach (ReplicatedPeer peer in new[] { peerA, peerB })
        {
            await using IDocumentSession session = peer.Store.LightweightSession();
            await fleet._eventOutbox.QueuePendingAsync(
                session, peer.Node.NodeId, fleet.ProjectId, peer.Fingerprint, fleet._clock, cancellationToken);
        }

        return fleet;
    }

    /// <summary>The sending half alone: this peer's new events reach its outbox ref, for a sweep that reads it to find.</summary>
    public async Task PublishAsync(ReplicatedPeer from, CancellationToken cancellationToken)
    {
        _clock = _clock.AddSeconds(1);
        await using IDocumentSession session = from.Store.LightweightSession();
        await _eventOutbox.QueuePendingAsync(
            session, from.Node.NodeId, ProjectId, from.Fingerprint, _clock, cancellationToken);
        await _messageOutbox.FlushAsync(
            session, RepositoryPath, from.Node.NodeId, ProjectId, ProjectKey, adoptUnassigned: false,
            from.Committer, from.SigningKey, _clock, cancellationToken);
    }

    /// <summary>The transport every peer shares, for a test that hands it to a real sweep engine.</summary>
    public InMemoryMessageTransport Transport { get; }

    public async Task<EventReplicationReadResult> ExchangeAsync(
        ReplicatedPeer from, ReplicatedPeer to, CancellationToken cancellationToken)
    {
        await PublishAsync(from, cancellationToken);
        _clock = _clock.AddSeconds(1);
        await using IDocumentSession receiving = to.Store.LightweightSession();
        return await _eventInbox.ReadFromAsync(
            receiving, RepositoryPath, from.Node.NodeId, ProjectId, to.Node.NodeId, to.Fingerprint, _clock,
            OneOwnerChain(), cancellationToken);
    }

    /// <summary>
    /// One owner root vouching for both A and B's own real node keys (the ones
    /// <see cref="SeedNodeFileAsync"/> wrote), each one an Owner-role project member: the shape a
    /// genuine one-owner fleet has today (every node under the same root, the Mac and Windows),
    /// which is what the trust chain <see cref="EventReplicationInbox"/>'s own gate now requires
    /// before it ever applies a project-settings-shaped event. A null chain, as this used to pass,
    /// would gate every one of those out unconditionally rather than exercise the ordinary,
    /// already-trusted case this fleet stands in for.
    /// </summary>
    private TrustChain OneOwnerChain()
    {
        const string root = "fleet-owner-root-fingerprint";
        TrustedOwner owner = new(
            root, "ssh-ed25519 AAAAFAKEroot test",
            [NodeFor(A), NodeFor(B)]);
        return new TrustChain(
            new Dictionary<string, TrustedOwner> { [root] = owner },
            [new ProjectMember(root, MembershipRole.Owner, DateTimeOffset.UnixEpoch)]);
    }

    /// <summary>Mirrors <see cref="SeedNodeFileAsync"/>'s own key line exactly, so the fingerprint
    /// this vouches matches the one <see cref="InMemoryMessageTransport"/> resolves for the same
    /// node from that identical node file.</summary>
    private static TrustedNode NodeFor(ReplicatedPeer peer)
    {
        string publicKeyLine = $"ssh-ed25519 AAAAFAKE{peer.Node.NodeId:N} test";
        return new TrustedNode(
            peer.Node.NodeId.ToString(), publicKeyLine, NodeKeyStore.Fingerprint(publicKeyLine), DateTimeOffset.UnixEpoch);
    }

    /// <summary>Both directions, the way two nodes whose sweeps interleave eventually see each other.</summary>
    public async Task ExchangeBothWaysAsync(CancellationToken cancellationToken)
    {
        await ExchangeAsync(A, B, cancellationToken);
        await ExchangeAsync(B, A, cancellationToken);
    }

    /// <summary>
    /// A review task exactly as <c>AutoPrReviewEngine.CreateOneAsync</c> leaves it: added, the
    /// reviewer assignment observed, published and assigned to the owner, so it sits Queued.
    /// The id is chosen by the caller because the survivor is decided by it.
    /// </summary>
    public async Task SeedReviewAsync(
        ReplicatedPeer peer, Guid taskId, DateTimeOffset addedAt, CancellationToken cancellationToken,
        string pullRequest = "acme/widgets#42", bool autoCreated = true, Guid? assignedTo = null)
    {
        Guid ownerId = assignedTo ?? peer.Node.OwnerId;
        TaskAdded added = TaskDecider.Add(
            taskId, ProjectId, $"Review pull request {pullRequest}", ["every finding is directed"],
            TaskType.PrReview, null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, pullRequest),
            addedAt, ownerId);
        List<object> events = [.. TaskSeed.Dispatchable(added, ownerId, addedAt)];
        if (autoCreated)
        {
            events.Insert(1, new PullRequestReviewAssignmentObserved(
                taskId, $"https://github.com/{pullRequest.Replace('#', '/')}", "brian", "alice", addedAt, addedAt));
        }

        await using IDocumentSession session = peer.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The node claims the task the way its dispatcher does: a claim event and the lease the heartbeat keeps alive.</summary>
    public async Task ClaimAsync(ReplicatedPeer peer, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = peer.Store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        TaskClaimed claimed = TaskDecider.Claim(task, peer.Node.NodeId, peer.Node.OwnerId, DomainId.New(), _clock);
        session.Events.Append(taskId, claimed);
        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = peer.Node.NodeId,
            LeaseGeneration = claimed.LeaseGeneration,
            HeartbeatAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    public static async Task<TaskListItem> ItemAsync(ReplicatedPeer peer, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = peer.Store.QuerySession();
        return (await query.LoadAsync<TaskListItem>(taskId, cancellationToken))!;
    }

    /// <summary>The ids of this peer's live (not Done, not Abandoned) review tasks on one pull request.</summary>
    public static async Task<IReadOnlyList<Guid>> LiveReviewsAsync(
        ReplicatedPeer peer, string pullRequest, CancellationToken cancellationToken)
    {
        await using IQuerySession query = peer.Store.QuerySession();
        string reference = new ExternalReference(WorkItemProvider.GitHubPullRequest, pullRequest).ToString();
        IReadOnlyList<TaskListItem> tasks = await query.Query<TaskListItem>()
            .Where(task => task.ExternalReference == reference)
            .ToListAsync(cancellationToken);
        return [.. tasks.Where(task => !task.State.IsTerminal).Select(task => task.Id)];
    }

    public async ValueTask DisposeAsync() => await B.Store.DisposeAsync();

    private static ReplicatedPeer Peer(string name, DocumentStore store, NodeContext node) => new(
        name, store, node,
        new PullRequestReviewDuplicateConvergence(
            store, node, NullLogger<PullRequestReviewDuplicateConvergence>.Instance),
        new LedgerCommitter(name, $"{name}@hall9k.local"), new LedgerSigningKey($"/dev/null/{name}"),
        $"{name}-owner-fingerprint");

    private static async Task SeedOwnerAsync(DocumentStore store, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(
            ownerId, OwnerDecider.Register(ownerId, "the shared owner", null, DateTimeOffset.UnixEpoch));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, CancellationToken cancellationToken)
    {
        LedgerCommitter committer = new("seed", "seed@hall9k.local");
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeId:N} test\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml", content,
                ExpectedBlobId: null, "seed node file", committer, new LedgerSigningKey("/dev/null/seed")),
            cancellationToken);
    }
}
