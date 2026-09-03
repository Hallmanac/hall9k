using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="TaskLogInteractionCommand.AppendInteractionAsync"/> against a real store — the
/// store round trip <see cref="Hall9k.Tests.Cli.TaskLogInteractionCommandTests"/>'s own doc
/// comment calls this command's integration-tier concern (independent pre-PR review, cycle 1,
/// low: the DB-free tests alone left the two guards and the append itself entirely
/// unexercised).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskLogInteractionCommandIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refuses_to_log_against_a_task_with_no_active_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Log something", ["done"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.CurrentRunId.Should().BeNull("this task was seeded Queued, never claimed");

        Func<Task> act = () => TaskLogInteractionCommand.AppendInteractionAsync(
            session, details, Settings(), cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*no active run*");
    }

    [Fact]
    public async Task Refuses_to_log_against_a_stale_current_run_id_with_no_run_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Log something", ["done"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
            task.Apply(claimed);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            // Deliberately no RunAggregate stream started for runId: CurrentRunId names a run
            // whose stream was never started, the stale-projection shape FetchStreamStateAsync's
            // own guard exists to catch.
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.CurrentRunId.Should().Be(runId);

        Func<Task> act = () => TaskLogInteractionCommand.AppendInteractionAsync(
            session, details, Settings(), cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>().WithMessage("*no run stream*");

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamStateAsync(runId, cts.Token)).Should().BeNull(
            "appending here must never implicitly create the run stream on a guard refusal");
    }

    [Fact]
    public async Task Appends_a_human_directed_interaction_to_the_runs_own_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Log something", ["done"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
            task.Apply(claimed);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seed.Events.StartStream<RunAggregate>(runId,
                new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, task.LeaseGeneration,
                    DomainId.New(), "/tmp/log-interaction-worktree", "task/log-interaction-branch",
                    ExecutorMode.Subscription, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        ExternalInteractionLogged logged = await TaskLogInteractionCommand.AppendInteractionAsync(
            session, details,
            Settings(party: "the operator", summary: "Skip the workaround", humanDirected: true, reason: "Real bug"),
            cts.Token);
        await session.SaveChangesAsync(cts.Token);

        logged.RunId.Should().Be(runId);
        logged.LoggedByOwnerId.Should().Be(node.OwnerId);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExternalInteractions.Should().ContainSingle();
        ExternalInteractionRecord record = run.ExternalInteractions[0];
        record.Party.Should().Be("the operator");
        record.Summary.Should().Be("Skip the workaround");
        record.HumanDirected.Should().BeTrue();
        record.Reason.Should().Be("Real bug");
    }

    private static TaskLogInteractionCommand.Settings Settings(
        string party = "another agent session", string summary = "Shared the worktree path", bool humanDirected = false,
        string? reason = null) => new()
    {
        Task = "unused-in-this-path",
        Party = party,
        Summary = summary,
        HumanDirected = humanDirected,
        Reason = reason,
    };

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
