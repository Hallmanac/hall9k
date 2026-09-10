using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The command surfaces that need a real store and, each for its own reason, redirect
/// process-wide platform state to get it — which is why all five already sat serialized together
/// in the <c>Hall9kHome</c> collection, and why sharing one container between them costs nothing
/// they were not already paying. Each was its own class for two to seven tests; every assertion
/// here reads what its own test seeded, by id or by its own file name, so a sibling seam's rows
/// are as invisible as a sibling test's already were.
/// <para>
/// Blocker context assembly at claim time (Decisions Log #36): raw handoffs at or below the
/// node's blocker threshold, a condensing session above it, and the raw handoffs again whenever
/// that session cannot deliver — condensing is an optimization over a context that already
/// exists, so it may never be the reason a dispatch loses one.
/// </para>
/// <para>
/// <see cref="TaskRegisterSessionCommand.RegisterAsync"/> — the store round trip (the
/// Claimed+interactive and run-state guards, the double-booking guard, and the append itself)
/// that <see cref="Hall9k.Tests.Cli.TaskRegisterSessionCommandTests"/>' own doc comment calls that
/// command's integration-tier concern (independent pre-PR review, cycle 1, both lenses, medium:
/// nothing before it exercised the command's actual domain behavior).
/// </para>
/// <para>
/// Backlog tracking — every published task is tracked automatically: the two internal helpers
/// <c>h9k task publish</c> calls once it decides a project's backlog policy applies
/// (<see cref="TaskPushToJiraCommand.TryAutoRequestAsync"/> for jira and
/// <see cref="TaskLinkIssueCommand.LinkAsync"/> for github-issues, the recording half once the
/// platform's own <c>gh issue create</c> claim has been read back), against a real Postgres store
/// rather than gh or Jira's HTTP. gh's own read-back is unit-tested with a recorded process at the
/// connector level instead (<c>Hall9k.Tests.Connectors.GitHubWorkItemProviderTests</c>), the same
/// split the rest of this codebase draws between deciders and adapters.
/// </para>
/// <para>
/// Connection credential rotation — what happens to the token a previous registration stored when
/// the connection is pointed somewhere else (PLAN.md §10). The stored file is named from the site
/// and the account precisely so that re-registering the same account overwrites it, and that
/// guarantee holds for exactly one shape of rotation. Move the credential to an environment
/// variable and the connection records <c>env:…</c> while the file keeps a working token;
/// re-register the same site as a different account and the derived name changes, so the old
/// account's token survives beside the new one. Either way a secret nobody meant to keep sits on
/// disk with nothing in <c>h9k connection list</c> mentioning it. Origin incident (2026-08-21):
/// the pre-PR review of the Jira branch traced both paths. Those tests each store under a file
/// name of their own, because the last question the decision asks is whether ANY connection still
/// points at that file — a query over every connection, so a name reused across tests would make
/// one test's live credential look like another's superseded one. That is the same discipline that
/// makes them safe beside the other seams here.
/// </para>
/// <para>
/// <see cref="SpendPressure.ReadAsync"/> reconciles a published <see cref="NodeDispatchLoad"/> row
/// against this shell's freshly-resolved config the same way for both halves of the spend setting
/// — the budget and the period it resets on — because a daemon that has never had a budget still
/// publishes a compiled-default period on every sweep (independent pre-PR review, cycle 7,
/// adversarial lens: trusting that default whenever it was merely non-empty, rather than gating it
/// on the same <c>BudgetIsEnforced</c> flag the budget itself uses, reported a window nobody
/// configured and the daemon was not enforcing).
/// </para>
/// </summary>
// Several of these drive a command all the way to its success path, which rings the doorbell
// (Hall9k.Cli.Infrastructure.Doorbell). That resolves its connection through the ambient
// HALL9K_CONNECTION_STRING rather than this fixture, so each such test points it at the fixture
// for its own duration. The register-session tests mutate CLAUDE_PID, and the spend-pressure ones
// mutate Hall9k__SpendBudgetTokens and Hall9k__SpendPeriod. All of it is process-wide state, same
// as DatabaseDoctorTests, which is what puts this class in the Hall9kHome collection.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class StoreBackedCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    // Declared above _home on purpose: field initializers run in declaration order, so this has to
    // read HALL9K_HOME before SetTempHome overwrites it.
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");

    private readonly string _home = SetTempHome();

    /// <summary>
    /// The home every seam in this class reads through, redirected before anything reads it. A
    /// field initializer rather than a constructor body, because a type with a primary constructor
    /// cannot declare one of its own. The spend-pressure tests set HALL9K_HOME to this same
    /// directory again themselves, after creating it, which is what they always did.
    /// </summary>
    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    /// <summary>
    /// Writes the scripted summary as a session's terminal result event, the way a real
    /// claude session ends (log #2), then returns without marking the pid alive — the scripted
    /// session already ran to completion synchronously, so SessionResultWaiter completes off the
    /// result file alone rather than waiting out a process that will never die. A null script
    /// spawns a process that never reports one.
    /// </summary>
    private sealed class ScriptedExecutor(string? summary) : IExecutor
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
        DocumentStore store = postgres.Store;

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 3, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new("condensed");
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
        DocumentStore store = postgres.Store;

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new("## What your blockers handed down\n\nAll four agreed on one convention.");
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
        DocumentStore store = postgres.Store;

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new(null);
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
        DocumentStore store = postgres.Store;

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 4, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new(
            "Sure — here is a summary of what the four blockers said.");
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
        DocumentStore store = postgres.Store;

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
        DocumentStore store = postgres.Store;

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

        public IReadOnlyList<int> TerminateTree(int processId, DateTimeOffset startedAt) =>
            inner.TerminateTree(processId, startedAt);
    }

    [Fact]
    public async Task A_task_with_no_blockers_assembles_nothing_and_spawns_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid runId = DomainId.New();
        TaskDetails dependent = await SeedAsync(store, runId, blockerCount: 0, cts.Token);

        FakeProcessManager processes = new();
        ScriptedExecutor executor = new("condensed");
        string? context = await NewAssembler(store, executor, processes, threshold: 3)
            .AssembleAsync(runId, RunPaths.GlobalDirectory(runId), dependent, SomeProject(), "/tmp/worktree", ExecutorMode.Subscription, cts.Token);

        context.Should().BeNull("historical tasks declared no edges and dispatch exactly as they always did");
        executor.Spawns.Should().BeEmpty();
    }


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
        // Restored rather than cleared: this class holds five seams that each redirect
        // process-wide state, and putting back what was there is the only teardown that is correct
        // whichever of them ran last.
        Environment.SetEnvironmentVariable("HALL9K_HOME", _previousHome);

        foreach ((string name, string? value) in _previousSpendSettings)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

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

    // ── h9k task register-session ──
    private static readonly DateTimeOffset SessionNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refuses_when_the_task_carries_no_active_interactive_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Never claimed interactively", ["done"], TaskType.Chore,
                    null, null, null, SessionNow, node.OwnerId),
                node.OwnerId, SessionNow);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*only a task with an active interactive claim*");
    }

    [Fact]
    public async Task Refuses_against_a_stale_current_run_id_with_no_run_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        // Deliberately no RunAggregate stream started for the claimed run: this reproduces a
        // claim whose run record never landed (the process died while cutting the worktree).
        (Guid taskId, _, _) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.CurrentRunId.Should().NotBeNull();

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*has no record*");
    }

    [Fact]
    public async Task Refuses_once_the_run_has_already_moved_to_the_standard_pipeline()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, SessionNow));
            seed.Events.Append(runId, new AgentSessionCompleted(runId, SessionNow, node.NodeId));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*h9k task deliver (or handback)*");
    }

    [Fact]
    public async Task Registers_the_first_session_and_appends_InteractiveSessionStarted_on_the_runs_own_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, SessionNow));
            await seed.SaveChangesAsync(cts.Token);
        }

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, Environment.ProcessId.ToString()));

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        (Guid registeredRunId, int processId) = await TaskRegisterSessionCommand.RegisterAsync(
            session, details, force: false, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        registeredRunId.Should().Be(runId);
        processId.Should().Be(Environment.ProcessId);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ActiveSessions.Should().ContainSingle(active => active.Role == AgentRole.Interactive
            && active.ProcessId == Environment.ProcessId
            && active.Name == string.Empty,
            "no ~/.claude/sessions/<pid>.json exists for this test process, so the honest blank — not a "
            + "fabricated task-shortid-role guess — is what gets recorded (independent pre-PR review, "
            + "conformance lens, cycle 1)");
    }

    /// <summary>
    /// The double-booking guard this fix adds (independent pre-PR review, both lenses, cycle 1,
    /// medium): a second registration attempt, from a session CLAUDE_PID identifies as a
    /// different, still-live process than the one already recorded, must be refused rather than
    /// silently overwriting the first session's own liveness record — the exact failure
    /// <c>TaskWorkCommand.ReenterAsync</c>'s own comment names for the parallel <c>h9k task work</c>
    /// door onto this same collision.
    /// </summary>
    [Fact]
    public async Task Refuses_a_second_registration_while_the_first_sessions_process_is_still_alive()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        using Process thisProcess = Process.GetCurrentProcess();
        DateTimeOffset firstSessionStartedAt = InteractiveSessionLiveness.ReadStartedAt(thisProcess);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, SessionNow));
            // The "first session" is this very test process — guaranteed alive for the whole
            // test, with its own genuinely-readable start time, exactly as a real self-registered
            // session's own record would read.
            seed.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), firstSessionStartedAt, Environment.ProcessId, Environment.MachineName,
                "register-session-first"));
            await seed.SaveChangesAsync(cts.Token);
        }

        // The "second session" claims a different CLAUDE_PID than the one already recorded — a
        // second terminal, or a stale prompt pasted again — so IsSelfInvocation must not match it.
        int otherPid = Environment.ProcessId == 1 ? 2 : 1;
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, otherPid.ToString()));

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*still attached in another terminal*");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ActiveSessions.Should().ContainSingle(active => active.ProcessId == Environment.ProcessId,
            "the refused second registration must never overwrite the first session's own record");
    }

    /// <summary>
    /// The same session re-registering (a retry, or the operator re-pasting the identical
    /// starting prompt into the same still-running session) matches on CLAUDE_PID and must not be
    /// refused — the exemption <see cref="InteractiveSessionLiveness.IsSelfInvocation"/> exists
    /// for, exercised here through the real double-booking guard rather than in isolation.
    /// </summary>
    [Fact]
    public async Task Allows_the_same_session_to_register_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        using Process thisProcess = Process.GetCurrentProcess();
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(thisProcess);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, SessionNow,
                SessionName: "register-session-" + SessionRoleName.InteractiveClaim));
            seed.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), startedAt, Environment.ProcessId, Environment.MachineName,
                "register-session-first"));
            await seed.SaveChangesAsync(cts.Token);
        }

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, Environment.ProcessId.ToString()));

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// The fence added by this fix (independent pre-PR review, adversarial lens, cycle 1): two
    /// sessions racing the prompt pasted twice both load <c>RunDetails</c> while
    /// <c>ActiveSessions</c> still reads empty, so both pass the double-booking check above and
    /// both call <see cref="TaskRegisterSessionCommand.RegisterAsync"/> — without a fence on the
    /// append, the second would silently overwrite the first session's own liveness record
    /// (<c>RunDetailsProjection.StartSession</c>'s single-slot <c>ActiveSessions</c>) rather than
    /// losing loudly at save time.
    /// </summary>
    [Fact]
    public async Task Fences_two_concurrent_first_registrations_racing_the_same_empty_ActiveSessions()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, SessionNow));
            await seed.SaveChangesAsync(cts.Token);
        }

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, Environment.ProcessId.ToString()));

        await using IDocumentSession first = store.LightweightSession();
        await using IDocumentSession second = store.LightweightSession();
        TaskDetails details1 = (await first.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        TaskDetails details2 = (await second.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        // Both read ActiveSessions still empty (neither has appended yet), so both pass the
        // double-booking check the same way the two-terminal race in the scenario above does.
        await TaskRegisterSessionCommand.RegisterAsync(first, details1, force: false, cts.Token);
        await TaskRegisterSessionCommand.RegisterAsync(second, details2, force: false, cts.Token);

        await first.SaveChangesAsync(cts.Token);
        Func<Task> losing = () => second.SaveChangesAsync(cts.Token);

        await losing.Should().ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            "the fence must catch the second registration at save time rather than let it silently overwrite "
            + "the first session's own ActiveSessions record");
    }

    private static async Task<(Guid TaskId, Guid RunId, NodeContext Node)> SeedClaimedInteractiveTaskAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession seed = store.LightweightSession();
        TaskAggregate task = new();
        TaskAdded added = TaskDecider.Add(taskId, DomainId.New(), "Register a session against an interactive claim",
            ["done"], TaskType.Chore, null, null, null, SessionNow, node.OwnerId);
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, SessionNow, node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], SessionNow, node.OwnerId);
        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, SessionNow);
        seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, claimed);
        await seed.SaveChangesAsync(cancellationToken);

        return (taskId, runId, node);
    }

    // ── backlog tracking ──
    private static readonly DateTimeOffset BacklogNow = new(2026, 8, 27, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Jira_auto_request_is_skipped_with_no_connection_registered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        await ClearConnectionsAsync(store, cts.Token);
        Guid taskId = await SeedTaskAsync(store, cts.Token);

        TaskPushToJiraCommand.AutoRequestOutcome outcome =
            await TaskPushToJiraCommand.TryAutoRequestAsync(store, taskId, DomainId.New(), cts.Token);

        outcome.Should().Be(TaskPushToJiraCommand.AutoRequestOutcome.NoJiraConnection);

        TaskAggregate? task = await LoadAsync(store, taskId, cts.Token);
        task!.PendingPublicationProvider.Should().BeNull("nothing was requested without a connection to verify against");
    }

    [Fact]
    public async Task Jira_auto_request_asks_for_a_card_once_a_connection_is_registered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        await ClearConnectionsAsync(store, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ConnectionAggregate>(DomainId.New(), ConnectionDecider.Register(
                DomainId.New(), DomainId.New(), WorkItemProvider.Jira, "brian@example.com",
                CredentialReference.EnvironmentVariable("JIRA_TOKEN"), BacklogNow,
                new Uri("https://hall9k.atlassian.net")));
            await session.SaveChangesAsync(cts.Token);
        }

        Guid taskId = await SeedTaskAsync(store, cts.Token);

        // A successful request rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell), which
        // resolves its connection off HALL9K_CONNECTION_STRING rather than this fixture, so it
        // has to be pointed at the fixture for the one call below that actually succeeds.
        string? previousConnectionString = Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        TaskPushToJiraCommand.AutoRequestOutcome outcome;
        try
        {
            outcome = await TaskPushToJiraCommand.TryAutoRequestAsync(store, taskId, DomainId.New(), cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }

        outcome.Should().Be(TaskPushToJiraCommand.AutoRequestOutcome.Requested);

        TaskAggregate? task = await LoadAsync(store, taskId, cts.Token);
        task!.PendingPublicationProvider.Should().Be(WorkItemProvider.Jira);

        // TaskPublishCommand.TrackInBacklogAsync calls this after the publish transaction has
        // already committed, and its own doc comment promises the failure is "reported and
        // swallowed" rather than left to escape. A second outstanding request on the same task
        // is exactly the shape that promise has to hold for: TaskDecider.RequestWorkItemPublication
        // refuses it with DomainConflictException rather than silently no-opping, and this asserts
        // the method lets that through as a DomainException the caller can catch — reusing the
        // connection above rather than registering a second one, since only one Jira connection is
        // supported per install and a second registration in this shared-database test class would
        // make WorkItemConnections.FindJiraConnectionAsync itself ambiguous. The refusal comes from
        // TaskDecider before RequestAsync ever reaches the doorbell, so this does not need the
        // connection string redirected the way the first call above did.
        Func<Task> secondRequest = () => TaskPushToJiraCommand.TryAutoRequestAsync(store, taskId, DomainId.New(), cts.Token);

        (await secondRequest.Should().ThrowAsync<DomainConflictException>()).Which.Message
            .Should().Contain("already has a jira publication outstanding");

        // The same outstanding request has to stop TaskLinkIssueCommand.LinkAsync too, not just a
        // second RequestWorkItemPublication: linking a GitHub issue made by hand while this Jira
        // session is still running would clear PendingPublicationProvider out from under it
        // (TaskAggregate.Apply(WorkItemLinked)), so CardPublicationEngine stops watching the run
        // and the card it eventually writes has nothing recording or cleaning it up.
        await using (IDocumentSession session = store.LightweightSession())
        {
            Func<Task> link = () => TaskLinkIssueCommand.LinkAsync(
                session, taskId,
                new ImportedWorkItem(
                    new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#99"),
                    "Made by hand", null, WorkItemStatus.Open, null, BacklogNow),
                DomainId.New(), cts.Token);

            (await link.Should().ThrowAsync<DomainConflictException>()).Which.Message
                .Should().Contain("jira publication request outstanding");
        }

        task = await LoadAsync(store, taskId, cts.Token);
        task!.PendingPublicationProvider.Should().Be(WorkItemProvider.Jira, "the refused link must not disturb the running session's own bookkeeping");
        task.ExternalReference.Should().BeNull();
    }

    [Fact]
    public async Task Linking_a_freshly_created_issue_records_it_and_a_repeat_is_quiet()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        Guid taskId = await SeedTaskAsync(store, cts.Token);
        ImportedWorkItem issue = new(
            new ExternalReference(WorkItemProvider.GitHub, "Hallmanac/hall9k#77"),
            "Track every published task",
            null,
            WorkItemStatus.Open,
            new Uri("https://github.com/Hallmanac/hall9k/issues/77"),
            BacklogNow);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskLinkIssueCommand.LinkOutcome outcome =
                await TaskLinkIssueCommand.LinkAsync(session, taskId, issue, DomainId.New(), cts.Token);
            outcome.Should().Be(TaskLinkIssueCommand.LinkOutcome.Linked);
            await session.SaveChangesAsync(cts.Token);
        }

        TaskAggregate? task = await LoadAsync(store, taskId, cts.Token);
        task!.ExternalReference.Should().Be(issue.Reference);

        await using (IDocumentSession session = store.LightweightSession())
        {
            // An agent (or, here, TaskPublishCommand) that could not tell whether an earlier
            // attempt landed calls this again; the second call must not throw or double-append.
            TaskLinkIssueCommand.LinkOutcome outcome =
                await TaskLinkIssueCommand.LinkAsync(session, taskId, issue, DomainId.New(), cts.Token);
            outcome.Should().Be(TaskLinkIssueCommand.LinkOutcome.AlreadyLinked);
            await session.SaveChangesAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Linking_an_issue_another_live_task_already_carries_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        ExternalReference reference = new(WorkItemProvider.GitHub, "Hallmanac/hall9k#88");
        Guid firstTaskId = await SeedTaskAsync(store, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            await TaskLinkIssueCommand.LinkAsync(
                session, firstTaskId,
                new ImportedWorkItem(reference, "First", null, WorkItemStatus.Open, null, BacklogNow),
                DomainId.New(), cts.Token);
            await session.SaveChangesAsync(cts.Token);
        }

        Guid secondTaskId = await SeedTaskAsync(store, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            Func<Task> secondLink = () => TaskLinkIssueCommand.LinkAsync(
                session, secondTaskId,
                new ImportedWorkItem(reference, "Second", null, WorkItemStatus.Open, null, BacklogNow),
                DomainId.New(), cts.Token);

            (await secondLink.Should().ThrowAsync<DomainConflictException>()).Which.Message
                .Should().Contain("github:Hallmanac/hall9k#88");
        }
    }


    private static async Task<TaskAggregate?> LoadAsync(IDocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        return await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
    }

    private static async Task<Guid> SeedTaskAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<OwnerAggregate>(ownerId, OwnerDecider.Register(
            ownerId, "Brian Hall", "brian@hallmanac.com", BacklogNow));

        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.GitHub, "Hallmanac", CredentialReference.GhCli, BacklogNow));

        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, ownerId, connectionId, "hall9k", "/repos/hall9k.git",
            new Uri("https://github.com/Hallmanac/hall9k"), null, BacklogNow));

        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, "Track every published task automatically",
            ["A project setting declares the backlog policy"], TaskType.Feature,
            agentContext: null, constraints: null, externalReference: null, BacklogNow, ownerId));

        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    // ── connection credential rotation ──
    private static readonly DateTimeOffset RotationNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Site = new("https://hall9k.atlassian.net");

    [Fact]
    public async Task A_token_rotated_into_an_environment_variable_does_not_stay_on_disk()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        CredentialReference stored = await CredentialVault.StoreAsync(
            "jira-rotated-to-a-variable", "alices-token", cts.Token);
        Guid connectionId = await RegisterAsync(store, "alice@corp.com", stored, cts.Token);

        CredentialReference rotated = CredentialReference.EnvironmentVariable("JIRA_API_TOKEN");
        CredentialReference? superseded = await ReregisterAsync(
            store, connectionId, "alice@corp.com", rotated, cts.Token);

        superseded.Should().Be(stored, "the connection reads the variable now and nothing reads the file");
        CredentialVault.Discard(superseded!).Should().Be(CredentialVault.FileFor(stored.Identifier!));
        File.Exists(CredentialVault.FileFor(stored.Identifier!)).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_account_at_the_same_site_does_not_leave_the_first_ones_token_behind()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        CredentialReference alice = await CredentialVault.StoreAsync(
            "jira-rotated-to-another-account-alice", "alices-token", cts.Token);
        Guid connectionId = await RegisterAsync(store, "alice@corp.com", alice, cts.Token);

        // The file name is derived from the account, so Bob's registration writes a new file
        // rather than overwriting Alice's.
        CredentialReference bob = await CredentialVault.StoreAsync(
            "jira-rotated-to-another-account-bob", "bobs-token", cts.Token);
        CredentialReference? superseded = await ReregisterAsync(
            store, connectionId, "bob@corp.com", bob, cts.Token);

        superseded.Should().Be(alice);
        CredentialVault.Discard(superseded!);
        File.Exists(CredentialVault.FileFor(alice.Identifier!)).Should().BeFalse();
        File.Exists(CredentialVault.FileFor(bob.Identifier!)).Should().BeTrue("the live credential is untouched");
    }

    [Fact]
    public async Task Re_registering_the_same_account_supersedes_nothing_because_the_file_was_overwritten()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        CredentialReference stored = await CredentialVault.StoreAsync(
            "jira-rotated-in-place", "alices-token", cts.Token);
        Guid connectionId = await RegisterAsync(store, "alice@corp.com", stored, cts.Token);

        // The ordinary token rotation: same site, same account, same file name, new contents.
        CredentialReference again = await CredentialVault.StoreAsync(
            "jira-rotated-in-place", "a-fresh-token", cts.Token);
        CredentialReference? superseded = await ReregisterAsync(
            store, connectionId, "alice@corp.com", again, cts.Token);

        superseded.Should().BeNull("deleting it would delete the credential just written");
        File.Exists(CredentialVault.FileFor(stored.Identifier!)).Should().BeTrue();
    }

    /// <summary>
    /// A first registration writes the token, then fails to commit. Nothing was ever recorded, so
    /// no connection points at the file and the command has to take it back off disk — otherwise
    /// a working API token sits in the credentials directory that nothing references and that
    /// <c>h9k connection list</c> does not mention.
    /// </summary>
    [Fact]
    public async Task A_registration_that_never_commits_does_not_leave_the_token_it_wrote()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        await using IDocumentSession session = store.LightweightSession();

        // The command settles the reference before it writes, precisely so this question can be
        // asked while the session is still healthy — the failure is where it would not be.
        CredentialReference planned = CredentialReference.File("jira-registration-that-never-committed");
        bool pointedAt = await ConnectionAddJiraCommand.PointedAtAsync(session, planned, cts.Token);
        await CredentialVault.StoreAsync(planned.Identifier!, "alices-token", cts.Token);

        pointedAt.Should().BeFalse("nothing was recorded, so no connection reads what was written");
        CredentialVault.Discard(planned).Should().Be(CredentialVault.FileFor(planned.Identifier!));
        File.Exists(CredentialVault.FileFor(planned.Identifier!)).Should().BeFalse();
    }

    /// <summary>
    /// The same failure during an ordinary rotation, where the write overwrote the very file the
    /// registered connection reads through. The new token verified against the same site and
    /// account, so that connection still works; removing the file would break a registration this
    /// command never managed to change.
    /// </summary>
    [Fact]
    public async Task A_rotation_that_never_commits_keeps_the_file_the_connection_still_reads()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;

        CredentialReference stored = await CredentialVault.StoreAsync(
            "jira-rotation-that-never-committed", "alices-token", cts.Token);
        await RegisterAsync(store, "alice@corp.com", stored, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        bool pointedAt = await ConnectionAddJiraCommand.PointedAtAsync(session, stored, cts.Token);
        await CredentialVault.StoreAsync(stored.Identifier!, "a-fresh-token", cts.Token);

        pointedAt.Should().BeTrue("the registered connection reads that same file");
        File.Exists(CredentialVault.FileFor(stored.Identifier!)).Should().BeTrue();
    }

    /// <summary>
    /// What <c>h9k connection add jira</c> does: register, then read the connection back, then
    /// re-register it, then ask what the first registration left behind.
    /// </summary>
    private static async Task<Guid> RegisterAsync(
        DocumentStore store, string email, CredentialReference credential, CancellationToken cancellationToken)
    {
        Guid connectionId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, DomainId.New(), WorkItemProvider.Jira, email, credential, RotationNow, Site));
        await session.SaveChangesAsync(cancellationToken);
        return connectionId;
    }

    private static async Task<CredentialReference?> ReregisterAsync(
        DocumentStore store,
        Guid connectionId,
        string email,
        CredentialReference credential,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ConnectionDetails existing = (await session.LoadAsync<ConnectionDetails>(connectionId, cancellationToken))!;
        ConnectionAggregate aggregate = (await session.Events
            .AggregateStreamAsync<ConnectionAggregate>(connectionId, token: cancellationToken))!;

        session.Events.Append(connectionId, ConnectionDecider.Reregister(
            aggregate, email, credential, RotationNow, Site));
        await session.SaveChangesAsync(cancellationToken);

        return await ConnectionAddJiraCommand.SupersededCredentialAsync(
            session, existing, credential, cancellationToken);
    }

    // ── spend pressure ──
    private static readonly DateTimeOffset SpendNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] SpendEnvironmentVariables =
    [
        "Hall9k__SpendBudgetTokens",
        "Hall9k__SpendPeriod",
    ];

    private readonly Dictionary<string, string?> _previousSpendSettings =
        SpendEnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);


    /// <summary>
    /// A daemon that has never had a budget still publishes its compiled-default period
    /// ("week") on every sweep, alongside a null budget. This shell resolves a genuinely
    /// configured budget and period ("day") that daemon has never seen. The period this shell
    /// reports must be its own configured one, not the unconfirmed daemon's compiled default —
    /// otherwise the calibration line (run, observe a real burn, set the budget under it) sums
    /// the wrong window entirely.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_budget_reports_this_shells_own_period_not_the_daemons_stale_default()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        Environment.SetEnvironmentVariable("Hall9k__SpendBudgetTokens", "5000000");
        Environment.SetEnvironmentVariable("Hall9k__SpendPeriod", "day");

        DocumentStore store = postgres.Store;
        // This class's tests share one PostgresFixture container: reset first, since both seed
        // a NodeDispatchLoad for this same machine and a leftover row from a sibling test would
        // make DispatchPressure.ReadFreshMeasurementAsync's freshest-row pick ambiguous.
        await store.Advanced.ResetAllData(cts.Token);
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new NodeDispatchLoad
            {
                Id = Guid.NewGuid(),
                MachineName = Environment.MachineName,
                LiveRuns = 0,
                MaxConcurrentRuns = 3,
                ObservedAt = SpendNow,
                SpendBudgetTokens = null,
                SpendPeriod = SpendPeriod.Week.Value,
            });
            await seed.SaveChangesAsync(cts.Token);
        }

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cts.Token);
        report.SpendPeriod.Value.Should().Be("day", "the env var this test set is what should resolve");

        await using IQuerySession query = store.QuerySession();
        SpendPressure spend = await SpendPressure.ReadAsync(query, report, SpendNow, cts.Token);

        spend.BudgetIsEnforced.Should().BeFalse("the published row's own budget is null — nothing is enforcing yet");
        spend.Period.Should().Be(
            "day",
            "an unconfirmed budget must read this shell's own configured period, not the daemon's " +
            "compiled-default 'week' it publishes even while unbudgeted");
    }

    /// <summary>
    /// A budget change note already exists for the budget half of this setting pair
    /// (<see cref="SpendPressure"/>'s own <c>PendingChangeNote</c>); the period half needs the
    /// identical treatment. Here the enforced budget and this shell's configured budget agree, so
    /// the note must still fire on the period disagreeing alone — the exact gap the adversarial
    /// finding named (a period-only config change showed no disagreement at all).
    /// </summary>
    [Fact]
    public async Task A_period_only_config_change_still_surfaces_the_pending_change_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("HALL9K_HOME", _home);
        Environment.SetEnvironmentVariable("Hall9k__SpendBudgetTokens", "5000000");
        Environment.SetEnvironmentVariable("Hall9k__SpendPeriod", "day");

        DocumentStore store = postgres.Store;
        // This class's tests share one PostgresFixture container: reset first, since both seed
        // a NodeDispatchLoad for this same machine and a leftover row from a sibling test would
        // make DispatchPressure.ReadFreshMeasurementAsync's freshest-row pick ambiguous.
        await store.Advanced.ResetAllData(cts.Token);
        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new NodeDispatchLoad
            {
                Id = Guid.NewGuid(),
                MachineName = Environment.MachineName,
                LiveRuns = 0,
                MaxConcurrentRuns = 3,
                ObservedAt = SpendNow,
                SpendBudgetTokens = 5_000_000,
                SpendPeriod = SpendPeriod.Week.Value,
            });
            await seed.SaveChangesAsync(cts.Token);
        }

        OperatingSettingsReport report = await OperatingSettingsResolver.ResolveAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        SpendPressure spend = await SpendPressure.ReadAsync(query, report, SpendNow, cts.Token);

        spend.BudgetIsEnforced.Should().BeTrue("the published row's budget matches what's confirmed enforced");
        spend.Period.Should().Be("week", "the enforced period is the daemon's own published one, still in force");
        spend.SummaryLine.Should().Contain(
            "differs from what the daemon is enforcing",
            "the budgets agree but the periods do not, and that disagreement must still be named");
    }

    /// <summary>
    /// Empties the connection list ahead of a test whose whole question is which Jira connection
    /// this install has. That is a global fact about the database — no fresh domain id scopes it —
    /// and the credential-rotation seam in this same class registers Jira connections of its own,
    /// so the answer is arranged here rather than inherited from whatever ran before. The same
    /// clear-then-register discipline <see cref="StoreBackedRecordTests"/>' own work-item-connection
    /// seam uses, for the same reason.
    /// </summary>
    private static async Task ClearConnectionsAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.DeleteWhere<ConnectionDetails>(connection => true);
        await session.SaveChangesAsync(cancellationToken);
    }
}