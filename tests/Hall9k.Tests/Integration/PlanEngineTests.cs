using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Challenge;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Handlers;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
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
/// The daemon's half of <c>h9k plan start</c>, against a real store and scripted sessions: one refinement session at a
/// time and never inside the sweep, both streams written in one save, a revision applied only to the draft the session read
/// and only through the child <c>h9k</c>, every way a session can end recorded from a closed list, a park that stops the
/// plan until it is answered, a challenge requested and absorbed, and a publish that follows the task rather than the exit code.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PlanEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-plan-checkout-").FullName;
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
        if (Directory.Exists(_checkout))
        {
            Directory.Delete(_checkout, recursive: true);
        }
    }

    public enum Ending
    {
        Success,
        ErrorResult,
        NoResult,
        NeverEnds,
    }

    /// <summary>A refinement session that writes what it is told to write and ends the way it is told to end.</summary>
    private sealed class ScriptedRefinement(
        FakeProcessManager processes, string? report, Ending ending = Ending.Success, string? revision = null,
        Action<AgentSpawnRequest>? onSpawn = null) : IExecutor
    {
        private int _nextPid = 8000;

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
                File.WriteAllText(RunPaths.RefinementReportFile(request.SessionId), report);
            }

            if (revision is not null)
            {
                File.WriteAllText(RunPaths.RefinementRevisionFile(request.SessionId), revision);
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

    /// <summary>The child <c>h9k</c>, played by a fake that does to the store what the real command would.</summary>
    private sealed class FakeCommands(DocumentStore store, Guid owner) : IPlanTaskCommands
    {
        public List<string> Calls { get; } = [];

        public bool RefuseRevise { get; set; }

        /// <summary>A person revises the draft after the daemon's own check and before the child reads it: the child's fenced append refuses.</summary>
        public bool PersonRevisesFirst { get; set; }

        public bool RefusePublish { get; set; }

        /// <summary>A person abandons the draft while the publish waits on the tracker: the publish exits non-zero and the task reads Abandoned.</summary>
        public bool AbandonDuringPublish { get; set; }

        /// <summary>A publish cut short: it commits and still exits non-zero.</summary>
        public bool PublishCommitsButFails { get; set; }

        public (bool NoExistingItem, bool Untracked)? LastAttestation { get; private set; }

        public async Task<TaskCommandResult> ReviseAsync(Guid planId, Guid sessionId, Guid taskId, string revisionFile, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add($"revise {taskId}");
            }

            if (RefuseRevise)
            {
                return new TaskCommandResult(1, false, "refused.txt");
            }

            await using IDocumentSession session = store.LightweightSession();
            if (PersonRevisesFirst)
            {
                session.Events.Append(taskId, new TaskRevised(
                    taskId, Optional<string>.Of("A person got there first"), default, default, default, default, default, Now, owner));
                await session.SaveChangesAsync(cancellationToken);
                return new TaskCommandResult(1, false, "refused.txt");
            }

            // As the real child does: recorded as the session's own revision, so it is not mistaken for a person's.
            session.Events.Append(taskId, new TaskRevised(
                taskId, Optional<string>.Of("Revised by the session"), default, default, default, default, default, Now, owner,
                RefinementSessionId: sessionId));
            await session.SaveChangesAsync(cancellationToken);
            return new TaskCommandResult(0, false, "ok.txt");
        }

        public async Task<TaskCommandResult> PublishAsync(
            Guid planId, Guid taskId, bool noExistingItem, bool untracked, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add($"publish {taskId}");
            }

            LastAttestation = (noExistingItem, untracked);
            if (AbandonDuringPublish)
            {
                await using IDocumentSession abandon = store.LightweightSession();
                abandon.Events.Append(taskId, new TaskAbandoned(taskId, "no longer wanted", Now, owner));
                await abandon.SaveChangesAsync(cancellationToken);
                return new TaskCommandResult(1, false, "refused.txt");
            }

            if (RefusePublish && !PublishCommitsButFails)
            {
                return new TaskCommandResult(1, false, "refused.txt");
            }

            await using IDocumentSession session = store.LightweightSession();
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
            session.Events.Append(taskId, TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, owner));
            await session.SaveChangesAsync(cancellationToken);
            return new TaskCommandResult(PublishCommitsButFails ? 1 : 0, false, "ok.txt");
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

    private PlanEngine NewEngine(IExecutor executor, FakeProcessManager processes, IPlanTaskCommands commands, Action<DaemonOptions>? tweak = null,
        ReadingCheckoutLeases? leases = null, TimeProvider? clock = null, RunningChallengeSessions? challengesRunning = null)
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
            leases ?? new ReadingCheckoutLeases(),
            commands, new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            new DaemonConnection(postgres.ConnectionString), Options.Create(options), NullLogger<PlanEngine>.Instance, clock: clock,
            challengesRunning: challengesRunning);
        _engines.Add(engine);
        return engine;
    }

    private FakeCommands NewCommands() => new(postgres.Store, _node.OwnerId);

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

    private async Task<Guid> SeedDraftAsync(Guid projectId, CancellationToken cancellationToken, string objective = "Refine me")
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid taskId = DomainId.New();
        session.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
            taskId, projectId, objective, ["A criterion"], TaskType.Feature, agentContext: "Some context",
            constraints: null, externalReference: null, Now, _node.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<Guid> StartPlanAsync(
        Guid projectId, CancellationToken cancellationToken, PlanPhase? phase = null, bool noExistingItem = false, bool untracked = false,
        DateTimeOffset? startedAt = null, params Guid[] drafts)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid planId = DomainId.New();
        session.Events.StartStream<PlanAggregate>(planId, new PlanStarted(
            planId, projectId, drafts, phase ?? PlanPhase.Refinement, noExistingItem, untracked, _node.OwnerId, startedAt ?? Now));
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

    /// <summary>A session runs as a background job, so what it records is awaited rather than assumed.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the condition should have come true");
            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task SweepUntilAsync(
        PlanEngine engine, Func<Task<bool>> condition, CancellationToken cancellationToken)
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

    private Task<bool> ParkedAsync(Guid planId, Guid draftId, PlanParkReason reason, CancellationToken cancellationToken) =>
        PlanAsync(planId, cancellationToken).ContinueWith(task => task.Result.Draft(draftId)!.ParkReason == reason, cancellationToken);

    private const string UnchangedReport = "Checked every claim.\n\nREFINEMENT: unchanged\n";
    private const string RevisedReport = "Rewrote the criteria.\n\nREFINEMENT: revised\n";
    private const string RevisionFile = "---\nobjective: Revised by the session\ncriteria:\n  - >-\n    It ships.\n---\nPointers.\n";

    [Fact]
    public async Task A_plan_dispatches_one_refinement_session_off_the_sweep_and_writes_both_streams_and_creates_no_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, UnchangedReport);
        PlanEngine engine = NewEngine(session, processes, NewCommands());

        PlanSweepResult first = await engine.PollOnceAsync(cts.Token);

        first.Dispatched.Should().Be(1);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).Draft(draft)!.Phase == PlanPhase.Challenge, cts.Token);
        AgentSpawnRequest spawn = session.Spawns.Should().ContainSingle().Subject;
        spawn.TaskId.Should().Be(draft);
        spawn.SessionName.Should().Contain("refinement");
        spawn.SkipPermissions.Should().BeTrue();
        spawn.UsesReviewPermissions.Should().BeFalse();
        spawn.Model.Value.Should().Be(new DaemonOptions().ResolveModel(Hall9k.Domain.Features.Run.AgentRole.Refinement, null, null).Value);
        spawn.RunDirectory.Should().Be(RunPaths.GlobalDirectory(spawn.SessionId));
        Path.GetFullPath(spawn.WorktreePath).Should().Be(Path.GetFullPath(_checkout));
        spawn.Prompt.Should().Contain("Refine me").And.Contain("commit 0123abc").And.Contain(RunPaths.RefinementReportFile(spawn.SessionId));

        IReadOnlyList<object> task = await StreamAsync(draft, cts.Token);
        DraftRefinementDispatched dispatched = task.OfType<DraftRefinementDispatched>().Should().ContainSingle().Subject;
        dispatched.SessionId.Should().Be(spawn.SessionId);
        dispatched.NodeId.Should().Be(_node.NodeId);
        dispatched.Purpose.Should().Be(RefinementPurpose.Refine);
        DraftRefinementRecorded recorded = task.OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        recorded.Outcome.Should().Be(RefinementOutcome.Unchanged);
        recorded.Failure.Should().Be(RefinementFailureReason.Unknown);
        task.OfType<PublicationTokensRecorded>().Should().ContainSingle()
            .Which.Model!.Value.Should().Be(spawn.Model.Value, "the spend lands under the model it ran on");
        task.OfType<TaskRevised>().Should().BeEmpty();

        IReadOnlyList<object> plan = await StreamAsync(planId, cts.Token);
        plan.OfType<PlanRefinementDispatched>().Should().ContainSingle().Which.SessionId.Should().Be(spawn.SessionId);
        PlanDraftMoved moved = plan.OfType<PlanDraftMoved>().Should().ContainSingle().Subject;
        (moved.Phase, moved.Round, moved.Outcome).Should().Be((PlanPhase.Challenge, 1, PlanOutcome.Unchanged));

        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.Query<RunDetails>().Where(run => run.TaskId == draft).CountAsync(cts.Token)).Should().Be(0, "a plan creates no run");
    }

    [Fact]
    public async Task The_next_sweep_requests_the_challenge_for_the_starter_in_the_same_save_as_the_plans_record_of_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, UnchangedReport);

        PlanSweepResult sweep = await NewEngine(session, processes, NewCommands()).PollOnceAsync(cts.Token);

        sweep.Steps.Should().Be(1);
        session.Spawns.Should().BeEmpty("a plan at challenge starts no refinement session first");
        DraftChallengeRequested requested = (await StreamAsync(draft, cts.Token)).OfType<DraftChallengeRequested>().Should().ContainSingle().Subject;
        requested.RequestedByOwnerId.Should().Be(_node.OwnerId);
        PlanChallengeRequested plan = (await StreamAsync(planId, cts.Token)).OfType<PlanChallengeRequested>().Should().ContainSingle().Subject;
        (plan.DraftId, plan.Round).Should().Be((draft, 1));
        (await PlanAsync(planId, cts.Token)).Draft(draft)!.ChallengeRequestedAt.Should().NotBeNull();

        // A second sweep asks for nothing more while the first request is outstanding.
        (await NewEngine(session, processes, NewCommands()).PollOnceAsync(cts.Token)).Steps.Should().Be(0);
    }

    [Fact]
    public async Task A_revision_is_applied_by_running_the_child_h9k_and_the_draft_goes_to_its_first_challenge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, RevisedReport, revision: RevisionFile), processes, commands);

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await PlanAsync(planId, cts.Token)).Draft(draft)!.Phase == PlanPhase.Challenge, cts.Token);

        commands.Calls.Should().ContainSingle().Which.Should().Be($"revise {draft}");
        IReadOnlyList<object> task = await StreamAsync(draft, cts.Token);
        task.OfType<TaskRevised>().Should().ContainSingle();
        DraftRefinementRecorded recorded = task.OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        (recorded.Outcome, recorded.Failure).Should().Be((RefinementOutcome.Revised, RefinementFailureReason.Unknown));
        (await PlanAsync(planId, cts.Token)).Draft(draft)!.Outcome.Should().Be(PlanOutcome.Revised);
    }

    [Fact]
    public async Task A_revision_that_names_a_model_is_refused_without_running_the_command_and_the_plan_parks()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(
            new ScriptedRefinement(processes, RevisedReport, revision: RevisionFile.Replace("---\nobjective", "---\nmodel: claude-haiku-5-5\nobjective")),
            processes, commands);

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.RevisionRefused, cts.Token), cts.Token);

        commands.Calls.Should().BeEmpty();
        DraftRefinementRecorded recorded = (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        (recorded.Outcome, recorded.Failure).Should().Be((RefinementOutcome.Revised, RefinementFailureReason.RevisionRefused));
        File.ReadAllText(RunPaths.PlanReviseOutputFile(planId, recorded.SessionId!.Value)).Should().Contain("names a model");
    }

    [Fact]
    public async Task A_refusal_from_the_child_command_parks_as_revision_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        commands.RefuseRevise = true;

        await NewEngine(new ScriptedRefinement(processes, RevisedReport, revision: RevisionFile), processes, commands).PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.RevisionRefused, cts.Token), cts.Token);

        (await StreamAsync(draft, cts.Token)).OfType<TaskRevised>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_revised_report_without_a_revision_file_is_unreadable_and_parks_as_a_failed_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();

        await NewEngine(new ScriptedRefinement(processes, RevisedReport), processes, NewCommands()).PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.SessionFailed, cts.Token), cts.Token);

        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle()
            .Which.Failure.Should().Be(RefinementFailureReason.UnreadableReport);
    }

    [Fact]
    public async Task A_draft_changed_after_dispatch_is_not_revised_and_the_plan_parks_and_a_change_the_prompt_does_not_carry_does_not_count()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid changed = await SeedDraftAsync(projectId, cts.Token, "Will be changed");
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: changed);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();

        // A person revises the objective while the session runs.
        ScriptedRefinement session = new(processes, RevisedReport, revision: RevisionFile, onSpawn: _ =>
        {
            using IDocumentSession edit = postgres.Store.LightweightSession();
            edit.Events.Append(changed, new TaskRevised(
                changed, Optional<string>.Of("A person changed it"), default, default, default, default, default, Now, _node.OwnerId));
            edit.SaveChangesAsync().GetAwaiter().GetResult();
        });
        await NewEngine(session, processes, commands).PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, changed, PlanParkReason.DraftChanged, cts.Token), cts.Token);

        commands.Calls.Should().BeEmpty("the revision is not applied to a draft the session did not read");
        (await StreamAsync(changed, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle()
            .Which.Failure.Should().Be(RefinementFailureReason.DraftChanged);

        Guid effortOnly = await SeedDraftAsync(projectId, cts.Token, "Effort only");
        Guid second = await StartPlanAsync(projectId, cts.Token, drafts: effortOnly);
        ScriptedRefinement quiet = new(processes, RevisedReport, revision: RevisionFile, onSpawn: _ =>
        {
            using IDocumentSession edit = postgres.Store.LightweightSession();
            edit.Events.Append(effortOnly, new TaskRevised(
                effortOnly, default, default, default, default, default, default, Now, _node.OwnerId,
                Effort: Optional<AgentEffort>.Of(AgentEffort.Low)));
            edit.SaveChangesAsync().GetAwaiter().GetResult();
        });
        await NewEngine(quiet, processes, commands).PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await PlanAsync(second, cts.Token)).Draft(effortOnly)!.Phase == PlanPhase.Challenge, cts.Token);
        commands.Calls.Should().ContainSingle(call => call.Contains(effortOnly.ToString()));
    }

    [Theory]
    [InlineData("No closing line.", Ending.Success, "UnreadableReport")]
    [InlineData(null, Ending.Success, "NoReport")]
    [InlineData(UnchangedReport, Ending.ErrorResult, "SessionError")]
    [InlineData(UnchangedReport, Ending.NoResult, "NoResult")]
    public async Task A_session_that_ends_badly_records_a_closed_failure_on_the_task_parks_the_plan_and_keeps_its_files(
        string? report, Ending ending, string expectedReason)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, report, ending);

        await NewEngine(session, processes, NewCommands()).PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.SessionFailed, cts.Token), cts.Token);

        DraftRefinementRecorded recorded = (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        recorded.Failure.Value.Should().Be(expectedReason);
        recorded.Outcome.Should().Be(RefinementOutcome.Unknown);
        Directory.Exists(session.Spawns[0].RunDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task A_session_past_its_bound_is_terminated_and_recorded_as_timed_out_and_the_plan_parks()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();

        PlanEngine engine = NewEngine(
            new ScriptedRefinement(processes, UnchangedReport, Ending.NeverEnds), processes, NewCommands(),
            options => options.PlanRefinementTimeout = TimeSpan.FromMilliseconds(300));
        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.SessionFailed, cts.Token), cts.Token);

        processes.Terminations.Should().ContainSingle();
        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle()
            .Which.Failure.Should().Be(RefinementFailureReason.TimedOut);
    }

    [Fact]
    public async Task A_split_parks_the_plan_on_the_draft_as_split_proposed_and_applies_no_revision()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(
            new ScriptedRefinement(processes, "Two increments.\n\nREFINEMENT: split\nTASK: Read the flag\nTASK: Act on the flag\n"),
            processes, commands);

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.SplitProposed, cts.Token), cts.Token);

        commands.Calls.Should().BeEmpty("a split applies no revision");
        DraftRefinementRecorded recorded = (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        (recorded.Outcome, recorded.Failure).Should().Be((RefinementOutcome.Split, RefinementFailureReason.Unknown));
    }

    [Fact]
    public async Task A_question_parks_the_plan_and_nothing_starts_until_a_resume_which_runs_the_session_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid asked = await SeedDraftAsync(projectId, cts.Token, "Asks");
        Guid sibling = await SeedDraftAsync(projectId, cts.Token, "Sibling");
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: [asked, sibling]);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, "Only a person can settle this.\n\nREFINEMENT: question\n");
        PlanEngine engine = NewEngine(session, processes, NewCommands());

        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, asked, PlanParkReason.Question, cts.Token), cts.Token);

        for (int sweep = 0; sweep < 3; sweep++)
        {
            await engine.PollOnceAsync(cts.Token);
        }

        session.Spawns.Should().ContainSingle("a parked plan starts no new session for any of its drafts, the sibling's included");

        await using (IDocumentSession session2 = postgres.Store.LightweightSession())
        {
            PlanAggregate plan = (await session2.Events.AggregateStreamAsync<PlanAggregate>(planId, token: cts.Token))!;
            session2.Events.Append(planId, PlanDecider.Resume(plan, _node.OwnerId, Now));
            await session2.SaveChangesAsync(cts.Token);
        }

        await SweepUntilAsync(engine, () => Task.FromResult(session.Spawns.Count >= 2), cts.Token);
        session.Spawns.Select(spawn => spawn.TaskId).Should().Contain(asked);
    }

    [Fact]
    public async Task A_verdict_publish_as_written_makes_the_draft_ready_and_the_plan_publishes_it_with_the_attestation_and_ends_published()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, noExistingItem: true, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, commands);
        await engine.PollOnceAsync(cts.Token);

        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.PublishAsWritten, cts.Token);
        await engine.PollOnceAsync(cts.Token);

        commands.Calls.Should().Equal($"publish {draft}");
        commands.LastAttestation.Should().Be((true, false));
        PlanDetails plan = await PlanAsync(planId, cts.Token);
        plan.State.Should().Be(PlanState.Published);
        plan.Draft(draft)!.Published.Should().BeTrue();
        (await StreamAsync(planId, cts.Token)).OfType<PlanPublished>().Should().ContainSingle();
        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(draft, cts.Token))!;
        task.State.Should().Be(TaskState.Published, "assigning and queueing stay a person's act");
        task.AssigneeOwnerId.Should().BeNull();
    }

    [Fact]
    public async Task A_ready_draft_a_person_revised_afterwards_parks_as_draft_changed_and_nothing_publishes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid ready = await SeedDraftAsync(projectId, cts.Token, "Ready");
        Guid sibling = await SeedDraftAsync(projectId, cts.Token, "Sibling");
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: [ready, sibling]);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(ready, ChallengeVerdict.PublishAsWritten, cts.Token);
        await engine.PollOnceAsync(cts.Token);
        (await PlanAsync(planId, cts.Token)).Draft(ready)!.Ready.Should().BeTrue();

        await ReviseObjectiveAsync(ready, cts.Token);
        await SweepUntilAsync(engine, () => ParkedAsync(planId, ready, PlanParkReason.DraftChanged, cts.Token), cts.Token);

        commands.Calls.Should().BeEmpty("a draft nobody judged since its revision is not published");
        (await PlanAsync(planId, cts.Token)).Draft(ready)!.Ready.Should().BeFalse();
    }

    [Fact]
    public async Task A_verdict_that_landed_on_a_draft_revised_while_the_challenge_ran_parks_instead_of_making_it_ready()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, commands);
        await engine.PollOnceAsync(cts.Token);

        Guid challengeSession = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(draft, new DraftChallengeDispatched(
                draft, challengeSession, _node.NodeId, Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High));
            await session.SaveChangesAsync(cts.Token);
        }

        await ReviseObjectiveAsync(draft, cts.Token);
        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.PublishAsWritten, cts.Token, dispatchedSession: challengeSession);
        await SweepUntilAsync(engine, () => ParkedAsync(planId, draft, PlanParkReason.DraftChanged, cts.Token), cts.Token);

        commands.Calls.Should().BeEmpty("the verdict judged the text as it stood at dispatch");
        (await PlanAsync(planId, cts.Token)).Draft(draft)!.Ready.Should().BeFalse();
    }

    [Fact]
    public async Task A_publish_as_revised_verdict_gets_one_apply_session_and_then_publishes_with_no_second_challenge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        ScriptedRefinement session = new(processes, RevisedReport, revision: RevisionFile);
        PlanEngine engine = NewEngine(session, processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.PublishAsRevised, cts.Token, must: 1);

        await SweepUntilAsync(engine, async () => (await PlanAsync(planId, cts.Token)).State == PlanState.Published, cts.Token);

        session.Spawns.Should().ContainSingle();
        session.Spawns[0].Prompt.Should().Contain("Apply the verdict's must and should changes").And.Contain("publish as revised (1 must");
        commands.Calls.Should().Equal($"revise {draft}", $"publish {draft}");
        (await StreamAsync(draft, cts.Token)).OfType<DraftChallengeRequested>().Should().ContainSingle("no second challenge");
        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementDispatched>().Should().ContainSingle()
            .Which.Purpose.Should().Be(RefinementPurpose.ApplyVerdict);
    }

    [Fact]
    public async Task A_person_revising_while_a_publish_as_revised_verdict_waits_parks_the_plan_instead_of_applying_it_to_unjudged_text()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        ScriptedRefinement session = new(processes, RevisedReport, revision: RevisionFile);
        PlanEngine engine = NewEngine(session, processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.PublishAsRevised, cts.Token, must: 1);

        // The launch hold keeps the apply session from starting, which is what leaves the verdict waiting.
        await AppendToNodeAsync(new NodeLaunchHoldRaised(_node.NodeId, "claude is signed out", Now), cts.Token);
        await engine.PollOnceAsync(cts.Token);
        (await PlanAsync(planId, cts.Token)).Draft(draft)!.Purpose.Should().Be(RefinementPurpose.ApplyVerdict);
        await ReviseObjectiveAsync(draft, cts.Token);
        await AppendToNodeAsync(new NodeLaunchHoldCleared(_node.NodeId, Now), cts.Token);

        await engine.PollOnceAsync(cts.Token);

        (await ParkedAsync(planId, draft, PlanParkReason.DraftChanged, cts.Token)).Should().BeTrue();
        session.Spawns.Should().BeEmpty("the verdict judged text that is no longer the draft");
        commands.Calls.Should().BeEmpty("nothing was applied or published");

        // The resume is the person's answer: the next step is the new challenge, not a second park for the same revision.
        await using (IDocumentSession resume = postgres.Store.LightweightSession())
        {
            resume.Events.Append(planId, new PlanResumed(planId, projectId, _node.OwnerId, Now));
            await resume.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);

        (await StreamAsync(draft, cts.Token)).OfType<DraftChallengeRequested>().Should().HaveCount(
            2, "the first challenge was the verdict's own; the resume sent the draft back to one of the text as it now stands");
        (await StreamAsync(planId, cts.Token)).OfType<PlanParked>().Should().ContainSingle("the resume was not met with a second park");
        session.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task Do_not_publish_on_the_last_round_parks_the_plan_without_starting_another_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, RevisedReport, revision: RevisionFile);
        PlanEngine engine = NewEngine(session, processes, NewCommands(), options => options.PlanRoundCap = 1);
        await engine.PollOnceAsync(cts.Token);

        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.DoNotPublish, cts.Token, must: 1);
        await engine.PollOnceAsync(cts.Token);

        (await ParkedAsync(planId, draft, PlanParkReason.RoundsExhausted, cts.Token)).Should().BeTrue();
        session.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task A_revision_the_child_refuses_because_a_person_revised_first_parks_as_draft_changed_and_not_as_a_refused_revision()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        commands.PersonRevisesFirst = true;

        await NewEngine(new ScriptedRefinement(processes, RevisedReport, revision: RevisionFile), processes, commands).PollOnceAsync(cts.Token);
        await EventuallyAsync(() => ParkedAsync(planId, draft, PlanParkReason.DraftChanged, cts.Token), cts.Token);

        commands.Calls.Should().ContainSingle("the daemon's own check saw nothing; the child is what refused");
        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle()
            .Which.Failure.Should().Be(RefinementFailureReason.DraftChanged);
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<TaskDetails>(draft, cts.Token))!.RevisedSinceLastDraftSession.Should().BeTrue("the person's text is still unread by any session");
    }

    [Fact]
    public async Task A_plan_asks_for_one_challenge_at_a_time_so_a_queued_one_cannot_start_after_the_plan_parks()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid first = await SeedDraftAsync(projectId, cts.Token, "First");
        Guid second = await SeedDraftAsync(projectId, cts.Token, "Second");
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: [first, second]);
        FakeProcessManager processes = new();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, NewCommands());

        await engine.PollOnceAsync(cts.Token);

        (await StreamAsync(first, cts.Token)).OfType<DraftChallengeRequested>().Should().ContainSingle();
        (await StreamAsync(second, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty("it waits for the first to land");

        // The first ends with no verdict: the plan parks, and the second is never asked for.
        await AppendChallengeOutcomeAsync(first, ChallengeVerdict.Unknown, cts.Token, failure: ChallengeFailureReason.TimedOut);
        await engine.PollOnceAsync(cts.Token);

        (await ParkedAsync(planId, first, PlanParkReason.ChallengeFailed, cts.Token)).Should().BeTrue();
        (await StreamAsync(second, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty();
    }

    [Fact]
    public async Task Another_nodes_expiry_for_a_challenge_still_running_here_does_not_park_the_plan_and_the_sessions_verdict_moves_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, noExistingItem: true, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        RunningChallengeSessions challengesRunning = new();
        PlanEngine engine = NewEngine(
            new ScriptedRefinement(processes, UnchangedReport), processes, commands, challengesRunning: challengesRunning);
        await engine.PollOnceAsync(cts.Token);

        Guid challengeSession = await AppendChallengeDispatchAsync(draft, cts.Token);
        challengesRunning.Add(challengeSession);
        await AppendChallengeOutcomeAsync(
            draft, ChallengeVerdict.Unknown, cts.Token, failure: ChallengeFailureReason.DispatchExpired, dispatchedSession: challengeSession);
        await engine.PollOnceAsync(cts.Token);

        PlanDetails waiting = await PlanAsync(planId, cts.Token);
        waiting.IsParked.Should().BeFalse("this node is still running the session the expiry names");
        waiting.Draft(draft)!.ChallengeRequestedAt.Should().NotBeNull("the plan still waits for the session's own outcome");

        // The challenge engine records the verdict before it lets go of the session.
        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.PublishAsWritten, cts.Token, dispatchedSession: challengeSession);
        challengesRunning.Remove(challengeSession);
        await engine.PollOnceAsync(cts.Token);

        (await StreamAsync(planId, cts.Token)).OfType<PlanDraftMoved>().Should().Contain(moved => moved.DraftId == draft && moved.Ready);
        (await StreamAsync(planId, cts.Token)).OfType<PlanParked>().Should().BeEmpty();
        commands.Calls.Should().Equal($"publish {draft}");
    }

    [Fact]
    public async Task Another_nodes_expiry_for_a_challenge_this_node_dispatched_but_no_longer_runs_parks_the_plan()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        RunningChallengeSessions challengesRunning = new();
        PlanEngine engine = NewEngine(
            new ScriptedRefinement(processes, UnchangedReport), processes, NewCommands(), challengesRunning: challengesRunning);
        await engine.PollOnceAsync(cts.Token);

        // Dispatched here and then lost, as after a restart: nothing here runs the session, so it cannot hold the plan.
        Guid challengeSession = await AppendChallengeDispatchAsync(draft, cts.Token);
        challengesRunning.Add(challengeSession);
        challengesRunning.Remove(challengeSession);
        await AppendChallengeOutcomeAsync(
            draft, ChallengeVerdict.Unknown, cts.Token, failure: ChallengeFailureReason.DispatchExpired, dispatchedSession: challengeSession);
        await engine.PollOnceAsync(cts.Token);

        (await ParkedAsync(planId, draft, PlanParkReason.ChallengeFailed, cts.Token)).Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_publish_parks_and_a_draft_that_reads_published_counts_as_published_however_the_step_ended()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid refused = await SeedDraftAsync(projectId, cts.Token, "Refused");
        Guid cutShort = await SeedDraftAsync(projectId, cts.Token, "Cut short");
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: [cutShort, refused]);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(cutShort, ChallengeVerdict.PublishAsWritten, cts.Token);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(refused, ChallengeVerdict.PublishAsWritten, cts.Token);

        commands.PublishCommitsButFails = true;
        commands.RefusePublish = true;
        await engine.PollOnceAsync(cts.Token);
        PlanDetails afterFirst = await PlanAsync(planId, cts.Token);
        afterFirst.Draft(cutShort)!.Published.Should().BeTrue("the task reads Published, whatever the exit code said");
        afterFirst.Draft(refused)!.Published.Should().BeTrue("the fake commits for every draft in this mode");

        // A plain refusal: nothing commits, the plan parks, and what is already published stays published.
        Guid second = await SeedDraftAsync(projectId, cts.Token, "Second");
        Guid third = await SeedDraftAsync(projectId, cts.Token, "Third");
        Guid parkedPlan = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: [second, third]);
        FakeCommands refusing = NewCommands();
        refusing.RefusePublish = true;
        PlanEngine refusingEngine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, refusing);
        await refusingEngine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(second, ChallengeVerdict.PublishAsWritten, cts.Token);
        await refusingEngine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(third, ChallengeVerdict.PublishAsWritten, cts.Token);
        await refusingEngine.PollOnceAsync(cts.Token);

        (await ParkedAsync(parkedPlan, second, PlanParkReason.PublishRefused, cts.Token)).Should().BeTrue();
        refusing.Calls.Should().ContainSingle("a park stops the plan before it publishes the rest");

        refusing.RefusePublish = false;
        await using (IDocumentSession resume = postgres.Store.LightweightSession())
        {
            PlanAggregate plan = (await resume.Events.AggregateStreamAsync<PlanAggregate>(parkedPlan, token: cts.Token))!;
            resume.Events.Append(parkedPlan, PlanDecider.Resume(plan, _node.OwnerId, Now));
            await resume.SaveChangesAsync(cts.Token);
        }

        await refusingEngine.PollOnceAsync(cts.Token);
        (await PlanAsync(parkedPlan, cts.Token)).State.Should().Be(PlanState.Published);
        refusing.Calls.Should().HaveCount(3, "a resume publishes the rest");
    }

    [Fact]
    public async Task A_draft_published_or_abandoned_by_hand_answers_a_park_and_the_plan_carries_on_without_a_resume()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid stuck = await SeedDraftAsync(projectId, cts.Token, "Stuck");
        Guid other = await SeedDraftAsync(projectId, cts.Token, "Other");
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: [stuck, other]);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(stuck, ChallengeVerdict.Unknown, cts.Token, failure: ChallengeFailureReason.TimedOut);
        await engine.PollOnceAsync(cts.Token);
        (await ParkedAsync(planId, stuck, PlanParkReason.ChallengeFailed, cts.Token)).Should().BeTrue();
        (await StreamAsync(other, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty(
            "a plan parked on a sibling asks for no further challenge, so none can start after the park");
        commands.Calls.Should().BeEmpty("the plan is parked, so nothing is published");

        await using (IDocumentSession byHand = postgres.Store.LightweightSession())
        {
            byHand.Events.Append(stuck, new TaskAbandoned(stuck, "no longer wanted", Now, _node.OwnerId));
            await byHand.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(other, ChallengeVerdict.PublishAsWritten, cts.Token);
        await engine.PollOnceAsync(cts.Token);

        PlanDetails plan = await PlanAsync(planId, cts.Token);
        plan.Draft(stuck)!.Abandoned.Should().BeTrue();
        plan.IsParked.Should().BeFalse();
        commands.Calls.Should().Equal($"publish {other}");
        plan.State.Should().Be(PlanState.Published);
    }

    [Fact]
    public async Task A_session_that_ends_on_its_own_after_the_plan_was_cancelled_applies_no_revision()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();

        // The cancel lands while the session runs and the sweep that would stop it is busy elsewhere, so the session
        // ends normally with a revision to apply.
        void CancelThePlan(AgentSpawnRequest request)
        {
            using IDocumentSession cancel = postgres.Store.LightweightSession();
            PlanAggregate plan = cancel.Events.AggregateStreamAsync<PlanAggregate>(planId).GetAwaiter().GetResult()!;
            cancel.Events.Append(planId, PlanDecider.Cancel(plan, "starting over", _node.OwnerId, Now));
            cancel.SaveChangesAsync().GetAwaiter().GetResult();
        }

        PlanEngine engine = NewEngine(
            new ScriptedRefinement(processes, RevisedReport, revision: RevisionFile, onSpawn: CancelThePlan), processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await EventuallyAsync(async () => (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Any(), cts.Token);

        commands.Calls.Should().BeEmpty("a cancelled plan applies nothing");
        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Single().Failure.Should().Be(RefinementFailureReason.Cancelled);
        (await StreamAsync(draft, cts.Token)).OfType<TaskRevised>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_draft_abandoned_while_its_publish_ran_is_recorded_abandoned_and_not_published()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, PlanPhase.Challenge, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, commands);
        await engine.PollOnceAsync(cts.Token);
        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.PublishAsWritten, cts.Token);

        commands.AbandonDuringPublish = true;
        await engine.PollOnceAsync(cts.Token);

        PlanDetails plan = await PlanAsync(planId, cts.Token);
        plan.Draft(draft)!.Abandoned.Should().BeTrue();
        plan.Draft(draft)!.Published.Should().BeFalse();
        (await StreamAsync(planId, cts.Token)).OfType<PlanDraftPublished>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_first_session_for_a_draft_that_already_has_a_verdict_is_told_the_verdict_and_its_report()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        await AppendChallengeOutcomeAsync(draft, ChallengeVerdict.DoNotPublish, cts.Token, must: 2);
        await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, UnchangedReport);

        await NewEngine(session, processes, NewCommands()).PollOnceAsync(cts.Token);

        session.Spawns.Should().ContainSingle().Which.Prompt
            .Should().Contain("do not publish (2 must").And.Contain("already judged this draft").And.NotContain("going to its first challenge");
    }

    [Fact]
    public async Task A_launch_hold_a_spent_budget_or_a_project_at_zero_stops_every_new_session_and_challenge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        FakeProcessManager processes = new();

        Guid zeroProject = await SeedProjectAsync(cts.Token, maxParallel: 0);
        Guid zeroDraft = await SeedDraftAsync(zeroProject, cts.Token);
        Guid zeroAtChallenge = await SeedDraftAsync(zeroProject, cts.Token, "At challenge");
        await StartPlanAsync(zeroProject, cts.Token, drafts: zeroDraft);
        await StartPlanAsync(zeroProject, cts.Token, PlanPhase.Challenge, drafts: zeroAtChallenge);
        ScriptedRefinement session = new(processes, UnchangedReport);
        PlanEngine engine = NewEngine(session, processes, NewCommands());
        await engine.PollOnceAsync(cts.Token);
        session.Spawns.Should().BeEmpty("max parallel tasks 0 pauses a plan's sessions too");
        (await StreamAsync(zeroAtChallenge, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty();

        Guid project = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(project, cts.Token, "Held");
        await StartPlanAsync(project, cts.Token, drafts: draft);
        await using (IDocumentSession hold = postgres.Store.LightweightSession())
        {
            hold.Events.Append(_node.NodeId, new NodeLaunchHoldRaised(_node.NodeId, "claude is signed out", Now));
            await hold.SaveChangesAsync(cts.Token);
        }

        // Only this project's plan is held by the launch hold; the zero-project plans stay held for their own reason.
        PlanEngine held = NewEngine(session, processes, NewCommands());
        await held.PollOnceAsync(cts.Token);
        session.Spawns.Should().BeEmpty("the node's launch hold stands");

        await using (IDocumentSession clear = postgres.Store.LightweightSession())
        {
            clear.Events.Append(_node.NodeId, new NodeLaunchHoldCleared(_node.NodeId, Now));
            await clear.SaveChangesAsync(cts.Token);
        }

        PlanEngine budgeted = NewEngine(session, processes, NewCommands(), options => options.SpendBudgetTokens = 1);
        await using (IDocumentSession spend = postgres.Store.LightweightSession())
        {
            spend.Events.Append(draft, new PublicationTokensRecorded(draft, 5_000, 100, 0m, DateTimeOffset.UtcNow, 0, 0, AgentModel.FromInput(AgentModel.ChallengeDefault)));
            await spend.SaveChangesAsync(cts.Token);
        }

        await budgeted.PollOnceAsync(cts.Token);
        session.Spawns.Should().BeEmpty("the spend budget is spent");
    }

    [Fact]
    public async Task One_session_runs_at_a_time_on_a_node_oldest_plan_first_and_the_second_waits_for_the_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid older = await SeedDraftAsync(projectId, cts.Token, "Older");
        Guid newer = await SeedDraftAsync(projectId, cts.Token, "Newer");
        await StartPlanAsync(projectId, cts.Token, startedAt: Now.AddMinutes(5), drafts: newer);
        await StartPlanAsync(projectId, cts.Token, startedAt: Now, drafts: older);
        FakeProcessManager processes = new();
        ScriptedRefinement session = new(processes, UnchangedReport, Ending.NeverEnds);
        PlanEngine engine = NewEngine(session, processes, NewCommands());

        PlanSweepResult first = await engine.PollOnceAsync(cts.Token);
        PlanSweepResult second = await engine.PollOnceAsync(cts.Token);

        first.Dispatched.Should().Be(1);
        second.Dispatched.Should().Be(0);
        session.Spawns.Should().ContainSingle().Which.TaskId.Should().Be(older, "the oldest plan goes first");
    }

    [Fact]
    public async Task Cancelling_a_plan_stops_its_running_session_without_applying_anything_and_leaves_the_draft_a_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, RevisedReport, Ending.NeverEnds, RevisionFile), processes, commands);
        await engine.PollOnceAsync(cts.Token);

        await using (IDocumentSession cancel = postgres.Store.LightweightSession())
        {
            PlanAggregate plan = (await cancel.Events.AggregateStreamAsync<PlanAggregate>(planId, token: cts.Token))!;
            cancel.Events.Append(planId, PlanDecider.Cancel(plan, "starting over", _node.OwnerId, Now));
            await cancel.SaveChangesAsync(cts.Token);
        }

        await SweepUntilAsync(
            engine,
            async () => (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Any(),
            cts.Token);

        processes.Terminations.Should().ContainSingle();
        commands.Calls.Should().BeEmpty("nothing is applied");
        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Single().Failure.Should().Be(RefinementFailureReason.Cancelled);
        (await StreamAsync(planId, cts.Token)).OfType<PlanParked>().Should().BeEmpty("a cancelled plan records nothing more");
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<TaskDetails>(draft, cts.Token))!.State.Should().Be(TaskState.Draft);
        (await query.LoadAsync<TaskDetails>(draft, cts.Token))!.RunningRefinementSessionId.Should().BeNull();
    }

    [Fact]
    public async Task Abandoning_the_draft_a_session_is_refining_stops_it_though_the_plan_has_another_open_draft()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid sibling = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: [draft, sibling]);
        FakeProcessManager processes = new();
        FakeCommands commands = NewCommands();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, RevisedReport, Ending.NeverEnds, RevisionFile), processes, commands);
        await engine.PollOnceAsync(cts.Token);
        Guid running = (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementDispatched>().Any()
            ? draft
            : sibling;

        await using (IDocumentSession byHand = postgres.Store.LightweightSession())
        {
            byHand.Events.Append(running, new TaskAbandoned(running, "no longer wanted", Now, _node.OwnerId));
            await byHand.SaveChangesAsync(cts.Token);
        }

        await SweepUntilAsync(
            engine,
            async () => (await StreamAsync(running, cts.Token)).OfType<DraftRefinementRecorded>().Any(),
            cts.Token);

        processes.Terminations.Should().ContainSingle("the session is stopped though its plan goes on");
        commands.Calls.Should().BeEmpty("nothing is applied to a closed draft");
        (await StreamAsync(running, cts.Token)).OfType<DraftRefinementRecorded>().Single().Failure.Should().Be(RefinementFailureReason.DraftClosed);
        (await PlanAsync(planId, cts.Token)).IsLive.Should().BeTrue();
    }

    [Fact]
    public async Task A_session_running_when_the_daemon_restarted_is_recorded_as_failed_stopped_and_never_dispatched_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid planId = await StartPlanAsync(projectId, cts.Token, drafts: draft);
        Guid orphan = DomainId.New();
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(draft, new DraftRefinementDispatched(
                draft, orphan, _node.NodeId, Now, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, RefinementPurpose.Refine));
            session.Events.Append(planId, new PlanRefinementDispatched(planId, projectId, draft, orphan, RefinementPurpose.Refine, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        // The identity a spawn writes beside the report: the previous daemon process died with the session still running.
        DateTimeOffset startedAt = Now.AddMinutes(-10);
        Directory.CreateDirectory(RunPaths.GlobalDirectory(orphan));
        await File.WriteAllTextAsync(RunPaths.RefinementProcessFile(orphan), $"4242\n{startedAt.UtcTicks}\n", cts.Token);
        FakeProcessManager processes = new();
        processes.MarkAlive(4242);
        ScriptedRefinement session2 = new(processes, UnchangedReport);

        PlanSweepResult sweep = await NewEngine(session2, processes, NewCommands()).PollOnceAsync(cts.Token);

        sweep.Recovered.Should().Be(1);
        session2.Spawns.Should().BeEmpty("a session a restart left behind is never re-dispatched");
        processes.Terminations.Should().ContainSingle().Which.Should().Be((4242, startedAt));
        DraftRefinementRecorded recorded = (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        recorded.Failure.Should().Be(RefinementFailureReason.DaemonRestarted);
        recorded.SessionId.Should().Be(orphan);
        (await ParkedAsync(planId, draft, PlanParkReason.SessionFailed, cts.Token)).Should().BeTrue();
    }

    [Fact]
    public async Task A_draft_the_task_records_as_already_running_a_session_is_not_given_a_second_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        await StartPlanAsync(projectId, cts.Token, drafts: draft);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(draft, new DraftRefinementDispatched(
                draft, DomainId.New(), DomainId.New(), DateTimeOffset.UtcNow.AddMinutes(-1), AgentModel.FromInput(AgentModel.ChallengeDefault),
                AgentEffort.High, RefinementPurpose.Refine));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedRefinement scripted = new(processes, UnchangedReport);

        PlanSweepResult sweep = await NewEngine(scripted, processes, NewCommands()).PollOnceAsync(cts.Token);

        sweep.Dispatched.Should().Be(0);
        scripted.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_nodes_refinement_dispatch_stands_until_the_foreign_ceiling_and_is_then_ended_on_the_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid otherNode = DomainId.New();
        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(draft, new DraftRefinementDispatched(
                draft, DomainId.New(), otherNode, clock.GetUtcNow().AddMinutes(-30), AgentModel.FromInput(AgentModel.ChallengeDefault),
                AgentEffort.High, RefinementPurpose.Refine));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, NewCommands(), clock: clock);
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0, "inside the ceiling the other node is still the one to finish it");

        clock.Advance(TimeSpan.FromMinutes(40));
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0, "the first sweep that finds it overdue only starts the settle period");

        clock.Advance(new DaemonOptions().ForeignSessionSettlePeriod);
        PlanSweepResult expired = await engine.PollOnceAsync(cts.Token);

        expired.Expired.Should().Be(1);
        DraftRefinementRecorded ended = (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().ContainSingle().Subject;
        ended.Failure.Should().Be(RefinementFailureReason.DispatchExpired);
        ended.NodeId.Should().Be(otherNode, "the report, if any, is on the node that ran the session");
    }

    [Fact]
    public async Task An_overdue_refinement_dispatch_whose_outcome_arrives_within_the_settle_period_is_never_expired()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        Guid draft = await SeedDraftAsync(projectId, cts.Token);
        Guid otherNode = DomainId.New();
        Guid sessionId = DomainId.New();
        AgentModel model = AgentModel.FromInput(AgentModel.ChallengeDefault);
        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(draft, new DraftRefinementDispatched(
                draft, sessionId, otherNode, clock.GetUtcNow().AddHours(-2), model, AgentEffort.High, RefinementPurpose.Refine));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        PlanEngine engine = NewEngine(new ScriptedRefinement(processes, UnchangedReport), processes, NewCommands(), clock: clock);
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0, "a node just back from suspension may not have its inbox yet");
        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>().Should().BeEmpty();

        // The dispatching node's outcome, recorded long ago, arrives through replication.
        await using (IDocumentSession session = postgres.Store.LightweightSession())
        {
            session.Events.Append(draft, new DraftRefinementRecorded(
                draft, sessionId, otherNode, RefinementOutcome.Unchanged, RefinementFailureReason.Unknown, RefinementPurpose.Refine, model,
                AgentEffort.High, clock.GetUtcNow().AddHours(-1.9)));
            await session.SaveChangesAsync(cts.Token);
        }

        clock.Advance(new DaemonOptions().ForeignSessionSettlePeriod + TimeSpan.FromMinutes(1));
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0);

        (await StreamAsync(draft, cts.Token)).OfType<DraftRefinementRecorded>()
            .Should().ContainSingle().Which.Failure.Should().Be(RefinementFailureReason.Unknown);
    }

    private async Task AppendToNodeAsync(object @event, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.Append(_node.NodeId, @event);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task ReviseObjectiveAsync(Guid draft, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.Append(draft, new TaskRevised(
            draft, Optional<string>.Of("A person changed it"), default, default, default, default, default, Now, _node.OwnerId));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid> AppendChallengeDispatchAsync(Guid draft, CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.Append(draft, new DraftChallengeDispatched(
            draft, sessionId, _node.NodeId, DateTimeOffset.UtcNow, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High));
        await session.SaveChangesAsync(cancellationToken);
        return sessionId;
    }

    private async Task AppendChallengeOutcomeAsync(
        Guid draft, ChallengeVerdict verdict, CancellationToken cancellationToken, int must = 0, ChallengeFailureReason? failure = null,
        Guid? dispatchedSession = null)
    {
        // An outcome applies only to the session the draft holds as running, so one with no dispatch named gets its own.
        Guid sessionId = dispatchedSession ?? DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        if (dispatchedSession is null)
        {
            session.Events.Append(draft, new DraftChallengeDispatched(
                draft, sessionId, _node.NodeId, DateTimeOffset.UtcNow, AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High));
        }

        session.Events.Append(draft, new DraftChallengeRecorded(
            draft, sessionId, _node.NodeId, verdict, failure ?? ChallengeFailureReason.Unknown, must, 0, 0,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, DateTimeOffset.UtcNow.AddSeconds(5)));
        await session.SaveChangesAsync(cancellationToken);
    }
}
