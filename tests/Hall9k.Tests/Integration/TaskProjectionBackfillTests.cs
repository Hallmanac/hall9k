using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The migration every task-projection shape change needs. The projections are Inline, so a
/// task that stopped receiving events before a change still carries the older document shape,
/// and each reader of the new field mistakes absent-because-old for nothing-was-recorded: the
/// lifecycle split (Decisions Log #34) left pre-split documents with no assignedOwnerId key,
/// which the claim filter reads as nobody's work, and the dead-blocker recovery (Decisions Log
/// #61) left pre-recovery documents with no deadDependencyReasons map, which the surviving-hold
/// fallback reads as no blocker having said anything. An older document is simulated the way it
/// actually exists: the current projection writes the document, then the key is stripped back
/// off it in the database.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskProjectionBackfillTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_task_projected_before_the_lifecycle_split_is_re_projected_and_claimable_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                Add(taskId, "Queued long before the split"), node.OwnerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await StripAssignedOwnerAsync(taskId, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty(
            "this is the defect: a document without the key never matches the claim filter, "
            + "and an unclaimable task looks exactly like an idle queue");

        IReadOnlyList<Guid> rebuilt = await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token);

        rebuilt.Should().Equal(taskId);
        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            row.AssignedOwnerId.Should().Be(node.OwnerId, "the events always said whose work it is");
            row.State.Should().Be(TaskState.Queued, "replaying the stream reproduces the rest of the document too");

            TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            details.AssignedOwnerId.Should().Be(node.OwnerId, "h9k task show reads this one");
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(taskId);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem row = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            row.State.Should().Be(
                TaskState.Claimed,
                "the repair must leave the document where ordinary Inline projection left it: a "
                + "rebuild that stored it a version ahead of its stream would drop the very next "
                + "event, and the claim it just accepted would never reach the board");
        }
    }

    [Fact]
    public async Task A_task_that_genuinely_has_no_owner_is_not_re_projected_on_every_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid draft = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(draft, Add(draft, "Still being written"));
            await seed.SaveChangesAsync(cts.Token);
        }

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().BeEmpty(
            "an unassigned draft stores assignedOwnerId as an explicit null, which is what makes "
            + "an absent key a sound marker for the pre-split shape — and the backfill self-terminating");
    }

    [Fact]
    public async Task A_dependent_projected_before_the_recovery_keeps_the_hold_its_stream_still_records()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid dependent = DomainId.New();
        await SeedDependentHeldByTwoDeadBlockersAsync(store, dependent, first, second, cts.Token);

        // What a document written before Decisions Log #61 looks like: dead blockers listed and
        // a reason displayed, but no per-blocker map for the surviving-hold fallback to read.
        await StripKeyAsync(dependent, "deadDependencyReasons", cts.Token);

        IReadOnlyList<Guid> rebuilt = await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token);
        rebuilt.Should().Equal(dependent);

        // The event the repair exists for, and the one a rebuild that left the document a
        // version ahead of its stream would silently drop.
        await using (IDocumentSession recover = store.LightweightSession())
        {
            TaskAggregate held = (await recover.Events.AggregateStreamAsync<TaskAggregate>(
                dependent, token: cts.Token))!;
            recover.Events.Append(dependent, TaskDecider.DependencyRecovered(
                held, first, "it is back in the pipeline", Now.AddHours(1)));
            await recover.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.DeadDependencies.Should().Equal([second], "only the recovered blocker stops counting");
        row.DependencyFailureReason.Should().Be(
            "the second blocker will never close out",
            "the surviving hold is what the stream records, not silence about a blocker still dead");

        TaskDetails details = (await query.LoadAsync<TaskDetails>(dependent, cts.Token))!;
        details.DependencyFailureReason.Should().Be(
            row.DependencyFailureReason, "h9k task show reads the same surviving hold");
    }

    [Fact]
    public async Task A_dependent_already_carrying_its_recorded_reasons_is_not_re_projected_on_every_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid dependent = DomainId.New();
        await SeedDependentHeldByTwoDeadBlockersAsync(store, dependent, first, second, cts.Token);

        (await TaskLifecycleProjectionBackfill.RunAsync(store, cts.Token)).Should().BeEmpty(
            "the current projections always write deadDependencyReasons — populated here, and an "
            + "explicit empty map on a task with nothing dead — which is what makes an absent key "
            + "a sound marker for the pre-recovery shape, and the backfill self-terminating");
    }

    /// <summary>
    /// A task Blocked on two blockers, both recorded dead, written through the same deciders the
    /// resolver uses. The blockers themselves need no streams here: what is under test is the
    /// dependent's document, and the records that hold it live on the dependent's own stream.
    /// </summary>
    private static async Task SeedDependentHeldByTwoDeadBlockersAsync(
        IDocumentStore store, Guid dependent, Guid first, Guid second, CancellationToken cancellationToken)
    {
        TaskDependencyGraph graph = new([
            Blocker(first, "The first blocker"),
            Blocker(second, "The second blocker"),
        ]);

        TaskAdded added = TaskDecider.Add(
            dependent, DomainId.New(), "Waits on two", ["it is done"], TaskType.Chore,
            null, null, null, Now, DomainId.New(), blockedBy: [first, second]);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, DomainId.New(), Now, graph);

        TaskDependencyFailed firstDied = TaskDecider.DependencyFailed(
            task, first, "the first blocker will never close out", Now);
        task.Apply(firstDied);
        TaskDependencyFailed secondDied = TaskDecider.DependencyFailed(
            task, second, "the second blocker will never close out", Now);

        await using IDocumentSession seed = store.LightweightSession();
        seed.Events.StartStream<TaskAggregate>(dependent, [.. lifecycle, firstDied, secondDied]);
        await seed.SaveChangesAsync(cancellationToken);
    }

    private static TaskDependency Blocker(Guid id, string objective) => new(
        id, objective, TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
        PullRequestUrl: null, []);

    /// <summary>Turns the stored documents back into the shape an older projection wrote.</summary>
    private async Task StripAssignedOwnerAsync(Guid taskId, CancellationToken cancellationToken) =>
        await StripKeyAsync(taskId, "assignedOwnerId", cancellationToken);

    private async Task StripKeyAsync(Guid taskId, string key, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string table in (string[])["mt_doc_tasklistitem", "mt_doc_taskdetails"])
        {
            await using NpgsqlCommand command = new(
                $"update {table} set data = data - '{key}' where id = @id", connection);
            command.Parameters.AddWithValue("id", taskId);
            (await command.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }

    private static TaskAdded Add(Guid id, string objective) => TaskDecider.Add(
        id, DomainId.New(), objective, ["it is done"], TaskType.Chore,
        null, null, null, Now, DomainId.New());

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private DispatchEngine NewEngine(DocumentStore store, NodeContext node) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            Options.Create(new DaemonOptions { MaxConcurrentRuns = 10, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);
}
