using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k task handoff (idea 202383dc, item 3), through the transport seam: no test here touches a
/// real repository or remote (Brian's 2026-09-13 testing rule) — <see cref="FakeLedger"/> stands in
/// for the project's own ledger and <see cref="InMemoryMessageTransport"/> for the outbox/inbox. The
/// CLI command itself hardcodes a real <c>GitLedger</c> (the same shape every other record-writing
/// command in this codebase does), so these tests drive the decider and
/// <see cref="TaskRecordPublication"/> directly — exactly what the command's own body does — rather
/// than touching that command.
/// <para>
/// The one test below opens a genuinely second store: it is the only place in this class that
/// claims an event crossed to another node, so it has to actually run that event through
/// <see cref="EventReplicationOutbox"/>/<see cref="EventReplicationInbox"/> into a second
/// <see cref="DocumentStore"/> in its own schema, the same "a test that genuinely needs a store of
/// its own" shape <c>EventReplicationTests</c> already establishes (independent pre-PR review, cycle
/// 1, conformance lens: a shared-schema re-aggregation of the same rows it just wrote never touches
/// the replication seam at all). The class's other two tests, which claimed nothing about crossing
/// nodes, were dropped as duplicates of coverage <c>MessageTransportTests</c> and
/// <c>TaskRecordIntegrationTests</c> already establish on the single shared Postgres schema
/// (independent pre-PR review, cycle 2).
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskHandoffIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private const string RepositoryPath = "/repos/handoff-test";
    private static readonly LedgerCommitter Committer = new("Handoff Test", "handoff-test@hall9k.local");
    // FakeLedger never reads this path — it only checks a signing key is present, the same gate
    // GitLedger itself enforces — so a real key on disk is never needed here.
    private static readonly LedgerSigningKey SigningKey = new("/fake/signing-key-never-read-by-fakeledger");

    private DocumentStore OpenStoreB() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.DatabaseSchemaName = "task_handoff_node_b";
        opts.ConfigureHall9k(AutoCreate.All);
    });

    [Fact]
    public async Task The_event_replicates_and_the_second_store_shows_the_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid taskId = await SeedClaimedTaskAsync(store, ownerId, projectId, nodeA, cts.Token);
        await SeedProjectAsync(store, projectId, cts.Token);

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        EventReplicationOutbox replicationOutbox = new(new ReplicationProjectResolver());
        EventReplicationInbox replicationInbox = new(transport);
        MessageOutbox messageOutbox = new(transport);

        await using DocumentStore storeB = OpenStoreB();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<NodeAggregate>(nodeA, new NodeRegistered(nodeA, ownerId, "node-a", "macOS", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now, cts.Token);
        }

        // The holding node leaves the note after the switch-on point: the same append h9k task
        // handoff performs, through TaskDecider.LeaveHandoff directly.
        await using (IDocumentSession appendSession = store.LightweightSession())
        {
            TaskAggregate task = (await appendSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskHandoffNoted noted = TaskDecider.LeaveHandoff(
                task, "Migration script drafted but untested.", nodeA, "owner-a-fingerprint", Now.AddSeconds(1));
            appendSession.Events.Append(taskId, noted);
            await appendSession.SaveChangesAsync(cts.Token);
        }

        // The record writer renders the latest note into the ledger record — the same write
        // TaskHandoffCommand performs once the event has landed.
        await using (IDocumentSession recordSession = store.LightweightSession())
        {
            TaskAggregate updated = (await recordSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            ProjectDetails project = (await recordSession.LoadAsync<ProjectDetails>(projectId, cts.Token))!;
            await TaskRecordPublication.WriteAsync(
                recordSession, updated, project, nodeA, "NODE-A", "owner-a-fingerprint", Now.AddSeconds(1), ledger,
                Committer, SigningKey, cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            await replicationOutbox.QueuePendingAsync(session, nodeA, projectId, "owner-a-fingerprint", Now.AddSeconds(2), cts.Token);
            await messageOutbox.FlushAsync(
                session, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false, Committer,
                SigningKey, Now.AddSeconds(2), cts.Token);
        }

        // Node B: reads node A's outbox into its own, otherwise-empty store — a genuinely separate
        // Marten store in its own schema on the identical container, standing in for a replicated
        // node's own read of the identical events (idea 202383dc, M2a).
        await using (IDocumentSession session = storeB.LightweightSession())
        {
            EventReplicationReadResult read = await replicationInbox.ReadFromAsync(
                session, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(3),
                trustChain: null, cts.Token);
            read.SenderIgnored.Should().BeFalse();
            read.EventsApplied.Should().BeGreaterThan(0);
        }

        await using (IQuerySession session = storeB.QuerySession())
        {
            TaskAggregate replicated = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            replicated.HandoffNote.Should().Be("Migration script drafted but untested.");
            replicated.HandoffNoteAuthorNodeId.Should().Be(nodeA);
            replicated.HandoffNoteAuthorOwnerRootFingerprint.Should().Be("owner-a-fingerprint");
            replicated.HandoffNoteAt.Should().Be(Now.AddSeconds(1));
        }

        // The ledger record every node in the project shares (A3a) — written once, above, by
        // whichever node holds the task, and read back here the same way any node would.
        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token);
        stored.Exists.Should().BeTrue();
        TaskRecord record = TaskRecord.TryParse(stored.Content)!;
        record.HandoffNote.Should().NotBeNull("the record writer composes the note fresh, like every other field");
        record.HandoffNote!.Note.Should().Be("Migration script drafted but untested.");
        record.HandoffNote.AuthorNodeId.Should().Be(nodeA);
        record.HandoffNote.AuthorOwnerFingerprint.Should().Be("owner-a-fingerprint");
    }

    // The nudge's own transport round trip (a handoff-kind envelope arriving as an ordinary
    // unread message) is dropped here rather than kept as its own test (independent pre-PR
    // review, cycle 1, both lenses): the transport never special-cases a message kind, so
    // MessageTransportTests.Two_nodes_exchange_notes_in_both_directions (kind, stored body,
    // ReceivedAt) and MessageTransportTests.An_unrecognized_kind_is_stored_rather_than_refused (an
    // arbitrary kind string surviving the identical round trip) already prove every behavior this
    // test claimed, through the identical FakeLedger/InMemoryMessageTransport seam, for a
    // RECOGNIZED kind's cheaper case; MessageKindTests.Parse_RecognizesHandoff covers Handoff's own
    // parse. What this test's own body assertion claimed — that the command puts no note text in
    // the nudge — was tautological: it hand-wrote the same envelope body it then asserted against,
    // never driving TaskHandoffCommand's own nudge composition at all.

    // ── seeding ──────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid> SeedClaimedTaskAsync(
        DocumentStore store, Guid ownerId, Guid projectId, Guid nodeId, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, "Migrate the widgets table", ["Migration completes cleanly"], TaskType.Chore,
            agentContext: null, constraints: null, externalReference: null, Now, ownerId));
        await session.SaveChangesAsync(cancellationToken);

        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, ownerId));
        await session.SaveChangesAsync(cancellationToken);

        task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.Assign(task, ownerId, [], Now, ownerId));
        await session.SaveChangesAsync(cancellationToken);

        task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        session.Events.Append(taskId, TaskDecider.Claim(
            task, nodeId, ownerId, DomainId.New(), Now, ownerRootFingerprint: "owner-a-fingerprint"));
        await session.SaveChangesAsync(cancellationToken);

        return taskId;
    }

    private static async Task SeedProjectAsync(DocumentStore store, Guid projectId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new ProjectDetails
        {
            Id = projectId,
            Name = "handoff-test",
            RepositoryPath = RepositoryPath,
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedNodeFileAsync(FakeLedger ledger, Guid nodeId, CancellationToken cancellationToken)
    {
        string content = $"node_id: \"{nodeId}\"\npublic_key: \"ssh-ed25519 AAAAFAKE{nodeId:N} test\"\n";
        await ledger.WriteAsync(
            new LedgerWriteRequest(
                RepositoryPath, $"refs/hall9k/ledger/nodes/{nodeId}", $"nodes/{nodeId}/node.yaml", content,
                ExpectedBlobId: null, "seed node file", Committer, SigningKey),
            cancellationToken);
    }
}
