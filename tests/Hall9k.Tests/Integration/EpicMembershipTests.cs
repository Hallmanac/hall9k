using FluentAssertions;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// An epic is its own stream, and a task's membership rides along on the task's own stream —
/// two independent records that both have to project correctly for h9k epic show and h9k task
/// list --epic to answer honestly (Decisions Log #99).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class EpicMembershipTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_task_joins_an_epic_at_add_and_the_epic_shows_it_as_a_member()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId,
                epicId: epicId);
            session.Events.StartStream<TaskAggregate>(taskId, added);

            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            TaskDetails? details = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            TaskListItem? row = await session.LoadAsync<TaskListItem>(taskId, cts.Token);

            details!.EpicId.Should().Be(epicId, "membership is recorded on the task's own stream");
            row!.EpicId.Should().Be(epicId, "the lean row backing h9k task list --epic carries it too");
        }
    }

    [Fact]
    public async Task A_task_leaves_its_epic_through_revision_and_projections_agree()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: [], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId,
                epicId: epicId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            task.EpicId.Should().Be(epicId);

            TaskRevised revised = TaskDecider.Revise(
                task, Optional<string>.None, Optional<IReadOnlyList<string>>.None, Optional<string>.None,
                Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None, Optional<AgentModel>.None,
                Now.AddHours(1), ownerId, Optional<Guid?>.Of(null));
            session.Events.Append(taskId, revised);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            TaskDetails? details = await session.LoadAsync<TaskDetails>(taskId, cts.Token);
            TaskListItem? row = await session.LoadAsync<TaskListItem>(taskId, cts.Token);

            details!.EpicId.Should().BeNull("leaving is the same revision gate, with the field cleared");
            row!.EpicId.Should().BeNull();
        }
    }

    [Fact]
    public async Task Closing_an_epic_never_happens_on_its_own_when_its_last_task_finishes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });

        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid epicId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<EpicAggregate>(
                epicId, EpicDecider.Add(epicId, projectId, "Interactive mode", Now, ownerId));
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Wire the prompt loop", acceptanceCriteria: ["it works"], TaskType.Feature,
                agentContext: null, constraints: null, externalReference: null, Now, ownerId,
                epicId: epicId);
            session.Events.StartStream<TaskAggregate>(taskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Assign(task, ownerId, [], Now, ownerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Complete(task, task.CurrentRunId!.Value, "https://example/pr/1", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession session = store.QuerySession())
        {
            EpicDetails? epic = await session.LoadAsync<EpicDetails>(epicId, cts.Token);
            epic!.State.Should().Be(
                EpicState.Open, "nothing closes an epic automatically, not even its last member task closing out");
        }
    }
}
