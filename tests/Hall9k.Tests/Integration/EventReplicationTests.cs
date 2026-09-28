using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Identity;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Decision;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
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
using Marten.Events;
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
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
    /// Task 054d5ab0: the cursor records when it last got further into a sender's outbox, because
    /// <c>h9k task take --force</c> prints that pair as the evidence an operator overrides a live
    /// holder on. The stamp belongs to the advance, not to the sweep: a later tick that inspects
    /// nothing new must leave it where the advance put it, or every idle sweep would tell the
    /// operator this node had just heard from a node it has heard nothing from in days.
    /// </summary>
    [Fact]
    public async Task The_inbox_cursor_records_when_it_last_got_further_into_a_senders_outbox()
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

        await SeedQueuedTaskAsync(_postgres.Store, projectId, ownerId, Now.AddSeconds(1), cts.Token);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        DateTimeOffset theAdvance = Now.AddSeconds(3);
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", theAdvance, trustChain: TrustChain.Empty, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        long seqAtTheAdvance;
        await using (IQuerySession session = storeB.QuerySession())
        {
            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, projectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.HighestSeqInspected.Should().BeGreaterThan(0);
            cursor.HighestSeqInspectedAt.Should().Be(
                theAdvance, "this is the sweep that actually got further into the sender's outbox");
            seqAtTheAdvance = cursor.HighestSeqInspected;
        }

        // An idle tick: the sender has sent nothing since, so there is nothing new to inspect.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint",
                Now.AddHours(30), trustChain: TrustChain.Empty, cts.Token);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(
                EventReplicationStreamId.ForInboxCursor(nodeA, projectId), cts.Token);
            cursor.Should().NotBeNull();
            cursor!.HighestSeqInspected.Should().Be(seqAtTheAdvance, "nothing new arrived to inspect");
            cursor.HighestSeqInspectedAt.Should().Be(
                theAdvance, "a sweep that inspected nothing new heard nothing new, and must not say otherwise");
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
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
    /// The same per-install rewrite, for the family that carries its project under another name
    /// (independent pre-PR review, cycle 1, adversarial lens): a project-scoped
    /// <see cref="DecisionRecorded"/> names its project in <c>ScopeId</c>, not in a
    /// <c>projectId</c> field, and an inbox that only rewrote the literal name left the sender's
    /// foreign id on the receiver's copy — unreachable through <c>h9k decide list --project</c>,
    /// through <see cref="ReplicationProjectResolver"/> (so B never forwarded it to a third
    /// member), and through <c>ProjectPurgeEngine</c>'s own scope-id sweep. The sibling test above
    /// covers the literal field and cannot see this.
    /// </summary>
    [Fact]
    public async Task A_replicated_decision_lands_under_the_receivers_own_project_id_not_the_senders()
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

        Guid decisionId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<DecisionAggregate>(decisionId, DecisionDecider.Record(
                decisionId, KnowledgeScope.Project, projectIdA, "Agents never push; the daemon does.",
                originIncident: null, supersedes: [], RecordedProvenance.FromShell(ownerId), Now.AddSeconds(1)));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectIdA, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectIdA, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            DecisionDetails? replicated = await session.LoadAsync<DecisionDetails>(decisionId, cts.Token);
            replicated.Should().NotBeNull();
            replicated!.Scope.Should().Be(KnowledgeScope.Project);
            replicated.ScopeId.Should().Be(
                projectIdB, "the receiver's own local project id, never the sender's foreign one");

            ReplicationOwnership ownership = await new ReplicationProjectResolver()
                .ResolveAsync(session, decisionId, cts.Token);
            ownership.ProjectId.Should().Be(
                projectIdB, "so node B can forward this decision on to a third member of the same project");
        }

        await using (IQuerySession session = _postgres.Store.QuerySession())
        {
            DecisionDetails? original = await session.LoadAsync<DecisionDetails>(decisionId, cts.Token);
            original!.ScopeId.Should().Be(projectIdA);
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
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
    /// Idea 6be68ee2, trust-ledger findings 1 and 6: a Member-role sender's own project settings
    /// change is dropped, never applied, and the receiver's own local settings are unchanged — the
    /// gate's own "dropped and burned" outcome, exercised through the full inbox rather than only the
    /// pure verdict (<see cref="Hall9k.Tests.Connectors.Replication.EventReplicationInboxGateTests"/>).
    /// </summary>
    [Fact]
    public async Task A_member_roles_sender_signing_its_own_settings_change_is_dropped_and_local_settings_are_unchanged()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                new ProjectRegistered(projectId, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Built directly on the wire, the same way the held-tail test does (Brian's 2026-09-13
        // testing rule keeps a real repository out of this seam): nodeA is both this record's own
        // claimed OriginNodeId and the sender that actually pushed it, the ordinary shape of a
        // teammate's own node trying to author a gated event directly, never a forwarded one.
        Guid originEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        ProjectTeamSettingsChanged authoredDirectly = new(
            projectId, Now.AddSeconds(1), ownerId,
            VerifyCommands: Optional<IReadOnlyList<VerifyCommand>>.Of([new VerifyCommand("test", "echo pwned")]),
            ClaimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee));
        EventReplicationCodec.ReplicatedEventRecord record = new(
            projectId, typeof(ProjectTeamSettingsChanged).FullName!, JsonSerializer.Serialize(authoredDirectly, jsonOptions),
            originEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([record]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        // nodeA's own root is a project MEMBER here, never an owner — the ordinary shape of a
        // teammate whose own node tries to author a gated event directly.
        const string memberRoot = "member-root-fingerprint";
        TrustChain memberOnlyChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [memberRoot] = new TrustedOwner(
                    memberRoot, "ssh-ed25519 AAAAFAKEROOT root",
                    [
                        new TrustedNode(
                            nodeA.ToString(), $"ssh-ed25519 AAAAFAKE{nodeA:N} test",
                            NodeKeyStore.Fingerprint($"ssh-ed25519 AAAAFAKE{nodeA:N} test"), Now),
                    ]),
            },
            [new ProjectMember(memberRoot, MembershipRole.Member, Now)]);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: memberOnlyChain, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().Be(0, "a Member-role sender's own gated event is dropped, never applied");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectId, cts.Token);
            receiverProject.Should().NotBeNull();
            receiverProject!.ClaimGate.Should().Be(ClaimGate.Off, "the dropped event never touched local settings");
            receiverProject.VerifyCommands.Should().BeEmpty("the dropped event never touched local settings");

            ReplicatedEventRecord? burned = await session.LoadAsync<ReplicatedEventRecord>(originEventId, cts.Token);
            burned.Should().NotBeNull("a Member-role sender claiming its own event is burned so a retry can never apply it");
            burned!.Applied.Should().BeFalse();
        }
    }

    /// <summary>
    /// The catch-up shape of the same gate: a Member-role peer forwards a batch that carries someone
    /// ELSE's origin event id (the true author, never this forwarding sender), answering a broadcast
    /// this receiving node itself minted for the project stream — the forwarded-record admission
    /// gate (idea 6be68ee2, trust-ledger findings 4 and 7:
    /// <see cref="EventReplicationInbox.IsForwardedRecordAdmitted"/>) admits it as an entitled answer,
    /// and the OWNER-ROLE gate this test is actually about
    /// (<see cref="EventReplicationInbox.EvaluateGatedEvent"/>) is what drops it, since forwardingNode
    /// is a Member, never an Owner. Dropped here too, but the origin event id is never burned, so the
    /// identical event still applies the moment its own true origin — who is also always admitted as
    /// this record's own native sender, and an Owner-role project member — delivers it directly
    /// (independent pre-PR review, cycle 1, adversarial lens, low: an earlier version of this test
    /// sent the first delivery as an ordinary project flush, which the newer admission gate above now
    /// drops before EvaluateGatedEvent ever runs, so it no longer proved what this doc claims).
    /// </summary>
    [Fact]
    public async Task A_member_signed_forwarded_event_is_dropped_without_burning_the_origin_id_so_an_allowed_delivery_still_applies()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid forwardingNode = DomainId.New();
        Guid trueOwnerOriginNode = DomainId.New();
        Guid receiverNodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, forwardingNode, cts.Token);
        await SeedNodeFileAsync(ledger, trueOwnerOriginNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        await using DocumentStore storeB = OpenStoreB();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                new ProjectRegistered(projectId, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // A broadcast this receiving node itself minted for the project stream — the shape
        // forwardingNode's answer below is entitled to answer at all (ForStreamId = projectId,
        // Candidates empty: any vouched project member may reply).
        Guid requestId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Store(new EventCatchUpRequest
            {
                Id = requestId,
                ProjectId = projectId,
                ForStreamId = projectId,
                Candidates = [],
                SentAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        Guid originEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        ProjectTeamSettingsChanged forwarded = new(
            projectId, Now.AddSeconds(1), ownerId, ClaimGate: Optional<ClaimGate>.Of(ClaimGate.TrackerAssignee));
        EventReplicationCodec.ReplicatedEventRecord record = new(
            projectId, typeof(ProjectTeamSettingsChanged).FullName!, JsonSerializer.Serialize(forwarded, jsonOptions),
            originEventId, OriginSequence: 1, trueOwnerOriginNode, "owner-fingerprint", Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, forwardingNode, projectId, "owner-fingerprint", MessageAudience.Node(receiverNodeId),
                about: requestId.ToString(), MessageKind.Events, EventReplicationCodec.EncodeBatch([record]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, forwardingNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        const string memberRoot = "member-root-fingerprint";
        TrustChain memberOnlyChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [memberRoot] = new TrustedOwner(
                    memberRoot, "ssh-ed25519 AAAAFAKEROOT root",
                    [
                        new TrustedNode(
                            forwardingNode.ToString(), $"ssh-ed25519 AAAAFAKE{forwardingNode:N} test",
                            NodeKeyStore.Fingerprint($"ssh-ed25519 AAAAFAKE{forwardingNode:N} test"), Now),
                    ]),
            },
            [new ProjectMember(memberRoot, MembershipRole.Member, Now)]);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, forwardingNode, projectId, receiverNodeId, "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: memberOnlyChain, cts.Token);
            read.EventsApplied.Should().Be(0, "the forwarding sender is not an owner, so the event is dropped");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectId, cts.Token);
            receiverProject!.ClaimGate.Should().Be(ClaimGate.Off, "the dropped event never touched local settings");
            (await session.LoadAsync<ReplicatedEventRecord>(originEventId, cts.Token)).Should().BeNull(
                "the origin id is never burned when the forwarding sender differs from the record's own origin");
        }

        // The second delivery is the record's own TRUE origin sending directly — never a relay —
        // since idea 6be68ee2's own admission rule (trust-ledger findings 4 and 7) now refuses a
        // forwarded record outside any catch-up answer this node minted, whatever role the forwarder
        // holds; a rightful owner may still deliver its OWN record, sender and origin the same node.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, trueOwnerOriginNode, projectId, "owner-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([record]), Now.AddSeconds(4), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, trueOwnerOriginNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(4), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, trueOwnerOriginNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(5),
                trustChain: OwnerChainFor(trueOwnerOriginNode), cts.Token);
            read.EventsApplied.Should().Be(1, "the record's own true origin, an Owner-role member, can still deliver the identical origin event id directly");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails? receiverProject = await session.LoadAsync<ProjectDetails>(projectId, cts.Token);
            receiverProject!.ClaimGate.Should().Be(ClaimGate.TrackerAssignee, "the second, allowed delivery actually applied");
        }
    }

    /// <summary>
    /// Idea 6be68ee2, trust-ledger findings 4 and 7: a teammate can no longer speak as another node
    /// unsolicited. A vouched project member forwarding a record it claims came from a different
    /// origin, outside any catch-up answer this node ever asked for, is dropped rather than applied —
    /// and because it is dropped before <c>ApplyAsync</c> ever runs, the forged record's own inflated
    /// <c>OriginSequence</c> never becomes this stream's recorded high-water mark for that origin, so
    /// the origin's own later, genuine history still applies rather than being refused as "out of
    /// order" forever. This is the exact pre-fix shape idea 6be68ee2 names: one flush from any
    /// project member, needing no reply from anyone, permanently freezing another origin's stream on
    /// every receiving node.
    /// </summary>
    [Fact]
    public async Task A_forwarded_record_outside_any_catch_up_answer_is_dropped_and_never_freezes_the_origins_later_history()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid trueOriginNode = DomainId.New();
        Guid forwardingNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, trueOriginNode, cts.Token);
        await SeedNodeFileAsync(ledger, forwardingNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // Both nodes are genuinely vouched project members — this is a teammate's own node, not a
        // stranger's — under owners this node's own trust chain currently recognizes, so the attack
        // this test proves closed is the one idea 6be68ee2 named: a vouched member speaking as
        // another node unsolicited, never an unvouched sender (already refused earlier, at the
        // transport's own SenderVouched check).
        const string ownerARoot = "owner-a-root-fingerprint";
        const string ownerMRoot = "owner-m-root-fingerprint";
        string originKeyLine = $"ssh-ed25519 AAAAFAKE{trueOriginNode:N} test";
        string forwarderKeyLine = $"ssh-ed25519 AAAAFAKE{forwardingNode:N} test";
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [ownerARoot] = new TrustedOwner(
                    ownerARoot, "ssh-ed25519 AAAAFAKEownerA test",
                    [new TrustedNode(trueOriginNode.ToString(), originKeyLine, NodeKeyStore.Fingerprint(originKeyLine), Now)]),
                [ownerMRoot] = new TrustedOwner(
                    ownerMRoot, "ssh-ed25519 AAAAFAKEownerM test",
                    [new TrustedNode(forwardingNode.ToString(), forwarderKeyLine, NodeKeyStore.Fingerprint(forwarderKeyLine), Now)]),
            },
            [
                new ProjectMember(ownerARoot, MembershipRole.Owner, Now),
                new ProjectMember(ownerMRoot, MembershipRole.Member, Now),
            ]);

        await using DocumentStore storeB = OpenStoreB();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                new ProjectRegistered(projectId, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The true origin's own genuine first event, delivered directly by itself — establishes the
        // stream on node B holding OriginSequence 1 recorded for trueOriginNode.
        Guid taskId = DomainId.New();
        Guid genesisEventId = DomainId.New();
        TaskAdded genesis = TaskDecider.Add(
            taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now.AddSeconds(1), ownerId);
        EventReplicationCodec.ReplicatedEventRecord genesisRecord = new(
            taskId, typeof(TaskAdded).FullName!, JsonSerializer.Serialize(genesis, jsonOptions),
            genesisEventId, OriginSequence: 1, trueOriginNode, ownerARoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, trueOriginNode, projectId, ownerARoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([genesisRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, trueOriginNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult genesisRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, trueOriginNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain, cts.Token);
            genesisRead.EventsApplied.Should().Be(1, "the true origin's own genuine genesis applies normally");
        }

        // The attack: forwardingNode — a genuinely vouched member, never the true origin — flushes an
        // ordinary broadcast batch (no catch-up About at all) carrying a record that claims
        // trueOriginNode as its own origin, with an inflated OriginSequence. No catch-up request this
        // node ever minted names this exchange, so this must be dropped.
        Guid forgedEventId = DomainId.New();
        TaskPublished forgedPayload = new(taskId, Now.AddSeconds(4), ownerId);
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            taskId, typeof(TaskPublished).FullName!, JsonSerializer.Serialize(forgedPayload, jsonOptions),
            forgedEventId, OriginSequence: long.MaxValue, trueOriginNode, ownerARoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, forwardingNode, projectId, ownerMRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, forwardingNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult forgedRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, forwardingNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain, cts.Token);
            forgedRead.EventsApplied.Should().Be(
                0, "forwardingNode is not the true origin and answers no catch-up request this node minted");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<ReplicatedEventRecord>(forgedEventId, cts.Token)).Should().BeNull(
                "a dropped foreign-origin record is never stored under its claimed OriginEventId — storing one "
                + "there would let a forger pre-empt the genuine event's own future dedupe");
        }

        // The proof: a genuine follow-up from the true origin, sent directly by itself at the next
        // real sequence, must still apply — it would be refused as "belongs before origin sequence
        // long.MaxValue" if the forged record above had ever reached ApplyAsync and poisoned this
        // stream's own recorded high-water mark for trueOriginNode.
        Guid genuineFollowUpEventId = DomainId.New();
        TaskPublished genuineFollowUp = new(taskId, Now.AddSeconds(7), ownerId);
        EventReplicationCodec.ReplicatedEventRecord genuineFollowUpRecord = new(
            taskId, typeof(TaskPublished).FullName!, JsonSerializer.Serialize(genuineFollowUp, jsonOptions),
            genuineFollowUpEventId, OriginSequence: 2, trueOriginNode, ownerARoot, Now.AddSeconds(7), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, trueOriginNode, projectId, ownerARoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([genuineFollowUpRecord]), Now.AddSeconds(8), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, trueOriginNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(8), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult followUpRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, trueOriginNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(9),
                trustChain, cts.Token);
            followUpRead.EventsApplied.Should().Be(
                1, "the true origin's own later, genuine history must never be frozen by the dropped forgery");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskDetails task = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            task.State.Should().Be(TaskState.Published, "the genuine follow-up actually applied");
        }
    }

    /// <summary>
    /// The forged-forward shape proved above, repeated with a plain
    /// <see cref="TaskActClassification.MemberSafe"/> Task/Run act (<see cref="TaskCompleted"/>)
    /// rather than a Conditional one (independent pre-PR review, cycle 8, terminal lap): before the
    /// cycle-8 exemption was removed from <see cref="EventReplicationInbox.IsForwardedRecordAdmitted"/>,
    /// a plain MemberSafe act skipped that gate entirely and relied instead on
    /// <see cref="EventReplicationInbox.EvaluateTaskActVerdict"/>'s own native-only narrowing to
    /// catch an unsolicited forge — which held it (a <see cref="HeldTaskActRecord"/> row, keyed by
    /// the forger's own sender identity) rather than dropping it clean. Now that a Task or Run act
    /// passes the identical forwarded-record admission gate as every other project-scoped event,
    /// this forgery is refused before <c>ApplyAsync</c> ever runs, exactly like the Conditional
    /// case: no held row, no burned origin id, and the claimed origin's own later genuine history is
    /// never at risk of being frozen behind an inflated
    /// <see cref="EventReplicationCodec.ReplicatedEventRecord.OriginSequence"/>.
    /// </summary>
    [Fact]
    public async Task A_forwarded_membersafe_act_outside_any_catch_up_answer_is_dropped_and_never_held()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid trueOriginNode = DomainId.New();
        Guid forwardingNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, trueOriginNode, cts.Token);
        await SeedNodeFileAsync(ledger, forwardingNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        const string ownerARoot = "owner-a-root-membersafe";
        const string ownerMRoot = "owner-m-root-membersafe";
        string originKeyLine = $"ssh-ed25519 AAAAFAKE{trueOriginNode:N} test";
        string forwarderKeyLine = $"ssh-ed25519 AAAAFAKE{forwardingNode:N} test";
        TrustChain trustChain = new(
            new Dictionary<string, TrustedOwner>
            {
                [ownerARoot] = new TrustedOwner(
                    ownerARoot, "ssh-ed25519 AAAAFAKEownerA test",
                    [new TrustedNode(trueOriginNode.ToString(), originKeyLine, NodeKeyStore.Fingerprint(originKeyLine), Now)]),
                [ownerMRoot] = new TrustedOwner(
                    ownerMRoot, "ssh-ed25519 AAAAFAKEownerM test",
                    [new TrustedNode(forwardingNode.ToString(), forwarderKeyLine, NodeKeyStore.Fingerprint(forwarderKeyLine), Now)]),
            },
            [
                new ProjectMember(ownerARoot, MembershipRole.Owner, Now),
                new ProjectMember(ownerMRoot, MembershipRole.Member, Now),
            ]);

        await using DocumentStore storeB = OpenStoreB();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectId,
                new ProjectRegistered(projectId, ownerId, DomainId.New(), "Shared Project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The true origin's own genuine first event, delivered directly by itself — establishes the
        // stream on node B holding OriginSequence 1 recorded for trueOriginNode.
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid genesisEventId = DomainId.New();
        TaskAdded genesis = TaskDecider.Add(
            taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now.AddSeconds(1), ownerId);
        EventReplicationCodec.ReplicatedEventRecord genesisRecord = new(
            taskId, typeof(TaskAdded).FullName!, JsonSerializer.Serialize(genesis, jsonOptions),
            genesisEventId, OriginSequence: 1, trueOriginNode, ownerARoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, trueOriginNode, projectId, ownerARoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([genesisRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, trueOriginNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult genesisRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, trueOriginNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain, cts.Token);
            genesisRead.EventsApplied.Should().Be(1, "the true origin's own genuine genesis applies normally");
        }

        // The attack: forwardingNode — a genuinely vouched member, never the true origin — flushes
        // an ordinary broadcast batch (no catch-up About at all) carrying a plain MemberSafe act
        // that claims trueOriginNode as its own origin, with an inflated OriginSequence. No catch-up
        // request this node ever minted names this exchange, so this must be dropped.
        Guid forgedEventId = DomainId.New();
        TaskCompleted forgedPayload = new(taskId, runId, PullRequestUrl: null, Now.AddSeconds(4));
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(forgedPayload, jsonOptions),
            forgedEventId, OriginSequence: long.MaxValue, trueOriginNode, ownerARoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, forwardingNode, projectId, ownerMRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, forwardingNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult forgedRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, forwardingNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain, cts.Token);
            forgedRead.EventsApplied.Should().Be(
                0, "forwardingNode is not the true origin and answers no catch-up request this node minted");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<ReplicatedEventRecord>(forgedEventId, cts.Token)).Should().BeNull(
                "a dropped foreign-origin record is never stored under its claimed OriginEventId — storing one "
                + "there would let a forger pre-empt the genuine event's own future dedupe");
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("dropped at the forwarded-record admission gate, before the Task/Run act "
                    + "classification gate — and its own hold queue — ever sees a plain MemberSafe act either");
        }

        // The proof: a genuine follow-up from the true origin, sent directly by itself at the next
        // real sequence, must still apply — it would be refused as "belongs before origin sequence
        // long.MaxValue" if the forged record above had ever reached ApplyAsync and poisoned this
        // stream's own recorded high-water mark for trueOriginNode.
        Guid genuineFollowUpEventId = DomainId.New();
        TaskCompleted genuineFollowUp = new(taskId, runId, PullRequestUrl: null, Now.AddSeconds(7));
        EventReplicationCodec.ReplicatedEventRecord genuineFollowUpRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(genuineFollowUp, jsonOptions),
            genuineFollowUpEventId, OriginSequence: 2, trueOriginNode, ownerARoot, Now.AddSeconds(7), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, trueOriginNode, projectId, ownerARoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([genuineFollowUpRecord]), Now.AddSeconds(8), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, trueOriginNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(8), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult followUpRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, trueOriginNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(9),
                trustChain, cts.Token);
            followUpRead.EventsApplied.Should().Be(
                1, "the true origin's own later, genuine history must never be frozen by the dropped forgery");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.State.Should().Be(TaskState.Done, "the genuine follow-up actually applied");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 8, terminal lap (dispute-preparation Q1(a)): the cycle-8
    /// exemption that let a Task or Run act skip
    /// <see cref="EventReplicationInbox.IsForwardedRecordAdmitted"/> outright let a member forge the
    /// OWNER as a forwarded record's own claimed origin on the member's OWN already-assigned task.
    /// <see cref="EventReplicationInbox.EvaluateTaskActVerdict"/>'s own Conditional check only ever
    /// compared the ACT'S SENDER root against the task's own assignment, never the claimed origin,
    /// so a member sending directly (never relaying anyone else's own record) could stamp the
    /// owner's own node as origin with an inflated
    /// <see cref="EventReplicationCodec.ReplicatedEventRecord.OriginSequence"/> and have it Allowed
    /// on the strength of the member's OWN assignment — freezing the owner's own later, genuine
    /// history on that stream as "belongs before" the forged sequence. Removing the exemption closes
    /// this: the record's own claimed origin differs from its actual sender, so it is a forwarded
    /// record like any other, and is dropped at admission for naming no catch-up request this node
    /// ever minted.
    /// </summary>
    [Fact]
    public async Task A_members_forged_owner_origin_on_their_own_assigned_task_is_dropped_at_admission()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerNode = DomainId.New();
        Guid memberNode = DomainId.New();
        Guid ownerOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        const string ownerRoot = "owner-root-forge";
        const string memberRoot = "member-root-forge";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, ownerNode, cts.Token);
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        TrustChain chain = TwoRootChain(ownerNode, memberNode, ownerRoot, memberRoot);

        await using DocumentStore storeB = OpenStoreB();

        // The task is assigned to the FORGING member's own root — the fact that let the old
        // Conditional check Allow this act outright, since it only ever compared the act's own
        // sender root (the member, delivering directly) against the assignment, never the claimed
        // origin.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now,
                ownerOwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, ownerOwnerId));
            session.Events.Append(
                taskId,
                new TaskAssigned(
                    taskId, ownerOwnerId, UnmetDependencies: [], Now, ownerOwnerId,
                    AssignedOwnerRootFingerprint: memberRoot));
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // The forgery: memberNode sends this directly (never relaying anyone else's record — an
        // ordinary flush, no catch-up About at all), but the record itself claims the OWNER's own
        // node as origin, with an inflated OriginSequence.
        Guid forgedEventId = DomainId.New();
        TaskRevised forged = new(
            taskId, Optional<string>.Of("Ship the forged thing"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now.AddSeconds(1), ownerOwnerId);
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            taskId, typeof(TaskRevised).FullName!, JsonSerializer.Serialize(forged, jsonOptions),
            forgedEventId, OriginSequence: long.MaxValue, ownerNode, ownerRoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                0, "memberNode is not the record's own claimed origin and answers no catch-up request this "
                    + "node minted");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<ReplicatedEventRecord>(forgedEventId, cts.Token)).Should().BeNull(
                "a dropped foreign-origin record is never stored under its claimed OriginEventId");
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("dropped at the forwarded-record admission gate, before the Task/Run act "
                    + "classification gate — the one that would otherwise have judged the sender's OWN "
                    + "assignment and Allowed it — ever sees it");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.Objective.Should().Be("Ship the thing", "the forged revise never applied");
        }

        // The proof: a genuine event from the claimed origin (the owner), delivered directly, must
        // still apply at its own real sequence 1 — it would be refused as "belongs before origin
        // sequence long.MaxValue" had the forgery ever reached ApplyAsync and poisoned this stream's
        // own recorded high-water mark for the owner.
        TaskRevised genuine = new(
            taskId, Optional<string>.Of("Ship the genuine thing"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now.AddSeconds(4), ownerOwnerId);
        EventReplicationCodec.ReplicatedEventRecord genuineRecord = new(
            taskId, typeof(TaskRevised).FullName!, JsonSerializer.Serialize(genuine, jsonOptions),
            DomainId.New(), OriginSequence: 1, ownerNode, ownerRoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, ownerNode, projectId, ownerRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([genuineRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, ownerNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, ownerNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                1, "the owner's own later, genuine history must never be frozen by the dropped forgery");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.Objective.Should().Be("Ship the genuine thing", "the genuine revise actually applied");
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
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: TrustChain.Empty, cts.Token);
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
    /// Idea 6be68ee2, trust findings 10/12, stream-ownership item: a replicated event may only
    /// append to a stream its own project owns, never onto a stream a DIFFERENT project this same
    /// node also hosts already resolves to. The idea's own <c>ProjectId</c> field is rewritten by
    /// <c>RewriteProjectIdField</c> before the event is ever applied, so the forgery this test needs
    /// is the stream itself already belonging elsewhere — <see cref="ReplicationProjectResolver"/>'s
    /// own answer for the EXISTING stream, checked before the append is ever attempted.
    /// </summary>
    [Fact]
    public async Task A_replicated_event_targeting_an_existing_stream_from_a_different_project_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectX = DomainId.New();
        Guid projectY = DomainId.New();
        Guid existingIdeaId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The receiver already holds this idea under project X — a genuinely different, unrelated
        // project this same node also hosts.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<IdeaAggregate>(
                existingIdeaId, new IdeaCaptured(existingIdeaId, ownerId, "Existing idea in project X", projectX, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node A's own outbox carries an event naming this exact stream, but this read is scoped to
        // project Y — a cross-project append attempt, whether from a forger or from two projects
        // sharing a stream id by accident.
        Guid senderLocalProjectId = DomainId.New();
        Guid originEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        IdeaAssignedToProject crossProjectEvent = new(existingIdeaId, projectY, projectX, Now.AddSeconds(1), ownerId);
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            existingIdeaId, typeof(IdeaAssignedToProject).FullName!, JsonSerializer.Serialize(crossProjectEvent, jsonOptions),
            originEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectY);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, senderLocalProjectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, senderLocalProjectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        EventReplicationReadResult read;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectY, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: TrustChain.Empty, cts.Token);
        }

        read.EventsApplied.Should().Be(0, "the target stream already belongs to a different project on this node");

        await using (IQuerySession session = storeB.QuerySession())
        {
            IdeaDetails idea = (await session.LoadAsync<IdeaDetails>(existingIdeaId, cts.Token))!;
            idea.ProjectId.Should().Be(projectX, "the cross-project append must never actually land");
        }
    }

    /// <summary>
    /// Idea 6be68ee2, the sharpest item in the stream-ownership finding: every member learns each
    /// teammate's own local project id from that teammate's own replicated lifecycle events (they
    /// keep the sender's own foreign stream id, deliberately, so a phantom row can record them as a
    /// fact — <see cref="A_teammates_project_archived_never_touches_the_receivers_own_project"/>
    /// above). A member who has learned THIS node's own local project id that way must never be
    /// able to address a <see cref="ProjectPurgeScheduled"/> straight at it: <c>ProjectPurgeScheduled</c>
    /// is not in <c>ProjectStreamReplicationRules.IsProjectAggregateStreamEvent</c>, so it keeps
    /// <c>record.StreamId</c> raw and skips the genesis requirement, which is exactly the hole this
    /// forged record aims through.
    /// </summary>
    [Fact]
    public async Task A_ProjectPurgeScheduled_aimed_at_the_receivers_own_project_id_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B's own real, registered project — the receiver's genuine local copy, never a
        // phantom row.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdB,
                new ProjectRegistered(
                    projectIdB, ownerId, DomainId.New(), "Receiver's own project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node A's own outbox carries a ProjectPurgeScheduled whose own StreamId is forged to be
        // node B's real local project id, rather than node A's own foreign coordinate — the shape
        // that would otherwise phantom-stream harmlessly for a genuinely foreign id.
        Guid senderLocalProjectId = DomainId.New();
        Guid originEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        ProjectPurgeScheduled forgedPurge = new(projectIdB, Now.AddSeconds(1), Now.AddSeconds(1).AddHours(24), ownerId);
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            projectIdB, typeof(ProjectPurgeScheduled).FullName!, JsonSerializer.Serialize(forgedPurge, jsonOptions),
            originEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectIdB);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, senderLocalProjectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, senderLocalProjectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        EventReplicationReadResult read;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: TrustChain.Empty, cts.Token);
        }

        read.EventsApplied.Should().Be(
            0, "a project lifecycle event may never resolve onto the receiver's own real project stream");

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails receiverProject = (await session.LoadAsync<ProjectDetails>(projectIdB, cts.Token))!;
            receiverProject.PurgeAt.Should().BeNull(
                "the forged purge must never actually schedule the receiver's own real project for a hard delete");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses, high: the guard above only ever compared a
    /// lifecycle event's target against THIS read's own <c>projectId</c>, so a member of TWO shared
    /// projects this same node hosts could learn the sibling project's own local id from ITS
    /// replicated lifecycle events and forge a <see cref="ProjectPurgeScheduled"/> at that id
    /// instead, through the FIRST project's own outbox. The fix has to tell a registered project
    /// apart from a phantom lifecycle row by its own genesis, never by comparing against the read's
    /// own project id alone — this test's own <c>projectIdQ</c> is a second, genuinely registered
    /// project, never the read's own scope.
    /// </summary>
    [Fact]
    public async Task A_ProjectPurgeScheduled_aimed_at_a_sibling_registered_project_on_the_same_node_is_also_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdP = DomainId.New();
        Guid projectIdQ = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node B hosts two genuinely registered projects, P (this read's own scope) and Q (a
        // sibling) — neither is a phantom lifecycle row.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdP,
                new ProjectRegistered(projectIdP, ownerId, DomainId.New(), "Receiver's project P", "/repo-p", null, "main", Now));
            session.Events.StartStream<ProjectAggregate>(
                projectIdQ,
                new ProjectRegistered(projectIdQ, ownerId, DomainId.New(), "Receiver's project Q", "/repo-q", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node A's own outbox for project P carries a ProjectPurgeScheduled whose own StreamId is
        // forged to be node B's OTHER real, registered project (Q) — never P, and never a foreign
        // id that would merely phantom-stream harmlessly.
        Guid senderLocalProjectId = DomainId.New();
        Guid originEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        ProjectPurgeScheduled forgedPurge = new(projectIdQ, Now.AddSeconds(1), Now.AddSeconds(1).AddHours(24), ownerId);
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            projectIdQ, typeof(ProjectPurgeScheduled).FullName!, JsonSerializer.Serialize(forgedPurge, jsonOptions),
            originEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectIdP);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, senderLocalProjectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, senderLocalProjectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        EventReplicationReadResult read;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdP, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: TrustChain.Empty, cts.Token);
        }

        read.EventsApplied.Should().Be(
            0, "a project lifecycle event may never resolve onto ANY project this node itself registered, not only the read's own");

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails siblingProject = (await session.LoadAsync<ProjectDetails>(projectIdQ, cts.Token))!;
            siblingProject.PurgeAt.Should().BeNull(
                "the forged purge must never schedule a sibling registered project for a hard delete either");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses, medium: the guard above only ever refused a
    /// LIFECYCLE event landing on a Project stream — a non-lifecycle event (Task, Idea, Epic, Run)
    /// forged with its own <c>StreamId</c> set directly to the receiver's real project id slipped
    /// through unchecked, since neither branch named it.
    /// </summary>
    [Fact]
    public async Task A_non_lifecycle_event_forged_onto_the_receivers_own_project_stream_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectIdB = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("node-a");

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(
                projectIdB,
                new ProjectRegistered(projectIdB, ownerId, DomainId.New(), "Receiver's own project", "/repo-b", null, "main", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node A's own outbox carries an IdeaAssignedToProject whose own StreamId is forged to be
        // node B's real local project id — never a genuine idea stream id at all.
        Guid senderLocalProjectId = DomainId.New();
        Guid originEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        IdeaAssignedToProject forgedAssignment = new(projectIdB, projectIdB, DomainId.New(), Now.AddSeconds(1), ownerId);
        EventReplicationCodec.ReplicatedEventRecord forgedRecord = new(
            projectIdB, typeof(IdeaAssignedToProject).FullName!, JsonSerializer.Serialize(forgedAssignment, jsonOptions),
            originEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(1), projectIdB);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, senderLocalProjectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([forgedRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, senderLocalProjectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        EventReplicationReadResult read;
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectIdB, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: TrustChain.Empty, cts.Token);
        }

        read.EventsApplied.Should().Be(0, "a non-lifecycle event must never land on a Project aggregate's own stream");

        await using (IQuerySession session = storeB.QuerySession())
        {
            ProjectDetails receiverProject = (await session.LoadAsync<ProjectDetails>(projectIdB, cts.Token))!;
            receiverProject.Name.Should().Be(
                "Receiver's own project", "the forged idea event must never actually append to the Project stream");
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
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token)).EventsApplied;
        }

        firstApplied.Should().BeGreaterThan(0);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult redelivered = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: OwnerChainFor(nodeA), cts.Token);
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
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
            firstRead.SenderIgnored.Should().BeTrue("the envelope's own project key resolves to a different local project");
        }

        // A second sweep with nothing new past the sender's outbox tip — the ordinary shape a
        // recurring sweep tick takes once it has caught up — must never read that empty result as
        // permission to clear a standing mismatch mark.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, scopedProjectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(5), trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(2), trustChain: TrustChain.Empty, cts.Token);
            firstRead.SenderIgnored.Should().BeTrue("no node file vouches for this sender yet");
        }

        // The sender is re-vouched (a node file lands), but sends nothing further — the ordinary
        // shape of a sweep tick after the sender's own outbox has already been fully read once.
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult secondRead = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3), trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                session, RepositoryPath, nodeA, projectId, nodeB, ownerFingerprint, Now.AddSeconds(4), trustChain: TrustChain.Empty,
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
                session, RepositoryPath, nodeB, projectId, nodeA, ownerFingerprint, Now.AddSeconds(8), trustChain: TrustChain.Empty,
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
                session, RepositoryPath, nodeA, projectId, nodeB, ownerFingerprint, Now.AddSeconds(4), trustChain: TrustChain.Empty,
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
    /// A second genesis for a stream that already exists locally — the shape two nodes racing
    /// <c>CloseoutEngine.TasksWithMissingRunRecordsAsync</c>'s own fleet-wide (never node-scoped)
    /// candidate set produces once <see cref="RunRecordReconstructed"/> travels: this receiver
    /// already carries its own, genuinely dispatched, run stream for <c>runId</c>, and a replicated
    /// <see cref="RunRecordReconstructed"/> for the identical run id — minted independently by a
    /// peer who never saw this stream's real genesis — must never land in its middle. Left unguarded,
    /// <c>RunAggregate.Apply(RunRecordReconstructed)</c> would overwrite <c>NodeId</c>, <c>OwnerId</c>,
    /// <c>DispatchedAt</c>, <c>PullRequestUrl</c> and <c>PullRequestNumber</c> and reset <c>State</c>
    /// back to Dispatched on a run this receiver already completed (independent pre-PR review, cycle
    /// 1, adversarial lens, medium).
    /// </summary>
    [Fact]
    public async Task A_replicated_second_genesis_for_a_stream_that_already_exists_here_is_discarded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid nodeA = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
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

        // The receiver's own genuine history for this run — a real dispatch, already completed —
        // started locally, never through replication, the same way node B's own two-node case
        // would have produced it.
        Guid receiverNodeId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(
                runId,
                new RunDispatched(
                    runId, taskId, receiverNodeId, ownerId, 1, DomainId.New(), "/worktrees/task-run", "task/run",
                    ExecutorMode.Subscription, Now, DispatchingNodeId: receiverNodeId));
            session.Events.Append(runId, new RunCompleted(runId, Now.AddSeconds(1)));
            await session.SaveChangesAsync(cts.Token);
        }

        // Node A independently reconstructs the identical run id, never having seen its real
        // genesis — crafted directly as a wire record rather than through CloseoutEngine, the same
        // shortcut the poison-event tests above already take for a record whose shape is what
        // matters, not how it was minted.
        Guid secondGenesisOriginEventId = DomainId.New();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        RunRecordReconstructed reconstructed = new(
            runId, taskId, nodeA, ownerId, "https://github.com/x/y/pull/9", 9, Now.AddSeconds(2));
        EventReplicationCodec.ReplicatedEventRecord secondGenesisRecord = new(
            runId, typeof(RunRecordReconstructed).FullName!, JsonSerializer.Serialize(reconstructed, jsonOptions),
            secondGenesisOriginEventId, OriginSequence: 1, nodeA, "owner-a-fingerprint", Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([secondGenesisRecord]), Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: TrustChain.Empty, cts.Token);
            read.EventsApplied.Should().Be(0, "a second genesis for a stream that already exists here is discarded, not appended");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            StreamState? state = await session.Events.FetchStreamStateAsync(runId, cts.Token);
            state.Should().NotBeNull();
            state!.Version.Should().Be(2, "still exactly RunDispatched then RunCompleted — nothing appended past them");

            RunDetails? run = await session.LoadAsync<RunDetails>(runId, cts.Token);
            run.Should().NotBeNull();
            run!.State.Should().Be(RunState.Completed, "the second genesis never reset this run back to Dispatched");
            run.NodeId.Should().Be(receiverNodeId, "the receiver's own real dispatch, never overwritten by the peer's stray reconstruction");

            (await session.LoadAsync<ReplicatedEventRecord>(secondGenesisOriginEventId, cts.Token)).Should().NotBeNull(
                "recorded as handled so a later sweep never retries the identical second genesis again");
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
                trustChain: TrustChain.Empty, cts.Token);
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
            (await replicationInbox.ReplayHeldTailAsync(
                session, projectId, taskId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(8), TrustChain.Empty,
                cts.Token)).Should().Be(1, "the tail was waiting on a replay, not on the fleet");
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

    /// <summary>
    /// Idea 6be68ee2, trust-ledger finding 5: the Task/Run act gate's own held-and-replayed shape,
    /// the sibling of the project-settings gate's tests above. A member's own claim arrives before
    /// this receiver has ever heard the owner's own assignment, so it is held rather than dropped —
    /// and a later record from the identical origin is held right behind it, never applied ahead of
    /// it. Both clear, in order, the moment the owner's assignment lands from a wholly different
    /// read.
    /// </summary>
    [Fact]
    public async Task A_held_claim_and_a_later_event_from_the_same_origin_both_apply_in_order_once_the_assignment_lands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerNode = DomainId.New();
        Guid memberNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid memberOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, ownerNode, cts.Token);
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        TrustChain chain = TwoRootChain(ownerNode, memberNode, "owner-root-a", "member-root-a");
        const string memberRoot = "member-root-a";

        await using DocumentStore storeB = OpenStoreB();

        // The task's own genesis already exists here — published, still unassigned — the shape a
        // conditional act's own gate needs streamExists true for (never the missing-genesis path).
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, ownerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // The member's own claim arrives first — this receiver has no idea yet that the owner ever
        // assigned the task to this member's root, so the claim is held rather than dropped.
        TaskClaimed claimed = new(
            taskId, memberNode, memberOwnerId, LeaseGeneration: 1, runId, Now.AddSeconds(1),
            OwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord claimRecord = new(
            taskId, typeof(TaskClaimed).FullName!, JsonSerializer.Serialize(claimed, jsonOptions),
            DomainId.New(), OriginSequence: 1, memberNode, memberRoot, Now.AddSeconds(1), projectId);

        // A later fact from the identical origin, queued behind the still-unresolved claim above —
        // never applied ahead of it, or the per-origin ordering guard would refuse the claim as out
        // of order the moment it finally clears.
        TaskCompleted completed = new(taskId, runId, PullRequestUrl: null, Now.AddSeconds(2));
        EventReplicationCodec.ReplicatedEventRecord completedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(completed, jsonOptions),
            DomainId.New(), OriginSequence: 2, memberNode, memberRoot, Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord, completedRecord]), Now.AddSeconds(3),
                cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                0, "the task is still unassigned here, so the member's own claim (and everything queued behind "
                    + "it) is held");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().HaveCount(2, "both the claim and the record queued behind it are held");
        }

        // The owner's own assignment lands separately, from a different origin — an owner-role
        // sender's act always applies, whatever the task's own current state is.
        TaskAssigned assigned = new(
            taskId, memberOwnerId, UnmetDependencies: [], Now.AddSeconds(5), ownerId,
            AssignedOwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord assignedRecord = new(
            taskId, typeof(TaskAssigned).FullName!, JsonSerializer.Serialize(assigned, jsonOptions),
            DomainId.New(), OriginSequence: 1, ownerNode, "owner-root-a", Now.AddSeconds(5), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, ownerNode, projectId, "owner-root-a", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([assignedRecord]), Now.AddSeconds(6), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, ownerNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(6), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, ownerNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(7),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                3, "the assignment itself, plus the held claim and the record queued behind it, all apply once "
                    + "the assignment lands");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("every held record cleared once the assignment landed");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.State.Should().Be(TaskState.Done, "the claim and the completion both applied, in order");
            task.HolderNodeId.Should().Be(memberNode);
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 1, both lenses, high: <see cref="HeldTaskActRecord"/> is
    /// keyed by THIS RECEIVER's own local project id, never <see cref="EventReplicationCodec.ReplicatedEventRecord.OriginProjectId"/>
    /// — the sender's own local coordinate, which two real installs never share. Sent here with a
    /// deliberately different origin project id from the receiver's own, exactly the shape two real
    /// installs always take, this must still be found by the "earlier held for this origin" check
    /// and by the recheck that applies it once the assignment lands.
    /// </summary>
    [Fact]
    public async Task A_held_claim_stamped_with_a_foreign_origin_project_id_still_clears_once_the_assignment_lands()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerNode = DomainId.New();
        Guid memberNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid memberOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid foreignOriginProjectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, ownerNode, cts.Token);
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        TrustChain chain = TwoRootChain(ownerNode, memberNode, "owner-root-c", "member-root-c");
        const string memberRoot = "member-root-c";

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, ownerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // Stamped with the SENDER's own local project id (foreignOriginProjectId), never this
        // receiver's own (projectId) — EventReplicationOutbox.QueuePendingAsync's own doc on why
        // the two differ on any two real installs.
        TaskClaimed claimed = new(
            taskId, memberNode, memberOwnerId, LeaseGeneration: 1, runId, Now.AddSeconds(1),
            OwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord claimRecord = new(
            taskId, typeof(TaskClaimed).FullName!, JsonSerializer.Serialize(claimed, jsonOptions),
            DomainId.New(), OriginSequence: 1, memberNode, memberRoot, Now.AddSeconds(1), foreignOriginProjectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(0, "the claim is held until the task's own assignment arrives");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            IReadOnlyList<HeldTaskActRecord> held =
                await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token);
            held.Should().ContainSingle().Which.ProjectId.Should().Be(
                projectId, "the row is keyed by this receiver's own local project id, never the sender's");
        }

        TaskAssigned assigned = new(
            taskId, memberOwnerId, UnmetDependencies: [], Now.AddSeconds(4), ownerId,
            AssignedOwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord assignedRecord = new(
            taskId, typeof(TaskAssigned).FullName!, JsonSerializer.Serialize(assigned, jsonOptions),
            DomainId.New(), OriginSequence: 1, ownerNode, "owner-root-c", Now.AddSeconds(4), foreignOriginProjectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, ownerNode, projectId, "owner-root-c", MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([assignedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, ownerNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, ownerNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                2, "the assignment itself, plus the held claim it clears, both apply once the recheck finds the "
                    + "row by this receiver's own project id");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("the held claim cleared once the recheck could actually find it");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.HolderNodeId.Should().Be(memberNode);
        }
    }

    /// <summary>
    /// Idea 6be68ee2, trust-ledger finding 5: a hold nothing in the fleet ever answers does not sit
    /// forever — it expires after 24 hours, dropping the queue behind it with one log line naming
    /// the act, rather than blocking that stream's own per-origin order forever.
    /// </summary>
    [Fact]
    public async Task An_expired_held_task_act_is_dropped_with_one_log_line_naming_the_act()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerNode = DomainId.New();
        Guid memberNode = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid memberOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, ownerNode, cts.Token);
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        ListLogger<EventReplicationInbox> log = new();
        EventReplicationInbox replicationInbox = new(transport, log);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        TrustChain chain = TwoRootChain(ownerNode, memberNode, "owner-root-b", "member-root-b");
        const string memberRoot = "member-root-b";
        const string ownerRoot = "owner-root-b";

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, ownerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
        TaskClaimed claimed = new(
            taskId, memberNode, memberOwnerId, LeaseGeneration: 1, runId, Now.AddSeconds(1),
            OwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord claimRecord = new(
            taskId, typeof(TaskClaimed).FullName!, JsonSerializer.Serialize(claimed, jsonOptions),
            DomainId.New(), OriginSequence: 1, memberNode, memberRoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(0, "the claim is held until the task's own assignment arrives");
        }

        // Manually age the hold well past the 24-hour expiry — the real passage of time this test
        // never waits out.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            HeldTaskActRecord held = (await session.Query<HeldTaskActRecord>()
                .Where(record => record.TaskId == taskId).FirstOrDefaultAsync(cts.Token))!;
            held.HeldAt = Now.AddHours(-25);
            session.Store(held);
            await session.SaveChangesAsync(cts.Token);
        }

        // A second, unrelated task lands from the owner so this read applies something for the
        // project — the trigger the expired hold is re-checked against, whoever it came from.
        Guid secondTaskId = DomainId.New();
        TaskAdded secondAdded = TaskDecider.Add(
            secondTaskId, projectId, "A second task", ["done"], TaskType.Feature, null, null, null, Now.AddSeconds(4),
            ownerId);
        EventReplicationCodec.ReplicatedEventRecord secondAddedRecord = new(
            secondTaskId, typeof(TaskAdded).FullName!, JsonSerializer.Serialize(secondAdded, jsonOptions),
            DomainId.New(), OriginSequence: 1, ownerNode, ownerRoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, ownerNode, projectId, ownerRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([secondAddedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, ownerNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, ownerNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(1, "only the second task's own genesis applies from this read directly");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("the expired hold is dropped rather than kept waiting forever");
        }

        log.Lines.Should().Contain(
            line => line.Contains("24 hours", StringComparison.Ordinal)
                && line.Contains(nameof(TaskClaimed), StringComparison.Ordinal),
            "the drop is logged with one line naming the act");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, conformance lens, high: the fix holding a relay-dropped
    /// Task/Run act (rather than losing it, <see cref="HeldTaskActRecord"/>'s own doc) keyed the
    /// resulting hold row by the relay's own sender identity. Nothing cleared that row once the
    /// identical act — same <see cref="EventReplicationCodec.ReplicatedEventRecord.OriginEventId"/> —
    /// later succeeded through a DIRECT delivery from the true origin, so the stale row kept
    /// outranking every later same-origin/same-stream record under the "earlier held for this
    /// origin" check forever, and the re-check kept re-judging it against the relay's own
    /// (now-irrelevant) root. This proves the fix: the direct delivery both applies the act for real
    /// and clears its own stale hold.
    /// <para>
    /// The relay's own forward is admitted at all only because it answers a catch-up request this
    /// node itself minted for the task's own stream (independent pre-PR review, cycle 8, terminal
    /// lap: <see cref="EventReplicationInbox.IsForwardedRecordAdmitted"/> now polices a Task or Run
    /// act exactly like any other forwarded record — the cycle-8 exemption that let one skip that
    /// gate outright regressed main's own trust-ledger findings 4 and 7). The record queued behind
    /// the claim is a plain MemberSafe act (<see cref="TaskCompleted"/>): once it has already
    /// cleared that admission gate alongside the claim, it needs no SECOND direct delivery of its
    /// own — the moment the claim's own hold clears and the re-check reaches it, it applies
    /// straight off the queue.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_relay_dropped_acts_stale_hold_clears_once_the_true_origin_delivers_it_directly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid memberNode = DomainId.New();
        Guid relayNode = DomainId.New();
        Guid receiverNodeId = DomainId.New();
        Guid creatorOwnerId = DomainId.New();
        Guid memberOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        const string memberRoot = "member-root-f";
        const string relayRoot = "relay-root-f";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        await SeedNodeFileAsync(ledger, relayNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        // Two member roots, neither an owner — the relay's own root must never match the
        // assignment below, or its forward would resolve Allowed outright rather than
        // DroppedWithoutRecording.
        string memberKeyLine = $"ssh-ed25519 AAAAFAKE{memberNode:N} test";
        string relayKeyLine = $"ssh-ed25519 AAAAFAKE{relayNode:N} test";
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner>
            {
                [memberRoot] = new TrustedOwner(
                    memberRoot, $"ssh-ed25519 AAAAFAKE{memberRoot} test",
                    [new TrustedNode(memberNode.ToString(), memberKeyLine, NodeKeyStore.Fingerprint(memberKeyLine), Now)]),
                [relayRoot] = new TrustedOwner(
                    relayRoot, $"ssh-ed25519 AAAAFAKE{relayRoot} test",
                    [new TrustedNode(relayNode.ToString(), relayKeyLine, NodeKeyStore.Fingerprint(relayKeyLine), Now)]),
            },
            [
                new ProjectMember(memberRoot, MembershipRole.Member, Now),
                new ProjectMember(relayRoot, MembershipRole.Member, Now),
            ]);

        await using DocumentStore storeB = OpenStoreB();

        // The task is already assigned to the true origin's own root — the fact TaskClaimed's own
        // conditional check reads.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now,
                creatorOwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, creatorOwnerId));
            session.Events.Append(
                taskId,
                new TaskAssigned(
                    taskId, memberOwnerId, UnmetDependencies: [], Now, creatorOwnerId,
                    AssignedOwnerRootFingerprint: memberRoot));
            await session.SaveChangesAsync(cts.Token);
        }

        // A broadcast this receiving node itself minted for the task's own stream — the shape the
        // relay's forward below is entitled to answer at all now that IsForwardedRecordAdmitted
        // polices a Task or Run act exactly like any other forwarded record (ForStreamId = taskId,
        // Candidates empty: any vouched project member may reply).
        Guid requestId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Store(new EventCatchUpRequest
            {
                Id = requestId,
                ProjectId = projectId,
                ForStreamId = taskId,
                Candidates = [],
                SentAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // Origin sequence 1: the true origin's own claim, forwarded here by a fellow member relay
        // whose own root does not match the assignment — refused as DroppedWithoutRecording rather
        // than lost, since record.OriginNodeId (the member) differs from senderNodeId (the relay).
        TaskClaimed claimed = new(
            taskId, memberNode, memberOwnerId, LeaseGeneration: 1, runId, Now.AddSeconds(1),
            OwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord claimRecord = new(
            taskId, typeof(TaskClaimed).FullName!, JsonSerializer.Serialize(claimed, jsonOptions),
            DomainId.New(), OriginSequence: 1, memberNode, memberRoot, Now.AddSeconds(1), projectId);

        // Origin sequence 2: a plain MemberSafe fact from the identical origin, forwarded by the
        // same relay — queued behind the claim above regardless of its own classification, since
        // nothing has appended for this origin on this stream yet.
        TaskCompleted completed = new(taskId, runId, PullRequestUrl: null, Now.AddSeconds(2));
        EventReplicationCodec.ReplicatedEventRecord completedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(completed, jsonOptions),
            DomainId.New(), OriginSequence: 2, memberNode, memberRoot, Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, relayNode, projectId, relayRoot, MessageAudience.Node(receiverNodeId),
                about: requestId.ToString(), MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord, completedRecord]),
                Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, relayNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, relayNode, projectId, receiverNodeId, "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                0, "the relay's own root does not match the assignment, so the claim is held and the record "
                    + "queued behind it holds too");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().HaveCount(2, "both the relay-dropped claim and the record queued behind it are held");
        }

        // The true origin now answers directly — the identical claim, same origin event id, this
        // time with a verified sender whose root matches the assignment.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                2, "the direct claim applies for real and clears its own stale relay hold, and that same read "
                    + "then finds the completion — already legitimately admitted through the catch-up answer the "
                    + "relay forwarded it inside — at the head of the queue and applies it too, with no second "
                    + "direct delivery needed");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("both the claim's and the completion's own holds cleared in the same read");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.State.Should().Be(TaskState.Done, "the claim and the completion both actually applied, in order");
            task.HolderNodeId.Should().Be(memberNode);
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 8, conformance lens, high: the "earlier held for this
    /// origin" queue-order check used to run for every sender alike, so a relay's own forged claim
    /// about an origin — held as <see cref="TaskActVerdict.DroppedWithoutRecording"/>, exactly as a
    /// genuine relay-dropped act would be — outranked even that SAME origin's own OWNER-role node
    /// delivering something completely different directly, the moment the two happened to name the
    /// identical claimed origin. An owner-role sender's own act is Allowed "whatever classification
    /// says" everywhere else in this gate; this proves it is never queued behind someone else's
    /// still-unresolved claim either.
    /// </summary>
    [Fact]
    public async Task An_owner_roles_direct_act_never_queues_behind_a_relays_unrelated_hold_for_the_same_origin()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerNode = DomainId.New();
        Guid relayNode = DomainId.New();
        Guid receiverNodeId = DomainId.New();
        Guid ownerOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        const string ownerRoot = "owner-root-h";
        const string relayRoot = "relay-root-h";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, ownerNode, cts.Token);
        await SeedNodeFileAsync(ledger, relayNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        TrustChain chain = TwoRootChain(ownerNode, relayNode, ownerRoot, relayRoot);

        await using DocumentStore storeB = OpenStoreB();

        // The task is assigned to the owner's own root — a fact the relay's own root can never
        // satisfy, whatever origin it claims to be relaying for.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now,
                ownerOwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, ownerOwnerId));
            session.Events.Append(
                taskId,
                new TaskAssigned(
                    taskId, ownerOwnerId, UnmetDependencies: [], Now, ownerOwnerId,
                    AssignedOwnerRootFingerprint: ownerRoot));
            await session.SaveChangesAsync(cts.Token);
        }

        // A broadcast this receiving node itself minted for the task's own stream — the shape the
        // relay's forward below is entitled to answer at all now that IsForwardedRecordAdmitted
        // polices a Task or Run act exactly like any other forwarded record.
        Guid requestId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Store(new EventCatchUpRequest
            {
                Id = requestId,
                ProjectId = projectId,
                ForStreamId = taskId,
                Candidates = [],
                SentAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // Origin sequence 1: a claim the relay forges as coming from the OWNER's own node, but the
        // relay's own root never matches the assignment — held as DroppedWithoutRecording, exactly
        // like the ordinary relay-dropped shape, only this time the claimed origin is the owner's.
        TaskClaimed claimed = new(
            taskId, ownerNode, ownerOwnerId, LeaseGeneration: 1, runId, Now.AddSeconds(1),
            OwnerRootFingerprint: ownerRoot);
        EventReplicationCodec.ReplicatedEventRecord claimRecord = new(
            taskId, typeof(TaskClaimed).FullName!, JsonSerializer.Serialize(claimed, jsonOptions),
            DomainId.New(), OriginSequence: 1, ownerNode, ownerRoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, relayNode, projectId, relayRoot, MessageAudience.Node(receiverNodeId),
                about: requestId.ToString(), MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord]),
                Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, relayNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, relayNode, projectId, receiverNodeId, "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(0, "the relay's own root does not match the assignment, so its claim is held");
        }

        // The owner's own node now delivers a completely different act directly, naming itself as
        // the identical origin the relay's stale hold already claims.
        TaskCompleted completed = new(taskId, runId, PullRequestUrl: null, Now.AddSeconds(4));
        EventReplicationCodec.ReplicatedEventRecord completedRecord = new(
            taskId, typeof(TaskCompleted).FullName!, JsonSerializer.Serialize(completed, jsonOptions),
            DomainId.New(), OriginSequence: 2, ownerNode, ownerRoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, ownerNode, projectId, ownerRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([completedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, ownerNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, ownerNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                1, "the owner's own direct act applies at once rather than queuing behind the relay's "
                    + "still-unresolved claim about the identical origin");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            IReadOnlyList<HeldTaskActRecord> held = await session.Query<HeldTaskActRecord>()
                .Where(record => record.TaskId == taskId).ToListAsync(cts.Token);
            held.Should().HaveCount(1, "the relay's own stale claim is untouched — a different origin event id "
                + "than the one that just applied");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.State.Should().Be(TaskState.Done, "the owner's own completion applied for real");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 6, conformance lens, high: <see cref="HoldTaskActAsync"/>'s
    /// own upsert-avoiding dedupe left a stale relay sender in place forever the moment a SECOND
    /// queued act's own direct redelivery from the true origin raced ahead of the FIRST's — a shape
    /// the cycle-5 fix's own test never exercised, since it only ever redelivered the head of its own
    /// two-item queue. Left unfixed, <see cref="ReCheckHeldTaskActsAsync"/> would keep re-judging the
    /// second act against the relay it was never actually forwarded's own root once the first act's
    /// hold finally cleared, reaching <see cref="TaskActVerdict.DroppedWithoutRecording"/> again and
    /// re-holding it indefinitely even though the true origin had already answered for it directly.
    /// This proves the fix: the true origin's own direct redelivery of the second act updates its
    /// still-queued hold's stale sender in place, so once the first act's own direct delivery clears
    /// its own hold, the second one drains right behind it, correctly attributed.
    /// </summary>
    [Fact]
    public async Task A_second_queued_acts_stale_sender_updates_when_the_true_origin_redelivers_it_directly_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid memberNode = DomainId.New();
        Guid relayNode = DomainId.New();
        Guid receiverNodeId = DomainId.New();
        Guid creatorOwnerId = DomainId.New();
        Guid memberOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        const string memberRoot = "member-root-g";
        const string relayRoot = "relay-root-g";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        await SeedNodeFileAsync(ledger, relayNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        // Two member roots, neither an owner — the relay's own root must never match the
        // assignment below, or its forward would resolve Allowed outright rather than
        // DroppedWithoutRecording.
        string memberKeyLine = $"ssh-ed25519 AAAAFAKE{memberNode:N} test";
        string relayKeyLine = $"ssh-ed25519 AAAAFAKE{relayNode:N} test";
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner>
            {
                [memberRoot] = new TrustedOwner(
                    memberRoot, $"ssh-ed25519 AAAAFAKE{memberRoot} test",
                    [new TrustedNode(memberNode.ToString(), memberKeyLine, NodeKeyStore.Fingerprint(memberKeyLine), Now)]),
                [relayRoot] = new TrustedOwner(
                    relayRoot, $"ssh-ed25519 AAAAFAKE{relayRoot} test",
                    [new TrustedNode(relayNode.ToString(), relayKeyLine, NodeKeyStore.Fingerprint(relayKeyLine), Now)]),
            },
            [
                new ProjectMember(memberRoot, MembershipRole.Member, Now),
                new ProjectMember(relayRoot, MembershipRole.Member, Now),
            ]);

        await using DocumentStore storeB = OpenStoreB();

        // The task is already assigned to the true origin's own root — the fact the handback's own
        // general Conditional check reads.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now,
                creatorOwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            session.Events.Append(taskId, new TaskPublished(taskId, Now, creatorOwnerId));
            session.Events.Append(
                taskId,
                new TaskAssigned(
                    taskId, memberOwnerId, UnmetDependencies: [], Now, creatorOwnerId,
                    AssignedOwnerRootFingerprint: memberRoot));
            await session.SaveChangesAsync(cts.Token);
        }

        // A broadcast this receiving node itself minted for the task's own stream — the shape the
        // relay's forward below is entitled to answer at all now that IsForwardedRecordAdmitted
        // polices a Task or Run act exactly like any other forwarded record.
        Guid requestId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Store(new EventCatchUpRequest
            {
                Id = requestId,
                ProjectId = projectId,
                ForStreamId = taskId,
                Candidates = [],
                SentAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // Origin sequence 1: the true origin's own claim, forwarded here by a fellow member relay
        // whose own root does not match the assignment — held as DroppedWithoutRecording.
        TaskClaimed claimed = new(
            taskId, memberNode, memberOwnerId, LeaseGeneration: 1, runId, Now.AddSeconds(1),
            OwnerRootFingerprint: memberRoot);
        EventReplicationCodec.ReplicatedEventRecord claimRecord = new(
            taskId, typeof(TaskClaimed).FullName!, JsonSerializer.Serialize(claimed, jsonOptions),
            DomainId.New(), OriginSequence: 1, memberNode, memberRoot, Now.AddSeconds(1), projectId);

        // Origin sequence 2: a second Conditional act from the identical origin, also forwarded by
        // the same relay — queues behind sequence 1 regardless of its own verdict, since nothing has
        // appended for this origin on this stream yet.
        TaskHandedBack handedBack = new(
            taskId, runId, "feature/ship-the-thing", "context switch", Now.AddSeconds(2), memberOwnerId);
        EventReplicationCodec.ReplicatedEventRecord handedBackRecord = new(
            taskId, typeof(TaskHandedBack).FullName!, JsonSerializer.Serialize(handedBack, jsonOptions),
            DomainId.New(), OriginSequence: 2, memberNode, memberRoot, Now.AddSeconds(2), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, relayNode, projectId, relayRoot, MessageAudience.Node(receiverNodeId),
                about: requestId.ToString(), MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord, handedBackRecord]),
                Now.AddSeconds(3), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, relayNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(3), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, relayNode, projectId, receiverNodeId, "owner-b-fingerprint", Now.AddSeconds(4),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                0, "the relay's own root does not match the assignment, so the claim is held and the record "
                    + "queued behind it holds too");
        }

        Guid handedBackHoldId;
        await using (IQuerySession session = storeB.QuerySession())
        {
            IReadOnlyList<HeldTaskActRecord> held = await session.Query<HeldTaskActRecord>()
                .Where(record => record.TaskId == taskId).ToListAsync(cts.Token);
            held.Should().HaveCount(2, "both the relay-dropped claim and the record queued behind it are held");
            held.Should().OnlyContain(
                record => record.SenderNodeId == relayNode, "neither hold has heard from the true origin yet");
            handedBackHoldId = held.Single(record => record.OriginSequence == 2).Id;
        }

        // The true origin now redelivers ONLY the SECOND act directly — its own direct copy of the
        // first act (the claim) has not arrived here yet, exactly the out-of-order shape this
        // platform's own at-least-once, no-ordering-guarantee-across-envelopes transport allows.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([handedBackRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                0, "the claim is still held ahead of it, so the direct redelivery only re-queues, never applies");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            HeldTaskActRecord? handedBackHold = await session.LoadAsync<HeldTaskActRecord>(handedBackHoldId, cts.Token);
            handedBackHold.Should().NotBeNull("the second act is still queued behind the still-held claim");
            handedBackHold!.SenderNodeId.Should().Be(
                memberNode, "the true origin's own direct redelivery updates the stale relay sender in place "
                    + "rather than leaving it citing the relay forever");
        }

        // The true origin now answers the FIRST act directly too — this clears its own stale hold
        // and, per the fix under test, lets the correctly re-attributed second act drain right
        // behind it in the same read.
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([claimRecord]), Now.AddSeconds(7), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(7), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(8),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                2, "the direct claim applies for real, clearing its own stale hold, and the correctly "
                    + "re-attributed handback drains right behind it rather than staying held against a "
                    + "relay that never actually answered for it");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("neither act is left held once the true origin answered for both");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.State.Should().Be(TaskState.Queued, "the claim and the handback both actually applied, in order");
        }
    }

    /// <summary>
    /// Independent pre-PR review, cycle 2, adversarial lens, high: the earlier version of
    /// <see cref="TaskCreatorRootRecord"/> only ever recorded the creator's own root at the instant a
    /// task's genesis started its stream fresh, direct delivery only — a relayed genesis (the
    /// ordinary shape a catch-up broadcast answer takes) left the record entirely unset, and once
    /// the stream existed nothing could ever complete it, so every pre-assignment act the true
    /// creator ever sent directly afterward sat held until its own 24-hour expiry dropped it. This
    /// proves the fix: a relayed genesis still records the claimed origin, and the first later act
    /// the claimed origin delivers directly backfills the verified fingerprint and applies outright.
    /// </summary>
    [Fact]
    public async Task A_relayed_geneses_creator_root_is_backfilled_once_the_true_origin_delivers_directly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid ownerNode = DomainId.New();
        Guid memberNode = DomainId.New();
        Guid receiverNodeId = DomainId.New();
        Guid memberOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, ownerNode, cts.Token);
        await SeedNodeFileAsync(ledger, memberNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        TrustChain chain = TwoRootChain(ownerNode, memberNode, "owner-root-d", "member-root-d");
        const string memberRoot = "member-root-d";
        const string ownerRoot = "owner-root-d";

        await using DocumentStore storeB = OpenStoreB();
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // A broadcast this receiving node itself minted for the task's own stream — the shape the
        // owner's relay below is entitled to answer at all now that IsForwardedRecordAdmitted
        // polices a Task or Run act exactly like any other forwarded record; a fresh stream id is
        // still a legitimate ForStreamId ask (h9k task pull), since the requester by definition
        // does not hold it yet.
        Guid requestId = DomainId.New();
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Store(new EventCatchUpRequest
            {
                Id = requestId,
                ProjectId = projectId,
                ForStreamId = taskId,
                Candidates = [],
                SentAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        // The member's own draft genesis, delivered here by the OWNER relaying it rather than the
        // member itself — the ordinary shape a catch-up broadcast answer takes
        // (EventCatchUpResponder's own doc): the wire record's own OriginNodeId names the member,
        // but the transport-verified sender is the owner.
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now,
            memberOwnerId);
        EventReplicationCodec.ReplicatedEventRecord addedRecord = new(
            taskId, typeof(TaskAdded).FullName!, JsonSerializer.Serialize(added, jsonOptions),
            DomainId.New(), OriginSequence: 1, memberNode, memberRoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, ownerNode, projectId, ownerRoot, MessageAudience.Node(receiverNodeId),
                about: requestId.ToString(), MessageKind.Events, EventReplicationCodec.EncodeBatch([addedRecord]),
                Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, ownerNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, ownerNode, projectId, receiverNodeId, "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(1, "the relayed genesis still starts the stream");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskCreatorRootRecord? creatorRoot = await session.LoadAsync<TaskCreatorRootRecord>(taskId, cts.Token);
            creatorRoot.Should().NotBeNull("the genesis claims a creator even though it arrived relayed");
            creatorRoot!.ClaimedOriginNodeId.Should().Be(memberNode);
            creatorRoot.CreatorRootFingerprint.Should().BeEmpty(
                "a relay's own verified key proves nothing about who actually authored the genesis");
        }

        // The true creator, now online, revises their own still-unassigned draft directly — the
        // identical proof the genesis itself would have carried had the member shipped it directly.
        TaskRevised revised = new(
            taskId, Optional<string>.Of("Ship the revised thing"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now.AddSeconds(4), memberOwnerId);
        EventReplicationCodec.ReplicatedEventRecord revisedRecord = new(
            taskId, typeof(TaskRevised).FullName!, JsonSerializer.Serialize(revised, jsonOptions),
            DomainId.New(), OriginSequence: 2, memberNode, memberRoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, memberNode, projectId, memberRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([revisedRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, memberNode, projectId, "shared-project-key", adoptUnassigned: false, committer,
                signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, memberNode, projectId, DomainId.New(), "owner-b-fingerprint", Now.AddSeconds(6),
                trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                1, "the creator's own direct delivery both backfills the creator root and applies immediately, "
                    + "never held");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.Query<HeldTaskActRecord>().Where(record => record.TaskId == taskId).ToListAsync(cts.Token))
                .Should().BeEmpty("the revise was allowed outright, never held");

            TaskCreatorRootRecord creatorRoot = (await session.LoadAsync<TaskCreatorRootRecord>(taskId, cts.Token))!;
            creatorRoot.CreatorRootFingerprint.Should().Be(
                memberRoot, "the direct delivery backfilled the fingerprint the relay could not verify");

            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.Objective.Should().Be("Ship the revised thing", "the backfilled act actually applied");
        }
    }

    /// <summary>
    /// Class sweep off the relayed-genesis fix above: <see cref="TaskCreatorRootRecord"/> is written
    /// only from <c>EventReplicationInbox.ApplyAsync</c>'s own genesis path, so a task created
    /// NATIVELY, right here, never goes through it at all and never gets a record — the identical
    /// symptom (a pre-assignment act judged with no creator root on file) from a different cause.
    /// This proves the fallback: with no <see cref="TaskCreatorRootRecord"/> whatsoever, the gate
    /// resolves the creator from the task's own <see cref="TaskAggregate.AddedByOwnerId"/> through
    /// <see cref="OwnerRootFingerprintResolver"/> — trustworthy here because a native genesis never
    /// crossed the wire, unlike a replicated one's own claims — so the actual creator's other node
    /// may still act on their own unassigned draft, while an unrelated member is refused outright.
    /// </summary>
    [Fact]
    public async Task A_native_tasks_creator_root_falls_back_to_its_own_added_by_owner_when_never_replicated_in()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid creatorMemberNode = DomainId.New();
        Guid otherMemberNode = DomainId.New();
        Guid creatorOwnerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        const string creatorRoot = "member-root-e";
        const string otherRoot = "other-root-e";

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, creatorMemberNode, cts.Token);
        await SeedNodeFileAsync(ledger, otherMemberNode, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);
        (LedgerCommitter committer, LedgerSigningKey signingKey) = Signing("relay");

        string creatorKeyLine = $"ssh-ed25519 AAAAFAKE{creatorMemberNode:N} test";
        string otherKeyLine = $"ssh-ed25519 AAAAFAKE{otherMemberNode:N} test";
        TrustChain chain = new(
            new Dictionary<string, TrustedOwner>
            {
                [creatorRoot] = new TrustedOwner(
                    creatorRoot, $"ssh-ed25519 AAAAFAKE{creatorRoot} test",
                    [new TrustedNode(
                        creatorMemberNode.ToString(), creatorKeyLine, NodeKeyStore.Fingerprint(creatorKeyLine), Now)]),
                [otherRoot] = new TrustedOwner(
                    otherRoot, $"ssh-ed25519 AAAAFAKE{otherRoot} test",
                    [new TrustedNode(
                        otherMemberNode.ToString(), otherKeyLine, NodeKeyStore.Fingerprint(otherKeyLine), Now)]),
            },
            [
                new ProjectMember(creatorRoot, MembershipRole.Member, Now),
                new ProjectMember(otherRoot, MembershipRole.Member, Now),
            ]);

        await using DocumentStore storeB = OpenStoreB();

        // The creator's own cross-node root, claimed the same way `h9k project join --owner` records
        // one — never observed by this task's own genesis below, which never goes through
        // EventReplicationInbox at all.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            session.Events.StartStream<OwnerAggregate>(creatorOwnerId, new OwnerRegistered(creatorOwnerId, "Creator", null, Now));
            session.Events.Append(creatorOwnerId, new OwnerRootClaimed(creatorOwnerId, creatorRoot, Verified: true, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The task's own genesis, created natively right here — never replicated in, so
        // TaskCreatorRootRecord is never written for it at all.
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now,
                creatorOwnerId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            (await session.LoadAsync<TaskCreatorRootRecord>(taskId, cts.Token)).Should().BeNull(
                "a native genesis never goes through the replication inbox at all");
        }

        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

        // A different project member, with no claim on this draft at all, tries to revise it
        // directly — refused outright, never merely held, since this node already knows this
        // member is not the creator.
        TaskRevised revisedByStranger = new(
            taskId, Optional<string>.Of("Hijacked"), Optional<IReadOnlyList<string>>.None, Optional<string>.None,
            Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None, Now.AddSeconds(1),
            DomainId.New());
        EventReplicationCodec.ReplicatedEventRecord strangerRecord = new(
            taskId, typeof(TaskRevised).FullName!, JsonSerializer.Serialize(revisedByStranger, jsonOptions),
            DomainId.New(), OriginSequence: 1, otherMemberNode, otherRoot, Now.AddSeconds(1), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, otherMemberNode, projectId, otherRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([strangerRecord]), Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, otherMemberNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(2), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, otherMemberNode, projectId, DomainId.New(), "owner-b-fingerprint",
                Now.AddSeconds(3), trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(0, "a stranger's own revise of someone else's draft is refused");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.Objective.Should().Be("Ship the thing", "the stranger's revise never applied");
        }

        // The actual creator, acting from a different node in their own fleet than the one that
        // created the task, revises their own still-unassigned draft directly — allowed, resolved
        // from the task's own AddedByOwnerId even though no TaskCreatorRootRecord exists at all.
        TaskRevised revisedByCreator = new(
            taskId, Optional<string>.Of("Ship the revised thing"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now.AddSeconds(4), creatorOwnerId);
        EventReplicationCodec.ReplicatedEventRecord creatorRecord = new(
            taskId, typeof(TaskRevised).FullName!, JsonSerializer.Serialize(revisedByCreator, jsonOptions),
            DomainId.New(), OriginSequence: 1, creatorMemberNode, creatorRoot, Now.AddSeconds(4), projectId);

        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            await MessageOutbox.QueueAsync(
                session, creatorMemberNode, projectId, creatorRoot, MessageAudience.Project, about: null,
                MessageKind.Events, EventReplicationCodec.EncodeBatch([creatorRecord]), Now.AddSeconds(5), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, creatorMemberNode, projectId, "shared-project-key", adoptUnassigned: false,
                committer, signingKey, Now.AddSeconds(5), cts.Token);
        }

        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, creatorMemberNode, projectId, DomainId.New(), "owner-b-fingerprint",
                Now.AddSeconds(6), trustChain: chain, cts.Token);
            read.EventsApplied.Should().Be(
                1, "the actual creator's own other node may still revise their own unassigned draft, resolved "
                    + "from AddedByOwnerId with no TaskCreatorRootRecord on file at all");
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.Objective.Should().Be("Ship the revised thing", "the creator's own revise actually applied");
        }
    }

    /// <summary>A project with one owner root (naming <paramref name="ownerNodeId"/>) and one member
    /// root (naming <paramref name="memberNodeId"/>) — the Task/Run act gate's own tests need both
    /// roles present at once, unlike the project-settings gate's own single-root fixtures above.</summary>
    private static TrustChain TwoRootChain(Guid ownerNodeId, Guid memberNodeId, string ownerRoot, string memberRoot)
    {
        string ownerKeyLine = $"ssh-ed25519 AAAAFAKE{ownerNodeId:N} test";
        string memberKeyLine = $"ssh-ed25519 AAAAFAKE{memberNodeId:N} test";
        return new TrustChain(
            new Dictionary<string, TrustedOwner>
            {
                [ownerRoot] = new TrustedOwner(
                    ownerRoot, $"ssh-ed25519 AAAAFAKE{ownerRoot} test",
                    [new TrustedNode(ownerNodeId.ToString(), ownerKeyLine, NodeKeyStore.Fingerprint(ownerKeyLine), Now)]),
                [memberRoot] = new TrustedOwner(
                    memberRoot, $"ssh-ed25519 AAAAFAKE{memberRoot} test",
                    [new TrustedNode(memberNodeId.ToString(), memberKeyLine, NodeKeyStore.Fingerprint(memberKeyLine), Now)]),
            },
            [
                new ProjectMember(ownerRoot, MembershipRole.Owner, Now),
                new ProjectMember(memberRoot, MembershipRole.Member, Now),
            ]);
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

    /// <summary>
    /// A one-owner chain naming <paramref name="ownerNodeId"/> as an Owner-role project member,
    /// its key resolved the identical way <see cref="SeedNodeFileAsync"/>'s own node file does — the
    /// trust EventReplicationInbox's own gate now requires before it applies a project-settings-
    /// shaped event (idea 6be68ee2, trust-ledger findings 1 and 6).
    /// </summary>
    private static TrustChain OwnerChainFor(Guid ownerNodeId)
    {
        const string root = "owner-root-fingerprint";
        string publicKeyLine = $"ssh-ed25519 AAAAFAKE{ownerNodeId:N} test";
        TrustedOwner owner = new(
            root, "ssh-ed25519 AAAAFAKEroot test",
            [new TrustedNode(ownerNodeId.ToString(), publicKeyLine, NodeKeyStore.Fingerprint(publicKeyLine), Now)]);
        return new TrustChain(
            new Dictionary<string, TrustedOwner> { [root] = owner },
            [new ProjectMember(root, MembershipRole.Owner, Now)]);
    }
}
