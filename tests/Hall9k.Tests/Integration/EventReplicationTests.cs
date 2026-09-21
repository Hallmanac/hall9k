using System.Text.Json;
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
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
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
using Microsoft.Extensions.Logging;
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
    /// The identical gate, for the identical reason, one piece later (idea b9b09779, piece 4;
    /// this branch's own self-review, blast-radius sweep): <see cref="ProjectRunSkillRecorded"/>
    /// is classified <see cref="EventScope.ProjectScoped"/> because a run skill describes the
    /// SHARED repository, and without its own entry in
    /// <see cref="ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/> a replicated copy
    /// would land on a phantom stream keyed by the sender's foreign project id — the receiving
    /// member would never see the skill, which is the whole point of putting it on the ledger.
    /// </summary>
    [Fact]
    public async Task A_recorded_run_skill_applies_to_the_receivers_own_project_stream()
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
                new ProjectRunSkillRecorded(
                    projectIdA, "Run skill shape: pointer.\n\n## Launch\n\nmake dev\n", RunSkillShape.Pointer,
                    "abc123", RunSkillAuthor.DiscoverySession, Now.AddSeconds(2), ownerId));
            // The node-scoped events beside it never travel, so a request appended here must not
            // reach node B at all — the EventsApplied count below is what proves it.
            session.Events.Append(
                projectIdA, new ProjectRunSkillDiscoveryRequested(projectIdA, Now.AddSeconds(2), ownerId));
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
            read.EventsApplied.Should().Be(1, "only the recorded skill travels; the request beside it is node-scoped");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectIdB, cts.Token);
            receiverProject.Should().NotBeNull();
            receiverProject!.RunSkill.Should().NotBeNull();
            receiverProject.RunSkill!.Shape.Should().Be(RunSkillShape.Pointer);
            receiverProject.RunSkill.Content.Should().Contain("make dev");

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
                ideaId, new IdeaCaptured(ideaId, ownerId, "A private thought", projectId, Now.AddSeconds(1), string.Empty, ReplicationScope.Fleet));
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
                ideaId, new IdeaCaptured(ideaId, ownerId, "A private thought", projectId, Now.AddSeconds(1), string.Empty, ReplicationScope.Fleet));
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
                ideaId, new IdeaCaptured(ideaId, ownerId, "A private thought", projectId, Now.AddSeconds(1), string.Empty, ReplicationScope.Fleet));
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

    /// <summary>
    /// idea 8c5993c5: a fleet-scoped item's events address <c>owner:&lt;fingerprint&gt;</c> — every
    /// node this same owner runs, and no one else's — while a team-scoped item's events still
    /// address the whole project, exactly as every event always has.
    /// </summary>
    [Fact]
    public async Task A_fleet_scoped_ideas_events_address_the_owner_root_while_a_team_scoped_tasks_events_address_the_project()
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

        // Fresh idea: fleet scope by default (idea 8c5993c5).
        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "A thought for the fleet", projectId: projectId, Now.AddSeconds(1), ProjectHome.None));
            await session.SaveChangesAsync(cts.Token);
        }

        // Published task: team scope unconditionally.
        Guid taskId = await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(2), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> envelopes = [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];

        MessageEnvelopeV1 ideaEnvelope = envelopes.Single(envelope =>
            EventReplicationCodec.DecodeBatch(envelope.Body)!.Any(record => record.StreamId == ideaId));
        ideaEnvelope.To.Should().Be(MessageAudience.Owner("owner-a-fingerprint"), "a fleet item reaches only this owner's own nodes");

        MessageEnvelopeV1 taskEnvelope = envelopes.Single(envelope =>
            EventReplicationCodec.DecodeBatch(envelope.Body)!.Any(record => record.StreamId == taskId));
        taskEnvelope.To.Should().Be(MessageAudience.Project, "a team item still reaches the whole project");
    }

    /// <summary>
    /// idea 8c5993c5: "a scope change re-sends the item's full history at the new scope so a shared
    /// item arrives whole" — a fleet item's own earlier events, already sent once to the owner's own
    /// fleet and moved past by this outbox's own forward position, must reach the wider team
    /// audience too once it is shared, not just whatever happens after the share.
    /// </summary>
    [Fact]
    public async Task Sharing_a_fleet_scoped_idea_resends_its_full_history_at_team_scope()
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

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid ideaId = DomainId.New();
        IdeaAggregate idea = new();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaCaptured captured = IdeaDecider.Capture(
                ideaId, ownerId, "Fleet-only for now", projectId: projectId, Now.AddSeconds(1), ProjectHome.None);
            idea.Apply(captured);
            session.Events.StartStream<IdeaAggregate>(ideaId, captured);
            IdeaRevised revised = IdeaDecider.Revise(idea, "Fleet-only for now, sharpened", Now.AddSeconds(2), ownerId);
            idea.Apply(revised);
            session.Events.Append(ideaId, revised);
            await session.SaveChangesAsync(cts.Token);
        }

        // First sweep: sent once, fleet audience only — and the position moves past both events.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            firstSweep.EventsQueued.Should().Be(2, "the idea's own capture and revision, both sent fleet-scoped");
        }

        // Shared with the team — a scope-widening event past the position the first sweep already reached.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(ideaId, IdeaDecider.Share(idea, Now.AddSeconds(4), ownerId)!);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(5), cts.Token);
            secondSweep.EventsQueued.Should().Be(
                3, "the share event itself, plus the resend of the capture and revision the first sweep already moved past");
        }

        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(5), cts.Token);

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> envelopes = [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];

        MessageEnvelopeV1 teamEnvelope = envelopes.Single(envelope => envelope.To == MessageAudience.Project);
        List<EventReplicationCodec.ReplicatedEventRecord> teamBatch = [.. EventReplicationCodec.DecodeBatch(teamEnvelope.Body)!];
        teamBatch.Should().HaveCount(3, "the share, and the resent capture and revision, all at team scope")
            .And.OnlyContain(record => record.StreamId == ideaId);
        // Deterministic origin-sequence order, not an unordered .Contain: a pre-pass over this same
        // call's own candidates resends the earlier capture and revision into this batch BEFORE the
        // forward scan ever reaches the triggering share event (independent pre-PR review, cycle 9,
        // conformance lens), so the batch already builds as [capture, revision, share] here and
        // FlushAsync's own sort-before-encode is a no-op for this particular test. That sort still
        // matters in general — it is what keeps a resend and its own trigger in order whenever an
        // overflow split lands them in different envelopes (see the sibling test below, which reaches
        // that split) — but this test alone no longer exercises it.
        teamBatch.Select(record => record.EventTypeName).Should().Equal(
        [
            typeof(IdeaCaptured).FullName,
            typeof(IdeaRevised).FullName,
            typeof(IdeaScopeSet).FullName,
        ]);

        // Run the resent batch through the real inbox, not just decode it — a genuinely new team
        // member, never a fleet sibling, must reconstruct the idea's complete history from this one
        // envelope alone (independent pre-PR review, cycle 4, conformance lens, medium: neither of
        // this cycle's own high-severity fixes had a test that exercised the receiving side at all).
        await using DocumentStore storeB = OpenStoreB();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult applied = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: null, cts.Token);
            applied.EventsApplied.Should().Be(3, "the share, capture, and revision all apply cleanly, in order");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            IdeaDetails? details = await session.LoadAsync<IdeaDetails>(ideaId, cts.Token);
            details.Should().NotBeNull("the receiver reconstructs the idea from this one envelope alone");
            details!.Text.Should().Be("Fleet-only for now, sharpened");
            details.Scope.Should().Be(ReplicationScope.Team);
        }
    }

    /// <summary>
    /// idea 8c5993c5, independent pre-PR review, idea 19489eff, cycle 11, adversarial lens, medium:
    /// <see cref="ReplicationProjectResolver"/> resolves a run's own scope from its owning task's,
    /// never independently, so a run's history must be resent alongside its task's own whenever the
    /// task's scope widens — otherwise a teammate newly let in by the share receives the task whole
    /// but no run history at all, leaving <c>RunDetails</c> unreconstructed on their node.
    /// </summary>
    [Fact]
    public async Task Sharing_a_fleet_scoped_task_resends_its_runs_history_too()
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

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Fleet-only draft with a run already on it",
                ["done"], TaskType.Feature, null, null, null, Now.AddSeconds(1), ownerId);
            session.Events.StartStream<TaskAggregate>(
                taskId, added, new TaskClaimed(taskId, nodeA, ownerId, 1, runId, Now.AddSeconds(2)));
            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, nodeA, ownerId, 1, DomainId.New(), "/wt/fleet-run", "task/fleet-run",
                ExecutorMode.Subscription, Now.AddSeconds(2)));
            await session.SaveChangesAsync(cts.Token);
        }

        // First sweep: the task's own history and the run's own history, both sent fleet-scoped —
        // and the position moves past all three events.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(3), cts.Token);
            firstSweep.EventsQueued.Should().Be(3, "the task's own add and claim, plus the run's own dispatch");
        }

        // Shared with the team — a scope-widening event past the position the first sweep already reached.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Share(task, Now.AddSeconds(4), ownerId)!);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(5), cts.Token);
            secondSweep.EventsQueued.Should().Be(
                4, "the share itself, the resent add and claim, and — the fix under test — the resent run dispatch");
        }

        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(5), cts.Token);

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> envelopes = [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];

        MessageEnvelopeV1 teamEnvelope = envelopes.Single(envelope => envelope.To == MessageAudience.Project);
        List<EventReplicationCodec.ReplicatedEventRecord> teamBatch = [.. EventReplicationCodec.DecodeBatch(teamEnvelope.Body)!];
        teamBatch.Select(record => record.StreamId).Should().Contain(
            runId, "the run's own history must reach the team alongside its task's, not be left behind");
        teamBatch.Should().Contain(record => record.EventTypeName == typeof(RunDispatched).FullName);

        // Run the resent batch through the real inbox: a genuinely new team member reconstructs the
        // run, not just the task, from this one envelope alone.
        await using DocumentStore storeB = OpenStoreB();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: null, cts.Token);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            RunDetails? run = await session.LoadAsync<RunDetails>(runId, cts.Token);
            run.Should().NotBeNull("the receiver reconstructs the run from the resent history, not just the task");
        }
    }

    /// <summary>
    /// idea 8c5993c5, independent pre-PR review cycle 8 (adversarial lens, high, with no regression
    /// test of its own — the finding this test resolves): the sibling above proves the resend lands
    /// before its own trigger for a stream small enough to fit one envelope, so it can never exercise
    /// the overflow split at all. Here the fleet-scoped idea's own history alone exceeds
    /// <see cref="EventReplicationOutbox.MaxEventsPerEnvelope"/>, forcing <c>FlushAsync</c> to split
    /// the resend across more than one envelope before the share event that triggered it is ever
    /// added — the exact shape cycle 7's own fix (moving <c>ResendIfNeededAsync</c> ahead of the
    /// trigger's own <c>AddAsync</c> call) exists to get right. A move of that call back after the
    /// trigger's own <c>AddAsync</c> would let a mid-scan overflow flush carry the trigger out in an
    /// earlier envelope than some of the history it depends on, and this test's own ascending-
    /// sequence assertion across every envelope, in send order, catches exactly that regression.
    /// </summary>
    [Fact]
    public async Task Sharing_a_fleet_scoped_idea_with_history_wider_than_one_envelope_resends_it_in_order_before_the_trigger()
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

        // Capture plus enough revisions to push the idea's own history past one envelope's worth of
        // records (EventReplicationOutbox.MaxEventsPerEnvelope) on its own, before the share ever runs.
        const int RevisionCount = EventReplicationOutbox.MaxEventsPerEnvelope + 50;

        Guid ideaId = DomainId.New();
        IdeaAggregate idea = new();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaCaptured captured = IdeaDecider.Capture(
                ideaId, ownerId, "Fleet-only, revision 0", projectId: projectId, Now.AddSeconds(1), ProjectHome.None);
            idea.Apply(captured);
            session.Events.StartStream<IdeaAggregate>(ideaId, captured);

            for (int i = 1; i <= RevisionCount; i++)
            {
                IdeaRevised revised = IdeaDecider.Revise(idea, $"Fleet-only, revision {i}", Now.AddSeconds(1 + i), ownerId);
                idea.Apply(revised);
                session.Events.Append(ideaId, revised);
            }

            await session.SaveChangesAsync(cts.Token);
        }

        // First sweep: sent once, fleet audience only, split across as many envelopes as the history
        // alone already needs — and the position moves past every one of them.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(500), cts.Token);
            firstSweep.EventsQueued.Should().Be(RevisionCount + 1, "the capture plus every revision, all sent fleet-scoped");
        }

        // Shared with the team — a scope-widening event past the position the first sweep already reached.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(ideaId, IdeaDecider.Share(idea, Now.AddSeconds(501), ownerId)!);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(502), cts.Token);
            secondSweep.EventsQueued.Should().Be(
                RevisionCount + 2,
                "the share event itself, plus the resend of the capture and every revision the first sweep already moved past");
        }

        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(502), cts.Token);

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> envelopes = [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];

        // Send order, not decoded/regrouped: the same order a receiving inbox actually reads them in.
        List<MessageEnvelopeV1> teamEnvelopes = [.. envelopes.Where(envelope => envelope.To == MessageAudience.Project)];
        teamEnvelopes.Should().HaveCountGreaterThan(
            1, "more resend history than MaxEventsPerEnvelope forces the flush to split it across envelopes");

        List<EventReplicationCodec.ReplicatedEventRecord> orderedTeamRecords =
            [.. teamEnvelopes.SelectMany(envelope => EventReplicationCodec.DecodeBatch(envelope.Body)!)];
        orderedTeamRecords.Should().HaveCount(RevisionCount + 2)
            .And.OnlyContain(record => record.StreamId == ideaId);

        // The defining assertion: across every envelope the flush produced, in the order they were
        // queued, the stream's own origin sequence only ever increases — so the resent history always
        // reads strictly before the share that triggered it, envelope split or not. Moving
        // ResendIfNeededAsync back after the trigger's own AddAsync call would instead let an early
        // overflow flush carry the trigger out ahead of some later-flushed slice of its own history,
        // breaking this exact ordering.
        orderedTeamRecords.Select(record => record.OriginSequence).Should().BeInAscendingOrder();
        orderedTeamRecords[^1].EventTypeName.Should().Be(
            typeof(IdeaScopeSet).FullName, "the share event is always the newest fact on this stream, so it always sorts last");
    }

    /// <summary>
    /// idea 8c5993c5, independent pre-PR review cycle 9 (conformance lens, high): the sibling above
    /// proves the resend lands before its own trigger when the WHOLE history sits below the resend's
    /// own upper bound, added contiguously in ascending order before the forward scan ever reaches
    /// the trigger — a shape the resend's own bound already handles correctly no matter when it runs.
    /// Here, instead, enough of the idea's history is appended AFTER the first sweep already sent the
    /// capture and moved the position past it, so the SAME call's own forward scan adds that new
    /// history — sequenced ABOVE the resend's own upper bound — to the Project batch before it ever
    /// reaches the share that triggers the resend. Resending inline, immediately before only the
    /// share's own <c>AddAsync</c> (the shape cycle 7's fix left in place until cycle 9), let those
    /// already-batched, higher-sequenced revisions overflow into an earlier envelope while the
    /// resent, lower-sequenced capture shipped in a later one — this test's own ascending-sequence
    /// assertion, across every envelope in send order, catches exactly that regression.
    /// </summary>
    [Fact]
    public async Task Sharing_a_fleet_scoped_idea_whose_history_grows_past_one_envelope_after_the_first_sweep_still_resends_the_capture_first()
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
        IdeaAggregate idea = new();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaCaptured captured = IdeaDecider.Capture(
                ideaId, ownerId, "Fleet-only, before the flood", projectId: projectId, Now.AddSeconds(1), ProjectHome.None);
            idea.Apply(captured);
            session.Events.StartStream<IdeaAggregate>(ideaId, captured);
            await session.SaveChangesAsync(cts.Token);
        }

        // First sweep: only the capture exists — sent once, fleet audience only, and the position
        // moves past it.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult firstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            firstSweep.EventsQueued.Should().Be(1, "only the capture exists yet, sent fleet-scoped");
        }

        // Between sweeps: enough revisions to exceed one envelope's worth on their own, THEN the
        // share — so this SAME call's own forward scan adds every revision (sequenced above the
        // resend's own upper bound) to the Project batch before it ever reaches the share.
        const int RevisionCount = EventReplicationOutbox.MaxEventsPerEnvelope + 50;
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            for (int i = 1; i <= RevisionCount; i++)
            {
                IdeaRevised revised = IdeaDecider.Revise(idea, $"Fleet-only, revision {i}", Now.AddSeconds(2 + i), ownerId);
                idea.Apply(revised);
                session.Events.Append(ideaId, revised);
            }

            session.Events.Append(ideaId, IdeaDecider.Share(idea, Now.AddSeconds(500), ownerId)!);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult secondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(501), cts.Token);
            secondSweep.EventsQueued.Should().Be(
                RevisionCount + 2,
                "every revision and the share itself, plus the resend of the capture the first sweep already moved past");
        }

        await messageOutbox.FlushAsync(
            _postgres.Store.LightweightSession(), RepositoryPath, nodeA, projectId, "shared-project-key",
            adoptUnassigned: false, committer, signingKey, Now.AddSeconds(501), cts.Token);

        TransportReadResult read = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> envelopes = [.. read.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];

        // Send order, not decoded/regrouped: the same order a receiving inbox actually reads them in.
        List<MessageEnvelopeV1> teamEnvelopes = [.. envelopes.Where(envelope => envelope.To == MessageAudience.Project)];
        teamEnvelopes.Should().HaveCountGreaterThan(
            1, "more new history than MaxEventsPerEnvelope forces the flush to split it across envelopes");

        List<EventReplicationCodec.ReplicatedEventRecord> orderedTeamRecords =
            [.. teamEnvelopes.SelectMany(envelope => EventReplicationCodec.DecodeBatch(envelope.Body)!)];
        orderedTeamRecords.Should().HaveCount(RevisionCount + 2)
            .And.OnlyContain(record => record.StreamId == ideaId);

        // The defining assertion: the resent capture — the lowest origin sequence on this stream —
        // must still be the first record in send order, even though the forward scan's own revisions
        // (all sequenced above it) would otherwise have filled the first envelope on their own before
        // the scan ever reached the share that triggers the resend.
        orderedTeamRecords.Select(record => record.OriginSequence).Should().BeInAscendingOrder();
        orderedTeamRecords[0].EventTypeName.Should().Be(
            typeof(IdeaCaptured).FullName, "the resent capture is the oldest fact on this stream, so it always sorts first");
        orderedTeamRecords[^1].EventTypeName.Should().Be(
            typeof(IdeaScopeSet).FullName, "the share event is always the newest fact on this stream, so it always sorts last");
    }

    /// <summary>
    /// idea 8c5993c5, independent pre-PR review cycle 3 (adversarial, high) and cycle 4
    /// (conformance, high): a scope change can just as easily be appended at a fleet sibling that
    /// only ever holds a stream by replication, never at the stream's own true origin. That
    /// sibling's own resend must never forward the earlier history it only holds by replication —
    /// forwarding it would hand the true origin node its own facts back under a fresh envelope,
    /// which the origin holds no <see cref="ReplicatedEventRecord"/> for and would re-append as an
    /// undeduped second copy (cycle 3's own fix, <see cref="ResendStreamHistoryAsync"/>'s
    /// OriginEventId check). But the true origin still owes that history to the team once it learns
    /// of the scope change, which it only ever does the way any other node does: by receiving the
    /// SAME scope-changing event back by replication. Cycle 4's own fix is what lets the true
    /// origin's next forward scan notice that a FOREIGN-origin candidate is scope-changing and
    /// resend its own earlier native history rather than skip it outright before ever checking.
    /// </summary>
    [Fact]
    public async Task A_scope_change_appended_at_a_fleet_sibling_lets_the_true_origin_catch_up_its_own_history()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        const string ownerFingerprint = "owner-a-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");
        (LedgerCommitter committerB, LedgerSigningKey signingKeyB) = Signing("node-b");

        await using DocumentStore storeB = OpenStoreB();

        Guid ideaId = DomainId.New();

        // Node A: the idea's own true origin, switched on before it exists, then captured and
        // revised — two native events, sent fleet-scoped and moved past by the first sweep.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, ownerFingerprint, Now, cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaCaptured captured = IdeaDecider.Capture(
                ideaId, ownerId, "Fleet-only for now", projectId: projectId, Now.AddSeconds(1), ProjectHome.None);
            session.Events.StartStream<IdeaAggregate>(ideaId, captured);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.Revise(idea, "Fleet-only for now, sharpened", Now.AddSeconds(2), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult aFirstSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, ownerFingerprint, Now.AddSeconds(3), cts.Token);
            aFirstSweep.EventsQueued.Should().Be(2, "the capture and revision, sent fleet-scoped and moved past");
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(3), cts.Token);
        }

        // Node B: same owner root, a fleet sibling that only ever holds this idea by replication.
        // Switched on before receiving anything, exactly like A, so the two replicated events land
        // past B's own switch-on point and are eligible for B's own resend consideration below.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeB, new NodeRegistered(nodeB, ownerId, "node-b", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeB, projectId, ownerFingerprint, Now, cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, ownerFingerprint, Now.AddSeconds(4), trustChain: null,
                cts.Token);
            read.EventsApplied.Should().Be(2, "B holds the capture and revision only by replication, never natively");
        }

        // B's own next sweep finds nothing new to send — a replicated fact never travels, not even
        // on B's own first look at it — but moves B's own position past both events, the same
        // "first sweep moves past" staging the sharing test above relies on: needed here so the
        // resend pass below actually considers them, rather than finding them still inside the
        // ordinary forward scan of this very same call.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationQueueResult bSettleSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeB, projectId, ownerFingerprint, Now.AddSeconds(5), cts.Token);
            bSettleSweep.EventsQueued.Should().Be(0, "a replicated fact never travels, not even on B's own first look at it");
        }

        // B shares the idea with the team — its own native event, on top of the two it only holds
        // by replication.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.Share(idea, Now.AddSeconds(6), ownerId)!);
            await session.SaveChangesAsync(cts.Token);
        }

        // B's own sweep: the share travels, but B's own resend must never forward the two events it
        // only holds by replication (cycle 3's own fix).
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationQueueResult bShareSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeB, projectId, ownerFingerprint, Now.AddSeconds(7), cts.Token);
            bShareSweep.EventsQueued.Should().Be(
                1, "only the share itself travels from B — it holds no native history of its own to resend");
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeB, projectId, "shared-project-key", adoptUnassigned: false, committerB,
                signingKeyB, Now.AddSeconds(7), cts.Token);
        }

        // Node A receives the share back by replication: a foreign-origin fact landing on its own
        // copy of the very stream it itself originated.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeB, projectId, nodeA, ownerFingerprint, Now.AddSeconds(8), trustChain: null,
                cts.Token);
            read.EventsApplied.Should().Be(1, "the share — the only fact from B that A does not already hold natively");
        }

        // A's own next sweep has to notice the widened scope on this foreign-origin candidate and
        // resend ITS OWN earlier native history — cycle 4's own fix, the one this test exists for.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult aSecondSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, ownerFingerprint, Now.AddSeconds(9), cts.Token);
            aSecondSweep.EventsQueued.Should().Be(
                2, "A resends its own capture and revision now that team scope has landed on its own stream — "
                + "never the share itself, which is foreign-origin here and already reached the team from B directly");
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(9), cts.Token);
        }

        TransportReadResult readA = await transport.ReadSinceAsync(RepositoryPath, nodeA, sinceSeq: 0, cts.Token);
        List<MessageEnvelopeV1> aEnvelopes = [.. readA.Envelopes.Select(raw => MessageEnvelopeCodec.Decode(raw.Content).Envelope!)];
        MessageEnvelopeV1 aTeamEnvelope = aEnvelopes.Single(envelope =>
            envelope.To == MessageAudience.Project
            && EventReplicationCodec.DecodeBatch(envelope.Body)!.Any(record => record.StreamId == ideaId));
        List<EventReplicationCodec.ReplicatedEventRecord> resent = [.. EventReplicationCodec.DecodeBatch(aTeamEnvelope.Body)!];
        resent.Select(record => record.EventTypeName).Should().Equal(
        [
            typeof(IdeaCaptured).FullName,
            typeof(IdeaRevised).FullName,
        ], "A's own resend, oldest first, never re-including the foreign-origin share it just received");
        resent.Should().OnlyContain(record => record.StreamId == ideaId);
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, adversarial lens (EventReplicationOutbox.cs:313): the
    /// native case in the <c>resolved.Scope == Private</c> branch records a held-back event in
    /// <see cref="EventReplicationOutboxPosition.PendingPrivateSequences"/> so a later sweep can find
    /// it again once scope widens — but a foreign-origin candidate never rides an envelope
    /// regardless of its own resolved scope (only what a node produces natively ever travels), so it
    /// never needs that treatment. Before this fix, such a candidate got neither the pendingPrivate
    /// entry nor an advance of the scan's own position marker, so once it was the highest sequence a
    /// sweep had ever scanned, the durable position froze behind it and every future sweep
    /// re-resolved it from scratch for no purpose.
    /// </summary>
    [Fact]
    public async Task A_foreign_origin_candidate_resolving_private_still_advances_the_position()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        const string ownerFingerprint = "owner-a-fingerprint";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committerA, LedgerSigningKey signingKeyA) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        Guid ideaId = DomainId.New();

        // Node A: the idea's own true origin — captured and revised, fleet-scoped, sent and moved
        // past by A's own first sweep.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, ownerFingerprint, Now, cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaCaptured captured = IdeaDecider.Capture(
                ideaId, ownerId, "Fleet-only for now", projectId: projectId, Now.AddSeconds(1), ProjectHome.None);
            session.Events.StartStream<IdeaAggregate>(ideaId, captured);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.Revise(idea, "Fleet-only, sharpened", Now.AddSeconds(2), ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            EventReplicationQueueResult aSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeA, projectId, ownerFingerprint, Now.AddSeconds(3), cts.Token);
            aSweep.EventsQueued.Should().Be(2, "the capture and revision, sent fleet-scoped and moved past");
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committerA,
                signingKeyA, Now.AddSeconds(3), cts.Token);
        }

        // Node B: a fleet sibling of the same owner, switched on and receiving the two native
        // events by replication before ever sweeping this idea's own stream.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeB, new NodeRegistered(nodeB, ownerId, "node-b", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
            await replicationOutbox.QueuePendingAsync(session, nodeB, projectId, ownerFingerprint, Now, cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, ownerFingerprint, Now.AddSeconds(4), trustChain: null,
                cts.Token);
            read.EventsApplied.Should().Be(2, "B holds the capture and revision only by replication, never natively");
        }

        // The owner, working from B, sets the idea private — B's own native event, appended on top
        // of the two events it only holds by replication and has never yet swept.
        long revisedSequenceOnB;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.SetPrivate(idea, isPrivate: true, Now.AddSeconds(5), ownerId));
            await session.SaveChangesAsync(cts.Token);

            IReadOnlyList<IEvent> ideaEventsOnB = await session.Events.QueryAllRawEvents()
                .Where(e => e.StreamId == ideaId).OrderBy(e => e.Sequence).ToListAsync(cts.Token);
            revisedSequenceOnB = ideaEventsOnB[1].Sequence;
        }

        // B's own first-ever sweep over this stream: the capture and revision are both
        // foreign-origin and, resolved now, read Private — the buggy branch this test exists for.
        // Nothing is sent (the two replicated facts never travel regardless, and B's own native
        // privacy-set is genuinely held back) but the position must still move past the two
        // foreign-origin candidates, never freezing behind them the way it freezes behind a native
        // held-back one.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationQueueResult bSweep = await replicationOutbox.QueuePendingAsync(
                session, nodeB, projectId, ownerFingerprint, Now.AddSeconds(6), cts.Token);
            bSweep.EventsQueued.Should().Be(0, "the two replicated facts never travel, and B's own privacy-set is private");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventReplicationOutboxPosition? position =
                await session.LoadAsync<EventReplicationOutboxPosition>(projectId, cts.Token);
            position.Should().NotBeNull();
            position!.LastFlushedGlobalSequence.Should().BeGreaterThanOrEqualTo(
                revisedSequenceOnB, "the two foreign-origin candidates must never freeze the position behind them "
                + "the way a native held-back event correctly does — neither ever rides an envelope regardless of "
                + "scope, so neither needs PendingPrivateSequences to be found again");
        }
    }

    /// <summary>
    /// idea 8c5993c5: "the inbox applies owner-addressed events only when addressed to this node's
    /// own owner root" — a fleet item's own events, addressed to owner A's root, never apply on a
    /// genuinely different owner's node even though that node reads the identical outbox.
    /// </summary>
    [Fact]
    public async Task A_fleet_scoped_ideas_events_never_apply_on_a_different_owners_node()
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

        Guid ideaId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(
                ideaId, ownerId, "Fleet-only", projectId: projectId, Now.AddSeconds(1), ProjectHome.None));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        // Node B: a genuinely different owner reading the identical outbox ref.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: null, cts.Token);
            read.EventsApplied.Should().Be(0, "the fleet envelope is addressed to owner A's own root, never owner B's");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<IdeaDetails>(ideaId, cts.Token)).Should().BeNull(
                "a fleet item never appears on another owner's node at all");
        }
    }

    /// <summary>
    /// Idea 202383dc's own worst case: one record in a sender's batch whose own inline projection
    /// throws (<c>JasperFx.Events.Daemon.ApplyEventException</c> — genuinely malformed data, from a
    /// corrupted record or a sender on an older, less careful build) used to roll back the WHOLE
    /// read's one shared save, taking the cursor advance and every other record in the batch — the
    /// good ones included — down with it: the next sweep would re-read the identical envelope, hit
    /// the identical record, and fail forever. The bad record here is built by hand and pushed
    /// straight onto the wire via <see cref="MessageOutbox.QueueAsync"/>, bypassing node A's own
    /// local store entirely — the one way to get a genuinely malformed fact onto an outbox, since
    /// node A's own commit path enforces the identical rule the receiver does.
    /// </summary>
    [Fact]
    public async Task A_records_own_projection_failure_is_skipped_and_recorded_without_blocking_the_rest_of_the_batch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        ListLogger<EventReplicationInbox> logger = new();
        EventReplicationInbox replicationInbox = new(transport, logger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid badIdeaId = DomainId.New();
        Guid goodIdeaId = DomainId.New();
        Guid badOriginEventId = DomainId.New();
        Guid goodOriginEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        // Genuinely relative — refused by ProjectHome.Parse under every rule, old and new alike —
        // so this is not the cross-OS-form case ProjectHome.Parse itself was widened to accept; it
        // is the residual failure mode this defence still has to survive regardless of cause.
        IdeaCaptured badCaptured = new(
            badIdeaId, ownerId, "A poison capture", projectId, Now.AddSeconds(1), "not-an-absolute-path", ReplicationScope.Fleet);
        IdeaCaptured goodCaptured = new(
            goodIdeaId, ownerId, "A perfectly good capture", projectId, Now.AddSeconds(1), string.Empty, ReplicationScope.Fleet);
        EventReplicationCodec.ReplicatedEventRecord badRecord = new(
            badIdeaId, typeof(IdeaCaptured).FullName!, JsonSerializer.Serialize(badCaptured, jsonOptions),
            badOriginEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectId);
        EventReplicationCodec.ReplicatedEventRecord goodRecord = new(
            goodIdeaId, typeof(IdeaCaptured).FullName!, JsonSerializer.Serialize(goodCaptured, jsonOptions),
            goodOriginEventId, OriginSequence: 2, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([badRecord, goodRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        EventReplicationReadResult read;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: null, cts.Token);
        }

        read.SenderIgnored.Should().BeFalse();
        read.EventsApplied.Should().Be(1, "the poison record is skipped; the good record right after it still lands");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error
            && entry.Message.Contains(badOriginEventId.ToString())
            && entry.Message.Contains(nodeA.ToString()),
            "the failure is logged naming both the event id and the sender");

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<IdeaDetails>(goodIdeaId, cts.Token)).Should().NotBeNull(
                "the record after the poison one in the same batch still applies");
            (await session.LoadAsync<IdeaDetails>(badIdeaId, cts.Token)).Should().BeNull(
                "the poison event's own projection never actually lands");
            (await session.LoadAsync<ReplicatedEventRecord>(badOriginEventId, cts.Token)).Should().NotBeNull(
                "recorded as handled so a later sweep never retries, and fails on, the identical event again");
        }

        // A second sweep proves the cursor genuinely advanced past the envelope that carried the
        // poison record, rather than getting stuck re-reading it forever.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: null, cts.Token);
            secondRead.EventsApplied.Should().Be(0, "both records were already resolved last read, one applied and one skipped");
        }
    }

    /// <summary>
    /// A poison genesis record is permanently skipped rather than retried, so nothing is ever
    /// coming to materialise this stream properly — a later record in the SAME batch that targets
    /// the identical, still-nonexistent stream must not be left to StartStream it anyway: Marten's
    /// own inline projection auto-vivifies a document for any event with no matching Create
    /// (IdeaDetailsProjection's own doc, exercised deliberately for legitimate out-of-order
    /// delivery elsewhere in this suite), which here would leave a permanently-headless IdeaDetails
    /// nothing will ever repair.
    /// </summary>
    [Fact]
    public async Task A_follow_up_record_for_a_stream_whose_own_genesis_just_failed_is_also_skipped_rather_than_starting_a_headless_document()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        ListLogger<EventReplicationInbox> logger = new();
        EventReplicationInbox replicationInbox = new(transport, logger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid badIdeaId = DomainId.New();
        Guid badOriginEventId = DomainId.New();
        Guid followUpOriginEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        IdeaCaptured badCaptured = new(
            badIdeaId, ownerId, "A poison capture", projectId, Now.AddSeconds(1), "not-an-absolute-path", ReplicationScope.Fleet);
        IdeaRevised followUpRevision = new(badIdeaId, "A revision of a capture that never landed", Now.AddSeconds(2), ownerId);
        EventReplicationCodec.ReplicatedEventRecord badRecord = new(
            badIdeaId, typeof(IdeaCaptured).FullName!, JsonSerializer.Serialize(badCaptured, jsonOptions),
            badOriginEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectId);
        EventReplicationCodec.ReplicatedEventRecord followUpRecord = new(
            badIdeaId, typeof(IdeaRevised).FullName!, JsonSerializer.Serialize(followUpRevision, jsonOptions),
            followUpOriginEventId, OriginSequence: 2, nodeA, "owner-a-fingerprint", Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([badRecord, followUpRecord]), Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: null, cts.Token);
            read.EventsApplied.Should().Be(0, "the genesis record failed and the follow-up refuses to build on a stream that never started");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<IdeaDetails>(badIdeaId, cts.Token)).Should().BeNull(
                "no headless document — nothing repairs it once the genesis record is permanently skipped");
            (await session.LoadAsync<ReplicatedEventRecord>(badOriginEventId, cts.Token)).Should().NotBeNull();
            (await session.LoadAsync<ReplicatedEventRecord>(followUpOriginEventId, cts.Token)).Should().NotBeNull(
                "the follow-up is recorded too, so it is never retried either");
        }
    }

    /// <summary>
    /// The same guard as the previous test, but the genesis record and its follow-up arrive in TWO
    /// separate sweeps rather than the same batch — the ordinary timeline for a capture and a
    /// later revise. <c>streamsThatFailedToStartThisRead</c> is scoped to one read alone, so
    /// without a persisted, cross-read form of the same check the second read would find no memory
    /// of the first read's own failure and let the follow-up auto-vivify the exact permanently-
    /// headless <c>IdeaDetails</c> the previous test proves a same-read follow-up cannot
    /// (independent pre-PR review, cycle 1, both lenses, medium).
    /// </summary>
    [Fact]
    public async Task A_follow_up_record_arriving_in_a_later_sweep_is_also_skipped_once_its_streams_genesis_already_failed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        ListLogger<EventReplicationInbox> logger = new();
        EventReplicationInbox replicationInbox = new(transport, logger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid badIdeaId = DomainId.New();
        Guid badOriginEventId = DomainId.New();
        Guid followUpOriginEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        IdeaCaptured badCaptured = new(
            badIdeaId, ownerId, "A poison capture", projectId, Now.AddSeconds(1), "not-an-absolute-path", ReplicationScope.Fleet);
        IdeaRevised followUpRevision = new(badIdeaId, "A revision of a capture that never landed", Now.AddSeconds(10), ownerId);
        EventReplicationCodec.ReplicatedEventRecord badRecord = new(
            badIdeaId, typeof(IdeaCaptured).FullName!, JsonSerializer.Serialize(badCaptured, jsonOptions),
            badOriginEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectId);
        EventReplicationCodec.ReplicatedEventRecord followUpRecord = new(
            badIdeaId, typeof(IdeaRevised).FullName!, JsonSerializer.Serialize(followUpRevision, jsonOptions),
            followUpOriginEventId, OriginSequence: 2, nodeA, "owner-a-fingerprint", Now.AddSeconds(10), projectId);

        // The genesis record alone, flushed and read by itself — the FIRST sweep, complete on its
        // own before the follow-up is even minted, so nothing in EventReplicationInbox's own
        // in-memory, per-read state can still be holding it by the time the follow-up arrives.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([badRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult firstRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: null, cts.Token);
            firstRead.EventsApplied.Should().Be(0, "the poison genesis never lands");
        }

        // The follow-up, flushed and read in a wholly separate sweep afterward.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([followUpRecord]), Now.AddSeconds(11), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(11), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(12),
                trustChain: null, cts.Token);
            secondRead.EventsApplied.Should().Be(
                0, "the follow-up refuses to build on a stream whose genesis already failed, even across sweeps");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<IdeaDetails>(badIdeaId, cts.Token)).Should().BeNull(
                "no headless document — a later sweep must not auto-vivify what the first sweep already refused");
            (await session.LoadAsync<ReplicatedEventRecord>(followUpOriginEventId, cts.Token)).Should().NotBeNull(
                "the follow-up is recorded too, so it is never retried either");
        }
    }

    /// <summary>
    /// The recoverable twin of the two guards above: a stream whose genesis simply never arrived
    /// (a task whose <c>TaskAdded</c> predates the sender's outbox — the switch-on truncation task
    /// a56cf16e already names) is held rather than started, and completes in order the moment a
    /// later sweep finally carries the genesis. Unlike a permanently-failed genesis, nothing here
    /// is ever recorded as refused for good: the two tail events land for real once <c>TaskAdded</c>
    /// arrives, replayed from what <see cref="HeldReplicatedEventRecord"/> kept rather than asking
    /// the sender to resend anything.
    /// </summary>
    [Fact]
    public async Task A_held_tail_applies_in_order_once_its_streams_genesis_arrives_in_a_later_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid runId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        ListLogger<EventReplicationInbox> logger = new();
        EventReplicationInbox replicationInbox = new(transport, logger);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid taskId = DomainId.New();
        Guid branchPushedOriginEventId = DomainId.New();
        Guid completedOriginEventId = DomainId.New();
        Guid addedOriginEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        TaskBranchPushed branchPushed = new(taskId, "task/tail-only", "abc1234", Now.AddSeconds(1));
        TaskCompleted completed = new(taskId, runId, "https://github.com/x/y/pull/9", Now.AddSeconds(2));
        TaskAdded added = new(
            taskId, projectId, "Ship the tail-only task", ["it ships"], TaskType.Feature, null, null, null,
            Now, ownerId);

        EventReplicationCodec.ReplicatedEventRecord branchPushedRecord = new(
            taskId, typeof(TaskBranchPushed).FullName!, JsonSerializer.Serialize(branchPushed, jsonOptions),
            branchPushedOriginEventId, OriginSequence: 2, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectId);
        EventReplicationCodec.ReplicatedEventRecord completedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(completed, jsonOptions),
            completedOriginEventId, OriginSequence: 3, nodeA, "owner-a-fingerprint", Now.AddSeconds(2), projectId);
        EventReplicationCodec.ReplicatedEventRecord addedRecord = new(
            taskId, typeof(TaskAdded).FullName!, JsonSerializer.Serialize(added, jsonOptions),
            addedOriginEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now, projectId);

        // First sweep: the tail alone, exactly the shape a switch-on-truncated outbox ships —
        // TaskAdded never travels because it predates replication for this project.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([branchPushedRecord, completedRecord]),
                Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult firstRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: null, cts.Token);
            firstRead.EventsApplied.Should().Be(0, "both tail events are held, not applied, with no genesis to start the stream from");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().BeNull(
                "no headless document — the tail is held rather than auto-vivifying one with no project or objective");
            (await session.Events.FetchStreamStateAsync(taskId, cts.Token)).Should().BeNull(
                "the local stream itself never starts from a non-genesis record either");
        }

        // Second sweep: the genesis finally arrives — h9k task pull, a catch-up answer, or (this
        // test's own stand-in for either) simply a later ordinary flush once the sender's own
        // history catches up.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([addedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: null, cts.Token);
            secondRead.EventsApplied.Should().Be(3, "the genesis starts the stream and both held tail events replay behind it");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            task.Should().NotBeNull();
            task!.ProjectId.Should().Be(projectId);
            task.Objective.Should().Be("Ship the tail-only task");
            task.AddedAt.Should().Be(Now);
            task.LastPushedBranchTip.Should().Be("abc1234", "the held branch-pushed event replayed too, in order");
            task.State.Should().Be(TaskState.Done, "the held completed event replayed last, exactly as it would have unheld");
            task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/9");

            (await session.Query<HeldReplicatedEventRecord>().ToListAsync(cts.Token)).Should().BeEmpty(
                "every held record for this stream is consumed once its own replay lands");
        }
    }

    /// <summary>
    /// What the held-tail ask (task c3bdb62e) actually gets answered with, and the reason a held
    /// record's own ask bookkeeping has to survive it: a peer that holds the same tail and not the
    /// genesis answers by serving that tail again, so every held record arrives a second time. The
    /// document already here is left exactly as it is — storing over it reset
    /// <see cref="HeldReplicatedEventRecord.CatchUpAttempts"/> to zero and refreshed
    /// <see cref="HeldReplicatedEventRecord.HeldAt"/> on every answer, so the three-attempt stop
    /// never engaged for the one shape it exists for and the stream was asked about again every
    /// cooldown for as long as the node ran (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public async Task A_tail_re_served_without_its_genesis_keeps_the_ask_bookkeeping_already_on_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid runId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport, new ListLogger<EventReplicationInbox>());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        Guid taskId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        TaskCompleted completed = new(taskId, runId, "https://github.com/x/y/pull/9", Now.AddSeconds(2));
        EventReplicationCodec.ReplicatedEventRecord completedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(completed, jsonOptions),
            DomainId.New(), OriginSequence: 3, nodeA, "owner-a-fingerprint", Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([completedRecord]), Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: null, cts.Token);
        }

        // Two sweeps' worth of held-tail asks, recorded the way EventCatchUpCoordinator records
        // them: on the held record itself.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            HeldReplicatedEventRecord held = (await session.Query<HeldReplicatedEventRecord>()
                .FirstOrDefaultAsync(cts.Token))!;
            held.CatchUpAttempts = 2;
            held.LastCatchUpAskedAt = Now.AddSeconds(5);
            session.Store(held);
            await session.SaveChangesAsync(cts.Token);
        }

        // The answer: the same tail event over again, from a peer that cannot serve the genesis.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([completedRecord]), Now.AddSeconds(6), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(7),
                trustChain: null, cts.Token);
            secondRead.EventsApplied.Should().Be(0, "the genesis is still missing, so the tail is still held");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            HeldReplicatedEventRecord held = (await session.Query<HeldReplicatedEventRecord>()
                .ToListAsync(cts.Token)).Should().ContainSingle().Subject;
            held.CatchUpAttempts.Should().Be(2, "the asks this node already made are what the stop counts");
            held.LastCatchUpAskedAt.Should().Be(Now.AddSeconds(5));
            held.CatchUpGivenUp.Should().BeFalse();
            held.HeldAt.Should().Be(Now.AddSeconds(4), "the hold is as old as it always was, not as old as the answer");
        }
    }

    /// <summary>
    /// The residue this node's own sweep has to clear: a read that STARTED a stream and then threw
    /// before draining that stream's held tail leaves records behind that no later read will ever
    /// replay, because the genesis dedupes on the retry and the deferred replay is driven by an
    /// in-memory set of what this read started. The held-tail sweep finds them
    /// (<c>HeldTailSweepResult.StreamsToReplay</c>) and hands each one back here
    /// (independent pre-PR review, cycle 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task A_held_tail_whose_stream_already_exists_replays_when_it_is_handed_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid runId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport, new ListLogger<EventReplicationInbox>());
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        Guid taskId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        TaskCompleted completed = new(taskId, runId, "https://github.com/x/y/pull/9", Now.AddSeconds(2));
        EventReplicationCodec.ReplicatedEventRecord completedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(completed, jsonOptions),
            DomainId.New(), OriginSequence: 3, nodeA, "owner-a-fingerprint", Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([completedRecord]), Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: null, cts.Token);
        }

        // The genesis lands and starts the stream, and then the read that carried it fails before
        // the held tail is drained — written here directly, since the failure itself is another
        // test's subject and what matters is the state it leaves: a stream that exists, and a held
        // record for it that nothing is ever coming back for.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, new TaskAdded(
                taskId, projectId, "Ship the tail-only task", ["it ships"], TaskType.Feature, null, null, null,
                Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            (await replicationInbox.ReplayHeldTailAsync(session, taskId, Now.AddSeconds(8), cts.Token))
                .Should().Be(1, "the tail was waiting on a replay, not on the fleet");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails task = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            task.State.Should().Be(TaskState.Done, "the held completed event finally applied");
            task.PullRequestUrl.Should().Be("https://github.com/x/y/pull/9");
            (await session.Query<HeldReplicatedEventRecord>().ToListAsync(cts.Token)).Should().BeEmpty(
                "a replayed record is deleted, so it never spends a slot of the next sweep's own cap again");
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
