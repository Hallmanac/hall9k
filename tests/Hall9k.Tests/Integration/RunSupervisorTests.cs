using Hall9k.Domain.Infrastructure.Storage;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Review;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

// Both classes redirect the process-wide HALL9K_HOME; sharing a collection serializes
// them so one test's home is never yanked out from under the other's tail loop.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class RunSupervisorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // Shaped like a real cached session: nearly all input arrives as cache reads (log #30).
    private const string ResultLine =
        """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":1200,"cache_read_input_tokens":840000,"cache_creation_input_tokens":21000,"output_tokens":300},"total_cost_usd":0.0123}""";

    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task Fake_agent_stream_is_tailed_to_completion_with_tokens_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        int processId = SpawnFakeAgent(runId,
            $"sleep 0.3; echo '{{\"type\":\"assistant\"}}'; sleep 0.3; echo '{ResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);
        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        details.InputTokens.Should().Be(1200);
        details.CacheReadInputTokens.Should().Be(840_000, "cache reads are the bulk of a cached session's input");
        details.CacheCreationInputTokens.Should().Be(21_000);
        details.OutputTokens.Should().Be(300);
        details.CostUsd.Should().Be(0.0123m, "the cost is what the result reported");

        await using IQuerySession query = store.QuerySession();
        var activity = await query.LoadAsync<Hall9k.Domain.Features.Run.Documents.RunActivity>(runId, cts.Token);
        activity!.StreamBytesRead.Should().BeGreaterThan(0, "the tail cursor persists progress");
    }

    [Fact]
    public async Task Daemon_restart_mid_run_adopts_the_orphan_and_completes_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        // Agent outlives the "first daemon": takes ~4s, first monitor is killed after ~1s.
        int processId = SpawnFakeAgent(runId,
            $"echo '{{\"type\":\"assistant\"}}'; sleep 4; echo '{ResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        using (CancellationTokenSource firstDaemon = new())
        {
            RunSupervisor doomed = NewSupervisor(store, node);
            doomed.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, firstDaemon.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            firstDaemon.Cancel();
        }

        // The "restarted daemon": adoption finds the live process and resumes tailing.
        RunSupervisor restarted = NewSupervisor(store, node);
        OrphanAdoption adoption = await restarted.AdoptOrphansAsync(cts.Token);
        adoption.RunsAdopted.Should().BeGreaterThanOrEqualTo(1,
            "the catch-up report (Decisions Log #31) counts this adoption");
        // GreaterThanOrEqualTo: this node is shared across the class's tests (node identity
        // is per machine name), so adoption may also resume another test's Verifying stray
        // as a background pipeline; the tail assertions below pin down THIS run's adoption.
        restarted.ActiveCount.Should().BeGreaterThanOrEqualTo(1, "the live orphan must be adopted, not killed (log #7)");

        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        details.InputTokens.Should().Be(1200);
        details.CacheReadInputTokens.Should().Be(840_000);
    }

    /// <summary>
    /// h9k task deliver pushes the branch and appends AgentSessionCompleted on an interactive
    /// run's stream with the delivering node's own id (Decisions Log #103), moving it to
    /// Verifying with no monitor. This proves the pickup half of that hand-off:
    /// ResumeStrandedPipelinesAsync notices the stranded run and starts driving it through the
    /// same pipeline a headless run's own completion would — matched here by the delivering
    /// node's own id, the ordinary <c>NodeId == nodeId</c> branch, exactly as
    /// <c>NodeLoad</c> counts it against that node's own ceiling from this point on.
    /// </summary>
    [Fact]
    public async Task Resume_stranded_pipelines_adopts_an_interactively_delivered_run_by_its_delivering_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        using DocumentStore store = NewStore();

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Interactive delivery test task", ["it completes"],
                    TaskType.Chore, null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            // Deliberately no TaskLease: an interactive claim holds no liveness lease.

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                "/tmp/wt-interactive-test", "task/interactive-test", ExecutorMode.Subscription, Now));
            // The production shape: h9k task deliver stamps its own node id, not the sentinel.
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now, DeliveredByNodeId: node.NodeId));
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node);

        await supervisor.ResumeStrandedPipelinesAsync(cts.Token);

        // GreaterThanOrEqualTo, not equal to (same reasoning as
        // Daemon_restart_mid_run_adopts_the_orphan_and_completes_it above): this node identity
        // is shared across the class's tests, so the sweep may also pick up another test's own
        // stranded Verifying/UnderReview run in the same pass. What this test pins down is that
        // OUR run — now carrying this node's own real id, not the interactive sentinel — was
        // among them, matched by ordinary node ownership.
        supervisor.ActiveCount.Should().BeGreaterThanOrEqualTo(1,
            "delivery stamped this node's own id on the run, so the ordinary NodeId == nodeId match picks it up");

        await using IQuerySession query = store.QuerySession();
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.NodeId.Should().Be(node.NodeId,
            "the delivering node's id replaces the interactive sentinel from AgentSessionCompleted onward");
    }

    [Fact]
    public async Task Agent_dying_without_a_result_fails_run_and_task_and_releases_the_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        int processId = SpawnFakeAgent(runId, "echo '{\"type\":\"assistant\"}'; exit 1");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Failed", cts.Token);
        details.FailureReason.Should().Contain("without a result");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("failure releases the lease");
    }

    /// <summary>
    /// Catch-up's defect (backlog 39, origin incident 2026-08-21): every adopted case except
    /// ReviewParked used to skip the lease refresh, so the expiry sweep that runs one line
    /// later in startup order requeued the very task adoption had just reattached — two
    /// generations, one worktree, a full review cycle each. Adoption must win outright: the
    /// lease is refreshed before the sweep ever looks, so the same task is never both adopted
    /// and requeued.
    /// </summary>
    [Fact]
    public async Task Catch_up_adoption_of_a_live_process_refreshes_the_lease_so_the_sweep_never_requeues_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        // Long-running agent; by the time the "restarted daemon" adopts it the heartbeat
        // already reads as expired — exactly the sleep-through-restart shape.
        int processId = SpawnFakeAgent(runId, "echo '{\"type\":\"assistant\"}'; sleep 30");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1,
                HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            });
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node);
        OrphanAdoption adoption = await supervisor.AdoptOrphansAsync(cts.Token);
        adoption.RunsAdopted.Should().BeGreaterThanOrEqualTo(1);

        // The sweep gets a process manager that reports every pid dead (Copilot review, PR
        // #30): SweepExpiredLeasesAsync has its own local-liveness check
        // (DispatchEngine.LocalRunProcessIsAlive), and a real UnixProcessManager here would
        // see the still-sleeping agent alive and refresh the lease on that basis alone — the
        // assertions below would pass even with AdoptOrphansAsync's own
        // RefreshAdoptedLeaseAsync deleted. Denying the sweep that signal means the only
        // thing that can keep the lease fresh by the time it runs is adoption's own refresh.
        DaemonOptions options = new() { MaxConcurrentTaskRuns = 500, LeaseTimeout = TimeSpan.FromSeconds(60) };
        DispatchEngine engine = new(
            store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            Options.Create(options), NullLogger<DispatchEngine>.Instance);
        await engine.SweepExpiredLeasesAsync(cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            task.State.Value.Should().Be(
                "Claimed", "adoption already reattached this run — the sweep must not also requeue it");

            TaskLease lease = (await query.LoadAsync<TaskLease>(taskId, cts.Token))!;
            lease.HeartbeatAt.Should().BeAfter(
                DateTimeOffset.UtcNow.AddMinutes(-1), "adoption refreshed the heartbeat before the sweep ran");
        }

        try
        {
            Process.GetProcessById(processId).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// The generation fence (backlog 39): a stale generation's run — one a requeue-and-
    /// reclaim already superseded — must not fail the task the live generation is working,
    /// nor take that generation's lease with it. Origin incident (2026-08-21 evening): this
    /// exact path wrote the task Failed while the live generation's fix session was
    /// mid-flight, and a dependent's crying-wolf hold re-armed off the lie.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_run_dying_does_not_fail_the_live_generations_task_or_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid staleRunId) = await SeedClaimedTaskAsync(store, cts.Token);

        // A requeue-and-reclaim moved the task on to generation 2 under a fresh run while
        // the stale run (generation 1) is still the one this test's fake agent is attached to.
        Guid liveRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, liveRunId, Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        int processId = SpawnFakeAgent(staleRunId, "echo '{\"type\":\"assistant\"}'; exit 1");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, staleRunId, processId, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        supervisor.StartMonitoring(staleRunId, RunPaths.GlobalDirectory(staleRunId), taskId, processId, startedAt, cts.Token);

        RunDetails staleDetails = await WaitForStateAsync(store, staleRunId, "Failed", cts.Token);
        staleDetails.FailureReason.Should().Contain("without a result");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim must survive the stale run's failure");
        task2.LeaseGeneration.Should().Be(2);

        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's failure must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));
    }

    /// <summary>
    /// The startup-adoption grouping fix itself (backlog 39, this task's headline acceptance
    /// criterion): a requeue-and-reclaim that landed while the daemon was down leaves one task
    /// with two non-terminal runs on this node — the stale generation this daemon was still
    /// tailing, and the fresh claim the live generation holds. AdoptOrphansAsync must adopt
    /// only the live one and retire the stale one, never both — adopting both double-books the
    /// task exactly like the live-process check a few lines above already exists to prevent.
    /// </summary>
    [Fact]
    public async Task Two_non_terminal_runs_on_one_task_adopt_the_live_generation_and_retire_the_stale_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid staleRunId) = await SeedClaimedTaskAsync(store, cts.Token);

        // A requeue-and-reclaim moved the task on to generation 2 under a fresh run while the
        // stale run (generation 1) is still recorded non-terminal for this node — exactly the
        // shape a catch-up running during downtime leaves behind.
        Guid liveRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, liveRunId, Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(liveRunId, new RunDispatched(
                liveRunId, taskId, node.NodeId, node.OwnerId, 2, DomainId.New(),
                "/tmp/wt-test-live", "task/test-live", ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        int staleProcessId = SpawnFakeAgent(staleRunId, "echo '{\"type\":\"assistant\"}'; sleep 30");
        await RecordProcessStartedAsync(store, staleRunId, staleProcessId, cts.Token);
        int liveProcessId = SpawnFakeAgent(liveRunId, "echo '{\"type\":\"assistant\"}'; sleep 30");
        await RecordProcessStartedAsync(store, liveRunId, liveProcessId, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        try
        {
            await supervisor.AdoptOrphansAsync(cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails staleDetails = (await query.LoadAsync<RunDetails>(staleRunId, cts.Token))!;
            staleDetails.State.Should().Be(RunState.Superseded,
                "the stale generation's run must be retired, not adopted alongside the live one");

            RunDetails liveDetails = (await query.LoadAsync<RunDetails>(liveRunId, cts.Token))!;
            liveDetails.State.IsLive.Should().BeTrue(
                "the live generation's own run is adopted and left running, not retired alongside its stale sibling");

            TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            task2.State.Value.Should().Be("Claimed", "the live generation's claim is untouched");
            task2.LeaseGeneration.Should().Be(2);
            (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
                "the stale run's retirement must not release the live generation's lease");

            logger.Lines.Should().Contain(line =>
                line.Contains(staleRunId.ToString()) && line.Contains("retired instead of adopted"),
                "the grouping check must name the stale run it chose not to adopt");
        }
        finally
        {
            try { Process.GetProcessById(staleProcessId).Kill(entireProcessTree: true); } catch (ArgumentException) { }
            try { Process.GetProcessById(liveProcessId).Kill(entireProcessTree: true); } catch (ArgumentException) { }
        }
    }

    /// <summary>
    /// The usage-limit shape parks rather than fails (backlog 40): the run stream
    /// records what was observed, but the task stays Claimed — worktree and lease intact —
    /// instead of going through TaskDecider.Fail and releasing them.
    /// </summary>
    [Fact]
    public async Task A_budget_exhausted_result_parks_the_run_and_leaves_the_task_claimed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        const string budgetResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Claude AI usage limit reached|1762952400"}""";
        int processId = SpawnFakeAgent(runId,
            $"echo '{{\"type\":\"assistant\"}}'; echo '{budgetResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "BudgetParked", cts.Token);
        details.ParkedReason.Should().Be("token budget exhausted - resumes when the subscription window resets");
        details.FailureReason.Should().BeNull("this is a wait, not a failure");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "the work is intact; nothing here demands a human retry");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "a budget park keeps the lease exactly the way a review park does");
    }

    /// <summary>
    /// The primary-session half of error-result retry (task: a session that reports an error
    /// result is retried once in place, measured 2026-09-05: bursty across only 18 distinct
    /// hours, the shape of a provider-side burst rather than a code defect): a generic error —
    /// distinct from the recognizable usage-limit shape the budget-park test above answers —
    /// is retried once, in the same worktree, rather than failing the run outright.
    /// </summary>
    [Fact]
    public async Task A_primary_sessions_error_result_is_retried_once_and_then_succeeds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        const string errorResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Internal server error"}""";
        int processId = SpawnFakeAgent(runId, $"echo '{{\"type\":\"assistant\"}}'; echo '{errorResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        ScriptedResumeExecutor resumeExecutor = new(ResultLine);
        RunSupervisor supervisor = NewSupervisor(
            store, node, executor: resumeExecutor,
            options: new DaemonOptions { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) });
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        // Not WaitForStateAsync(..., "Verifying", ...): AgentSessionCompleted moves the run to
        // Verifying at the FIRST (errored) session's own completion, before the retry even
        // spawns — polling for that state alone races the retry and can pass without ever
        // observing it complete (adversarial pre-PR review, cycle 1). Waiting for the second
        // AgentSessionCompleted — the resumed session's own — is what actually proves the
        // retry ran to a clean result, regardless of whatever state the run moves to next.
        await WaitForEventCountAsync<AgentSessionCompleted>(store, runId, 2, cts.Token);
        resumeExecutor.Spawns.Should().ContainSingle("exactly one retry spawn for the primary session");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        RunSessionErrorRetried retry = events.OfType<RunSessionErrorRetried>().Single();
        retry.Leg.Should().Be(RunSessionLeg.Build);
        events.OfType<AgentSessionCompleted>().Should().HaveCount(
            2, "the original errored session and its resumed retry both completed");
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.FailureReason.Should().BeNull("the transient error was retried, not failed");
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "the retried session is still doing the work");
    }

    /// <summary>
    /// The residue this task exists to narrow the failures down to: a second consecutive error
    /// on the primary session's own retry spends the one retry and fails the run exactly as
    /// before, with the identical reason text a genuinely broken session always got.
    /// </summary>
    [Fact]
    public async Task A_second_consecutive_error_on_the_primary_session_fails_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        const string errorResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Internal server error"}""";
        int processId = SpawnFakeAgent(runId, $"echo '{{\"type\":\"assistant\"}}'; echo '{errorResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        ScriptedResumeExecutor resumeExecutor = new(errorResultLine);
        RunSupervisor supervisor = NewSupervisor(
            store, node, executor: resumeExecutor,
            options: new DaemonOptions { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) });
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Failed", cts.Token);
        details.FailureReason.Should().Be(
            "Agent reported an error result.", "the second consecutive error fails the run with today's reason text unchanged");
        resumeExecutor.Spawns.Should().ContainSingle("only the one retry is spent before the run fails");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunSessionErrorRetried>().Should().ContainSingle(
            "only the first error earns a retry; the run fails outright on the second");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Failed");
    }

    /// <summary>
    /// AgentSessionCompleted moves the run to Verifying, and the retry decision
    /// (RunSessionErrorRetried) is durably saved, before the resumed spawn itself ever happens
    /// (RunSupervisor.RetryBuildSessionAsync's own doc comment) — so a daemon that dies in that
    /// gap and restarts must not read Verifying as "the work is done" and hand an unresumed
    /// build to the review loop. AdoptOrphansAsync has to finish the retry itself instead
    /// (independent pre-PR review, cycle 1, conformance finding).
    /// </summary>
    [Fact]
    public async Task Daemon_restart_mid_backoff_finishes_the_pending_build_session_retry_instead_of_treating_it_as_done()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        // The exact durable commit CompleteRunAsync makes before the backoff wait even starts —
        // simulating a crash landing right after it, before RetryBuildSessionAsync's own resumed
        // spawn ever ran.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            session.Events.Append(runId, new RunSessionErrorRetried(
                runId, RunSessionLeg.Build, Cycle: null, Lens: null, "Internal server error", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedResumeExecutor resumeExecutor = new(ResultLine);
        RunSupervisor restarted = NewSupervisor(store, node, executor: resumeExecutor);
        OrphanAdoption adoption = await restarted.AdoptOrphansAsync(cts.Token);

        resumeExecutor.Spawns.Should().ContainSingle(
            "the crash-stranded retry must be finished on restart, not silently dropped as already done");
        adoption.RunsAdopted.Should().BeGreaterThanOrEqualTo(1);

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunResumed>().Should().ContainSingle("the pending retry's own resumed spawn landed on restart");
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.PendingBuildSessionErrorRetry.Should().BeFalse("the resumed session actually spawned");
    }

    /// <summary>
    /// If the claim moved on during the backoff (an abandon, a lease-expiry requeue-and-reclaim,
    /// or any other release) there is nothing left here to resume — the same guard
    /// TokenBudgetRetryEngine.RetryOneAsync already applies before its own resume spawn. The run
    /// must be retired with RunSuperseded rather than left live at Verifying with no monitor
    /// (which would pin a NodeLoad slot forever) or failed with a reason implying the agent
    /// erred twice when it never got the chance to run again at all.
    /// </summary>
    [Fact]
    public async Task A_claim_that_moved_on_during_the_backoff_retires_the_run_instead_of_resuming_or_failing_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            session.Events.Append(runId, new RunSessionErrorRetried(
                runId, RunSessionLeg.Build, Cycle: null, Lens: null, "Internal server error", Now));
            await session.SaveChangesAsync(cts.Token);

            // The claim moved on before the retry's own resumed spawn ran: a second generation
            // reclaimed the task under a different run.
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedResumeExecutor resumeExecutor = new(ResultLine);
        RunSupervisor restarted = NewSupervisor(store, node, executor: resumeExecutor);
        await restarted.AdoptOrphansAsync(cts.Token);

        resumeExecutor.Spawns.Should().BeEmpty("the claim moved on; there is nothing left here to resume");

        await using IQuerySession query = store.QuerySession();
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.State.Value.Should().Be("Superseded", "retired explicitly rather than left live or reported as a second agent error");
        details.FailureReason.Should().BeNull("a superseded run was never actually failed");
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's own claim is untouched");
        task2.LeaseGeneration.Should().Be(2);
    }

    /// <summary>
    /// A follow-up that met a review thread it could not honestly judge parks for the human
    /// instead of pushing (Decisions Log #62): the never-loop rule the pre-PR fix session runs
    /// on, applied to a reviewer's thread. Both positions land beside the run, and the pipeline
    /// stops where it stands.
    /// </summary>
    [Fact]
    public async Task A_follow_up_that_disputes_a_review_thread_parks_instead_of_pushing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token, asFollowUp: true);

        const string disputed =
            "Answered three threads. The fourth asks for a different projection shape.\n"
            + "RESOLUTION: disputed";
        // printf, not echo: the JSON carries an escaped newline and sh's echo expands it,
        // which would split the result line in half and leave nothing parseable.
        int processId = SpawnFakeAgent(runId, $"printf '%s\\n' '{DisputedResultLine(disputed)}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "ReviewParked", cts.Token);
        details.ParkedReason.Should().Contain("disputed a review thread");
        details.ParkedReason.Should().Contain("h9k review resolve", "a park names the human's way back in");
        details.FailureReason.Should().BeNull("a park is a waiting state, not a failure");

        File.ReadAllText(RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId))).Should().Contain(
            "different projection shape", "the human reads the agent's position, not just the marker");
    }

    /// <summary>
    /// The rebase counterpart of the review-thread dispute above (backlog 44,
    /// AgentPromptBuilder.AppendRebaseDisputeRules): a rebase follow-up that hits a conflict it
    /// cannot honestly resolve parks the same way, but with its own artifact and reason text —
    /// pointing the human at <c>--needs-fixes</c> rather than the generic message, since a
    /// rebase dispute has no diff to sign off as merge-ready.
    /// </summary>
    [Fact]
    public async Task A_follow_up_that_disputes_a_rebase_conflict_parks_with_its_own_artifact_and_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(
            store, cts.Token, asFollowUp: true, followUpKind: FollowUpKind.Rebase);

        const string disputed =
            "Rebased cleanly except one file: both branches rewrote the same retry policy.\n"
            + "RESOLUTION: disputed";
        int processId = SpawnFakeAgent(runId, $"printf '%s\\n' '{DisputedResultLine(disputed)}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "ReviewParked", cts.Token);
        details.ParkedReason.Should().Contain("rebase conflict");
        details.ParkedReason.Should().Contain(
            "--needs-fixes", "merge-ready has no meaning here — nothing has been rebased yet");
        details.FailureReason.Should().BeNull("a park is a waiting state, not a failure");

        File.ReadAllText(RunPaths.RebaseConflictDisputeFile(RunPaths.GlobalDirectory(runId))).Should().Contain(
            "retry policy", "the human reads the agent's position, not just the marker");
        File.Exists(RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId))).Should().BeFalse(
            "a rebase dispute writes its own artifact, not the review-thread one");
    }

    /// <summary>
    /// The generation fence on the thread-dispute park (adversarial review, cycle 3): a
    /// requeue-and-reclaim moved the task on to generation 2 while this follow-up — still
    /// generation 1, the exact double-booking shape backlog 39 exists to close — was mid-run.
    /// Its agent session ends with a disputed verdict, so <c>ParkedOnThreadDisputeAsync</c>'s
    /// fence check rejects it; the rejection must retire the run with RunSuperseded, the same
    /// as every other fence rejection in this diff, rather than leaving it live in Verifying
    /// with no monitor watching it and a NodeLoad slot pinned until the next restart's orphan
    /// adoption sweep.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_thread_dispute_park_retires_the_run_instead_of_leaving_it_live()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token, asFollowUp: true);

        // A requeue-and-reclaim moved the task on to generation 2 under a different run while
        // this follow-up's agent session is still in flight — the same shape as backlog 39's
        // other stale-generation tests, applied to the thread-dispute park.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        const string disputed =
            "Answered three threads. The fourth asks for a different projection shape.\n"
            + "RESOLUTION: disputed";
        int processId = SpawnFakeAgent(runId, $"printf '%s\\n' '{DisputedResultLine(disputed)}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Superseded", cts.Token);
        details.ParkedReason.Should().BeNull("the stale generation's dispute is never actually parked");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim is untouched by the stale run's park");
        task2.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's retirement must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains(runId.ToString()) && line.Contains("retired as superseded"),
            "the fence rejection must name the run it retired instead of parked");
    }

    /// <summary>The same marker from a first run is text, not an answer: only a follow-up was asked.</summary>
    [Fact]
    public async Task The_dispute_marker_is_read_only_from_follow_up_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        int processId = SpawnFakeAgent(runId,
            $"printf '%s\\n' '{DisputedResultLine("Quoting the rules: RESOLUTION: disputed is how a follow-up parks.")}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node).StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        // Verifying is where a build run goes next; the gates then fail it on the missing
        // worktree, which is fine — what matters is that it was never parked.
        await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ParkedReason.Should().BeNull();
    }

    /// <summary>
    /// The marker is only an answer where the question was asked (Decisions Log #62). A CI-fix
    /// follow-up was never taught this vocabulary, so a summary of its own that happens to
    /// quote the line — the skill file is in the repo it is working in — is text, and parking
    /// on it would hand a human a "disputed review thread" whose position is about CI.
    /// </summary>
    [Fact]
    public async Task A_checks_follow_up_quoting_the_marker_is_not_read_as_a_dispute()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(
            store, cts.Token, asFollowUp: true, followUpKind: FollowUpKind.FailingChecks);

        int processId = SpawnFakeAgent(runId, $"printf '%s\\n' '{DisputedResultLine(
            "Fixed the flaky test. The skill file's park line reads RESOLUTION: disputed.")}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node).StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        // Verifying is where the run goes instead; the gates then fail it on the missing
        // worktree, which is fine — what matters is that it was never parked.
        await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ParkedReason.Should().BeNull();
        File.Exists(RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId))).Should().BeFalse(
            "nothing was disputed, so no position was written");
    }

    private static string DisputedResultLine(string summary) =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = summary,
            usage = new { input_tokens = 10, output_tokens = 10 },
        });

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedClaimedTaskAsync(
        DocumentStore store, CancellationToken cancellationToken,
        bool asFollowUp = false, FollowUpKind? followUpKind = null)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Executor test task", ["it completes"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        // A follow-up's kind lives on the task, recorded by the reopen that dispatched it, so
        // a run that has one is seeded through the real edges: claim, complete, reopen, claim.
        object[] reopen = [];
        if (followUpKind is not null)
        {
            var firstClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            task.Apply(firstClaim);
            var completed = TaskDecider.Complete(task, DomainId.New(), "https://github.com/x/y/pull/1", Now);
            task.Apply(completed);
            var reopened = TaskDecider.Reopen(
                task, DomainId.New(), "task/test", "CI checks failing on the pull request.",
                followUpKind, automatic: true, Now, node.OwnerId);
            task.Apply(reopened);
            reopen = [firstClaim, completed, reopened];
        }

        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, .. reopen, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
            "/tmp/wt-test", "task/test", ExecutorMode.Subscription, Now, IsFollowUp: asFollowUp));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    /// <summary>
    /// <see cref="SeedClaimedTaskAsync"/>'s task carries a project id that was never actually
    /// registered — fine for every other test here, since nothing along their paths ever loads
    /// <c>ProjectDetails</c> back. The error-result retry path does (<c>PrimarySessionResumer</c>
    /// needs the project's own <c>SkipPermissions</c>), so this variant registers a real project
    /// first and points the task at it.
    /// </summary>
    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedClaimedTaskWithProjectAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-session-error-retry-repo-{taskId:N}");
        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"session-error-retry-{taskId:N}", repositoryPath,
            new Uri("https://github.com/acme/web"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Executor test task", ["it completes"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
            "/tmp/wt-test", "task/test", ExecutorMode.Subscription, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    /// <summary>
    /// Scripted stand-in for the resumed process an error-result retry spawns (task: a session
    /// that reports an error result is retried once in place): writes the given result line
    /// straight into the run's main stream file, the same file a real `--resume` spawn's stdout
    /// redirect would truncate and rewrite.
    /// </summary>
    private sealed class ScriptedResumeExecutor(string resultLine) : IExecutor
    {
        private int _nextProcessId = 7_000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(RunPaths.StreamFile(request.RunDirectory), resultLine + "\n", cancellationToken);
            return new SpawnedAgent(_nextProcessId++, Now);
        }
    }

    private int SpawnFakeAgent(Guid runId, string script)
    {
        Directory.CreateDirectory(RunPaths.GlobalDirectory(runId));
        Process process = new();
        process.StartInfo = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"({script}) > \"{RunPaths.StreamFile(RunPaths.GlobalDirectory(runId))}\" 2> \"{RunPaths.StandardErrorFile(RunPaths.GlobalDirectory(runId))}\"");
        process.Start();
        return process.Id;
    }

    private static async Task<DateTimeOffset> RecordProcessStartedAsync(
        DocumentStore store, Guid runId, int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt;
        try
        {
            using Process process = Process.GetProcessById(processId);
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Very fast scripts can exit before we look them up; a nominal start time still
            // exercises the result-on-disk paths. Win32Exception is the zombie window the
            // production probes already guard (UnixProcessManager, DaemonProcess): the pid
            // still resolves after the child exits, but StartTime is no longer readable.
            startedAt = DateTimeOffset.UtcNow;
        }

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunProcessStarted(runId, processId, startedAt));
        await session.SaveChangesAsync(cancellationToken);
        return startedAt;
    }

    private static async Task<RunDetails> WaitForStateAsync(
        DocumentStore store, Guid runId, string state, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using IQuerySession query = store.QuerySession();
            RunDetails? details = await query.LoadAsync<RunDetails>(runId, cancellationToken);
            if (details?.State.Value == state)
            {
                return details;
            }

            await Task.Delay(250, cancellationToken);
        }

        await using IQuerySession final = store.QuerySession();
        RunDetails? reached = await final.LoadAsync<RunDetails>(runId, cancellationToken);
        throw new TimeoutException(
            $"Run {runId} never reached state {state}; it is {reached?.State.Value ?? "(no projection)"} "
            + $"(failure: {reached?.FailureReason ?? "none"}, park: {reached?.ParkedReason ?? "none"}).");
    }

    /// <summary>
    /// Polls the run's own stream, rather than its projected state, for at least
    /// <paramref name="count"/> events of type <typeparamref name="T"/> — a stable wait for a
    /// milestone that may not correspond to any single stable state (a resumed session's own
    /// completion, for instance, immediately hands off into whatever the pipeline does next).
    /// </summary>
    private static async Task WaitForEventCountAsync<T>(
        DocumentStore store, Guid runId, int count, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using IQuerySession query = store.QuerySession();
            int actual = (await query.Events.FetchStreamAsync(runId, token: cancellationToken))
                .Count(e => e.Data is T);
            if (actual >= count)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Run {runId} never recorded {count} {typeof(T).Name} event(s).");
    }

    private static RunSupervisor NewSupervisor(
        DocumentStore store, NodeContext node, IProcessManager? processManager = null, ILogger<RunSupervisor>? logger = null,
        IExecutor? executor = null, DaemonOptions? options = null)
    {
        processManager ??= ProcessManagers.ForCurrentPlatform();
        options ??= new DaemonOptions();
        IExecutor resolvedExecutor =
            executor ?? new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processManager, Options.Create(new DaemonOptions()));
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), resolvedExecutor, processManager);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processManager, Options.Create(new DaemonOptions())), processManager, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance);
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processManager, Options.Create(new DaemonOptions())), processManager,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        PrimarySessionResumer primarySessionResumer = new(resolvedExecutor);
        return new RunSupervisor(store, node, processManager, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            primarySessionResumer, Options.Create(options), logger ?? NullLogger<RunSupervisor>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
