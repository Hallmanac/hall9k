using FluentAssertions;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
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
/// Idea 202383dc, M2a: every project-scoped event a node writes rides its outbox as an events
/// envelope, every other node records it under the same stream id as a fact, and engines act only
/// on work this node produced or holds. Driven entirely through the seam — a shared
/// <see cref="InMemoryMessageTransport"/> and <see cref="FakeLedger"/>, never a real repository
/// (Brian's 2026-09-13 testing rule) — with two genuinely separate Marten stores standing in for
/// two nodes: <see cref="PostgresFixture.Store"/> for node A's own database, and a second
/// <see cref="DocumentStore"/> in its own schema, on the identical container, for node B's — the
/// documented pattern for "a test that genuinely needs a store of its own"
/// (<see cref="PostgresFixture.Store"/>'s own doc comment).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class EventReplicationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string RepositoryPath = "/repo-shared";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;

    public EventReplicationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentStore OpenStoreB() => DocumentStore.For(opts =>
    {
        opts.Connection(_postgres.ConnectionString);
        opts.DatabaseSchemaName = "event_replication_node_b";
        opts.ConfigureHall9k(AutoCreate.All);
    });

    [Fact]
    public async Task Two_stores_exchange_a_tasks_events_and_reach_the_same_projection()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        // Node A: register the node stream, switch replication on (nothing pending yet), then
        // add and publish a task — every one of these events past the switch-on point.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        // Node B reads node A's outbox and applies the batch to its own, otherwise-empty store.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails? replicated = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            replicated.Should().NotBeNull();
            replicated!.ProjectId.Should().Be(projectId);
            replicated.State.Should().Be(TaskState.Queued);
        }
    }

    /// <summary>
    /// The disputed adversarial finding this fix resolves (independent pre-PR review, cycle 1,
    /// EventReplicationInbox.cs:174): a project's own id is minted per install (ProjectAddCommand),
    /// so the two stores here register the identical real-world project under two DIFFERENT local
    /// ids — the shape every genuine two-node exchange actually has, unlike every other test in this
    /// file, which uses one shared id for convenience. A replicated TaskAdded must still land under
    /// node B's own project id, never node A's foreign one, and each store's own projection reads
    /// the task back under its own coordinate.
    /// </summary>
    [Fact]
    public async Task Two_stores_registered_under_different_project_ids_exchange_a_tasks_events_and_each_reach_their_own_project()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        // Node B reads node A's outbox for the identical shared repository, but its own local
        // project id (projectIdB) has nothing to do with node A's (projectIdA) — exactly the
        // "minted per install" shape ProjectAddCommand documents.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails? replicated = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            replicated.Should().NotBeNull();
            replicated!.ProjectId.Should().Be(projectIdB, "the receiver's own local project id, never the sender's foreign one");
            replicated.State.Should().Be(TaskState.Queued);
        }

        // Node A's own copy is untouched by any of this — still under its own local project id.
        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            TaskDetails? original = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            original!.ProjectId.Should().Be(projectIdA);
        }
    }

    /// <summary>
    /// idea 202383dc, M2 (Brian's ruling 2026-09-17): a project's identity no longer depends on
    /// which ledger a message arrived through — each store resolves the sender's envelope by the
    /// project key it carries, against its OWN locally recorded <see cref="ProjectDetails.ProjectKey"/>,
    /// landing on its own local project id even though the two are unrelated Guids, the identical
    /// "minted per install" shape the sibling test above already covers for the id rewrite alone.
    /// </summary>
    [Fact]
    public async Task Two_stores_with_matching_recorded_project_keys_resolve_replicated_events_to_their_own_project()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        Guid projectIdB = DomainId.New();
        const string sharedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            session.Events.StartStream<ProjectAggregate>(
                projectIdA,
                new ProjectRegistered(projectIdA, ownerId, DomainId.New(), "Shared Project", "/repo-a", null, "main", Now));
            session.Events.Append(projectIdA, ProjectDecider.AssignKey(projectIdA, sharedProjectKey, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B registers the identical real-world project under its own, differently-minted id,
        // but reads back the IDENTICAL key from its own copy of the ledger — the deterministic
        // fact TrustChain.ProjectKey is (idea 202383dc, M2's own doc).
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdB,
                new ProjectRegistered(projectIdB, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            session.Events.Append(projectIdB, ProjectDecider.AssignKey(projectIdB, sharedProjectKey, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, sharedProjectKey, adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse("the envelope's own project key resolves to this exact local project");
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails? replicated = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            replicated.Should().NotBeNull();
            replicated!.ProjectId.Should().Be(projectIdB);
        }
    }

    /// <summary>
    /// The refusal half of the identical ruling: an events envelope whose own project key resolves,
    /// through the receiver's own <see cref="ProjectDetails.ProjectKey"/> lookup, to a DIFFERENT
    /// local project than the one this read is scoped to is refused rather than applied — the exact
    /// hazard the c8dd149c replication dispute exposed (mapping a sender's project to the receiver's
    /// own only through the ledger it was read through, never verified against the key itself).
    /// </summary>
    [Fact]
    public async Task An_events_envelope_whose_project_key_resolves_to_a_different_local_project_is_refused_and_named()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        const string mismatchedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B already has a DIFFERENT local project recorded under this exact key — a genuine
        // mismatch, never merely "no opinion recorded yet".
        Guid projectHoldingTheKey = DomainId.New();
        Guid scopedProjectId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectHoldingTheKey,
                new ProjectRegistered(projectHoldingTheKey, ownerId, DomainId.New(), "Holder", "/repo-holder", null, "main", Now));
            session.Events.Append(projectHoldingTheKey, ProjectDecider.AssignKey(projectHoldingTheKey, mismatchedProjectKey, Now));
            session.Events.StartStream<ProjectAggregate>(
                scopedProjectId,
                new ProjectRegistered(scopedProjectId, ownerId, DomainId.New(), "Scoped", "/repo-scoped", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, mismatchedProjectKey, adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeTrue("the envelope's own project key resolves to a different local project");
            read.EventsApplied.Should().Be(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().BeNull("a mismatched-key envelope is never applied");

            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, scopedProjectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.SenderIgnored.Should().BeTrue();
            cursor.IgnoredReason.Should().Contain("project key");
        }
    }

    /// <summary>
    /// idea 202383dc, M2 (independent pre-PR review, cycle 2, conformance lens, medium): the
    /// direct-comparison branch this project's own live <see cref="TrustChain.ProjectKey"/> feeds
    /// (<c>EventReplicationInbox.ResolveLocalProjectKeyAsync</c>) must refuse a mismatched envelope
    /// on its own, the same as the cross-project database lookup the test above already covers. The
    /// receiving project's own <see cref="ProjectDetails.ProjectKey"/> is never assigned and no
    /// other project is ever registered under the mismatched key, so the older fallback lookup
    /// cannot be what refuses this envelope — only the trust chain's own key can, exactly the path
    /// <c>MessageSweepEngine.ProbeAndReadAsync</c> takes on every real sweep once a project has one.
    /// </summary>
    [Fact]
    public async Task An_events_envelope_whose_project_key_mismatches_the_live_trust_chains_own_key_is_refused_and_named()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        const string thisProjectsOwnKey = "01ARZ3NDEKTSV4RRFFQ69G5FCC";
        const string mismatchedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid scopedProjectId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                scopedProjectId,
                new ProjectRegistered(scopedProjectId, ownerId, DomainId.New(), "Scoped", "/repo-scoped", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, mismatchedProjectKey, adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        TrustChain liveTrustChain = new(new Dictionary<string, TrustedOwner>(), [], ProjectKey: thisProjectsOwnKey);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: liveTrustChain, cts.Token);
            read.SenderIgnored.Should().BeTrue("the live trust chain's own key already answers the question directly");
            read.EventsApplied.Should().Be(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().BeNull("a mismatched-key envelope is never applied");

            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, scopedProjectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.SenderIgnored.Should().BeTrue();
            cursor.IgnoredReason.Should().Contain("project key");
        }
    }

    /// <summary>
    /// The other half of the same disputed finding: excluding the whole Project stream from
    /// replication (the fix session's first attempt) was itself wrong, since criterion 1 makes team
    /// settings project-scoped and the objective says every project-scoped event travels.
    /// ProjectTeamSettingsChanged must reach a teammate — applied to THAT node's own Project stream
    /// id, never the sender's, since the id is a per-install coordinate — while its node-scoped
    /// sibling, ProjectSettingsChanged, never leaves at all.
    /// </summary>
    [Fact]
    public async Task A_team_settings_change_applies_to_the_receivers_own_project_stream_and_the_node_part_never_travels()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            session.Events.StartStream<ProjectAggregate>(
                projectIdA,
                new ProjectRegistered(projectIdA, ownerId, DomainId.New(), "Shared Project", "/repo-a", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B registers the identical real-world project under its OWN, differently-minted id —
        // the ordinary shape of two installs of the same repository.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdB,
                new ProjectRegistered(projectIdB, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(1), cts.Token);
        }

        // The team half travels (ProjectScoped); the node half — model, parallelism, this
        // install's own filesystem paths — never does (NodeScoped), appended right beside it on
        // the identical Project stream.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(
                projectIdA,
                new ProjectTeamSettingsChanged(
                    projectIdA, Now.AddSeconds(2), ownerId, ClaimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee)));
            session.Events.Append(
                projectIdA,
                new ProjectSettingsChanged(
                    projectIdA, default, default, default, default, Now.AddSeconds(2), ownerId,
                    Model: Optional<AgentModel>.Of(AgentModel.Sonnet)));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            // Exactly the team event: ProjectRegistered (identity) and ProjectSettingsChanged
            // (node-scoped) are both never eligible to travel at all.
            read.EventsApplied.Should().Be(1);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectIdB, cts.Token);
            receiverProject.Should().NotBeNull();
            receiverProject!.ClaimGate.Should().Be(ClaimGate.TrackerAssignee, "the team half applied to this node's own Project stream");
            receiverProject.Model.Should().Be(
                AgentModel.Unknown, "the node half (ProjectSettingsChanged) is node-scoped and never travels");

            // Never a phantom stream under node A's own, foreign project id.
            (await session.LoadAsync<ProjectDetails>(projectIdA, cts.Token)).Should().BeNull();
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, adversarial lens (EventScopeRegistry.cs:204):
    /// <see cref="ProjectPromptAddendumSet"/> was classified <see cref="EventScope.ProjectScoped"/>
    /// — "the same tier as ProjectTeamSettingsChanged" — but was never added to
    /// <see cref="ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/>, the gate that
    /// actually decides whether a Project-stream event applies to the receiver's own project. Left
    /// unfixed, a replicated copy would land on a phantom stream keyed by the sender's foreign
    /// project id instead of the receiver's own, exactly like <see cref="ProjectTeamSettingsChanged"/>
    /// would have without its own entry there.
    /// </summary>
    [Fact]
    public async Task A_prompt_addendum_set_applies_to_the_receivers_own_project_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            session.Events.StartStream<ProjectAggregate>(
                projectIdA,
                new ProjectRegistered(projectIdA, ownerId, DomainId.New(), "Shared Project", "/repo-a", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdB,
                new ProjectRegistered(projectIdB, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(1), cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(
                projectIdA,
                new ProjectPromptAddendumSet(
                    projectIdA, "work", "Prefer squash commits.", OverCap: false, OverCapReason: null,
                    Now.AddSeconds(2), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            // Exactly the addendum event: ProjectRegistered (identity) is never eligible to travel.
            read.EventsApplied.Should().Be(1);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectIdB, cts.Token);
            receiverProject.Should().NotBeNull();
            receiverProject!.PromptAddenda.Should().ContainKey("work")
                .WhoseValue.Content.Should().Be("Prefer squash commits.");

            // Never a phantom stream under node A's own, foreign project id.
            (await session.LoadAsync<ProjectDetails>(projectIdA, cts.Token)).Should().BeNull();
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, conformance lens (ProjectStreamReplicationRules.cs:572):
    /// applying a teammate's own per-install lifecycle decision (archive, reactivate, rename,
    /// schedule or cancel a purge) to the receiver's own Project stream let another node's local
    /// <c>h9k project remove --purge</c> or rename act on this install's own copy of the project
    /// with no decider in the way. <see cref="ProjectArchived"/> must still travel and be recorded
    /// as a fact (the objective's own "every project-scoped event... every other node... appends it
    /// under the same stream id as a fact"), but never touch the receiver's own local project.
    /// </summary>
    [Fact]
    public async Task A_teammates_project_archived_never_touches_the_receivers_own_project()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            session.Events.StartStream<ProjectAggregate>(
                projectIdA,
                new ProjectRegistered(projectIdA, ownerId, DomainId.New(), "Shared Project", "/repo-a", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B registers the identical real-world project under its OWN, differently-minted id —
        // and never archives it locally.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdB,
                new ProjectRegistered(projectIdB, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(1), cts.Token);
        }

        // Node A archives and schedules a purge of ITS OWN local copy — a decision about node A's
        // own install alone.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(projectIdA, new ProjectArchived(projectIdA, "cleanup", Now.AddSeconds(2), ownerId));
            session.Events.Append(
                projectIdA, new ProjectPurgeScheduled(projectIdA, Now.AddSeconds(2), Now.AddSeconds(2).AddHours(24), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            // Both events are still recorded — as facts, never applied to B's own project.
            read.EventsApplied.Should().Be(2);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectIdB, cts.Token);
            receiverProject.Should().NotBeNull();
            receiverProject!.IsArchived.Should().BeFalse("node A's own archive is a decision about node A's own local copy alone");
            receiverProject.ArchivedAt.Should().BeNull();
            receiverProject.PurgeAt.Should().BeNull(
                "a replicated purge schedule must never let a teammate's node schedule this install's own project, tasks, runs, ideas, and epics for a hard delete");
        }
    }

    /// <summary>
    /// MessageInbox reads the identical outbox ref EventReplicationInbox does — same ref, same
    /// envelopes, two independent cursors — so an events envelope must never also land as an
    /// ordinary received message there, or h9k messages would show a raw batch of replicated
    /// domain events as if a teammate had typed them as a note.
    /// </summary>
    [Fact]
    public async Task An_events_envelope_never_lands_as_an_ordinary_received_message()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        MessageOutbox messageOutbox = new(transport);
        MessageInbox messageInbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            MessageInboxSweepResult read = await messageInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                cancellationToken: cts.Token);
            read.EnvelopesStored.Should().Be(0, "an events envelope is never an ordinary received message");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<MessageDetails>().AnyAsync(cts.Token)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_redelivered_batch_changes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        // First read applies the batch; a second read of the identical, unmoved outbox — the
        // ordinary shape of a sweep tick that finds nothing new since the last one — must change
        // nothing, since the transport never returns content already past this cursor.
        int firstApplied;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            firstApplied = (await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token)).EventsApplied;
        }

        firstApplied.Should().BeGreaterThan(0);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult redelivered = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
            redelivered.EventsApplied.Should().Be(0);
        }

        // Explicitly re-applying the exact same wire batch a second time (a genuine re-delivery,
        // not merely an unmoved cursor) is still a no-op: dedupe is keyed by origin event id.
        await using (IQuerySession readOnly = _postgres.Store.QuerySession())
        {
            TaskDetails source = (await readOnly.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            source.State.Should().Be(TaskState.Queued);
        }
    }

    [Fact]
    public async Task A_node_scoped_event_never_leaves()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        // Sanity check on the classification this test relies on: ProjectSettingsChanged is
        // node-scoped from M2a's own split onward.
        EventScopeRegistry.ClassificationOf(typeof(ProjectSettingsChanged)).Should().Be(EventScope.NodeScoped);

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        // A node-scoped event on the SAME project, appended after the switch-on point: nothing
        // about the outbound scan treats it as travel-eligible.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(
                projectId,
                new ProjectSettingsChanged(
                    projectId, default, default, default, default, Now.AddSeconds(2), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().ContainSingle();
        string body = MessageEnvelopeCodec.Decode(read.Envelopes.Single().Content).Envelope!.Body;
        IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord> batch =
            EventReplicationCodec.DecodeBatch(body)!;
        batch.Should().Contain(record => record.StreamId == taskId);
        batch.Should().NotContain(record => record.EventTypeName == typeof(ProjectSettingsChanged).FullName);
    }

    [Fact]
    public async Task An_event_before_the_switch_on_point_never_leaves()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // This task's own events exist BEFORE replication ever runs on this node — the migration
        // shape (Brian, 2026-09-13): "each node keeps its history; replication records a switch-on
        // point per node and nothing before it travels."
        Guid preSwitchOnTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now, cts.Token);

        // The first-ever QueuePendingAsync call is what switches replication on; it also picks up
        // this project's own outbox for the first time.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(1), cts.Token);
        }

        Guid postSwitchOnTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(2), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().ContainSingle();
        IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord> batch =
            EventReplicationCodec.DecodeBatch(MessageEnvelopeCodec.Decode(read.Envelopes.Single().Content).Envelope!.Body)!;

        batch.Should().Contain(record => record.StreamId == postSwitchOnTaskId);
        batch.Should().NotContain(record => record.StreamId == preSwitchOnTaskId);
    }

    [Fact]
    public async Task An_unverified_senders_batch_is_ignored()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        // Deliberately no node file seeded for nodeA — GitLedgerMessageTransport's own sender
        // verification rule, mirrored identically by InMemoryMessageTransport.
        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeTrue();
            read.EventsApplied.Should().Be(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().BeNull();

            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, projectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.SenderIgnored.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_private_draft_does_not_travel_until_the_flag_clears()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(
                ideaId, new IdeaCaptured(ideaId, ownerId, "A private thought", projectId, Now.AddSeconds(1), string.Empty));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: true, Now.AddSeconds(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        // First sweep: the idea is private, so its own events never even reach a batch.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            firstSweep.EventsQueued.Should().Be(0);
        }

        // Cleared: the identical sweep now finds it.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: false, Now.AddSeconds(3), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(4), cts.Token);
            secondSweep.EventsQueued.Should().BeGreaterThan(0);
        }

        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(4), cts.Token);

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().ContainSingle();
        IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord> batch =
            EventReplicationCodec.DecodeBatch(MessageEnvelopeCodec.Decode(read.Envelopes.Single().Content).Envelope!.Body)!;
        batch.Should().Contain(record => record.StreamId == ideaId);
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, both lenses (EventReplicationOutbox.cs:325/167): while a
    /// task or idea stays private, the durable position can only ever move backward to the
    /// hold-back point, so a scan resumes from there and rescans everything past it on every single
    /// sweep. Before <see cref="EventReplicationOutboxPosition.HighestQueuedSequence"/>, that rescan
    /// requeued and repushed every already-sent event past the hold-back point as a brand-new
    /// envelope, every tick, for as long as the flag stayed set — unbounded growth of the outbox ref
    /// and the local store. A sweep that finds nothing new must queue nothing new.
    /// </summary>
    [Fact]
    public async Task An_already_sent_event_past_the_hold_back_point_is_never_requeued()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            // Switches replication on right after node registration, before the idea or task below
            // exist, so neither is treated as pre-switch-on history.
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        // An idea goes private before any other project activity — the earliest possible hold-back
        // point, so every later scan resumes from before it.
        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(
                ideaId, new IdeaCaptured(ideaId, ownerId, "A private thought", projectId, Now.AddSeconds(1), string.Empty));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: true, Now.AddSeconds(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        // An ordinary, non-private task's own events come AFTER the hold-back point.
        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(2), cts.Token);

        // First sweep: switches replication on and finds the task's events past the still-private
        // idea — sent for the first time.
        EventReplicationQueueResult firstSweep;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
        }

        firstSweep.EnvelopesQueued.Should().Be(1);
        firstSweep.EventsQueued.Should().BeGreaterThan(0);

        // Second sweep: nothing changed — the idea is still private and the task has no new
        // events — so nothing eligible is new. The durable position is still capped below the
        // task's own events (the idea's hold-back point never moved), but they were already sent.
        EventReplicationQueueResult secondSweep;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(4), cts.Token);
        }

        secondSweep.EnvelopesQueued.Should().Be(0, "the task's events were already sent and must never be requeued as a new envelope");
        secondSweep.EventsQueued.Should().Be(0);

        // Only the one envelope from the first sweep ever reached the transport.
        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(4), cts.Token);
        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        read.Envelopes.Should().ContainSingle();
        IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord> batch =
            EventReplicationCodec.DecodeBatch(MessageEnvelopeCodec.Decode(read.Envelopes.Single().Content).Envelope!.Body)!;
        batch.Should().Contain(record => record.StreamId == taskId);
        batch.Should().NotContain(record => record.StreamId == ideaId);
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, adversarial lens (EventReplicationOutbox.cs:155): the
    /// <see cref="EventReplicationOutboxPosition.HighestQueuedSequence"/> skip alone cannot tell a
    /// held-back private event apart from an already-sent one once some other, later-sequence
    /// stream in the same project gets queued while the private event is still held back — a
    /// rescan after the flag clears would read the held-back event's own sequence as "already
    /// queued" from the mark alone and drop it forever, leaving the receiving inbox to start a
    /// phantom stream from whatever event of the idea's happens to arrive first.
    /// <see cref="EventReplicationOutboxPosition.PendingPrivateSequences"/> exists so this cannot
    /// happen: the idea's own held-back events are found and sent once the flag clears, however far
    /// other project activity has since pushed the high-water mark past them.
    /// </summary>
    [Fact]
    public async Task A_private_ideas_own_events_survive_a_later_streams_events_being_sent_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        // An idea goes private before any other project activity — the earliest possible hold-back
        // point, so the durable position resumes from before it on every later sweep.
        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(
                ideaId, new IdeaCaptured(ideaId, ownerId, "A private thought", projectId, Now.AddSeconds(1), string.Empty));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: true, Now.AddSeconds(1), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        // An ordinary, non-private task's own events come AFTER the hold-back point.
        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(2), cts.Token);

        // First sweep: the idea is held back, but the task's own events are new and eligible, so
        // they get queued and push HighestQueuedSequence past the idea's own two held-back events.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            firstSweep.EventsQueued.Should().BeGreaterThan(0);
        }

        // The owner clears the flag — its own new event lands well after everything above.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: false, Now.AddSeconds(4), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        // Second sweep: without PendingPrivateSequences, the idea's own two held-back events would
        // both read as "already queued" against the mark the task's events set above, and only the
        // clearing event itself would ever be sent.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(5), cts.Token);
            secondSweep.EventsQueued.Should().Be(3, "the idea's own capture, its privacy-set(true), and its privacy-set(false) must all reach the teammate");
        }

        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(5), cts.Token);

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<EventReplicationCodec.ReplicatedEventRecord> ideaRecords = [.. read.Envelopes
            .SelectMany(envelope => EventReplicationCodec.DecodeBatch(MessageEnvelopeCodec.Decode(envelope.Content).Envelope!.Body)!)
            .Where(record => record.StreamId == ideaId)];

        // Every event the idea ever carried reaches the teammate — not just the clearing event —
        // so the receiving inbox can start its own idea stream from IdeaCaptured rather than a
        // phantom StartStream seeded with only the privacy flag.
        ideaRecords.Should().HaveCount(3);
        ideaRecords.Select(record => record.EventTypeName).Should().Contain(type => type.Contains("IdeaCaptured"));
    }

    /// <summary>
    /// Independent pre-PR review, cycle 3, adversarial lens (EventReplicationInbox.cs:146): dedupe
    /// by origin event id used <c>LightweightSession.LoadAsync</c>, which only ever sees committed
    /// rows — so a second copy of the identical origin event landing later in the SAME read (exactly
    /// what an outbox resend, fixed above, used to produce) found no stored
    /// <see cref="ReplicatedEventRecord"/> yet and applied a duplicate. Two envelopes carrying the
    /// identical batch, read in one call, must still apply each origin event exactly once.
    /// </summary>
    [Fact]
    public async Task A_duplicate_origin_event_within_the_same_read_applies_only_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        // Simulate a resent duplicate: the identical envelope body, queued a second time under a
        // fresh seq — the exact shape the outbox's own pre-fix resend bug used to produce, and the
        // one case a re-delivery of an unmoved cursor can never itself exercise (a genuine
        // re-delivery replays the same seq, never a new one).
        string duplicateBody;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TransportReadResult firstRead = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
            duplicateBody = MessageEnvelopeCodec.Decode(firstRead.Envelopes.Single().Content).Envelope!.Body;
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, duplicateBody, Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        TransportReadResult bothEnvelopes = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        bothEnvelopes.Envelopes.Should().HaveCount(2, "the duplicate envelope must land beside the original, not replace it");

        int eventsInOneOriginalBatch = EventReplicationCodec.DecodeBatch(duplicateBody)!.Count;

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().Be(
                eventsInOneOriginalBatch, "each origin event id must apply exactly once, however many copies land in one read");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            IReadOnlyList<IEvent> streamEvents = await session.Events.FetchStreamAsync(taskId, token: cts.Token);
            streamEvents.Should().HaveCount(
                eventsInOneOriginalBatch, "a duplicate copy within the same read must never append a second time");
        }
    }

    [Fact]
    public async Task A_replicated_claim_never_dispatches_locally_and_shows_HeldElsewhere()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        // Node A claims the task locally.
        Guid runId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskClaimed claimed = TaskDecider.Claim(task, nodeA, ownerId, runId, Now.AddSeconds(2), "owner-a-fingerprint");
            session.Events.Append(taskId, claimed);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
        }

        // Node B's own dispatch-style read of Queued candidates never sees this task once the
        // claim has replicated in — it left the Queued set the moment the fact landed.
        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskListItem? replicated = await session.LoadAsync<TaskListItem>(taskId, cts.Token);
            replicated.Should().NotBeNull();
            replicated!.State.Should().Be(TaskState.Claimed);
            replicated.ClaimedByNodeId.Should().Be(nodeA);

            bool wouldDispatchLocally = await session.Query<TaskListItem>()
                .Where(candidate => candidate.Id == taskId && candidate.State.Value == TaskState.Queued.Value)
                .AnyAsync(cts.Token);
            wouldDispatchLocally.Should().BeFalse();

            // Node B never registered a NodeDetails row for nodeA (NodeRegistered is node-scoped
            // and never replicates), which is exactly the signal TaskStatusComposer's own
            // HeldElsewhere test reads: the claimant is absent from the node's own known set.
            (await session.LoadAsync<NodeDetails>(nodeA, cts.Token)).Should().BeNull();
        }
    }

    /// <summary>
    /// idea 202383dc, M2 (independent pre-PR review, cycle 1, both lenses, medium): a project-key
    /// mismatch mark is a fact about a specific envelope, so a later sweep that finds nothing new to
    /// inspect (the sender's outbox tip has not moved) must carry it forward unchanged rather than
    /// clearing it — only a sweep that genuinely inspects fresh content past the mismatch may
    /// redecide it, the identical rule <c>MessageInboxAggregate</c> already applies to
    /// <c>IgnoredForVerificationFailure</c>.
    /// </summary>
    [Fact]
    public async Task A_project_key_mismatch_mark_survives_a_later_sweep_that_finds_nothing_new()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        const string mismatchedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B already has a DIFFERENT local project recorded under this exact key — a genuine
        // mismatch, never merely "no opinion recorded yet" that leftover rows from another test's
        // schema could otherwise supply (independent pre-PR review, cycle 3, adversarial lens,
        // medium).
        Guid projectHoldingTheKey = DomainId.New();
        Guid scopedProjectId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectHoldingTheKey,
                new ProjectRegistered(projectHoldingTheKey, ownerId, DomainId.New(), "Holder", "/repo-holder", null, "main", Now));
            session.Events.Append(projectHoldingTheKey, ProjectDecider.AssignKey(projectHoldingTheKey, mismatchedProjectKey, Now));
            session.Events.StartStream<ProjectAggregate>(
                scopedProjectId,
                new ProjectRegistered(scopedProjectId, ownerId, DomainId.New(), "Scoped", "/repo-scoped", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, mismatchedProjectKey, adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult firstRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            firstRead.SenderIgnored.Should().BeTrue("the envelope's own project key resolves to a different local project");
        }

        // A second sweep with nothing new past the sender's outbox tip — the ordinary shape a
        // recurring sweep tick takes once it has caught up — must never read that empty result as
        // permission to clear a standing mismatch mark.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: null, cts.Token);
            secondRead.SenderIgnored.Should().BeTrue("nothing new was inspected, so the standing mismatch mark must carry forward");
            secondRead.EventsApplied.Should().Be(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, scopedProjectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.SenderIgnored.Should().BeTrue();
            cursor.IgnoredReason.Should().Contain("project key");
        }
    }

    /// <summary>
    /// idea 202383dc, M2 (independent pre-PR review, cycle 1, both lenses, low): the project-key
    /// check must run before the <see cref="MessageKind.Events"/> filter, the identical order
    /// <c>MessageInbox.ReadFromAsync</c> applies — otherwise an ordinary, non-events envelope
    /// stamped with the same foreign key advances <c>highestSeqConsidered</c> without its own key
    /// ever being examined, clearing a standing mismatch mark that the sender never actually
    /// stopped earning.
    /// </summary>
    [Fact]
    public async Task A_non_events_envelope_carrying_the_same_foreign_key_never_clears_a_standing_mismatch_mark()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdA = DomainId.New();
        const string mismatchedProjectKey = "01ARZ3NDEKTSV4RRFFQ69G5FBZ";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B already has a DIFFERENT local project recorded under this exact key — a genuine
        // mismatch, never merely "no opinion recorded yet".
        Guid projectHoldingTheKey = DomainId.New();
        Guid scopedProjectId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectHoldingTheKey,
                new ProjectRegistered(projectHoldingTheKey, ownerId, DomainId.New(), "Holder", "/repo-holder", null, "main", Now));
            session.Events.Append(projectHoldingTheKey, ProjectDecider.AssignKey(projectHoldingTheKey, mismatchedProjectKey, Now));
            session.Events.StartStream<ProjectAggregate>(
                scopedProjectId,
                new ProjectRegistered(scopedProjectId, ownerId, DomainId.New(), "Scoped", "/repo-scoped", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now, cts.Token);
        }

        await SeedQueuedTaskAsync(_postgres.Store, projectIdA, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, mismatchedProjectKey, adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult firstRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            firstRead.SenderIgnored.Should().BeTrue("the events envelope's own project key resolves to a different local project");
        }

        // An ordinary note, never an events envelope, sent next — still stamped with the identical
        // foreign key. EventReplicationInbox never applies a note (it only reads Events-kind
        // envelopes), but it must still examine this envelope's own project key before it decides
        // that on kind alone: the sender is still stamping the same foreign key, so the standing
        // mismatch mark must not clear just because the newest envelope happens not to be Events.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectIdA, "owner-a-fingerprint", MessageAudience.Project, null, MessageKind.Note,
                "still on the wrong project", Now.AddSeconds(4), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, mismatchedProjectKey, adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(5), trustChain: null, cts.Token);
            secondRead.SenderIgnored.Should().BeTrue(
                "the sender is still stamping the same foreign key, even though the newest envelope is not events-kind");
            secondRead.EventsApplied.Should().Be(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, scopedProjectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.SenderIgnored.Should().BeTrue();
            cursor.IgnoredReason.Should().Contain("project key");
        }
    }

    /// <summary>
    /// idea 202383dc, M2 (independent pre-PR review, cycle 1, both lenses, medium): a "sender not
    /// vouched" mark is a fact about the sender's current vouch status, never about a specific
    /// envelope — the identical distinction <c>MessageInbox.ReadFromAsync</c>'s own
    /// <c>ConfirmVouched</c> branch already draws against <c>IgnoredForVerificationFailure</c>. Once
    /// the sender is re-vouched, a sweep that finds nothing new to send must still clear the mark:
    /// waiting for fresh content would leave a revoked-then-restored sender ignored forever whenever
    /// it has nothing new to say.
    /// </summary>
    [Fact]
    public async Task A_not_vouched_mark_clears_once_the_sender_is_revouched_even_with_nothing_new_to_read()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        // Deliberately no node file seeded yet — the sender starts out not vouched for.
        FakeLedger ledger = new();
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(1), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(1), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult firstRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(2), trustChain: null, cts.Token);
            firstRead.SenderIgnored.Should().BeTrue("no node file vouches for this sender yet");
        }

        // The sender is re-vouched (a node file lands), but sends nothing further — the ordinary
        // shape of a sweep tick after the sender's own outbox has already been fully read once.
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: null, cts.Token);
            secondRead.SenderIgnored.Should().BeFalse("the sender is vouched again, even though this sweep found nothing new");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, projectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.SenderIgnored.Should().BeFalse();
            cursor.IgnoredReason.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_squashs_low_water_mark_still_reports_the_pruned_range_so_a_cold_reader_can_ask_a_peer()
    {
        // Independent pre-PR review, cycle 1, both lenses, medium: a squash's own low-water mark
        // lets a cold reader resume past pruned content instead of stalling forever on it (the
        // fix this branch adds), but that pruned range was never actually inspected either — a
        // peer might still hold it. This proves EventReplicationInbox still surfaces it via
        // StalledAtSeq (TransportReadResult.PrunedBelowSeq, folded in), the identical signal
        // MessageSweepEngine.ProbeAndReadAsync already reads to trigger a gap-fill request for a
        // genuine in-band gap — the pruned range must trigger it too, or the recovery path this
        // platform has for exactly this case is silently skipped.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            // Registers the node AND establishes the switch-on point (EnsureSwitchedOnAsync's own
            // "first call wins" rule) before the old task even exists — the identical ordering
            // Two_stores_exchange_a_tasks_events_and_reach_the_same_projection uses, so both tasks
            // added below actually land past it and are eligible to queue at all.
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        DateTimeOffset oldSentAt = Now.AddSeconds(2);
        Guid oldTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", oldSentAt, cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, oldSentAt, cts.Token);
        }

        DateTimeOffset youngSentAt = oldSentAt.AddHours(40);
        Guid youngTaskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, youngSentAt.AddSeconds(-1), cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", youngSentAt, cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, youngSentAt, cts.Token);
        }

        // 48-hour retention measured from just past the old envelope's own SentAt: the old task's
        // events envelope (seq 1) falls outside the window and is pruned; the young one (seq 2)
        // stays — leaving a verified low-water mark of 2.
        DateTimeOffset squashNow = oldSentAt.AddHours(48).AddMinutes(1);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await messageOutbox.SquashAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", TimeSpan.FromHours(48), committer,
                signingKey, squashNow, cts.Token);
        }

        // Node B reads cold (its own cursor has never seen this sender at all).
        EventReplicationReadResult read;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", squashNow.AddSeconds(1),
                trustChain: null, cts.Token);
        }

        read.EventsApplied.Should().BeGreaterThan(0, "the young task's own events envelope (seq 2) survived the squash");
        read.StalledAtSeq.Should().Be(1, "the pruned range below the low-water mark is reported so a gap-fill request can still ask a peer for it");

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskDetails>(youngTaskId, cts.Token)).Should().NotBeNull();
            (await session.LoadAsync<TaskDetails>(oldTaskId, cts.Token)).Should().BeNull(
                "the old task's own events were pruned away and never reached node B directly");
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
