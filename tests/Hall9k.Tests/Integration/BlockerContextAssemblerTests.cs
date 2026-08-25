using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// Context assembly at claim time (Decisions Log #36): raw handoffs at or below the node's
/// blocker threshold, a condensing session above it, and the raw handoffs again whenever
/// that session cannot deliver — condensing is an optimization over a context that already
/// exists, so it may never be the reason a dispatch loses one.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class BlockerContextAssemblerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    /// <summary>
    /// Writes the scripted summary as a session's terminal result event, the way a real
    /// claude session ends (log #2). A null script spawns a process that never reports one.
    /// </summary>
    private sealed class ScriptedExecutor(string? summary, FakeProcessManager processes) : IExecutor
    {
        private int _nextPid = 7000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            int pid = _nextPid++;
            string streamFile = request.SessionArtifactName is { } name
                ? RunPaths.SessionStreamFile(request.RunDirectory, name)
                : RunPaths.StreamFile(request.RunDirectory);
            Directory.CreateDirectory(request.RunDirectory);

            if (summary is null)
            {
                // Dead on arrival with nothing written: the died-without-a-result path.
                return new SpawnedAgent(pid, Now);
            }

            processes.MarkAlive(pid);
            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 20 },
                ["result"] = summary,
            });
            await File.WriteAllTextAsync(streamFile, line + "\n", cancellationToken);
            return new SpawnedAgent(pid, Now);
        }
    }

    [Fact]
    public async Task At_or_below_the_threshold_the_handoffs_pass_through_raw()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 3, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new("condensed", processes);
        string? context = await NewAssembler(store, executor, processes, threshold: 3)
            .AssembleAsync(runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        executor.Spawns.Should().BeEmpty("three blockers is not above a threshold of three");
        context.Should().Contain("Handoff from blocker 1");
        context.Should().Contain("Handoff from blocker 3");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ContextSynthesisSessions.Should().Be(0);
    }

    [Fact]
    public async Task Above_the_threshold_a_synthesis_session_condenses_the_handoffs_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new("## What your blockers handed down\n\nAll four agreed on one convention.", processes);
        string? context = await NewAssembler(store, executor, processes, threshold: 3)
            .AssembleAsync(runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        executor.Spawns.Should().ContainSingle("four blockers is above a threshold of three");
        executor.Spawns[0].Prompt.Should().Contain("Handoff from blocker 1",
            "the condenser is handed the raw handoffs it condenses");
        context.Should().Be("## What your blockers handed down\n\nAll four agreed on one convention.");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ContextSynthesisSessions.Should().Be(1, "the pass is recorded on the run that paid for it");
        run.OutputTokens.Should().Be(20, "a platform-dispatched session records its tokens (log #30)");

        string artifact = await File.ReadAllTextAsync(RunPaths.BlockerContextFile(RunPaths.GlobalDirectory(runId)), cts.Token);
        artifact.Should().Contain("All four agreed",
            "what the agent was actually handed is inspectable beside the run's other artifacts");
    }

    [Fact]
    public async Task A_synthesis_that_returns_nothing_falls_back_to_the_raw_handoffs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new(null, processes);
        string? context = await NewAssembler(store, executor, processes, threshold: 3)
            .AssembleAsync(runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        context.Should().Contain("Handoff from blocker 1",
            "a dead condenser costs the run its condensing, never its context");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ContextSynthesized.Should().BeFalse(
            "the run records that it fell back rather than that it was condensed");
    }

    /// <summary>
    /// Non-blank is not the bar. The condensed text is pasted into the dependent's prompt
    /// verbatim, so a response that drops the document's heading would land there as
    /// unlabelled prose continuing the objective — the structure lost silently, which is the
    /// one thing the raw handoffs can never do.
    /// </summary>
    [Fact]
    public async Task A_synthesis_that_answers_without_the_heading_falls_back_to_the_raw_handoffs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new(
            "Sure — here is a summary of what the four blockers said.", processes);
        string? context = await NewAssembler(store, executor, processes, threshold: 3)
            .AssembleAsync(runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        context.Should().Contain("Handoff from blocker 1",
            "a document the prompt cannot label is no more usable than an empty one");
        context.Should().NotContain("here is a summary");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ContextSynthesized.Should().BeFalse(
            "the run records the fallback rather than claiming it was condensed");
    }

    /// <summary>
    /// The ceiling that keeps one hung condenser from stalling the node: RunLauncher.LaunchAsync
    /// is awaited inside the dispatch loop, so this wait is the one place a dispatch blocks on
    /// an agent and the only place a timeout is load-bearing.
    /// </summary>
    [Fact]
    public async Task A_synthesis_that_hangs_is_terminated_and_the_dispatch_starts_on_the_raw_handoffs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        // Alive, and never writes a result: the hung-session shape.
        SilentExecutor executor = new(processes);
        BlockerContextAssembler assembler = new(
            store, executor, processes,
            Options.Create(new DaemonOptions
            {
                BlockerSynthesisThreshold = 3,
                BlockerSynthesisTimeout = TimeSpan.FromMilliseconds(200),
            }),
            NullLogger<BlockerContextAssembler>.Instance);

        string? context = await assembler.AssembleAsync(
            runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        context.Should().Contain("Handoff from blocker 1",
            "the wait ends, but the context the run already had does not");
        processes.IsAlive(executor.SpawnedPid, Now).Should().BeFalse(
            "a timed-out condenser is terminated rather than left burning tokens for nobody");

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ContextSynthesized.Should().BeFalse();
    }

    /// <summary>Spawns a process that stays alive and never reports a result.</summary>
    private sealed class SilentExecutor(FakeProcessManager processes) : IExecutor
    {
        public int SpawnedPid { get; private set; }

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            SpawnedPid = 8100;
            processes.MarkAlive(SpawnedPid);
            return Task.FromResult(new SpawnedAgent(SpawnedPid, Now));
        }
    }

    /// <summary>
    /// The daemon stopping mid-wait is not a timeout, so the cancellation propagates and the
    /// claim is abandoned rather than quietly started on the raw handoffs. What the timeout
    /// path established still holds, though: nothing else will ever adopt this session —
    /// adoption reattaches to pids recorded by RunProcessStarted, which a synthesis session
    /// never reaches — so letting it through without a kill would strand an agent burning
    /// tokens for a dispatch that no longer exists.
    /// </summary>
    [Fact]
    public async Task A_daemon_shutdown_mid_wait_terminates_the_condenser_before_the_cancellation_propagates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        using CancellationTokenSource shutdown = new();
        SilentExecutor executor = new(processes);
        BlockerContextAssembler assembler = new(
            store, executor, new ShutdownOnFirstLivenessCheck(processes, shutdown),
            Options.Create(new DaemonOptions
            {
                BlockerSynthesisThreshold = 3,
                // Far beyond the test's life: the wait ends on the daemon's token, not this.
                BlockerSynthesisTimeout = TimeSpan.FromMinutes(5),
            }),
            NullLogger<BlockerContextAssembler>.Instance);

        Func<Task> assemble = () => assembler.AssembleAsync(
            runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, shutdown.Token);

        await assemble.Should().ThrowAsync<OperationCanceledException>(
            "cancellation of the daemon itself propagates as it should, and is not a timeout");
        processes.IsAlive(executor.SpawnedPid, Now).Should().BeFalse(
            "a session the dispatch has stopped caring about must not outlive it");
    }

    /// <summary>
    /// Stops the daemon the first time the wait asks whether the session is still alive,
    /// which is the shutdown landing squarely inside the wait rather than around it.
    /// </summary>
    private sealed class ShutdownOnFirstLivenessCheck(
        FakeProcessManager inner, CancellationTokenSource shutdown) : IProcessManager
    {
        public SpawnedProcess Spawn(ProcessSpawnRequest request) => inner.Spawn(request);

        public bool IsAlive(int processId, DateTimeOffset startedAt)
        {
            bool alive = inner.IsAlive(processId, startedAt);
            shutdown.Cancel();
            return alive;
        }

        public void Terminate(int processId, DateTimeOffset startedAt) => inner.Terminate(processId, startedAt);
    }

    [Fact]
    public async Task A_task_with_no_blockers_assembles_nothing_and_spawns_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 0, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new("condensed", processes);
        string? context = await NewAssembler(store, executor, processes, threshold: 3)
            .AssembleAsync(runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        context.Should().BeNull("historical tasks declared no edges and dispatch exactly as they always did");
        executor.Spawns.Should().BeEmpty();
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static BlockerContextAssembler NewAssembler(
        DocumentStore store, IExecutor executor, FakeProcessManager processes, int threshold) =>
        new(store, executor, processes,
            Options.Create(new DaemonOptions { BlockerSynthesisThreshold = threshold }),
            NullLogger<BlockerContextAssembler>.Instance);

    /// <summary>
    /// A dependent whose run stream already exists (the launcher appends RunDispatched before
    /// assembling), behind <paramref name="blockerCount"/> blockers that each closed out with
    /// a handoff of their own.
    /// </summary>
    private static async Task<TaskDetails> SeedAsync(
        DocumentStore store, Guid runId, int blockerCount, CancellationToken cancellationToken)
    {
        Guid ownerId = DomainId.New();
        Guid dependentId = DomainId.New();
        List<Guid> blockers = [];

        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 1; i <= blockerCount; i++)
            {
                Guid blockerId = DomainId.New();
                blockers.Add(blockerId);
                session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                    TaskDecider.Add(
                        blockerId, DomainId.New(), $"Blocker {i}", ["merged"], TaskType.Chore,
                        null, null, null, Now, ownerId),
                    ownerId, Now));

                Guid blockerRunId = DomainId.New();
                session.Events.StartStream<RunAggregate>(blockerRunId,
                    new RunDispatched(
                        blockerRunId, blockerId, DomainId.New(), ownerId, 1, DomainId.New(),
                        "/tmp/worktree", $"task/{i}", ExecutorMode.Subscription, Now),
                    new PullRequestOpened(blockerRunId, $"https://github.com/x/y/pull/{i}", i, Now),
                    new PullRequestMerged(blockerRunId, Now, Now),
                    new RunHandoffRecorded(
                        blockerRunId, HandoffOutcome.Captured, $"Handoff from blocker {i}.", Now),
                    new RunCompleted(blockerRunId, Now));
            }

            await session.SaveChangesAsync(cancellationToken);
        }

        // Publish is the cycle-detection gate and reads the committed graph, so the dependent
        // is declared only once its blockers exist — the ordering a human follows too.
        await using IQuerySession query = store.QuerySession();
        TaskDependencyGraph graph = await TaskSeed.DependencyGraphAsync(query, blockers, cancellationToken);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(dependentId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    dependentId, DomainId.New(), "Integrate everything", ["it integrates"], TaskType.Chore,
                    null, null, null, Now, ownerId, blockedBy: blockers),
                ownerId, Now, graph));

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, dependentId, DomainId.New(), ownerId, 1, DomainId.New(),
                "/tmp/worktree", "task/integrate", ExecutorMode.Subscription, Now));

            await session.SaveChangesAsync(cancellationToken);
        }

        await using IQuerySession reread = store.QuerySession();
        return (await reread.LoadAsync<TaskDetails>(dependentId, cancellationToken))!;
    }

    private static ProjectDetails SomeProject() => new()
    {
        Name = "hall9k",
        BaseBranch = "main",
        Model = AgentModel.Unknown,
    };

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
}
