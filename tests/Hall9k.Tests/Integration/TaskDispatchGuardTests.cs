using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
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
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The claim guard as the dispatcher actually applies it (Decisions Log #34): Queued, and
/// assigned to this node's owner. Everything else — drafts, published tasks, blocked tasks,
/// another owner's work — is structurally invisible rather than filtered out by policy.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TaskDispatchGuardTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Only_queued_tasks_assigned_to_this_nodes_owner_are_claimed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid otherOwner = DomainId.New();
        Guid mine = DomainId.New();
        Guid theirs = DomainId.New();
        Guid draft = DomainId.New();
        Guid published = DomainId.New();
        Guid blocked = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(mine, TaskSeed.Dispatchable(
                Add(mine, "Mine to run"), node.OwnerId, Now));
            seed.Events.StartStream<TaskAggregate>(theirs, TaskSeed.Dispatchable(
                Add(theirs, "Someone else's"), otherOwner, Now));
            seed.Events.StartStream<TaskAggregate>(draft, Add(draft, "Still being written"));

            TaskAdded readyToAssign = Add(published, "Ready, but not started");
            TaskAggregate aggregate = new();
            aggregate.Apply(readyToAssign);
            seed.Events.StartStream<TaskAggregate>(
                published, readyToAssign, TaskDecider.Publish(aggregate, TaskDependencyGraph.Empty, Now, node.OwnerId));

            seed.Events.StartStream<TaskAggregate>(blocked, BlockedOn(blocked, mine, node.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Equal(
            [mine],
            "assignment to this node's owner is the whole rule, and it is the only path to a claim");
    }

    [Fact]
    public async Task The_sweep_unblocks_a_task_whose_dependency_closed_out_and_holds_one_whose_dependency_died()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid merged = DomainId.New();
        Guid abandoned = DomainId.New();
        Guid waitsOnMerged = DomainId.New();
        Guid waitsOnAbandoned = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedClosedOut(seed, merged, node.OwnerId);

            TaskAdded walkAway = Add(abandoned, "Not worth doing after all");
            TaskAggregate aggregate = new();
            aggregate.Apply(walkAway);
            seed.Events.StartStream<TaskAggregate>(
                abandoned, walkAway, TaskDecider.Abandon(aggregate, "Superseded", Now, node.OwnerId));

            seed.Events.StartStream<TaskAggregate>(waitsOnMerged, BlockedOn(waitsOnMerged, merged, node.OwnerId));
            seed.Events.StartStream<TaskAggregate>(waitsOnAbandoned, BlockedOn(waitsOnAbandoned, abandoned, node.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        reevaluation.Unblocked.Should().Equal(waitsOnMerged);
        reevaluation.Parked.Should().Equal(waitsOnAbandoned);

        await using IQuerySession query = store.QuerySession();
        TaskListItem unblocked = (await query.LoadAsync<TaskListItem>(waitsOnMerged, cts.Token))!;
        unblocked.State.Should().Be(TaskState.Queued);

        TaskListItem held = (await query.LoadAsync<TaskListItem>(waitsOnAbandoned, cts.Token))!;
        held.State.Should().Be(TaskState.Blocked, "an abandoned blocker must not silently unblock its dependents");
        held.DependencyFailureReason.Should().Contain("will never close out", "and must not silently strand them");

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Contain(waitsOnMerged);
    }

    [Fact]
    public async Task The_sweep_parks_a_task_whose_dependency_reached_Done_on_a_run_that_will_never_complete()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid resolvedByHand = DomainId.New();
        Guid pullRequestClosed = DomainId.New();
        Guid waitsOnResolved = DomainId.New();
        Guid waitsOnClosed = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedResolvedFromFailure(seed, resolvedByHand, node.OwnerId);
            SeedPullRequestClosedUnmerged(seed, pullRequestClosed, node.OwnerId);
            seed.Events.StartStream<TaskAggregate>(
                waitsOnResolved, BlockedOn(waitsOnResolved, resolvedByHand, node.OwnerId));
            seed.Events.StartStream<TaskAggregate>(
                waitsOnClosed, BlockedOn(waitsOnClosed, pullRequestClosed, node.OwnerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        reevaluation.Unblocked.Should().BeEmpty("neither dependency ever reached true closeout");
        reevaluation.Parked.Should().BeEquivalentTo([waitsOnResolved, waitsOnClosed]);

        await using IQuerySession query = store.QuerySession();
        foreach (Guid dependent in (Guid[])[waitsOnResolved, waitsOnClosed])
        {
            TaskListItem held = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
            held.State.Should().Be(TaskState.Blocked);
            held.DependencyFailureReason.Should().Contain(
                "reads Done",
                "a dependency that can no longer be merged must say so rather than strand its dependents");
        }
    }

    /// <summary>
    /// The attestation exit from Failed (log #27): the task ends Done while the run that
    /// carried it stays Failed, so no merge will ever be observed for it.
    /// </summary>
    private static void SeedResolvedFromFailure(IDocumentSession session, Guid taskId, Guid ownerId)
    {
        Guid runId = DomainId.New();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(Add(taskId, "Resolved by hand"), ownerId, Now);

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        task.Apply(claimed);
        TaskFailed failed = TaskDecider.Fail(task, runId, "the gates never went green", Now);
        task.Apply(failed);
        TaskResolved resolved = TaskDecider.Resolve(task, "the objective was met anyway", null, Now, ownerId);

        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, failed, resolved]);
        session.Events.StartStream<RunAggregate>(
            runId, Dispatch(runId, taskId, ownerId, "task/resolved"),
            new RunFailed(runId, "the gates never went green", Now));
    }

    /// <summary>Done with a pull request open, then closed without merging: the same dead end, a different door.</summary>
    private static void SeedPullRequestClosedUnmerged(IDocumentSession session, Guid taskId, Guid ownerId)
    {
        Guid runId = DomainId.New();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(Add(taskId, "Rejected on review"), ownerId, Now);

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, runId, "https://github.com/x/y/pull/9", Now);

        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, completed]);
        session.Events.StartStream<RunAggregate>(
            runId, Dispatch(runId, taskId, ownerId, "task/rejected"),
            new PullRequestClosed(runId, Now.AddHours(1), Now.AddHours(1)));
    }

    private static RunDispatched Dispatch(Guid runId, Guid taskId, Guid ownerId, string branch) => new(
        runId, taskId, DomainId.New(), ownerId, 1, DomainId.New(), "/wt", branch,
        ExecutorMode.Subscription, Now);

    /// <summary>A task at true closeout: Done, with the run the closeout monitor completed.</summary>
    private static void SeedClosedOut(IDocumentSession session, Guid taskId, Guid ownerId)
    {
        Guid runId = DomainId.New();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(Add(taskId, "The blocker"), ownerId, Now);

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        task.Apply(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, runId, "https://github.com/x/y/pull/7", Now);

        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, completed]);
        session.Events.StartStream<RunAggregate>(
            runId,
            new RunDispatched(
                runId, taskId, DomainId.New(), ownerId, 1, DomainId.New(), "/wt", "task/blocker",
                ExecutorMode.Subscription, Now),
            new RunCompleted(runId, Now.AddHours(1)));
    }

    /// <summary>The three lifecycle events that leave a task Blocked on one open dependency.</summary>
    private static object[] BlockedOn(Guid id, Guid dependencyId, Guid ownerId)
    {
        TaskAdded added = Add(id, "Waits on another task", dependencyId);
        TaskAggregate task = new();
        task.Apply(added);

        TaskDependency blocker = new(
            dependencyId, "The blocker", TaskState.Queued, IsClosedOut: false, CurrentRunState: null, []);
        TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph([blocker]), Now, ownerId);
        task.Apply(published);

        return [added, published, TaskDecider.Assign(task, ownerId, [blocker], Now, ownerId)];
    }

    private static TaskAdded Add(Guid id, string objective, params Guid[] blockedBy) => TaskDecider.Add(
        id, DomainId.New(), objective, ["it is done"], TaskType.Chore,
        null, null, null, Now, DomainId.New(), blockedBy: blockedBy);

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
