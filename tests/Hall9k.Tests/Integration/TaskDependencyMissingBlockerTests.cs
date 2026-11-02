using FluentAssertions;
using Hall9k.Daemon.Purge;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// What the dependency sweep does with a blocker id that names no task on this node. A dependency
/// edge records no project for its blocker and a purge deletes the blocker project's own record,
/// so an unseen project is indistinguishable from a purged one; the only evidence the sweep
/// accepts is this node's own purge record (<see cref="PurgedTaskRecord"/>).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskDependencyMissingBlockerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Every way a blocker this node has no record of can look, none of which is evidence of a purge
    /// here: a blocker in another project, one in the dependent's own project, and one whose failure
    /// and recovery already replicated in from a node that did purge it and a node that could see it.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_blocker_this_node_has_no_record_of_leaves_the_dependent_blocked_and_records_nothing(
        bool sameProjectAsDependent, bool failureAndRecoveryAlreadyReplicated)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        Guid dependentProjectId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, sameProjectAsDependent ? dependentProjectId : DomainId.New(), dependentProjectId, ownerId, cts.Token);

        if (failureAndRecoveryAlreadyReplicated)
        {
            await using IDocumentSession session = store.LightweightSession();
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            TaskDependencyFailed failed = TaskDecider.DependencyFailed(task, blockerId, "the blocker no longer exists", Now);
            session.Events.Append(dependentId, failed);
            task.Apply(failed);
            session.Events.Append(
                dependentId, TaskDecider.DependencyRecovered(task, blockerId, "the blocker is back", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        int eventsBefore = await EventCountAsync(store, dependentId, cts.Token);

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().BeEmpty("this node cannot tell the blocker was purged, so it records nothing");
        (await EventCountAsync(store, dependentId, cts.Token)).Should().Be(eventsBefore);
        await AssertBlockedWithNoFailureAsync(store, dependentId, expectFailureEvents: failureAndRecoveryAlreadyReplicated, cts.Token);
    }

    /// <summary>
    /// The second case is a purge that committed after the sweep took its clock reading: the purge
    /// record is dated later than the sweep's <c>now</c>, yet the failure the sweep then records
    /// still counts as said, so the recovery that replicates back is not answered with a second one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_node_that_purged_the_blocker_records_the_failure_once_and_leaves_it_lifted_after_another_node_recovers_it(
        bool purgeCommittedAfterTheSweepStarted)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new PurgedTaskRecord(
                blockerId, DomainId.New(), purgeCommittedAfterTheSweepStarted ? Now.AddMinutes(1) : Now));
            await session.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation first = await SweepAsync(store, cts.Token);
        first.Parked.Should().ContainSingle(hold => hold.TaskId == dependentId);

        // A node that still has the blocker lifts the hold, and the event replicates back here.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.SetHeader(ReplicationEventHeaders.OriginNodeId, DomainId.New().ToString());
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            session.Events.Append(
                dependentId, TaskDecider.DependencyRecovered(task, blockerId, "the blocker is back on another node", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        int eventsBefore = await EventCountAsync(store, dependentId, cts.Token);

        DependencyReevaluation second = await SweepAsync(store, cts.Token);

        second.Parked.Should().BeEmpty("the failure was already recorded once, and the other node's recovery stands");
        (await EventCountAsync(store, dependentId, cts.Token)).Should().Be(eventsBefore);
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cts.Token))!;
        dependent.State.Should().Be(TaskState.Blocked);
        dependent.DependencyFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task A_node_that_purged_the_blocker_records_its_own_failure_when_the_only_earlier_one_was_replicated_from_another_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new PurgedTaskRecord(blockerId, DomainId.New(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Another node purged the same blocker first: its failure has this node's exact reason text
        // but arrives carrying the origin node header, and a node that still had the blocker then
        // lifted it. Produced by running the sweep in a session that stamps that header.
        await using (IDocumentSession foreign = store.LightweightSession())
        {
            foreign.SetHeader(ReplicationEventHeaders.OriginNodeId, DomainId.New().ToString());
            await TaskDependencyResolver.ForEveryBlockedTaskAsync(foreign, Now, cts.Token);
            await foreign.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            session.Events.Append(
                dependentId, TaskDecider.DependencyRecovered(task, blockerId, "the blocker is back on another node", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().ContainSingle(hold => hold.TaskId == dependentId);
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cts.Token))!;
        dependent.DependencyFailureReason.Should().NotBeNull("this node's own purge must be recorded, not assumed said by another node");
    }

    [Fact]
    public async Task A_node_that_purged_the_blocker_records_the_failure_when_the_only_earlier_local_one_replayed_as_a_no_op_on_a_claimed_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new PurgedTaskRecord(blockerId, DomainId.New(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // A foreign sweep supplies the reason text without counting as this node having spoken.
        await using (IDocumentSession foreign = store.LightweightSession())
        {
            foreign.SetHeader(ReplicationEventHeaders.OriginNodeId, DomainId.New().ToString());
            await TaskDependencyResolver.ForEveryBlockedTaskAsync(foreign, Now, cts.Token);
            await foreign.SaveChangesAsync(cts.Token);
        }

        string reason;
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            reason = task.RecordedDependencyFailure(blockerId) ?? throw new InvalidOperationException("the foreign sweep must have recorded a failure");
            session.Events.Append(
                dependentId, TaskDecider.DependencyRecovered(task, blockerId, "the blocker is back on another node", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // An acknowledged claim commits between a sweep's read and its unfenced append, so this
        // node's own failure lands on a Claimed task and replays as a no-op; the release then
        // returns the task to Blocked with nothing recorded.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            session.Events.Append(
                dependentId,
                new TaskClaimed(dependentId, DomainId.New(), ownerId, task.LeaseGeneration + 1, DomainId.New(), Now),
                new TaskDependencyFailed(dependentId, blockerId, reason, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            task.State.Should().Be(TaskState.Claimed);
            task.RecordedDependencyFailure(blockerId).Should().BeNull("the failure landed on a claimed task and was ignored");
            session.Events.Append(dependentId, TaskDecider.Requeue(task, RequeueReason.HumanRequested, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().ContainSingle(hold => hold.TaskId == dependentId);
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cts.Token))!;
        dependent.State.Should().Be(TaskState.Blocked);
        dependent.DependencyFailureReason.Should().NotBeNull("an ignored failure said nothing, so the purge is still unrecorded");
    }

    [Fact]
    public async Task A_node_that_purged_the_blocker_records_the_failure_when_the_only_earlier_local_one_was_recorded_before_the_purge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);

        // The behaviour from before the purge record existed: a missing blocker was failed outright,
        // here reproduced by a purge record dated well before the sweep, then recovered when the
        // blocker turned up.
        DateTimeOffset earlier = Now.AddDays(-2);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new PurgedTaskRecord(blockerId, DomainId.New(), earlier));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            await TaskDependencyResolver.ForEveryBlockedTaskAsync(session, earlier, cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            task.RecordedDependencyFailure(blockerId).Should().NotBeNull();
            session.Events.Append(
                dependentId, TaskDecider.DependencyRecovered(task, blockerId, "the blocker arrived", earlier));
            await session.SaveChangesAsync(cts.Token);
        }

        // The blocker's project is later purged here for real.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new PurgedTaskRecord(blockerId, DomainId.New(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().ContainSingle(hold => hold.TaskId == dependentId);
    }

    [Fact]
    public async Task A_purge_records_every_task_it_destroyed_in_the_same_save_and_the_record_is_a_document_not_an_event()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        Guid projectId = await SeedProjectAsync(store, ownerId, cts.Token);
        Guid[] taskIds = await SeedTasksAsync(store, projectId, ownerId, count: 2, cts.Token);
        Guid otherProjectId = await SeedProjectAsync(store, ownerId, cts.Token);
        Guid[] survivorIds = await SeedTasksAsync(store, otherProjectId, ownerId, count: 1, cts.Token);
        await SchedulePastDuePurgeAsync(store, projectId, ownerId, cts.Token);

        ProjectPurgeSweepResult result = await new ProjectPurgeEngine(store, NullLogger<ProjectPurgeEngine>.Instance)
            .SweepOnceAsync(cts.Token);

        result.ProjectsPurged.Should().Be(1);
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<PurgedTaskRecord> records = await query.Query<PurgedTaskRecord>().ToListAsync(cts.Token);
        records.Select(record => record.Id).Should().BeEquivalentTo(taskIds);
        records.Should().OnlyContain(record => record.ProjectId == projectId);
        records.Select(record => record.Id).Should().NotIntersectWith(survivorIds);

        // Nothing in the event log mentions the record, which is what keeps it on this node.
        IReadOnlyList<IEvent> events = await query.Events.QueryAllRawEvents().ToListAsync(cts.Token);
        events.Select(e => e.Data.GetType().Name).Should().NotContain(name => name.Contains("Purged", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_node_that_purged_the_blocker_records_the_failure_again_after_it_recovered_the_hold_itself_and_the_blocker_vanished_again(
        bool purgeRecordRefreshed)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new PurgedTaskRecord(blockerId, DomainId.New(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        (await SweepAsync(store, cts.Token)).Parked.Should().ContainSingle(hold => hold.TaskId == dependentId);

        // The project is rejoined and the blocker is alive here again, so this node's own sweep
        // lifts the hold. No origin header: it is this node's recovery, not a replicated one.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(dependentId, token: cts.Token)
                ?? throw new InvalidOperationException("seeded task must exist");
            session.Events.Append(
                dependentId, TaskDecider.DependencyRecovered(task, blockerId, "the blocker is back here", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // Then the project is purged here a second time. A real purge writes a fresh record with a
        // later date; the false case keeps the old record, so only the recovery can tell it apart.
        if (purgeRecordRefreshed)
        {
            await using IDocumentSession session = store.LightweightSession();
            session.Store(new PurgedTaskRecord(blockerId, DomainId.New(), Now.AddHours(1)));
            await session.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().ContainSingle(hold => hold.TaskId == dependentId);
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cts.Token))!;
        dependent.DependencyFailureReason.Should().NotBeNull("the second purge is a new thing to say");
    }

    private static async Task<DependencyReevaluation> SweepAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await TaskDependencyResolver.ForEveryBlockedTaskAsync(session, Now, cancellationToken);
    }

    private static async Task AssertBlockedWithNoFailureAsync(
        IDocumentStore store, Guid dependentId, bool expectFailureEvents, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cancellationToken))!;
        dependent.State.Should().Be(TaskState.Blocked);
        dependent.DependencyFailureReason.Should().BeNull("a blocker this node never purged proves nothing about it");
        IReadOnlyList<IEvent> events = await query.Events.FetchStreamAsync(dependentId, token: cancellationToken);
        events.Select(e => e.Data).OfType<TaskDependencyFailed>().Any().Should().Be(expectFailureEvents);
    }

    private static async Task<int> EventCountAsync(IDocumentStore store, Guid streamId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        return (await query.Events.FetchStreamAsync(streamId, token: cancellationToken)).Count;
    }

    /// <summary>
    /// A dependent blocked on a task that existed long enough to be declared and is not here: the
    /// blocker's stream and rows are removed by hand, which is exactly what a node that never
    /// received them looks like, and deliberately leaves no <see cref="PurgedTaskRecord"/>.
    /// </summary>
    private static async Task<(Guid BlockerId, Guid DependentId)> SeedDependentOfUnseenBlockerAsync(
        IDocumentStore store, Guid blockerProjectId, Guid dependentProjectId, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid blockerId = (await SeedTasksAsync(store, blockerProjectId, ownerId, count: 1, cancellationToken))[0];
        Guid dependentId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            dependentId, dependentProjectId, "a task blocked on a task this node cannot see", ["done"], TaskType.Chore,
            null, null, null, Now, ownerId, blockedBy: [blockerId]);

        await using IDocumentSession session = store.LightweightSession();
        TaskDependencyGraph graph = await TaskSeed.DependencyGraphAsync(session, [blockerId], cancellationToken);
        session.Events.StartStream<TaskAggregate>(dependentId, TaskSeed.Dispatchable(added, ownerId, Now, graph));
        session.QueueSqlCommand("delete from mt_events where stream_id = ?", blockerId);
        session.QueueSqlCommand("delete from mt_streams where id = ?", blockerId);
        session.QueueSqlCommand("delete from mt_doc_taskdetails where id = ?", blockerId);
        session.QueueSqlCommand("delete from mt_doc_tasklistitem where id = ?", blockerId);
        await session.SaveChangesAsync(cancellationToken);
        return (blockerId, dependentId);
    }

    private static async Task<Guid> SeedProjectAsync(IDocumentStore store, Guid ownerId, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        ProjectRegistered registered = ProjectDecider.Register(
            id, ownerId, DomainId.New(), $"project-{id:N}", "/repos/p.git", null, "main", Now);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(id, registered);
        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    private static async Task<Guid[]> SeedTasksAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, int count, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => DomainId.New())];
        await using IDocumentSession session = store.LightweightSession();
        for (int index = 0; index < ids.Length; index++)
        {
            session.Events.StartStream<TaskAggregate>(ids[index], TaskSeed.Dispatchable(
                TaskDecider.Add(
                    ids[index], projectId, $"Task {index}", ["done"], TaskType.Chore,
                    null, null, null, Now.AddSeconds(index), ownerId),
                ownerId, Now.AddSeconds(index)));
        }

        await session.SaveChangesAsync(cancellationToken);
        return ids;
    }

    private static async Task SchedulePastDuePurgeAsync(
        IDocumentStore store, Guid projectId, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectAggregate aggregate = await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken)
            ?? throw new InvalidOperationException("seeded project must exist");
        ProjectArchived archived = ProjectDecider.Archive(aggregate, null, Now, ownerId);
        session.Events.Append(projectId, archived);
        aggregate.Apply(archived);
        session.Events.Append(
            projectId, ProjectDecider.SchedulePurge(aggregate, Now, ownerId, gracePeriod: TimeSpan.FromHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
    }
}
