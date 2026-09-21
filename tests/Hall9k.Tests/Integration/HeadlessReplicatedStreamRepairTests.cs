using FluentAssertions;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The one-time fix for a task or idea whose own genesis event never arrived before this build's
/// replication guard existed (<see cref="HeadlessReplicatedStreamRepair"/>). The corrupted shape is
/// built directly, the way it actually exists on a Mac an earlier build already produced it on: a
/// local stream started from a tail event (never a genesis), with the replication headers
/// <c>EventReplicationInbox.ApplyAsync</c> stamps and the <see cref="ReplicatedEventRecord"/> dedupe
/// rows it leaves behind — never through a real git ledger or GitHub (memory: no-git-integration-tests).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class HeadlessReplicatedStreamRepairTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_headless_task_stream_is_repaired_its_records_re_held_and_its_stream_freed_for_a_real_genesis()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        Guid branchPushedOriginEventId = DomainId.New();
        Guid completedOriginEventId = DomainId.New();
        Guid runId = DomainId.New();

        TaskBranchPushed branchPushed = new(taskId, "task/tail-only", "def5678", Now.AddSeconds(1));
        TaskCompleted completed = new(taskId, runId, "https://github.com/x/y/pull/61", Now.AddSeconds(2));

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamAction start = session.Events.StartStream(taskId, branchPushed);
            StampReplicationHeaders(start.Events[^1], senderNodeId, branchPushedOriginEventId, originSequence: 10);
            session.Store(new ReplicatedEventRecord
            {
                Id = branchPushedOriginEventId, StreamId = taskId, ProjectId = projectId, AppliedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamAction append = session.Events.Append(taskId, completed);
            StampReplicationHeaders(append.Events[^1], senderNodeId, completedOriginEventId, originSequence: 11);
            session.Store(new ReplicatedEventRecord
            {
                Id = completedOriginEventId, StreamId = taskId, ProjectId = projectId, AppliedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        // The defect, confirmed before the repair runs: a needs-you row with no project.
        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            row.ProjectId.Should().Be(Guid.Empty);
            row.AddedAt.Should().Be(default);
        }

        IReadOnlyList<Guid> repaired = await HeadlessReplicatedStreamRepair.RunAsync(store, cts.Token);

        repaired.Should().Equal(taskId);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().BeNull("the headless document is gone");
            (await query.LoadAsync<TaskDetails>(taskId, cts.Token)).Should().BeNull();
            (await query.LoadAsync<ReplicatedEventRecord>(branchPushedOriginEventId, cts.Token)).Should().BeNull(
                "its dedupe row is gone too, so a future genesis is never blocked by it as already applied");
            (await query.LoadAsync<ReplicatedEventRecord>(completedOriginEventId, cts.Token)).Should().BeNull();

            IReadOnlyList<HeldReplicatedEventRecord> held = await query.Query<HeldReplicatedEventRecord>()
                .Where(record => record.StreamId == taskId)
                .ToListAsync(cts.Token);
            held.Should().HaveCount(2, "both tail records are preserved, never lost, only re-held");
            held.Should().Contain(record =>
                record.Id == branchPushedOriginEventId && record.ProjectId == projectId
                && record.SenderNodeId == senderNodeId && record.OriginSequence == 10);
            held.Should().Contain(record =>
                record.Id == completedOriginEventId && record.ProjectId == projectId && record.OriginSequence == 11);

            (await query.Events.FetchStreamStateAsync(taskId, cts.Token)).Should().BeNull(
                "the corrupted local stream itself is purged, freeing the id for a real genesis to start fresh");
        }

        // The freed stream can now genuinely start from its true genesis, exactly as if this
        // node had never received anything for it before — no hand SQL, and the sender's own
        // ledger was never touched to get here.
        TaskAdded added = new(
            taskId, projectId, "Ship it for real", ["it ships"], TaskType.Feature, null, null, null,
            Now, DomainId.New());
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            details.ProjectId.Should().Be(projectId);
            details.Objective.Should().Be("Ship it for real");
            details.AddedAt.Should().Be(Now);
        }
    }

    /// <summary>
    /// The never-guess boundary: a native, non-replicated stream carries none of the origin
    /// headers this repair reads, so a task this install genuinely created itself — never touched
    /// by replication at all — is left exactly as it is rather than having its real history
    /// discarded on a guess.
    /// </summary>
    [Fact]
    public async Task A_stream_with_no_replication_headers_is_never_touched_even_if_it_looks_headless()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        TaskCompleted completed = new(taskId, runId, "https://github.com/x/y/pull/1", Now);

        await using (IDocumentSession session = store.LightweightSession())
        {
            // No StartStream from a genesis, and no replication header stamped at all — this
            // build's own inbox never produces this shape (it holds instead), but a hand-rolled
            // native append onto a stream nothing ever started is the shape this test needs to
            // prove the repair refuses rather than guesses at.
            session.Events.StartStream(taskId, completed);
            await session.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<Guid> repaired = await HeadlessReplicatedStreamRepair.RunAsync(store, cts.Token);

        repaired.Should().BeEmpty("nothing here can be safely reconstructed as a held replicated record");

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().NotBeNull(
                "left exactly as it is rather than discarded on a guess");
            (await query.Events.FetchStreamStateAsync(taskId, cts.Token)).Should().NotBeNull();
        }
    }

    private static void StampReplicationHeaders(IEvent appended, Guid senderNodeId, Guid originEventId, long originSequence)
    {
        appended.SetHeader(ReplicationEventHeaders.OriginNodeId, senderNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint, "owner-a-fingerprint");
        appended.SetHeader(ReplicationEventHeaders.OriginEventId, originEventId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginSequence, originSequence.ToString());
        appended.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, senderNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.ReceivedAt, Now.ToString("O"));
    }
}
