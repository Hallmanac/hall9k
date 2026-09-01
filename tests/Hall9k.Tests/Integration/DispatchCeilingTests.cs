using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The per-node concurrency ceiling (Decisions Log #64). Its own class so it gets its own
/// database: every assertion here is about how many runs a node is carrying, and a sibling
/// test's leftover lease would be counted as one of them.
/// </summary>
// One test asks whether a run's result reached disk, so this class redirects the
// process-wide HALL9K_HOME too and joins the collection that serializes the classes doing it.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class DispatchCeilingTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A terminal result line, the one thing the daemon reads out of a stream file.</summary>
    private const string ResultLine =
        """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":1200,"output_tokens":300},"total_cost_usd":0.0123}""";

    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-ceiling-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        try
        {
            if (Directory.Exists(_home))
            {
                Directory.Delete(_home, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_ceiling_defers_the_rest_of_the_queue_oldest_first_and_publishes_what_it_carries()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        Guid[] queued = await SeedQueuedAsync(store, node, count: 4, cts.Token);

        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Equal([queued[0], queued[1]],
            "the ceiling bounds how much of the queue is taken, never which end of it");

        await using (IQuerySession query = store.QuerySession())
        {
            foreach (Guid deferred in queued[2..])
            {
                TaskListItem task = (await query.LoadAsync<TaskListItem>(deferred, cts.Token))!;
                task.State.Value.Should().Be("Queued",
                    "waiting for a slot is not a new state — Queued already honestly means waiting");
            }

            NodeDispatchLoad load = (await query.LoadAsync<NodeDispatchLoad>(node.NodeId, cts.Token))!;
            load.LiveRuns.Should().Be(2,
                "the sweep that fills the node is the one whose deferrals need explaining, so it "
                + "publishes what it is carrying when it leaves, not what it found on arrival");
            load.MaxConcurrentRuns.Should().Be(2, "the ceiling is configured directly in runs (Decisions Log #109)");
            load.MachineName.Should().Be(Environment.MachineName);
        }

        // Nothing has finished, so nothing more starts.
        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty();

        await using (IQuerySession query = store.QuerySession())
        {
            NodeDispatchLoad load = (await query.LoadAsync<NodeDispatchLoad>(node.NodeId, cts.Token))!;
            load.LiveRuns.Should().Be(2, "two claims are two session trees this node is carrying");
        }

        // One run ends; exactly one slot opens, and the oldest deferred task takes it.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Delete<TaskLease>(claimed[0].TaskId);
            await session.SaveChangesAsync(cts.Token);
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(queued[2]);
    }

    [Fact]
    public async Task A_parked_run_gives_its_slot_back_although_it_keeps_its_lease()
    {
        // A park is waiting on a human with nothing resident. The lease deliberately stays
        // alive so the expiry sweep cannot requeue the task out from under its worktree
        // (Decisions Log #24) — which is exactly why counting leases would let one abandoned
        // review hold a slot on a two-run laptop until someone came back to it.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        await SeedParkedRunAsync(store, node, cts.Token);
        Guid[] queued = await SeedQueuedAsync(store, node, count: 1, cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(queued[0]);
    }

    [Fact]
    public async Task A_requeued_run_stops_holding_a_slot_rather_than_reading_Running_forever()
    {
        // The sweep only requeues past a dead process, so the run it took the lease from is
        // over: recording that is what keeps the live-run count from drifting up by one every
        // time an agent dies, until a daemon restart's orphan adoption notices.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        Guid[] queued = await SeedQueuedAsync(store, node, count: 2, cts.Token);
        ClaimedWork claimed = (await engine.ClaimEligibleAsync(cts.Token)).Should().ContainSingle().Subject;

        // The launcher records the agent process, which then dies without a result.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(claimed.RunId,
                new RunDispatched(claimed.RunId, claimed.TaskId, node.NodeId, node.OwnerId,
                    claimed.LeaseGeneration, DomainId.New(), "/wt/one", "task/one",
                    ExecutorMode.Subscription, Now),
                new RunProcessStarted(claimed.RunId, 4242, Now));

            TaskLease lease = (await session.LoadAsync<TaskLease>(claimed.TaskId, cts.Token))!;
            lease.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            session.Store(lease);
            await session.SaveChangesAsync(cts.Token);
        }

        (await engine.SweepExpiredLeasesAsync(cts.Token)).Should().Be(1);

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(claimed.RunId, cts.Token))!;
            run.State.Should().Be(RunState.Failed);
            run.FailureReason.Should().Contain("Lease expired", "the run records how it ended, never a guess");
        }

        // The slot came back with it: the requeued task is claimable again, one at a time.
        (await engine.ClaimEligibleAsync(cts.Token)).Should().ContainSingle()
            .Which.TaskId.Should().Be(queued[0], "the requeued task is still the oldest in the queue");
    }

    [Fact]
    public async Task A_run_whose_pipeline_outlived_its_build_session_is_not_failed_by_the_sweep()
    {
        // Verifying and UnderReview are states the build agent has already exited from, so its
        // recorded pid says nothing about whether the run is over — and they are exactly the two
        // states startup adoption resumes, immediately before this sweep runs. Failing one would
        // record a failure for a pipeline executing at that moment, and the next event that
        // pipeline appends would silently undo it (pre-PR review, 2026-08-22).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        await SeedQueuedAsync(store, node, count: 1, cts.Token);
        ClaimedWork claimed = (await engine.ClaimEligibleAsync(cts.Token)).Should().ContainSingle().Subject;

        // The agent session ran, ended, and handed the run to the gates; then the daemon stopped
        // for longer than the lease timeout.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(claimed.RunId,
                new RunDispatched(claimed.RunId, claimed.TaskId, node.NodeId, node.OwnerId,
                    claimed.LeaseGeneration, DomainId.New(), "/wt/one", "task/one",
                    ExecutorMode.Subscription, Now),
                new RunProcessStarted(claimed.RunId, 4242, Now),
                new AgentSessionCompleted(claimed.RunId, Now));

            TaskLease lease = (await session.LoadAsync<TaskLease>(claimed.TaskId, cts.Token))!;
            lease.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            session.Store(lease);
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.SweepExpiredLeasesAsync(cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(claimed.RunId, cts.Token))!;
            run.State.Should().Be(RunState.Verifying,
                "the sweep never observed this run end, so it records nothing about how it ended");
            run.FailureReason.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_run_whose_agent_finished_while_the_daemon_was_down_is_not_failed_by_the_sweep()
    {
        // The third thing startup adoption resumes: the daemon stopped, the agent went on to
        // write its result, and the restart came after the lease timeout. The process is gone, so
        // the liveness check says nothing useful — but the result is on disk, which is exactly
        // what adoption re-monitors on (RunSupervisor.AdoptOrphansAsync), and it runs immediately
        // before this sweep. Failing the run here would record a failure for a session that
        // finished successfully, which the monitor's own events would then silently undo
        // (pre-PR review, 2026-08-22).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        await SeedQueuedAsync(store, node, count: 1, cts.Token);
        ClaimedWork claimed = (await engine.ClaimEligibleAsync(cts.Token)).Should().ContainSingle().Subject;

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(claimed.RunId,
                new RunDispatched(claimed.RunId, claimed.TaskId, node.NodeId, node.OwnerId,
                    claimed.LeaseGeneration, DomainId.New(), "/wt/one", "task/one",
                    ExecutorMode.Subscription, Now),
                new RunProcessStarted(claimed.RunId, 4242, Now));

            TaskLease lease = (await session.LoadAsync<TaskLease>(claimed.TaskId, cts.Token))!;
            lease.HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            session.Store(lease);
            await session.SaveChangesAsync(cts.Token);
        }

        // The agent's terminal result landed on disk while nothing was tailing it; 4242 is never
        // marked alive, so the sweep's liveness check finds the process gone.
        Directory.CreateDirectory(RunPaths.GlobalDirectory(claimed.RunId));
        await File.WriteAllTextAsync(RunPaths.StreamFile(RunPaths.GlobalDirectory(claimed.RunId)),
            $"{{\"type\":\"assistant\"}}\n{ResultLine}\n", cts.Token);

        (await engine.SweepExpiredLeasesAsync(cts.Token)).Should().Be(1,
            "the requeue is unchanged — only the false failure record is withheld");

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(claimed.RunId, cts.Token))!;
            run.State.Should().Be(RunState.Running,
                "a finished session is not a dead one, and adoption is about to re-monitor this run");
            run.FailureReason.Should().BeNull();
        }
    }

    [Fact]
    public async Task Another_nodes_run_is_left_for_that_node_to_conclude()
    {
        // The sweep reads every stale lease in the shared database, not only its own, and it
        // cannot ask another machine's operating system anything. A stopped daemon whose detached
        // agent is still working is the ordinary case (down is not death, log #29): failing that
        // run here would hide it from its own node's startup adoption, so the live session is
        // never re-monitored and its work is thrown away (pre-PR review, 2026-08-22).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        Guid otherNode = DomainId.New();
        (Guid taskId, Guid runId) = await SeedClaimedElsewhereAsync(store, node, otherNode, cts.Token);

        (await engine.SweepExpiredLeasesAsync(cts.Token)).Should().Be(1,
            "the task is the shared thing, so it is requeued for whoever can take it");

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.State.Should().Be(RunState.Running,
                "this node never observed that session end, and saying it did would strand a live agent");
            run.FailureReason.Should().BeNull();

            (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull();
        }
    }

    [Fact]
    public async Task A_failed_load_measurement_never_costs_the_claims_it_follows()
    {
        // Every task claimed is already leased in its own committed transaction, so letting the
        // telemetry write that follows throw would drop the claim list before RunLauncher saw it
        // — and a lease with no run behind it is stranded for good: the heartbeat service keeps
        // refreshing it, so the expiry sweep never finds it, and with no run document, startup
        // adoption cannot either (pre-PR review, 2026-08-22).
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 2);

        // One quiet cycle, so the load document's table exists and this store has noted that it
        // does; dropping it behind the store's back is then a database failure on the next write,
        // which is the shape of the transient drop this guard is for.
        (await engine.ClaimEligibleAsync(cts.Token)).Should().BeEmpty();
        await DropLoadTableAsync(cts.Token);

        Guid[] queued = await SeedQueuedAsync(store, node, count: 2, cts.Token);
        IReadOnlyList<ClaimedWork> claimed = await engine.ClaimEligibleAsync(cts.Token);

        claimed.Select(work => work.TaskId).Should().Equal(queued,
            "the claims are handed back to be launched, and the missing measurement costs one "
            + "stale number on the attention pane until the next sweep");

        await using IQuerySession query = store.QuerySession();
        foreach (ClaimedWork work in claimed)
        {
            (await query.LoadAsync<TaskLease>(work.TaskId, cts.Token)).Should().NotBeNull(
                "a lease whose launch never happened is the thing that can never be recovered");
        }
    }

    [Fact]
    public async Task The_deferred_queue_is_served_oldest_assignment_first_not_oldest_draft_first()
    {
        // Assignment is the act that put a task in this queue, so it is the act the queue is
        // ordered by (Decisions Log #64). A task drafted in January and assigned this morning
        // is behind one drafted and assigned last week — which ordering by AddedAt alone gets
        // backwards, and which only becomes visible once the ceiling makes the tail wait.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = Store();
        NodeContext node = await FreshNodeAsync(store, cts.Token);
        DispatchEngine engine = Engine(store, node, maxConcurrentRuns: 1);

        Guid draftedFirst = await SeedQueuedAsync(store, node, "Drafted in January",
            addedAt: Now, assignedAt: Now.AddHours(10), cts.Token);
        Guid assignedFirst = await SeedQueuedAsync(store, node, "Drafted last week",
            addedAt: Now.AddHours(5), assignedAt: Now.AddHours(1), cts.Token);

        (await engine.ClaimEligibleAsync(cts.Token)).Should().ContainSingle()
            .Which.TaskId.Should().Be(assignedFirst,
                "the older assignment goes first even though the other task was drafted earlier");

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Delete<TaskLease>(assignedFirst);
            await session.SaveChangesAsync(cts.Token);
        }

        (await engine.ClaimEligibleAsync(cts.Token)).Select(work => work.TaskId).Should().Equal(draftedFirst);
    }

    private DocumentStore Store() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>
    /// An empty database and a freshly bootstrapped node. The class shares one container, and
    /// every test here counts what its node is carrying — a sibling's leftover queued task
    /// would be claimed into the very slot under test.
    /// </summary>
    private static async Task<NodeContext> FreshNodeAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await store.Advanced.ResetAllData(cancellationToken);
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        return node;
    }

    /// <summary>An engine whose ceiling is stated directly in runs (Decisions Log #109).</summary>
    private DispatchEngine Engine(IDocumentStore store, NodeContext node, int maxConcurrentRuns) => new(
        store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
        Options.Create(new DaemonOptions
        {
            MaxConcurrentTaskRuns = maxConcurrentRuns,
            LeaseTimeout = TimeSpan.FromSeconds(60),
        }),
        NullLogger<DispatchEngine>.Instance);

    /// <summary>Queued, assigned work, oldest first — the order the dispatcher claims in.</summary>
    private static async Task<Guid[]> SeedQueuedAsync(
        IDocumentStore store, NodeContext node, int count, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => DomainId.New())];
        await using IDocumentSession session = store.LightweightSession();
        for (int index = 0; index < ids.Length; index++)
        {
            session.Events.StartStream<TaskAggregate>(ids[index], TaskSeed.Dispatchable(
                TaskDecider.Add(
                    ids[index], DomainId.New(), $"Task {index}", ["done"], TaskType.Chore,
                    null, null, null, Now.AddSeconds(index), node.OwnerId),
                node.OwnerId, Now));
        }

        await session.SaveChangesAsync(cancellationToken);
        return ids;
    }

    /// <summary>One queued task whose drafting and assignment happened at different moments.</summary>
    private static async Task<Guid> SeedQueuedAsync(
        IDocumentStore store, NodeContext node, string objective,
        DateTimeOffset addedAt, DateTimeOffset assignedAt, CancellationToken cancellationToken)
    {
        Guid id = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(id, TaskSeed.Dispatchable(
            TaskDecider.Add(id, DomainId.New(), objective, ["done"], TaskType.Chore,
                null, null, null, addedAt, node.OwnerId),
            node.OwnerId, assignedAt));

        await session.SaveChangesAsync(cancellationToken);
        return id;
    }

    /// <summary>A task another node claimed and is running, whose lease has gone silent here.</summary>
    private static async Task<(Guid TaskId, Guid RunId)> SeedClaimedElsewhereAsync(
        IDocumentStore store, NodeContext node, Guid otherNode, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Theirs", ["done"], TaskType.Chore,
                null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, otherNode, node.OwnerId, runId, Now)]);

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, otherNode, node.OwnerId, 1,
                DomainId.New(), "/wt/theirs", "task/theirs", ExecutorMode.Subscription, Now),
            new RunProcessStarted(runId, 4242, Now));

        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = otherNode,
            LeaseGeneration = 1,
            HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }

    /// <summary>
    /// Take the load document's table out from under a store that has already used it, which is
    /// the simplest honest stand-in for the database failing the write.
    /// </summary>
    private async Task DropLoadTableAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand drop = new("drop table if exists public.mt_doc_nodedispatchload", connection);
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A claimed task whose run has parked for a human, lease and all.</summary>
    private static async Task SeedParkedRunAsync(
        IDocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Parked", ["done"], TaskType.Chore,
                null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        session.Events.StartStream<TaskAggregate>(taskId,
            [.. lifecycle, TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now)]);

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, node.OwnerId, 1,
                DomainId.New(), "/wt/parked", "task/parked", ExecutorMode.Subscription, Now),
            new ReviewDispatched(runId, DomainId.New(), 1, 5001, Now, Now),
            new ReviewParked(runId, "No parseable verdict, even after a re-prompt.", Now));

        session.Store(new TaskLease
        {
            Id = taskId,
            NodeId = node.NodeId,
            LeaseGeneration = 1,
            HeartbeatAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(cancellationToken);
    }
}
