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
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
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

            seed.Events.StartStream<TaskAggregate>(blocked, BlockedOn(blocked, node.OwnerId, mine));
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
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
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

            seed.Events.StartStream<TaskAggregate>(waitsOnMerged, BlockedOn(waitsOnMerged, node.OwnerId, merged));
            seed.Events.StartStream<TaskAggregate>(waitsOnAbandoned, BlockedOn(waitsOnAbandoned, node.OwnerId, abandoned));
            await seed.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        reevaluation.Unblocked.Should().Equal(waitsOnMerged);
        reevaluation.Parked.Select(hold => hold.TaskId).Should().Equal(waitsOnAbandoned);

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
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
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
                waitsOnResolved, BlockedOn(waitsOnResolved, node.OwnerId, resolvedByHand));
            seed.Events.StartStream<TaskAggregate>(
                waitsOnClosed, BlockedOn(waitsOnClosed, node.OwnerId, pullRequestClosed));
            await seed.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        reevaluation.Unblocked.Should().BeEmpty("neither dependency ever reached true closeout");
        reevaluation.Parked.Select(hold => hold.TaskId).Should().BeEquivalentTo([waitsOnResolved, waitsOnClosed]);

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

    [Fact]
    public async Task Retrying_a_failed_blocker_lifts_the_hold_on_its_dependents_within_one_sweep()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid blocker = DomainId.New();
        Guid dependent = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedFailed(seed, blocker, runId, node.OwnerId);
            seed.Events.StartStream<TaskAggregate>(dependent, BlockedOn(dependent, node.OwnerId, blocker));
            await seed.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation held = await engine.ReevaluateBlockedTasksAsync(cts.Token);
        held.Parked.Select(hold => hold.TaskId).Should().Equal([dependent]);

        // The human's remedy, exactly as h9k task retry appends it.
        await using (IDocumentSession retry = store.LightweightSession())
        {
            TaskAggregate failed = (await retry.Events.AggregateStreamAsync<TaskAggregate>(
                blocker, token: cts.Token))!;
            retry.Events.Append(blocker, TaskDecider.Retry(
                failed, runId, "task/blocker", "worth another attempt", Now.AddHours(1), node.OwnerId));
            await retry.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation recovered = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        recovered.Recovered.Should().Equal(
            [new DependencyRecovery(dependent, null)],
            "one dispatch cycle is the whole latency budget, and nothing is left holding it");
        recovered.Parked.Should().BeEmpty("nothing is dead now, so nothing is re-recorded");
        recovered.Unblocked.Should().BeEmpty("the blocker still has to reach true closeout");

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.State.Should().Be(TaskState.Blocked, "it waits on the blocker the ordinary way now");
        row.DeadDependencies.Should().BeEmpty();
        row.DependencyFailureReason.Should().BeNull("this is what makes h9k status stop reading NeedsHuman");
        row.UnmetDependencies.Should().Equal(blocker);

        TaskDetails details = (await query.LoadAsync<TaskDetails>(dependent, cts.Token))!;
        details.DependencyFailureReason.Should().BeNull("h9k task show reads the same recovery");

        // History stays honest: the hold happened, and so did the recovery.
        Type[] recorded = [.. (await query.Events.FetchStreamAsync(dependent, token: cts.Token))
            .Select(@event => @event.Data.GetType())];
        recorded.Should().Contain(typeof(TaskDependencyFailed), "the hold happened, and is never rewritten");
        recorded.Should().Contain(typeof(TaskDependencyRecovered), "and so did the recovery");

        DependencyReevaluation quiet = await engine.ReevaluateBlockedTasksAsync(cts.Token);
        quiet.ChangedAnything.Should().BeFalse("a settled dependent must not churn a recovery every cycle");
    }

    [Fact]
    public async Task A_blocker_retried_and_failed_again_is_held_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid blocker = DomainId.New();
        Guid dependent = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedFailed(seed, blocker, runId, node.OwnerId);
            seed.Events.StartStream<TaskAggregate>(dependent, BlockedOn(dependent, node.OwnerId, blocker));
            await seed.SaveChangesAsync(cts.Token);
        }

        await engine.ReevaluateBlockedTasksAsync(cts.Token);

        await using (IDocumentSession retry = store.LightweightSession())
        {
            TaskAggregate failed = (await retry.Events.AggregateStreamAsync<TaskAggregate>(
                blocker, token: cts.Token))!;
            retry.Events.Append(blocker, TaskDecider.Retry(
                failed, runId, "task/blocker", "worth another attempt", Now.AddHours(1), node.OwnerId));
            await retry.SaveChangesAsync(cts.Token);
        }

        await engine.ReevaluateBlockedTasksAsync(cts.Token);

        Guid secondRunId = DomainId.New();
        await using (IDocumentSession again = store.LightweightSession())
        {
            TaskAggregate queued = (await again.Events.AggregateStreamAsync<TaskAggregate>(
                blocker, token: cts.Token))!;
            TaskClaimed claimed = TaskDecider.Claim(
                queued, DomainId.New(), node.OwnerId, secondRunId, Now.AddHours(2));
            queued.Apply(claimed);
            again.Events.Append(blocker, claimed, TaskDecider.Fail(
                queued, secondRunId, "the gates never went green, again", Now.AddHours(3)));
            await again.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reheld = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        reheld.Parked.Select(hold => hold.TaskId).Should().Equal(
            [dependent], "hold, recover, hold — each one observed");

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.DeadDependencies.Should().Equal(blocker);
        row.DependencyFailureReason.Should().Contain("will never close out");
    }

    [Fact]
    public async Task Resolving_a_failed_blocker_restates_the_hold_rather_than_leaving_stale_advice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid blocker = DomainId.New();
        Guid dependent = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedFailed(seed, blocker, runId, node.OwnerId);
            seed.Events.StartStream<TaskAggregate>(dependent, BlockedOn(dependent, node.OwnerId, blocker));
            await seed.SaveChangesAsync(cts.Token);
        }

        await engine.ReevaluateBlockedTasksAsync(cts.Token);

        await using (IDocumentSession resolve = store.LightweightSession())
        {
            TaskAggregate failed = (await resolve.Events.AggregateStreamAsync<TaskAggregate>(
                blocker, token: cts.Token))!;
            resolve.Events.Append(blocker, TaskDecider.Resolve(
                failed, "the objective was met anyway", null, Now.AddHours(1), node.OwnerId));
            await resolve.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation after = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        // Resolve is an attestation, not a merge: the run stays Failed, so no closeout
        // observation will ever arrive and the dependent is still stranded (log #34's
        // 2026-08-20 incident). What changes is the advice, which must stop naming a lever
        // the decider would now refuse.
        after.Recovered.Should().BeEmpty("a resolved blocker still cannot reach true closeout");
        after.Parked.Select(hold => hold.TaskId).Should().Equal([dependent]);
        after.Parked.Single().Reason.Should().Contain(
            "reads Done",
            "the operator log states the cause h9k task show reports, not a summary that outlived it");

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.DependencyFailureReason.Should().Contain(
            "reads Done", "the recorded reason must describe the death the blocker died, not the last one");
        row.DependencyFailureReason.Should().NotContain(
            "Retry or resolve it", "h9k task retry and h9k task resolve both refuse a Done task");

        DependencyReevaluation quiet = await engine.ReevaluateBlockedTasksAsync(cts.Token);
        quiet.ChangedAnything.Should().BeFalse("a restated hold is restated once, not every cycle");
    }

    [Fact]
    public async Task A_dependent_whose_blockers_all_change_is_reported_once_per_pass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid first = DomainId.New();
        Guid second = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid secondRunId = DomainId.New();
        Guid dependent = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedFailed(seed, first, firstRunId, node.OwnerId);
            SeedFailed(seed, second, secondRunId, node.OwnerId);
            seed.Events.StartStream<TaskAggregate>(
                dependent, BlockedOn(dependent, node.OwnerId, first, second));
            await seed.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation held = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        // Two dead blockers is two observations but one dependent, and the caller logs a line
        // per entry: a task that appeared twice would say the same thing to the human twice.
        held.Parked.Select(hold => hold.TaskId).Should().Equal(
            [dependent], "the lists carry tasks, not the blockers that changed");

        // The human retries both, so both holds lift in the same pass.
        await using (IDocumentSession retry = store.LightweightSession())
        {
            foreach ((Guid blocker, Guid runId) in new[] { (first, firstRunId), (second, secondRunId) })
            {
                TaskAggregate failed = (await retry.Events.AggregateStreamAsync<TaskAggregate>(
                    blocker, token: cts.Token))!;
                retry.Events.Append(blocker, TaskDecider.Retry(
                    failed, runId, "task/blocker", "worth another attempt", Now.AddHours(1), node.OwnerId));
            }

            await retry.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation recovered = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        recovered.Recovered.Should().Equal(
            [new DependencyRecovery(dependent, null)],
            "two holds lifting is still one dependent recovered, and nothing is left holding it");
        recovered.Parked.Should().BeEmpty("nothing is dead now");

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.DeadDependencies.Should().BeEmpty();
        row.DependencyFailureReason.Should().BeNull("neither blocker is holding it any more");
    }

    [Fact]
    public async Task A_recovery_that_leaves_another_blocker_dead_reports_the_hold_that_survives()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node);

        Guid retried = DomainId.New();
        Guid abandoned = DomainId.New();
        Guid runId = DomainId.New();
        Guid dependent = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            SeedFailed(seed, retried, runId, node.OwnerId);
            SeedAbandoned(seed, abandoned, node.OwnerId);
            seed.Events.StartStream<TaskAggregate>(
                dependent, BlockedOn(dependent, node.OwnerId, retried, abandoned));
            await seed.SaveChangesAsync(cts.Token);
        }

        await engine.ReevaluateBlockedTasksAsync(cts.Token);

        // Only one of the two blockers moves. The other stays dead, unchanged, so this pass
        // appends nothing about it and the dependent never lands in Parked.
        await using (IDocumentSession retry = store.LightweightSession())
        {
            TaskAggregate failed = (await retry.Events.AggregateStreamAsync<TaskAggregate>(
                retried, token: cts.Token))!;
            retry.Events.Append(retried, TaskDecider.Retry(
                failed, runId, "task/blocker", "worth another attempt", Now.AddHours(1), node.OwnerId));
            await retry.SaveChangesAsync(cts.Token);
        }

        DependencyReevaluation reevaluation = await engine.ReevaluateBlockedTasksAsync(cts.Token);

        reevaluation.Parked.Should().BeEmpty("the abandoned blocker was already recorded, and says nothing new");
        reevaluation.Recovered.Should().HaveCount(1);
        reevaluation.Recovered.Single().TaskId.Should().Be(dependent);
        reevaluation.Recovered.Single().SurvivingReason.Should().Contain(
            "was abandoned",
            "a recovery reported on its own would tell the operator the hold is lifted while "
            + "h9k status still reads the task as NeedsHuman");

        await using IQuerySession query = store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(dependent, cts.Token))!;
        row.DeadDependencies.Should().Equal([abandoned], "one blocker recovered; the other did not");
        row.DependencyFailureReason.Should().Be(
            reevaluation.Recovered.Single().SurvivingReason,
            "the operator log and the task surface state the same surviving hold");
    }

    /// <summary>A blocker a human abandoned: a dead end by design, and it stays dead.</summary>
    private static void SeedAbandoned(IDocumentSession session, Guid taskId, Guid ownerId)
    {
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(Add(taskId, "Given up on"), ownerId, Now);

        session.Events.StartStream<TaskAggregate>(
            taskId, [.. lifecycle, TaskDecider.Abandon(task, "it stopped being worth doing", Now, ownerId)]);
    }

    /// <summary>A task the daemon failed: Failed, on a run that failed with it.</summary>
    private static void SeedFailed(IDocumentSession session, Guid taskId, Guid runId, Guid ownerId)
    {
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(Add(taskId, "The blocker"), ownerId, Now);

        TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        task.Apply(claimed);
        TaskFailed failed = TaskDecider.Fail(task, runId, "the machine went down mid-run", Now);

        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, failed]);
        session.Events.StartStream<RunAggregate>(
            runId, Dispatch(runId, taskId, ownerId, "task/blocker"),
            new RunFailed(runId, "the machine went down mid-run", Now));
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

    /// <summary>The three lifecycle events that leave a task Blocked on the given dependencies.</summary>
    private static object[] BlockedOn(Guid id, Guid ownerId, params Guid[] dependencyIds)
    {
        TaskAdded added = Add(id, "Waits on another task", dependencyIds);
        TaskAggregate task = new();
        task.Apply(added);

        TaskDependency[] blockers =
        [
            .. dependencyIds.Select(dependencyId => new TaskDependency(
                dependencyId, "The blocker", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                PullRequestUrl: null, TaskType.Chore, [])),
        ];
        TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);
        task.Apply(published);

        return [added, published, TaskDecider.Assign(task, ownerId, blockers, Now, ownerId)];
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
            Options.Create(new DaemonOptions { MaxConcurrentAgentSessions = 100, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);
}
