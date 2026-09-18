using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k task handoff (idea 202383dc, item 3), through the transport seam: no test here touches a
/// real repository or remote (Brian's 2026-09-13 testing rule) — <see cref="FakeLedger"/> stands in
/// for the project's own ledger and <see cref="InMemoryMessageTransport"/> for the outbox/inbox, and
/// one shared Postgres schema stands in for two separate nodes' own separate local databases, the
/// same convention <c>MessageTransportTests</c> and <c>TaskRecordIntegrationTests</c> already
/// establish. The CLI command itself hardcodes a real <c>GitLedger</c> (the same shape every other
/// record-writing command in this codebase does), so these tests drive the decider and
/// <see cref="TaskRecordPublication"/> directly — exactly what the command's own body does — rather
/// than touching that command.
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

    [Fact]
    public async Task The_event_travels_and_the_second_store_shows_the_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid nodeA = DomainId.New();
        Guid taskId = await SeedClaimedTaskAsync(store, ownerId, projectId, nodeA, cts.Token);
        await SeedProjectAsync(store, projectId, cts.Token);

        FakeLedger ledger = new();

        // The holding node leaves the note: the same append h9k task handoff performs, through
        // TaskDecider.LeaveHandoff directly.
        await using (IDocumentSession appendSession = store.LightweightSession())
        {
            TaskAggregate task = (await appendSession.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskHandoffNoted noted = TaskDecider.LeaveHandoff(
                task, "Migration script drafted but untested.", nodeA, "owner-a-fingerprint", Now);
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
                recordSession, updated, project, nodeA, "NODE-A", "owner-a-fingerprint", Now, ledger, Committer,
                SigningKey, cts.Token);
        }

        // The second store: a fresh session re-aggregating this task's own stream, standing in for
        // a replicated node's own read of the identical events (idea 202383dc, M2a), and a fresh
        // read of the same ledger record every node in the project shares (A3a).
        await using IQuerySession secondStore = store.QuerySession();
        TaskAggregate replicated = (await secondStore.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        replicated.HandoffNote.Should().Be("Migration script drafted but untested.");
        replicated.HandoffNoteAuthorNodeId.Should().Be(nodeA);
        replicated.HandoffNoteAuthorOwnerRootFingerprint.Should().Be("owner-a-fingerprint");
        replicated.HandoffNoteAt.Should().Be(Now);

        LedgerFile stored = await ledger.ReadAsync(
            RepositoryPath, LedgerRefRegistry.Records.RefspecSource, LedgerRefRegistry.RecordPath(taskId), cts.Token);
        stored.Exists.Should().BeTrue();
        TaskRecord record = TaskRecord.TryParse(stored.Content)!;
        record.HandoffNote.Should().NotBeNull("the record writer composes the note fresh, like every other field");
        record.HandoffNote!.Note.Should().Be("Migration script drafted but untested.");
        record.HandoffNote.AuthorNodeId.Should().Be(nodeA);
        record.HandoffNote.AuthorOwnerFingerprint.Should().Be("owner-a-fingerprint");
    }

    [Fact]
    public async Task A_non_holder_is_refused_and_the_holder_is_named()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid holderNode = DomainId.New();
        Guid otherNode = DomainId.New();
        Guid taskId = await SeedClaimedTaskAsync(store, ownerId, projectId, holderNode, cts.Token);

        await using IQuerySession session = store.QuerySession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;

        Action act = () => TaskDecider.LeaveHandoff(task, "Not mine to leave.", otherNode, "owner-b-fingerprint", Now);

        act.Should().Throw<DomainConflictException>()
            .WithMessage($"*{DomainId.Short(holderNode)}*");
    }

    [Fact]
    public async Task The_nudge_arrives_as_an_unread_message_kind_handoff()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid nodeA = DomainId.New();
        Guid nodeB = DomainId.New();
        Guid taskId = DomainId.New();

        FakeLedger ledger = new();
        await SeedNodeFileAsync(ledger, nodeA, cts.Token);
        await SeedNodeFileAsync(ledger, nodeB, cts.Token);
        InMemoryMessageTransport transport = new(ledger);
        MessageOutbox outbox = new(transport);
        MessageInbox inbox = new(transport);

        await using (IDocumentSession sendSession = postgres.Store.LightweightSession())
        {
            // Never the note's own text: the nudge carries no payload beyond pointing at the task —
            // the note itself already travelled on the task's own event stream above.
            await MessageOutbox.QueueAsync(
                sendSession, nodeA, projectId, "owner-a-fingerprint", MessageAudience.Project,
                about: taskId.ToString(), MessageKind.Handoff,
                $"Task {taskId} got a handoff note — see h9k task show {taskId}.", Now, cts.Token);
            await outbox.FlushAsync(
                sendSession, RepositoryPath, nodeA, projectId, "shared-project-key", adoptUnassigned: false,
                Committer, SigningKey, Now, cts.Token);
        }

        MessageInboxSweepResult sweep;
        await using (IDocumentSession readSession = postgres.Store.LightweightSession())
        {
            sweep = await inbox.ReadFromAsync(
                readSession, RepositoryPath, nodeA, projectId, nodeB, "owner-b-fingerprint", Now.AddSeconds(1),
                cancellationToken: cts.Token);
        }

        sweep.EnvelopesStored.Should().Be(1, "a handoff-kind envelope is stored like any other ordinary message");

        await using IQuerySession assertSession = postgres.Store.QuerySession();
        MessageDetails? received = await assertSession.LoadAsync<MessageDetails>(
            MessageStreamId.ForMessage(nodeA, projectId, 1), cts.Token);
        received.Should().NotBeNull();
        received!.Kind.Should().Be(MessageKind.Handoff.Value);
        received.About.Should().Be(taskId.ToString());
        received.Body.Should().NotContain("Migration script", "the message is the nudge only — the note travels on the event, not here");
        received.ReceivedAt.Should().NotBeNull();
        received.HandledAt.Should().BeNull("unread — this is what h9k status's own unread count reads");
    }

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
