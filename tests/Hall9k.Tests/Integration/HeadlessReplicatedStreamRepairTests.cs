using FluentAssertions;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The fix for a task, idea, epic or run stream whose own genesis event never arrived before this
/// build's replication guard existed (<see cref="HeadlessReplicatedStreamRepair"/>). The corrupted
/// shape is built directly, the way it actually exists on a node an earlier build already produced
/// it on: a local stream started from a tail event (never a genesis), with the replication headers
/// <c>EventReplicationInbox.ApplyAsync</c> stamps and the <see cref="ReplicatedEventRecord"/> dedupe
/// rows it leaves behind — never through a real git ledger or GitHub (memory: no-git-integration-tests).
/// <para>
/// This tier is what proves the parts that only exist against a real store: the selection actually
/// finds a partial stream by reading every stream's own version 1 out of the event store, and the
/// stream id is genuinely freed in <c>mt_streams</c> so a later genesis can start it. The judgment
/// itself, meaning which streams qualify and what is held and what is dropped, is decided by a pure planner
/// with its own database-free tests (<c>Hall9k.Tests.Domain.PartialReplicatedStreamRepairTests</c>).
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class HeadlessReplicatedStreamRepairTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_partial_task_stream_is_repaired_its_records_re_held_and_its_stream_freed_for_a_real_genesis()
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

        HeadlessReplicatedStreamRepair.Report report =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);

        report.Skipped.Select(skipped => skipped.StreamId).Should().NotContain(taskId);
        report.Repaired.Select(plan => plan.StreamId).Should().Contain(taskId);
        report.Repaired.Single(plan => plan.StreamId == taskId).Aggregate
            .Should().Be(ReplicatedAggregate.Task);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().BeNull("the partial document is gone");
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
    /// The 13d6b371 shape, and the reason the v0.10.20 pass walked away from it: this node's own
    /// dispatcher claimed the phantom, so the stream carries native events with no origin headers,
    /// and that pass refused a stream outright the moment one event lacked them. The lease that
    /// claim left behind has held one of this node's run slots ever since, because the run it named
    /// never started a stream for the lease sweep to find.
    /// </summary>
    [Fact]
    public async Task A_partial_task_stream_the_dispatcher_claimed_is_repaired_with_the_claim_dropped_and_its_lease_deleted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        Guid nodeId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid dependencyId = DomainId.New();
        Guid returnedOriginEventId = DomainId.New();
        Guid abandonedRunId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamAction start = session.Events.StartStream(
                taskId, new TaskReturnedToDraft(taskId, "held for redesign", Now, ownerId));
            StampReplicationHeaders(start.Events[^1], senderNodeId, returnedOriginEventId, originSequence: 48028);
            session.Store(new ReplicatedEventRecord
            {
                Id = returnedOriginEventId, StreamId = taskId, ProjectId = projectId, AppliedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        // This node's own dispatcher, appending natively onto the phantom: no origin headers, no
        // dedupe row, and a TaskLease the heartbeat service keeps alive forever.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(
                taskId,
                new TaskDependencyCompleted(taskId, dependencyId, [], Now.AddHours(2)),
                new TaskClaimed(taskId, nodeId, ownerId, 1, abandonedRunId, Now.AddHours(2)));
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = nodeId, LeaseGeneration = 1, HeartbeatAt = Now.AddHours(9),
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
                TaskState.Claimed, "which is what h9k task show reads back: a phantom task stuck Working");
        }

        HeadlessReplicatedStreamRepair.Report report =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);

        PartialReplicatedStreamRepairPlan plan = report.Repaired.Single(repaired => repaired.StreamId == taskId);
        plan.DroppedNativeEventTypes.Should().Equal(nameof(TaskDependencyCompleted), nameof(TaskClaimed));
        plan.ClaimedRunId.Should().Be(abandonedRunId);
        plan.DeletesTaskLease.Should().BeTrue();
        plan.Held.Should().ContainSingle().Which.Id.Should().Be(returnedOriginEventId);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull(
                "a lease with no run counts as a live slot, so it goes with the claim it belonged to");
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().BeNull();
            (await query.Events.FetchStreamStateAsync(taskId, cts.Token)).Should().BeNull();
            (await query.Query<HeldReplicatedEventRecord>()
                .Where(record => record.StreamId == taskId)
                .CountAsync(cts.Token))
                .Should().Be(1, "only the replicated event is held; the native claim is dropped");
        }
    }

    /// <summary>The three partial streams actually sitting on this node are runs, and the
    /// v0.10.20 selection never queried a run document at all, so it could not have found one
    /// however plainly the events said so.</summary>
    [Fact]
    public async Task A_partial_run_stream_is_repaired_and_its_run_documents_removed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        Guid tokensOriginEventId = DomainId.New();
        Guid completedOriginEventId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamAction start = session.Events.StartStream(
                runId, new TokensRecorded(runId, 118, 4_200, 0.42m, Now, 8_239_942, 196_080, AgentModel.Unknown));
            StampReplicationHeaders(start.Events[^1], senderNodeId, tokensOriginEventId, originSequence: 91_000);
            StreamAction append = session.Events.Append(runId, new RunCompleted(runId, Now.AddMinutes(1)));
            StampReplicationHeaders(append.Events[^1], senderNodeId, completedOriginEventId, originSequence: 91_001);
            session.Store(new ReplicatedEventRecord
            {
                Id = tokensOriginEventId, StreamId = runId, ProjectId = projectId, AppliedAt = Now,
            });
            session.Store(new ReplicatedEventRecord
            {
                Id = completedOriginEventId, StreamId = runId, ProjectId = projectId, AppliedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunListItem>(runId, cts.Token))!.TaskId.Should().Be(
                Guid.Empty, "a run whose parent task never arrived has no task id to show");
        }

        HeadlessReplicatedStreamRepair.Report report =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);

        report.Repaired.Single(plan => plan.StreamId == runId).Aggregate.Should().Be(ReplicatedAggregate.Run);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunListItem>(runId, cts.Token)).Should().BeNull();
            (await query.LoadAsync<RunDetails>(runId, cts.Token)).Should().BeNull();
            (await query.Events.FetchStreamStateAsync(runId, cts.Token)).Should().BeNull();
            (await query.Query<HeldReplicatedEventRecord>()
                .Where(record => record.StreamId == runId)
                .CountAsync(cts.Token))
                .Should().Be(2);
        }
    }

    // The project-lifecycle exclusion is a pure rule in PartialReplicatedStreamRules, and
    // PartialReplicatedStreamRepairTests.A_phantom_project_lifecycle_stream_is_not_a_candidate
    // drives it directly. The only seam a copy here would add is "a non-candidate leaves the store
    // untouched through RunAsync", which the next test already proves against this same real store
    // (independent pre-PR review, cycle 1, adversarial lens).

    /// <summary>
    /// The never-guess boundary: a native, non-replicated stream's own first event carries none of
    /// the origin headers the selection reads, so a task this install genuinely created itself is
    /// never selected at all rather than having its real history discarded on a guess.
    /// </summary>
    [Fact]
    public async Task A_stream_with_no_replication_headers_is_never_touched_even_if_it_starts_mid_story()
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
            // prove the repair passes over rather than guesses at.
            session.Events.StartStream(taskId, completed);
            await session.SaveChangesAsync(cts.Token);
        }

        HeadlessReplicatedStreamRepair.Report report =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);

        report.Repaired.Select(plan => plan.StreamId).Should().NotContain(taskId);
        report.Skipped.Select(skipped => skipped.StreamId).Should().NotContain(taskId);

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token)).Should().NotBeNull(
                "left exactly as it is rather than discarded on a guess");
            (await query.Events.FetchStreamStateAsync(taskId, cts.Token)).Should().NotBeNull();
        }
    }

    /// <summary>A partial stream carrying a native event outside the three the dispatcher can reach
    /// a phantom through is reported by id with its reason and left alone.</summary>
    [Fact]
    public async Task A_partial_stream_carrying_an_unexpected_native_event_is_reported_and_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        Guid assignedOriginEventId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamAction start = session.Events.StartStream(
                taskId, new TaskAssigned(taskId, DomainId.New(), [], Now, DomainId.New()));
            StampReplicationHeaders(start.Events[^1], senderNodeId, assignedOriginEventId, originSequence: 55);
            session.Store(new ReplicatedEventRecord
            {
                Id = assignedOriginEventId, StreamId = taskId, ProjectId = projectId, AppliedAt = Now,
            });
            session.Events.Append(taskId, new TaskAbandoned(taskId, "given up on", Now.AddHours(1), DomainId.New()));
            await session.SaveChangesAsync(cts.Token);
        }

        HeadlessReplicatedStreamRepair.Report report =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);

        report.Repaired.Select(plan => plan.StreamId).Should().NotContain(taskId);
        report.Skipped.Should().ContainSingle(skipped => skipped.StreamId == taskId)
            .Which.Reason.Should().Contain(nameof(TaskAbandoned));

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.Events.FetchStreamStateAsync(taskId, cts.Token)).Should().NotBeNull(
                "a stream nothing can safely explain stays exactly as it is");
            (await query.LoadAsync<ReplicatedEventRecord>(assignedOriginEventId, cts.Token)).Should().NotBeNull();
        }
    }

    /// <summary>Idempotence against a real store: the first pass frees every stream it can, so the
    /// second finds nothing left to free.</summary>
    [Fact]
    public async Task A_second_pass_right_after_the_first_repairs_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        Guid publishedOriginEventId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamAction start = session.Events.StartStream(taskId, new TaskPublished(taskId, Now, DomainId.New()));
            StampReplicationHeaders(start.Events[^1], senderNodeId, publishedOriginEventId, originSequence: 3);
            session.Store(new ReplicatedEventRecord
            {
                Id = publishedOriginEventId, StreamId = taskId, ProjectId = projectId, AppliedAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        HeadlessReplicatedStreamRepair.Report first =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);
        first.Repaired.Select(plan => plan.StreamId).Should().Contain(taskId);

        HeadlessReplicatedStreamRepair.Report second =
            await HeadlessReplicatedStreamRepair.RunAsync(store, Now, cts.Token);
        second.Repaired.Select(plan => plan.StreamId).Should().NotContain(
            taskId, "the stream it freed on the first pass is not there to find on the second");
        second.Skipped.Select(skipped => skipped.StreamId).Should().NotContain(taskId);
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
