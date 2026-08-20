using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
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
/// The sleep-masquerading-as-death defenses (origin incident, 2026-08-18: a lid-close
/// turned two active runs into a five-generation storm). A lease this node holds asks
/// the OS before the timestamp is believed; a wall-clock jump between sweeps refreshes
/// local heartbeats before expiry is evaluated; a claim is refused while the previous
/// generation's agent still runs here. Remote leases keep the plain timeout.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LeaseLivenessTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_stale_heartbeat_with_a_live_local_process_is_refreshed_never_requeued()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        // The wake scenario: the laptop slept 50 minutes mid-run, so the heartbeat is
        // long past the timeout — but the agent process is alive and about to resume.
        (Guid taskId, _) = await SeedClaimedTaskWithRunAsync(
            store, node.NodeId, node.OwnerId, processId: 61234, heartbeatAt: Now.AddMinutes(-50), cts.Token);
        processes.MarkAlive(61234);

        await engine.SweepExpiredLeasesAsync(Now, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "a live local process means a live lease, whatever the heartbeat says");

        TaskLease lease = (await query.LoadAsync<TaskLease>(taskId, cts.Token))!;
        lease.HeartbeatAt.Should().Be(Now, "the sweep refreshes the lease it declined to expire");
    }

    [Fact]
    public async Task A_stale_heartbeat_with_a_dead_local_process_still_requeues_on_the_timeout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        (Guid taskId, _) = await SeedClaimedTaskWithRunAsync(
            store, node.NodeId, node.OwnerId, processId: 61235, heartbeatAt: Now.AddMinutes(-50), cts.Token);
        // 61235 is never marked alive: the OS was asked and said no.

        await engine.SweepExpiredLeasesAsync(Now, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Queued", "a dead process plus a stale heartbeat is honest abandonment");
        processes.LivenessQueries.Should().Contain(q => q.ProcessId == 61235, "the OS was consulted, not bypassed");
    }

    [Fact]
    public async Task A_silent_remote_lease_expires_on_the_timeout_without_asking_this_nodes_os()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        // Another node's lease went silent. Even a pid our own OS would call alive is
        // meaningless for a remote lease — pids only make sense on the node that owns them.
        Guid remoteNodeId = DomainId.New();
        (Guid taskId, _) = await SeedClaimedTaskWithRunAsync(
            store, remoteNodeId, node.OwnerId, processId: 61236, heartbeatAt: Now.AddMinutes(-50), cts.Token);
        processes.MarkAlive(61236);

        await engine.SweepExpiredLeasesAsync(Now, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Queued", "sleep detection only applies to leases this node holds");
        processes.LivenessQueries.Should().BeEmpty("this node's OS knows nothing about a remote node's pids");
    }

    [Fact]
    public async Task A_wall_clock_jump_refreshes_local_heartbeats_before_expiry_while_remote_leases_still_expire()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        DispatchEngine engine = NewEngine(store, node, new FakeProcessManager());

        // Neither task records a pid, so the wake refresh is the only thing that can
        // save the local one — this pins the refresh itself, not the process check.
        Guid localTaskId = await SeedClaimedTaskAsync(store, node.NodeId, node.OwnerId, heartbeatAt: Now.AddSeconds(-30), cts.Token);
        Guid remoteTaskId = await SeedClaimedTaskAsync(store, DomainId.New(), node.OwnerId, heartbeatAt: Now.AddSeconds(-30), cts.Token);

        // Baseline sweep, then the machine sleeps 45 minutes: the next sweep's wall
        // clock has jumped far beyond the expected cadence.
        await engine.SweepExpiredLeasesAsync(Now, cts.Token);
        DateTimeOffset afterWake = Now.AddMinutes(45);
        await engine.SweepExpiredLeasesAsync(afterWake, cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem local = (await query.LoadAsync<TaskListItem>(localTaskId, cts.Token))!;
        local.State.Value.Should().Be("Claimed",
            "the wake-time race is closed: local heartbeats refresh before expiry is evaluated");
        (await query.LoadAsync<TaskLease>(localTaskId, cts.Token))!.HeartbeatAt.Should().Be(afterWake);

        TaskListItem remote = (await query.LoadAsync<TaskListItem>(remoteTaskId, cts.Token))!;
        remote.State.Value.Should().Be("Queued",
            "this node's sleep says nothing about a remote node's silence — the timeout still rules there");
    }

    [Fact]
    public async Task A_claim_is_refused_while_the_previous_generations_process_is_alive_here()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = await NewNodeAsync(store, cts.Token);
        FakeProcessManager processes = new();
        DispatchEngine engine = NewEngine(store, node, processes);

        // A requeue slipped through (the pre-fix wake race), so the task is Queued while
        // generation 1's agent still runs in its worktree on this node.
        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Single flight", ["done"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, firstRunId, Now);
            task.Apply(claimed);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, claimed, TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now)]);

            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(firstRunId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/wt/single-flight", "task/single-flight", ExecutorMode.Subscription, Now),
                new RunProcessStarted(firstRunId, 61237, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        processes.MarkAlive(61237);
        IReadOnlyList<ClaimedWork> refused = await engine.ClaimEligibleAsync(cts.Token);
        refused.Should().NotContain(work => work.TaskId == taskId,
            "one task gets one agent per node while the previous generation is still alive");

        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Queued");
        }

        // The agent finishes (or dies): the very next cycle claims normally.
        processes.MarkDead(61237);
        IReadOnlyList<ClaimedWork> granted = await engine.ClaimEligibleAsync(cts.Token);
        granted.Should().Contain(work => work.TaskId == taskId, "the refusal is per cycle, not a ban");
        granted.Single(work => work.TaskId == taskId).LeaseGeneration.Should().Be(2);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static async Task<NodeContext> NewNodeAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);
        return node;
    }

    private DispatchEngine NewEngine(DocumentStore store, NodeContext node, FakeProcessManager processes) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), processes,
            Options.Create(new DaemonOptions { MaxConcurrentRuns = 50, LeaseTimeout = TimeSpan.FromSeconds(60) }),
            NullLogger<DispatchEngine>.Instance);

    /// <summary>A claimed task whose lease heartbeat sits at the given time; no run stream, no pid.</summary>
    private static async Task<Guid> SeedClaimedTaskAsync(
        DocumentStore store, Guid nodeId, Guid ownerId, DateTimeOffset heartbeatAt, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Sleep walk", ["done"], TaskType.Chore,
                null, null, null, Now.AddHours(-1), ownerId),
            ownerId, Now.AddHours(-1));
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now.AddHours(-1))]);
        session.Store(new TaskLease { Id = taskId, NodeId = nodeId, LeaseGeneration = 1, HeartbeatAt = heartbeatAt });
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>A claimed task whose current run has a recorded pid — the OS can be asked.</summary>
    private static async Task<(Guid TaskId, Guid RunId)> SeedClaimedTaskWithRunAsync(
        DocumentStore store, Guid nodeId, Guid ownerId, int processId, DateTimeOffset heartbeatAt,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Sleep walk", ["done"], TaskType.Chore,
                null, null, null, Now.AddHours(-1), ownerId),
            ownerId, Now.AddHours(-1));
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, nodeId, ownerId, runId, Now.AddHours(-1))]);
        session.Store(new TaskLease { Id = taskId, NodeId = nodeId, LeaseGeneration = 1, HeartbeatAt = heartbeatAt });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, nodeId, ownerId, 1, DomainId.New(),
                "/wt/sleep-walk", "task/sleep-walk", ExecutorMode.Subscription, Now.AddHours(-1)),
            new RunProcessStarted(runId, processId, Now.AddHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }
}
