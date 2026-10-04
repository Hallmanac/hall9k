using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A dequeue, a <c>release --unassign</c> and a pre-flight park mark themselves with
/// <c>KeepsAssignee</c>, and an event stored without that key replays as it always did, clearing the
/// assignee. Proven against stored rows, with the key stripped by hand, because constructing the
/// record without the parameter still serializes an explicit false rather than the missing key a
/// stream written before this change carries.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskAssigneeKeptReplayTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Root = "kept-replay-root";

    [Fact]
    public async Task A_dequeue_keeps_the_assignee_and_the_same_event_without_the_key_replays_as_a_clearing_unassign()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cts.Token);
        Guid kept = await SeedQueuedThenAsync(node, cts.Token, keepsAssignee: true);
        Guid legacy = await SeedQueuedThenAsync(node, cts.Token, keepsAssignee: false);

        // The inline read models were computed when the row was saved, so this stream is saved with the
        // marker false (what a binary that predates it effectively wrote) and the key is then removed:
        // what the stored row says is the missing key, and the aggregate replays straight from it.
        await StripKeyAsync(legacy, cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        foreach ((Guid taskId, bool keeps) in new[] { (kept, true), (legacy, false) })
        {
            TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskListItem list = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;

            task.State.Should().Be(TaskState.Published);
            task.AssignedOwnerId.Should().BeNull("the go stopped either way");
            list.AssignedOwnerId.Should().BeNull();
            (task.AssigneeOwnerId is not null).Should().Be(keeps, "aggregate");
            (list.AssigneeOwnerId is not null).Should().Be(keeps, "board row");
            (details.AssigneeOwnerId is not null).Should().Be(keeps, "task show");
        }

        IReadOnlyList<IEvent> events = await query.Events.FetchStreamAsync(legacy, token: cts.Token);
        events.OfType<IEvent<TaskUnassigned>>().Single().Data.KeepsAssignee.Should().BeFalse(
            "an event stored without the key replays with the constructor default, which clears the assignee");
    }

    private async Task<Guid> SeedQueuedThenAsync(NodeContext node, CancellationToken cancellationToken, bool keepsAssignee)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, DomainId.New(), "A queued task that is taken out of the queue", acceptanceCriteria: ["it is dequeued"],
            TaskType.Feature, agentContext: null, constraints: null, externalReference: null,
            DateTimeOffset.UtcNow, node.OwnerId);
        TaskPublished published = new(taskId, DateTimeOffset.UtcNow, node.OwnerId);
        TaskAssigned assigned = new(
            taskId, node.OwnerId, UnmetDependencies: [], DateTimeOffset.UtcNow, node.OwnerId, Root);
        TaskUnassigned dequeued = new(taskId, null, DateTimeOffset.UtcNow, node.OwnerId, KeepsAssignee: keepsAssignee);

        session.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, dequeued);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task StripKeyAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand strip = new(
            "update public.mt_events set data = data - 'keepsAssignee' where stream_id = @streamId and type = 'task_unassigned'",
            connection);
        strip.Parameters.AddWithValue("streamId", taskId);
        (await strip.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1, "exactly the one TaskUnassigned row is stripped");
    }
}
