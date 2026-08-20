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
/// The migration the lifecycle split needs (Decisions Log #34). The projections are Inline, so
/// a task that stopped receiving events before the split still carries the pre-split document
/// shape — no assignedOwnerId key at all — and the claim filter reads that as nobody's work.
/// A pre-split document is simulated the way it actually exists: the current projection writes
/// the document, then the key is stripped back off it in the database.
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

    /// <summary>Turns the stored documents back into the shape the pre-split projections wrote.</summary>
    private async Task StripAssignedOwnerAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string table in (string[])["mt_doc_tasklistitem", "mt_doc_taskdetails"])
        {
            await using NpgsqlCommand command = new(
                $"update {table} set data = data - 'assignedOwnerId' where id = @id", connection);
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
