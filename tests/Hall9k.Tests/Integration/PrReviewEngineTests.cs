using System.Text.Json;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
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
/// PrReviewEngine's own re-entrancy and reclaim-safety guards against a real store (cycle-1
/// conformance finding, `PrReviewEngine.cs:50`: the 525-line component that owns the whole
/// pr-review completion path had no coverage at all before this). Three shapes: a run reclaimed
/// by a fresh generation must retire as superseded rather than act under a stale name, whichever
/// step of <c>DriveAsync</c> discovers it; and a run finalizing after its task left Claimed some
/// other way completes the run without forcing a task transition that is no longer true.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class PrReviewEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class NoOpWorktreeManager : IWorktreeManager
    {
        public List<string> Removed { get; } = [];

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PrReviewEngine never cuts a fresh worktree of its own.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PrReviewEngine never checks out a follow-up worktree.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PrReviewEngine never creates its own checkout.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken)
        {
            Removed.Add(worktreePath);
            return Task.CompletedTask;
        }

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "not a real repository"));
    }

    private sealed class RefusingExecutor : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A reclaimed run must retire before dispatching anything.");
    }

    /// <summary>Records the spawn request instead of starting anything.</summary>
    private sealed class CapturingExecutor : IExecutor
    {
        public AgentSpawnRequest? Request { get; private set; }

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new SpawnedAgent(4242, Now));
        }
    }

    /// <summary>
    /// Scripted stand-in for the conformance lens's own claude session: the spawn writes the
    /// given summary as a terminal result event straight into the session's stream file, so
    /// <c>SessionResultWaiter</c> reads it back without ever needing the fake process to be
    /// alive. A null summary spawns nothing and reports a process that never existed — the
    /// died-without-a-result path — mirroring <c>ReviewEngineTests.ScriptedExecutor</c>.
    /// </summary>
    private sealed class ScriptedExecutor(string? summary) : IExecutor
    {
        private int _nextProcessId = 7_000;

        public FakeProcessManager Processes { get; } = new();

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            int processId = _nextProcessId++;
            if (summary is null)
            {
                return new SpawnedAgent(processId, Now);
            }

            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "result",
                ["subtype"] = "success",
                ["is_error"] = false,
                ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 1_000, ["output_tokens"] = 200 },
                ["total_cost_usd"] = 0.01,
                ["num_turns"] = 12,
                ["result"] = summary,
            });
            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(
                RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName!),
                line + "\n", cancellationToken);

            Processes.MarkAlive(processId);
            return new SpawnedAgent(processId, Now);
        }
    }

    /// <summary>
    /// The terminal-failure path <see cref="PrReviewEngine.RejectUnusableVerdictAsync"/> guards
    /// (verify cycle-2 conformance finding, `PrReviewEngine.cs:639`): no equivalent test existed
    /// for the conformance lens's own verdict gate, only for the three reclaim-fence points —
    /// so a regression that silently reverted this gate back to a pass-through would go
    /// unnoticed. A conformance summary with no `VERDICT:` line at all must fail the run rather
    /// than hand the owner a findings report built from a promise never kept.
    /// </summary>
    [Fact]
    public async Task A_verdict_less_conformance_session_fails_the_run_instead_of_parking_a_hollow_report()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new("Looked the pull request over; nothing further to add.");
        PrReviewEngine engine = NewEngine(store, executor, executor.Processes, new NoOpWorktreeManager());
        await engine.RecordAdversarialResultAsync(runDirectory, "Nothing found.\n\nVERDICT: merge-ready", cts.Token);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.Failed, "a verdict-less conformance session must fail the run, not park a hollow report");
        run.InputTokens.Should().Be(1_000, "the session's own spend is recorded even though its verdict was unusable");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed");
    }

    /// <summary>
    /// The pass-through half of the same gate: a well-formed verdict must still reach the park,
    /// not just avoid the failure path above.
    /// </summary>
    [Fact]
    public async Task A_well_formed_conformance_verdict_parks_the_findings_report()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Reviewed the pull request against its own title and description; it matches.\n\nVERDICT: merge-ready");
        PrReviewEngine engine = NewEngine(store, executor, executor.Processes, new NoOpWorktreeManager());
        await engine.RecordAdversarialResultAsync(runDirectory, "Nothing found.\n\nVERDICT: merge-ready", cts.Token);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.ReviewParked, "a usable verdict reaches the park, not a failure");
        run.InputTokens.Should().Be(1_000, "the completed session's spend is recorded on the successful path too");

        File.ReadAllText(RunPaths.ReviewFindingsFile(runDirectory, 1))
            .Should().Contain("matches", "the conformance lens's own findings text lands in the report");
    }

    /// <summary>
    /// A crash while the conformance session is still in flight must terminate it (adversarial
    /// review, cycle 7, `PrReviewEngine.cs:135`): the untrusted foreign checkout is otherwise
    /// left with a live agent process nobody will ever read the findings of, mirroring
    /// <c>ReviewEngineTests.A_crash_while_the_fix_session_is_in_flight_terminates_it_too</c>.
    /// The crash is induced the way that test induces its own — an artifact write that fails
    /// because the destination path is already a directory — landing after the conformance
    /// session's result is in hand but before <c>PrReviewConformanceCompleted</c> commits.
    /// </summary>
    [Fact]
    public async Task A_crash_while_the_conformance_session_is_in_flight_terminates_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Reviewed the pull request against its own title and description; it matches.\n\nVERDICT: merge-ready");
        PrReviewEngine engine = NewEngine(store, executor, executor.Processes, new NoOpWorktreeManager());
        await engine.RecordAdversarialResultAsync(runDirectory, "Nothing found.\n\nVERDICT: merge-ready", cts.Token);

        Directory.CreateDirectory(RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Conformance.Slug));

        await engine.ReviewAsync(runId, taskId, cts.Token);

        executor.Processes.Terminations.Should().ContainSingle(
            "the conformance session was still recorded in flight when the loop crashed writing its findings file");
        executor.Processes.Terminations.Single().ProcessId.Should().Be(
            7_000, "the conformance lens's own session, the only one this run ever dispatches");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.FailureReason.Should().Contain("Pr-review loop failed", "the crash is reported as itself, not as a verdict");
    }

    /// <summary>
    /// The generation fence rejects <c>DispatchConformanceAsync</c> before it ever spawns:
    /// the adversarial lens's own result is already on disk, but a fresh generation claimed the
    /// task in between — mirrors the fix `RunLauncher.LaunchAsync`'s own fence already applies
    /// (Copilot review, PR #30), extended here to PrReviewEngine's second dispatch point.
    /// </summary>
    [Fact]
    public async Task A_reclaimed_task_retires_the_stale_run_instead_of_dispatching_the_conformance_lens()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);
        await ReclaimUnderNewGenerationAsync(store, node, taskId, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        PrReviewEngine engine = NewEngine(store, new RefusingExecutor(), new FakeProcessManager(), new NoOpWorktreeManager());
        await engine.RecordAdversarialResultAsync(runDirectory, "Nothing found.\n\nVERDICT: merge-ready", cts.Token);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.Superseded, "the fence must retire the run rather than let it dispatch");
    }

    /// <summary>
    /// The conformance lens reads the same foreign pull-request checkout the adversarial lens
    /// already read (adversarial review cycle-3 ride-along, `PrReviewEngine.cs:271`): its own
    /// spawn request must carry <see cref="AgentSpawnRequest.UntrustedWorkingDirectory"/> the
    /// same way <c>RunLauncherTests</c> already covers for the primary session, so a checkout
    /// this platform did not cut itself never gets its own `.claude/` config or `.mcp.json`
    /// loaded for the second lens either.
    /// </summary>
    [Fact]
    public async Task Dispatching_the_conformance_lens_marks_the_spawn_request_untrusted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        PrReviewEngine engine = NewEngine(store, executor, new FakeProcessManager(), new NoOpWorktreeManager());
        await engine.RecordAdversarialResultAsync(runDirectory, "Nothing found.\n\nVERDICT: merge-ready", cts.Token);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        executor.Request.Should().NotBeNull("the adversarial result is recorded, so the conformance lens must dispatch next");
        executor.Request!.UntrustedWorkingDirectory.Should().BeTrue(
            "the conformance lens reads the same foreign pull-request checkout the adversarial lens did");
    }

    /// <summary>
    /// The same fence, at the composing/park step: both lenses' findings already landed, but
    /// the task moved to a fresh generation before this run could park its report.
    /// </summary>
    [Fact]
    public async Task A_reclaimed_task_retires_the_stale_run_instead_of_parking_the_findings_report()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);
        Guid conformanceSessionId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId,
                new AgentSessionCompleted(runId, Now),
                new PrReviewConformanceDispatched(runId, conformanceSessionId, 5_001, Now, Now, AgentModel.Sonnet),
                new PrReviewConformanceCompleted(runId, conformanceSessionId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        PrReviewEngine engine = NewEngine(store, new RefusingExecutor(), new FakeProcessManager(), new NoOpWorktreeManager());
        await engine.RecordAdversarialResultAsync(runDirectory, "Adversarial: nothing found.", cts.Token);
        Directory.CreateDirectory(runDirectory);
        await File.WriteAllTextAsync(
            RunPaths.ReviewLensFindingsFile(runDirectory, 1, ReviewLens.Conformance.Slug),
            "Conformance: nothing found.", cts.Token);

        await ReclaimUnderNewGenerationAsync(store, node, taskId, cts.Token);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.Superseded, "both lenses finished, but the reclaim must still win over parking");
    }

    /// <summary>
    /// The same fence again, at finalize: the owner already resolved the park (PrReviewDelivered
    /// on the stream), but a fresh generation reclaimed the task before the daemon's own resume
    /// got here.
    /// </summary>
    [Fact]
    public async Task A_reclaimed_task_retires_the_stale_run_instead_of_finalizing_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedDeliveredPrReviewRunAsync(store, node, cts.Token);
        await ReclaimUnderNewGenerationAsync(store, node, taskId, cts.Token);

        NoOpWorktreeManager worktrees = new();
        PrReviewEngine engine = NewEngine(store, new RefusingExecutor(), new FakeProcessManager(), worktrees);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.Superseded, "the live generation owns closing this task out now, not this run");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().NotBe("Done", "a stale run must never complete a task the live generation now owns");
    }

    /// <summary>
    /// Finalize completes the run even when the task is no longer Claimed by the time it runs
    /// (an abandon racing the owner's own merge-ready resolve, say) — but it must not force the
    /// task to Done in that case: <c>TaskDecider.Complete</c> only ever applies from Claimed, and
    /// a task the owner already gave up on staying Abandoned is the correct outcome, not a
    /// completion that overwrites their decision.
    /// </summary>
    [Fact]
    public async Task Finalize_completes_the_run_but_never_forces_a_non_claimed_task_to_done()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedDeliveredPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate aggregate = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskAbandoned abandoned = TaskDecider.Abandon(aggregate, "Superseded by hand.", Now, node.OwnerId);
            session.Events.Append(taskId, abandoned);
            await session.SaveChangesAsync(cts.Token);
        }

        NoOpWorktreeManager worktrees = new();
        PrReviewEngine engine = NewEngine(store, new RefusingExecutor(), new FakeProcessManager(), worktrees);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.Completed, "the run itself still finishes even though the task moved on");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Abandoned", "finalize must not overwrite a state the owner already chose");

        worktrees.Removed.Should().ContainSingle(path => path.Contains(runId.ToString("N")));
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static PrReviewEngine NewEngine(
        DocumentStore store, IExecutor executor, FakeProcessManager processes, IWorktreeManager worktrees) =>
        new(store, executor, processes, worktrees,
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);

    /// <summary>
    /// A pr-review task, published, assigned and claimed at generation 1 — exactly where
    /// <c>RunLauncher.LaunchAsync</c> hands off to <c>PrReviewEngine</c> once the adversarial
    /// lens (this run's own primary session) has been dispatched.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, string RunDirectory)> SeedClaimedPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-wt-{runId:N}");
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-run-{runId:N}");
        Directory.CreateDirectory(runDirectory);

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"pr-review-{taskId:N}", repositoryPath, null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Review pull request acme/web#42", ["every finding names a file and line"], TaskType.PrReview,
                null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#42"),
                Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/42",
            ExecutorMode.Subscription, Now, RunDirectory: runDirectory));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, runDirectory);
    }

    /// <summary>Extends <see cref="SeedClaimedPrReviewRunAsync"/> to a resolved park, ready for finalize.</summary>
    private async Task<(Guid TaskId, Guid RunId, string RunDirectory)> SeedDeliveredPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cancellationToken);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId,
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId, "Findings ready.", Now),
            new PrReviewDelivered(runId, "Walked and directed.", Now, node.OwnerId));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, runDirectory);
    }

    /// <summary>
    /// The reclaim shape every fence test needs: the lease expires, the daemon requeues the
    /// task, and a fresh generation claims it under a different run — the task's own
    /// <c>CurrentRunId</c> now names a run that is not the one under test.
    /// </summary>
    private static async Task ReclaimUnderNewGenerationAsync(
        DocumentStore store, NodeContext node, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        TaskRequeued requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
        task.Apply(requeued);
        TaskClaimed reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(reclaimed);
        session.Events.Append(taskId, requeued, reclaimed);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = task.LeaseGeneration, HeartbeatAt = Now });
        await session.SaveChangesAsync(cancellationToken);
    }
}
