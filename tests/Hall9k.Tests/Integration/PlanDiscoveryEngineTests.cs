using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The daemon's half of <c>h9k plan start --idea</c>, against a real store and scripted sessions: one discovery session at a time
/// off the sweep, the report copied by the daemon alone into the idea's workspace at the path the idea has at that moment, a park
/// at the gate that holds no session, every way a session can fail parked from a closed list with nothing copied, a restart that
/// records a left-behind session as failed and never dispatches it again, and the drafts a pursue cut carried on a plan's
/// ordinary path, told of each other and of the report.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PlanDiscoveryEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private const string PursueReport = "Recap.\n\nDISCOVERY: pursue\nTASK: Build the first piece\nTASK: Build the second piece\n";
    private const string StopReport = "Nothing to build.\n\nDISCOVERY: stop\n";

    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-discovery-checkout-").FullName;
    private readonly string _projectHome = Directory.CreateTempSubdirectory("h9k-discovery-home-").FullName;
    private readonly List<PlanEngine> _engines = [];
    private NodeContext _node = null!;

    public async Task InitializeAsync() =>
        _node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, CancellationToken.None);

    /// <summary>The store is shared by the tests of this class, and the engine drives every live plan on the node, so each test ends its plans.</summary>
    public async Task DisposeAsync()
    {
        foreach (PlanEngine engine in _engines)
        {
            await engine.WhenIdleAsync();
        }

        await using IDocumentSession session = postgres.Store.LightweightSession();
        foreach (PlanDetails plan in (await session.Query<PlanDetails>().ToListAsync()).Where(plan => plan.IsLive))
        {
            session.Events.Append(plan.Id, new PlanCancelled(plan.Id, plan.ProjectId, "test over", null, Now));
        }

        await session.SaveChangesAsync();
        _scopedHome.Dispose();
        foreach (string directory in new[] { _checkout, _projectHome })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public enum Ending
    {
        Success,
        ErrorResult,
        NoResult,
        NeverEnds,
    }

    /// <summary>A discovery session that writes what it is told to write and ends the way it is told to end.</summary>
    private sealed class ScriptedDiscovery(
        FakeProcessManager processes, string? report, Ending ending = Ending.Success, Action<AgentSpawnRequest>? onSpawn = null) : IExecutor
    {
        private int _nextPid = 9000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            lock (Spawns)
            {
                Spawns.Add(request);
            }

            onSpawn?.Invoke(request);
            int pid = Interlocked.Increment(ref _nextPid);
            Directory.CreateDirectory(request.RunDirectory);
            if (report is not null)
            {
                File.WriteAllText(RunPaths.DiscoveryReportFile(request.SessionId), report);
            }

            if (ending == Ending.NeverEnds)
            {
                processes.MarkAlive(pid);
            }
            else if (ending != Ending.NoResult)
            {
                string line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "result",
                    ["subtype"] = ending == Ending.Success ? "success" : "error_during_execution",
                    ["is_error"] = ending == Ending.ErrorResult,
                    ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 1000, ["output_tokens"] = 200 },
                    ["result"] = "done",
                });
                File.WriteAllText(RunPaths.StreamFile(request.RunDirectory), line + "\n");
            }

            return Task.FromResult(new SpawnedAgent(pid, Now));
        }
    }

    private sealed class FixedWorktrees : IWorktreeManager
    {
        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, $"already at origin/{branch}"));

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoLock.Instance);
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The child <c>h9k</c>, which a discovery plan never runs; a refinement of a cut draft would.</summary>
    private sealed class NoCommands : IPlanTaskCommands
    {
        public Task<TaskCommandResult> ReviseAsync(Guid planId, Guid sessionId, Guid taskId, string revisionFile, CancellationToken cancellationToken) =>
            Task.FromResult(new TaskCommandResult(1, false, "refused.txt"));

        public Task<TaskCommandResult> PublishAsync(
            Guid planId, Guid taskId, bool noExistingItem, bool untracked, CancellationToken cancellationToken) =>
            Task.FromResult(new TaskCommandResult(1, false, "refused.txt"));
    }

    private PlanEngine NewEngine(IExecutor executor, FakeProcessManager processes, Action<DaemonOptions>? tweak = null)
    {
        DaemonOptions options = new()
        {
            PlanRefinementTimeout = TimeSpan.FromSeconds(20),
            ForeignPublicationCeiling = TimeSpan.FromHours(1),
        };
        tweak?.Invoke(options);
        PlanEngine engine = new(
            postgres.Store, _node, executor, processes, new FixedWorktrees(),
            (_, _, _, _) => Task.FromResult(new ProcessResult(0, "0123abc\n", string.Empty)),
            new ReadingCheckoutLeases(),
            new NoCommands(), new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            new DaemonConnection(postgres.ConnectionString), Options.Create(options), NullLogger<PlanEngine>.Instance);
        _engines.Add(engine);
        return engine;
    }

    private async Task<Guid> SeedProjectAsync(CancellationToken cancellationToken, bool skipPermissions = true, int? maxParallel = null)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid projectId = DomainId.New();
        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, _node.OwnerId, DomainId.New(), $"plan-{projectId:N}", _checkout, null, "main", Now,
            skipPermissions: skipPermissions));
        await session.SaveChangesAsync(cancellationToken);
        if (maxParallel is { } cap)
        {
            await using IDocumentSession settings = postgres.Store.LightweightSession();
            ProjectAggregate project = (await settings.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken))!;
            settings.Events.Append(projectId, ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                changedAt: Now, changedByOwnerId: _node.OwnerId,
                maxParallelTasks: Optional<int?>.Of(cap)));
            await settings.SaveChangesAsync(cancellationToken);
        }

        return projectId;
    }

    /// <summary>An idea whose workspace exists on disk, under the global ideas directory or, with a home, under that home's own.</summary>
    private async Task<(Guid IdeaId, string Workspace)> SeedIdeaAsync(
        Guid projectId, CancellationToken cancellationToken, string text = "Let a plan start from an idea", ProjectHome? home = null)
    {
        Guid ideaId = DomainId.New();
        ProjectHome workspaceHome = home ?? ProjectHome.None;
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<IdeaAggregate>(ideaId, IdeaDecider.Capture(ideaId, _node.OwnerId, text, projectId, Now, workspaceHome));
        await session.SaveChangesAsync(cancellationToken);
        string directory = IdeaPaths.ResolveDirectory(workspaceHome, ProjectHomePaths.EntryDirectoryName(ideaId, text), ideaId);
        string workspace = IdeaPaths.EnsureWorkspace(directory);
        if (workspaceHome.HasValue)
        {
            // As capture does, so a directory the sweep renames is still found by the idea's id.
            HomeEntryLookup.EnsureIdentityMarker(directory, ideaId);
        }

        return (ideaId, workspace);
    }

    private async Task<Guid> StartPlanAsync(Guid projectId, Guid ideaId, CancellationToken cancellationToken, DateTimeOffset? startedAt = null)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid planId = DomainId.New();
        session.Events.StartStream<PlanAggregate>(planId, new PlanStarted(
            planId, projectId, [], PlanPhase.Discovery, false, false, _node.OwnerId, startedAt ?? Now, ideaId));
        await session.SaveChangesAsync(cancellationToken);
        return planId;
    }

    private async Task<PlanDetails> PlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.LoadAsync<PlanDetails>(planId, cancellationToken))!;
    }

    private async Task<IReadOnlyList<object>> StreamAsync(Guid streamId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return [.. (await query.Events.FetchStreamAsync(streamId, token: cancellationToken)).Select(recorded => recorded.Data)];
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the condition should have come true");
            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task SweepUntilAsync(PlanEngine engine, Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            await engine.PollOnceAsync(cancellationToken);
            if (await condition())
            {
                return;
            }

            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the condition should have come true");
            await Task.Delay(50, cancellationToken);
        }
    }

    [Fact]
    public async Task A_plan_from_an_idea_dispatches_one_session_naming_no_task_copies_its_report_into_the_workspace_and_parks_at_the_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        await File.WriteAllTextAsync(Path.Combine(workspace, "journal.md"), "A ruling a person made.");
        await File.WriteAllTextAsync(Path.Combine(workspace, "notes.md"), "Gathered notes.");
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, PursueReport);
        PlanEngine engine = NewEngine(session, processes);

        PlanSweepResult first = await engine.PollOnceAsync(cts.Token);

        first.Dispatched.Should().Be(1);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).AtGate, cts.Token);
        AgentSpawnRequest spawn = session.Spawns.Should().ContainSingle().Subject;
        spawn.TaskId.Should().BeNull("a discovery session has no task, as the courier's has none");
        spawn.SkipPermissions.Should().BeTrue();
        spawn.SessionName.Should().Contain("discovery");
        spawn.Model.Value.Should().Be(new DaemonOptions().ResolveModel(AgentRole.Refinement, null, null).Value, "the refinement role's chain, with no new setting");
        spawn.RunDirectory.Should().Be(RunPaths.GlobalDirectory(spawn.SessionId));
        Path.GetFullPath(spawn.WorktreePath).Should().Be(Path.GetFullPath(_checkout));
        spawn.Prompt.Should().Contain("Let a plan start from an idea").And.Contain("commit 0123abc").And.Contain(workspace)
            .And.Contain(Path.Combine(workspace, "journal.md")).And.Contain(RunPaths.DiscoveryReportFile(spawn.SessionId))
            .And.Contain("20 seconds").And.Contain("No task has been cut from this idea yet");

        // The daemon, not the session, wrote into the workspace: one new file, and the person's files untouched.
        string copy = Path.Combine(workspace, DiscoveryWorkspace.ReportFileName(planId));
        File.ReadAllText(copy).Should().Be(PursueReport);
        Directory.GetFiles(workspace).Select(Path.GetFileName).Order().Should().Equal(
            DiscoveryWorkspace.ReportFileName(planId), "journal.md", "notes.md");
        File.ReadAllText(Path.Combine(workspace, "journal.md")).Should().Be("A ruling a person made.");

        IReadOnlyList<object> stream = await StreamAsync(planId, cts.Token);
        PlanDiscoveryDispatched dispatched = stream.OfType<PlanDiscoveryDispatched>().Should().ContainSingle().Subject;
        (dispatched.SessionId, dispatched.IdeaId).Should().Be((spawn.SessionId, ideaId));
        PlanDiscoveryParked parked = stream.OfType<PlanDiscoveryParked>().Should().ContainSingle().Subject;
        (parked.Reason, parked.Failure, parked.SessionId, parked.ProjectId, parked.IdeaId)
            .Should().Be((PlanParkReason.Gate, RefinementFailureReason.Unknown, spawn.SessionId, projectId, ideaId));
        stream.OfType<PlanTokensRecorded>().Should().ContainSingle().Which.Model!.Value.Should().Be(spawn.Model.Value);

        // At the gate it holds no session and no slot, and waits with no time limit: another sweep does nothing.
        (await PlanAsync(planId, cts.Token)).Discovery!.RunningSessionId.Should().BeNull();
        PlanSweepResult idle = await engine.PollOnceAsync(cts.Token);
        (idle.Steps, idle.Dispatched).Should().Be((0, 0));
        session.Spawns.Should().HaveCount(1);

        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.Query<RunDetails>().CountAsync(cts.Token)).Should().Be(0, "a plan creates no run");
        PeriodSpend spend = await PeriodSpend.ReadAsync(query, Now.AddDays(-1), cts.Token);
        spend.ByModel.Should().Contain(entry => entry.Model.Value == spawn.Model.Value && entry.TotalInputTokens >= 1000,
            "the session's tokens land under the model it ran on");
    }

    [Fact]
    public async Task A_report_is_copied_to_the_directory_the_idea_has_when_the_session_ends_even_if_the_sweep_renamed_it_since_dispatch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token, "Old slug of the note", ProjectHome.Parse(_projectHome));
        string renamedWorkspace = workspace;
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, StopReport, onSpawn: _ =>
        {
            // What the render sweep does when a revised note changes the slug: the whole directory moves, workspace and all.
            string oldDirectory = Path.GetDirectoryName(workspace)!;
            string newDirectory = ProjectHomePaths.IdeaDirectory(_projectHome, ProjectHomePaths.EntryDirectoryName(ideaId, "A sharper note"));
            Directory.Move(oldDirectory, newDirectory);
            renamedWorkspace = Path.Combine(newDirectory, "workspace");
        });

        await NewEngine(session, processes).PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).AtGate, cts.Token);

        renamedWorkspace.Should().NotBe(workspace);
        File.ReadAllText(Path.Combine(renamedWorkspace, DiscoveryWorkspace.ReportFileName(planId))).Should().Be(StopReport);
        Directory.Exists(workspace).Should().BeFalse("nothing was written back at the path dispatch knew");
    }

    [Theory]
    [InlineData(Ending.ErrorResult, PursueReport, "SessionError")]
    [InlineData(Ending.NoResult, PursueReport, "NoResult")]
    [InlineData(Ending.Success, null, "NoReport")]
    [InlineData(Ending.Success, "A report with no closing block.", "UnreadableReport")]
    [InlineData(Ending.Success, "Prose.\n\nDISCOVERY: stop\nTASK: but a task\n", "UnreadableReport")]
    [InlineData(Ending.Success, "Prose.\n\nDISCOVERY: pursue\n", "UnreadableReport")]
    public async Task A_session_that_ends_in_error_with_no_result_or_no_readable_report_parks_as_session_failed_and_copies_nothing(
        Ending ending, string? report, string expectedFailure)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, report, ending);
        PlanEngine engine = NewEngine(session, processes);

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).Discovery!.IsParked, cts.Token);

        PlanDiscoveryParked parked = (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().ContainSingle().Subject;
        (parked.Reason, parked.Failure.Value).Should().Be((PlanParkReason.SessionFailed, expectedFailure));
        Directory.GetFiles(workspace).Should().BeEmpty("no file reaches the workspace");
        (await PlanAsync(planId, cts.Token)).AtGate.Should().BeFalse();
    }

    [Fact]
    public async Task A_session_that_outruns_its_bound_is_terminated_and_parks_as_session_failed_with_timed_out()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        PlanEngine engine = NewEngine(
            new ScriptedDiscovery(processes, PursueReport, Ending.NeverEnds), processes, options => options.PlanRefinementTimeout = TimeSpan.FromMilliseconds(500));

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).Discovery!.IsParked, cts.Token);

        (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().ContainSingle()
            .Which.Failure.Should().Be(RefinementFailureReason.TimedOut);
        processes.Terminations.Should().NotBeEmpty();
        Directory.GetFiles(workspace).Should().BeEmpty("a session that outran its bound is thrown away, even a readable report");
    }

    [Fact]
    public async Task A_readable_report_that_cannot_be_copied_parks_as_session_failed_and_stays_in_the_session_directory()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, PursueReport, onSpawn: _ => Directory.Delete(workspace, recursive: true));

        await NewEngine(session, processes).PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).Discovery!.IsParked, cts.Token);

        PlanDiscoveryParked parked = (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().ContainSingle().Subject;
        (parked.Reason, parked.Failure).Should().Be((PlanParkReason.SessionFailed, RefinementFailureReason.ReportNotCopied));
        File.Exists(RunPaths.DiscoveryReportFile(session.Spawns.Single().SessionId)).Should().BeTrue();
        Directory.Exists(workspace).Should().BeFalse("the workspace is never created by a copy");
    }

    [Fact]
    public async Task Cancelling_a_plan_with_no_drafts_stops_its_session_and_copies_nothing_and_leaves_the_idea_as_it_was()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        PlanEngine engine = NewEngine(new ScriptedDiscovery(processes, PursueReport, Ending.NeverEnds), processes);
        await engine.PollOnceAsync(cts.Token);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(planId, new PlanCancelled(planId, projectId, "changed my mind", _node.OwnerId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);
        await engine.WhenIdleAsync();

        processes.Terminations.Should().NotBeEmpty("the session was stopped");
        (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().BeEmpty();
        Directory.GetFiles(workspace).Should().BeEmpty();
        PlanDetails cancelled = await PlanAsync(planId, cts.Token);
        cancelled.State.Should().Be(PlanState.Cancelled);

        // The job stopped its own session, so the process file that marks a crash's leftover is gone, and a later sweep does not
        // take this ordinary cancel for one.
        Guid sessionId = cancelled.Discovery!.RunningSessionId!.Value;
        File.Exists(RunPaths.DiscoveryProcessFile(sessionId)).Should().BeFalse();
        int terminationsBefore = processes.Terminations.Count;
        await engine.PollOnceAsync(cts.Token);
        processes.Terminations.Should().HaveCount(terminationsBefore, "a cancel is not a session a crash left behind");
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!.State.Should().Be(IdeaState.Captured);
    }

    [Fact]
    public async Task An_idea_that_stops_being_captured_before_the_plan_has_drafts_ends_the_plan_cancelled_and_stops_its_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        PlanEngine engine = NewEngine(new ScriptedDiscovery(processes, PursueReport, Ending.NeverEnds), processes);
        await engine.PollOnceAsync(cts.Token);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cts.Token))!;
            session.Events.Append(ideaId, IdeaDecider.Conclude(idea, "done another way", Now, _node.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await SweepUntilAsync(engine, async () => (await PlanAsync(planId, cts.Token)).State == PlanState.Cancelled, cts.Token);
        await engine.WhenIdleAsync();

        processes.Terminations.Should().NotBeEmpty();
        Directory.GetFiles(workspace).Should().BeEmpty();
        (await StreamAsync(planId, cts.Token)).OfType<PlanCancelled>().Should().ContainSingle()
            .Which.CancelledByOwnerId.Should().BeNull("the platform ended it, not a person");
    }

    [Fact]
    public async Task A_plan_at_the_gate_whose_idea_is_concluded_ends_cancelled_but_once_drafts_exist_the_ideas_state_has_no_effect()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, _) = await SeedIdeaAsync(projectId, cts.Token);
        Guid gated = await StartPlanAsync(projectId, ideaId, cts.Token);
        await AppendAtGateAsync(gated, projectId, ideaId, cts.Token);
        (Guid otherIdea, _) = await SeedIdeaAsync(projectId, cts.Token, "Another idea");
        Guid withDrafts = await StartPlanAsync(projectId, otherIdea, cts.Token, Now.AddMinutes(1));
        Guid draft = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(draft, TaskDecider.Add(
                draft, projectId, "Cut from discovery", ["A criterion"], TaskType.Feature, "ctx", null, null, Now, _node.OwnerId));
            session.Events.Append(withDrafts, new PlanDiscoveryDispatched(withDrafts, projectId, otherIdea, DomainId.New(), Now));
            session.Events.Append(withDrafts, new PlanDiscoveryParked(
                withDrafts, projectId, otherIdea, PlanParkReason.Gate, null, RefinementFailureReason.Unknown, Now));
            session.Events.Append(withDrafts, new PlanDraftsCut(withDrafts, projectId, otherIdea, [draft], null, false, false, _node.OwnerId, Now));
            foreach (Guid concluded in new[] { ideaId, otherIdea })
            {
                IdeaAggregate idea = (await session.Events.AggregateStreamAsync<IdeaAggregate>(concluded, token: cts.Token))!;
                session.Events.Append(concluded, IdeaDecider.Conclude(idea, "done", Now, _node.OwnerId));
            }

            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedDiscovery spawns = new(processes, null, Ending.NeverEnds);
        PlanEngine engine = NewEngine(spawns, processes);
        await engine.PollOnceAsync(cts.Token);

        (await PlanAsync(gated, cts.Token)).State.Should().Be(PlanState.Cancelled);
        PlanDetails cut = await PlanAsync(withDrafts, cts.Token);
        cut.State.Should().Be(PlanState.Live, "the idea's state has no effect once drafts exist");
        cut.Drafts.Should().ContainSingle();
    }

    private async Task AppendAtGateAsync(Guid planId, Guid projectId, Guid ideaId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid sessionId = DomainId.New();
        session.Events.Append(planId, new PlanDiscoveryDispatched(planId, projectId, ideaId, sessionId, Now));
        session.Events.Append(planId, new PlanDiscoveryParked(
            planId, projectId, ideaId, PlanParkReason.Gate, sessionId, RefinementFailureReason.Unknown, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task A_session_a_restart_left_running_is_recorded_as_failed_on_the_next_start_and_is_never_dispatched_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, _) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        Guid lost = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(planId, new PlanDiscoveryDispatched(planId, projectId, ideaId, lost, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedDiscovery spawns = new(processes, PursueReport);
        PlanEngine afterRestart = NewEngine(spawns, processes);

        PlanSweepResult sweep = await afterRestart.PollOnceAsync(cts.Token);
        await afterRestart.PollOnceAsync(cts.Token);

        sweep.Recovered.Should().Be(1);
        spawns.Spawns.Should().BeEmpty("a left-behind session is never re-dispatched");
        PlanDiscoveryParked parked = (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().ContainSingle().Subject;
        (parked.Reason, parked.Failure, parked.SessionId).Should().Be((PlanParkReason.SessionFailed, RefinementFailureReason.DaemonRestarted, lost));

        // A person's resume is what dispatches discovery again.
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            PlanAggregate plan = (await session.Events.AggregateStreamAsync<PlanAggregate>(planId, token: cts.Token))!;
            session.Events.Append(planId, PlanDecider.Resume(plan, _node.OwnerId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await afterRestart.PollOnceAsync(cts.Token);
        await EventuallyAsync(() => Task.FromResult(spawns.Spawns.Count == 1), cts.Token);
        spawns.Spawns.Should().ContainSingle();
    }

    [Fact]
    public async Task A_session_a_restart_left_running_for_a_plan_cancelled_since_is_stopped_once_and_nothing_is_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, _) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        Guid lost = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(planId, new PlanDiscoveryDispatched(planId, projectId, ideaId, lost, Now));
            session.Events.Append(planId, new PlanCancelled(planId, projectId, "changed my mind", _node.OwnerId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        string processFile = RunPaths.DiscoveryProcessFile(lost);
        Directory.CreateDirectory(Path.GetDirectoryName(processFile) ?? throw new InvalidOperationException($"No directory for {processFile}."));
        await File.WriteAllTextAsync(processFile, $"4242\n{Now.UtcTicks}\n", cts.Token);
        FakeProcessManager processes = new();
        PlanEngine afterRestart = NewEngine(new ScriptedDiscovery(processes, PursueReport), processes);

        PlanSweepResult first = await afterRestart.PollOnceAsync(cts.Token);
        PlanSweepResult second = await afterRestart.PollOnceAsync(cts.Token);

        first.Recovered.Should().Be(1);
        second.Recovered.Should().Be(0, "the process file is removed once the session is stopped");
        processes.Terminations.Should().ContainSingle().Which.ProcessId.Should().Be(4242);
        File.Exists(processFile).Should().BeFalse();
        (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().BeEmpty();
    }

    [Fact]
    public async Task Sessions_take_turns_oldest_plan_first_and_a_second_waits_for_the_first_to_end()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid olderIdea, _) = await SeedIdeaAsync(projectId, cts.Token, "The older idea");
        (Guid newerIdea, _) = await SeedIdeaAsync(projectId, cts.Token, "The newer idea");
        Guid newer = await StartPlanAsync(projectId, newerIdea, cts.Token, Now.AddMinutes(5));
        Guid older = await StartPlanAsync(projectId, olderIdea, cts.Token, Now);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, PursueReport, Ending.NeverEnds);
        PlanEngine engine = NewEngine(session, processes);

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(() => Task.FromResult(session.Spawns.Count == 1), cts.Token);
        await engine.PollOnceAsync(cts.Token);

        AgentSpawnRequest only = session.Spawns.Should().ContainSingle("one at a time, and a discovery session takes no ceiling slot").Subject;
        only.Prompt.Should().Contain("The older idea").And.NotContain("The newer idea");
        (await PlanAsync(older, cts.Token)).Discovery!.RunningSessionId.Should().Be(only.SessionId);
        (await PlanAsync(newer, cts.Token)).Discovery!.RunningSessionId.Should().BeNull();
    }

    [Fact]
    public async Task A_project_at_zero_parallel_tasks_starts_no_discovery_session_and_the_plan_stays_in_discovery()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token, maxParallel: 0);
        (Guid ideaId, _) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, PursueReport);

        PlanSweepResult sweep = await NewEngine(session, processes).PollOnceAsync(cts.Token);

        (sweep.Steps, sweep.Dispatched).Should().Be((0, 0));
        session.Spawns.Should().BeEmpty();
        PlanDetails plan = await PlanAsync(planId, cts.Token);
        (plan.InDiscovery, plan.IsParked).Should().Be((true, false));
    }

    [Fact]
    public async Task A_dispatch_the_rules_refuse_where_the_session_would_start_parks_as_session_failed_with_start_failed_and_spawns_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token, skipPermissions: false);
        (Guid ideaId, _) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        FakeProcessManager processes = new();
        ScriptedDiscovery session = new(processes, PursueReport);

        await NewEngine(session, processes).PollOnceAsync(cts.Token);

        session.Spawns.Should().BeEmpty();
        PlanDiscoveryParked parked = (await StreamAsync(planId, cts.Token)).OfType<PlanDiscoveryParked>().Should().ContainSingle().Subject;
        (parked.Reason, parked.Failure, parked.SessionId).Should().Be((PlanParkReason.SessionFailed, RefinementFailureReason.StartFailed, null));
    }

    [Fact]
    public async Task The_refinement_of_a_draft_cut_from_discovery_is_told_its_siblings_and_the_report_and_a_plan_from_one_draft_is_told_no_siblings()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (Guid ideaId, string workspace) = await SeedIdeaAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, ideaId, cts.Token);
        await AppendAtGateAsync(planId, projectId, ideaId, cts.Token);
        Guid first = await SeedDraftAsync(projectId, ideaId, "Build the first piece", cts.Token);
        Guid second = await SeedDraftAsync(projectId, ideaId, "Build the second piece", cts.Token);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(planId, new PlanDraftsCut(planId, projectId, ideaId, [first, second], null, false, false, _node.OwnerId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedDiscovery discovery = new(processes, null);
        ScriptedRefinementProbe probe = new(processes);
        PlanEngine engine = NewEngine(probe, processes);

        PlanSweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Dispatched.Should().Be(1);
        await EventuallyAsync(() => Task.FromResult(probe.Spawns.Count == 1), cts.Token);
        AgentSpawnRequest spawn = probe.Spawns.Should().ContainSingle().Subject;
        spawn.TaskId.Should().Be(first);
        spawn.Prompt.Should().Contain("one of 2").And.Contain($"```\n- {second}: Build the second piece\n```").And.Contain("source material")
            .And.Contain("`blocked-by`").And.Contain("`stacked-on` as well").And.NotContain("`blocked-by` or `stacked-on`")
            .And.Contain(Path.Combine(workspace, DiscoveryWorkspace.ReportFileName(planId)))
            .And.NotContain($"- {first}: ");
        discovery.Spawns.Should().BeEmpty();
    }

    private async Task<Guid> SeedDraftAsync(Guid projectId, Guid ideaId, string objective, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid taskId = DomainId.New();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, objective, [], TaskType.Feature, agentContext: "Some context",
            constraints: null, externalReference: null, Now, _node.OwnerId, sourceIdeaId: ideaId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    /// <summary>A refinement session that records its spawn and never ends, so the test reads its prompt.</summary>
    private sealed class ScriptedRefinementProbe(FakeProcessManager processes) : IExecutor
    {
        private int _nextPid = 9500;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            lock (Spawns)
            {
                Spawns.Add(request);
            }

            int pid = Interlocked.Increment(ref _nextPid);
            Directory.CreateDirectory(request.RunDirectory);
            processes.MarkAlive(pid);
            return Task.FromResult(new SpawnedAgent(pid, Now));
        }
    }
}
