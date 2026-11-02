using FluentAssertions;
using Hall9k.Daemon.Purge;
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

    [Fact]
    public async Task A_blocker_in_another_project_this_node_has_no_record_of_leaves_the_dependent_blocked_with_nothing_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().BeEmpty();
        await AssertBlockedWithNoFailureAsync(store, dependentId, cts.Token);
    }

    [Fact]
    public async Task A_blocker_in_the_dependents_own_project_this_node_has_no_record_of_leaves_it_blocked_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        (_, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(store, projectId, projectId, ownerId, cts.Token);

        DependencyReevaluation reevaluation = await SweepAsync(store, cts.Token);

        reevaluation.Parked.Should().BeEmpty();
        await AssertBlockedWithNoFailureAsync(store, dependentId, cts.Token);
    }

    [Fact]
    public async Task A_node_with_no_purge_record_does_not_record_the_failure_again_after_another_node_lifted_the_hold()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        IDocumentStore store = postgres.Store;
        await store.Advanced.ResetAllData(cts.Token);
        Guid ownerId = DomainId.New();
        (Guid blockerId, Guid dependentId) = await SeedDependentOfUnseenBlockerAsync(
            store, DomainId.New(), DomainId.New(), ownerId, cts.Token);

        // The two replicated events a node that purged the blocker and then a node that could see
        // it would have written: a failure, then the recovery that lifted it.
        await using (IDocumentSession session = store.LightweightSession())
        {
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

        reevaluation.Parked.Should().BeEmpty("this node cannot tell the blocker was purged, so it does not flip the hold again");
        (await EventCountAsync(store, dependentId, cts.Token)).Should().Be(eventsBefore);
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cts.Token))!;
        dependent.State.Should().Be(TaskState.Blocked);
        dependent.DependencyFailureReason.Should().BeNull();
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

    private static async Task<DependencyReevaluation> SweepAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await TaskDependencyResolver.ForEveryBlockedTaskAsync(session, Now, cancellationToken);
    }

    private static async Task AssertBlockedWithNoFailureAsync(
        IDocumentStore store, Guid dependentId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        TaskDetails dependent = (await query.LoadAsync<TaskDetails>(dependentId, cancellationToken))!;
        dependent.State.Should().Be(TaskState.Blocked);
        dependent.DependencyFailureReason.Should().BeNull("a blocker this node never purged proves nothing about it");
        IReadOnlyList<IEvent> events = await query.Events.FetchStreamAsync(dependentId, token: cancellationToken);
        events.Select(e => e.Data).OfType<TaskDependencyFailed>().Should().BeEmpty();
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
