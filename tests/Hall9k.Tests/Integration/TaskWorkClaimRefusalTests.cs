using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k task work's entry-state refusals (task 688a1ccf-h9k), pinned against a real store because
/// <see cref="TaskWorkCommand.ClaimAndCutAsync"/> reads the dependency snapshot for a Published
/// task straight off Marten. Every case here throws before the method ever loads
/// <c>TaskDetails</c>/<c>ProjectDetails</c> or touches the filesystem, so no project or worktree
/// setup is needed — only the task's own stream.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskWorkClaimRefusalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_draft_task_is_refused_and_told_to_publish_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Still being written", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*Published or Queued*")
            .Where(exception => exception.Message.Contains("Publish it first"));
    }

    [Fact]
    public async Task A_blocked_task_is_refused_and_told_a_dependency_is_open()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Blocked*")
            .Where(exception => exception.Message.Contains("waiting on a dependency"));
    }

    [Fact]
    public async Task A_task_claimed_by_a_node_is_refused_as_headless_work_already_running()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Already running headless", ["it is done"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("claimed by a node running headless work already"));
    }

    [Fact]
    public async Task A_queued_task_assigned_to_a_different_owner_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid theirOwnerId = DomainId.New();
        Guid myOwnerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(taskId, DomainId.New(), "Someone else's queued work", ["it is done"],
                    TaskType.Chore, null, null, null, Now, theirOwnerId),
                theirOwnerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, myOwnerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage($"*assigned to {theirOwnerId}*")
            .Where(exception => exception.Message.Contains("an operator claims only their own owner's work"));
    }

    private static async Task WorkAsync(DocumentStore store, Guid taskId, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

        await TaskWorkCommand.ClaimAndCutAsync(
            store, session, task, fence, context, DomainId.New(), cancellationToken);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
