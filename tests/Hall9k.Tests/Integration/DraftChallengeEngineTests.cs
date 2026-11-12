using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Challenge;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
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
/// The daemon's half of <c>h9k task challenge</c>, against a real store and a scripted session: one session per request,
/// the verdict read from the report's closing block and nothing else, every way a challenge can end without one recorded
/// from a closed list, and a dispatch left behind by a stopped daemon recorded as failed and never dispatched again.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class DraftChallengeEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private const string GoodReport =
        "Findings.\n\nVERDICT: publish as revised\nCHANGE: must - make the request node scoped - registry line 138\n"
        + "CHANGE: should - say the home - prompt\nCHANGE: park - parallel challenges - trigger: a complaint\n";

    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-challenge-checkout-").FullName;

    public void Dispose()
    {
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

    /// <summary>A session that writes what it is told to write and ends the way it is told to end.</summary>
    private sealed class ScriptedChallenge(
        FakeProcessManager processes, string? report, Ending ending = Ending.Success, bool throwOnSpawn = false) : IExecutor
    {
        private int _nextPid = 7000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            if (throwOnSpawn)
            {
                throw new InvalidOperationException("claude is not installed");
            }

            int pid = _nextPid++;
            Directory.CreateDirectory(request.RunDirectory);
            if (report is not null)
            {
                File.WriteAllText(RunPaths.ChallengeReportFile(request.SessionId), report);
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

    private static DraftChallengeEngine NewEngine(
        DocumentStore store, NodeContext node, IExecutor executor, FakeProcessManager processes,
        TimeSpan? timeout = null, TimeSpan? foreignCeiling = null, ReadingCheckoutLeases? leases = null, TimeProvider? clock = null) =>
        new(store, node, executor, processes, new FixedWorktrees(), (_, _, _, _) => Task.FromResult(
                new ProcessResult(0, "0123abc\n", string.Empty)),
            leases ?? new ReadingCheckoutLeases(),
            Options.Create(new DaemonOptions
            {
                ChallengeTimeout = timeout ?? TimeSpan.FromSeconds(20),
                ForeignPublicationCeiling = foreignCeiling ?? TimeSpan.FromHours(1),
            }),
            NullLogger<DraftChallengeEngine>.Instance,
            clock);

    private sealed class FixedWorktrees : IWorktreeManager
    {
        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
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

    /// <summary>A project, and a draft in it with a challenge requested on this node.</summary>
    private async Task<(Guid TaskId, Guid ProjectId)> SeedAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken, bool skipPermissions = true,
        bool requested = true, string objective = "Challenge me")
    {
        await using IDocumentSession session = store.LightweightSession();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"challenge-{projectId:N}", _checkout, null, "main", Now,
            skipPermissions: skipPermissions));
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, objective, ["A criterion"], TaskType.Feature, agentContext: "Some context",
            constraints: null, externalReference: null, Now, node.OwnerId);
        TaskAggregate aggregate = new();
        aggregate.Apply(added);
        List<object> events = [added];
        if (requested)
        {
            events.Add(TaskDecider.RequestChallenge(aggregate, Now, node.OwnerId, false, "p", null));
        }

        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, projectId);
    }

    private static async Task<IReadOnlyList<object>> StreamAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        return [.. (await query.Events.FetchStreamAsync(taskId, token: cancellationToken)).Select(recorded => recorded.Data)];
    }

    [Fact]
    public async Task A_request_becomes_one_session_and_a_readable_report_becomes_one_verdict_event_with_the_counts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);

        DraftChallengeSweepResult sweep = await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        sweep.Should().Be(new DraftChallengeSweepResult(1, 1, 0, 0, 0));
        session.Spawns.Should().ContainSingle();
        AgentSpawnRequest spawn = session.Spawns[0];
        spawn.TaskId.Should().Be(taskId);
        spawn.SessionName.Should().Contain("challenge");
        spawn.Model.Value.Should().Be(AgentModel.ChallengeDefault);
        spawn.Effort.Should().Be(AgentEffort.High);
        spawn.WorkingDirectoryIsTheCheckout(_checkout);
        spawn.Prompt.Should().Contain("Challenge me").And.Contain("commit 0123abc").And.Contain("At most five model sessions");
        spawn.RunDirectory.Should().Be(RunPaths.GlobalDirectory(spawn.SessionId));

        IReadOnlyList<object> stream = await StreamAsync(store, taskId, cts.Token);
        DraftChallengeDispatched dispatched = stream.OfType<DraftChallengeDispatched>().Should().ContainSingle().Subject;
        dispatched.SessionId.Should().Be(spawn.SessionId);
        dispatched.NodeId.Should().Be(node.NodeId);
        DraftChallengeRecorded recorded = stream.OfType<DraftChallengeRecorded>().Should().ContainSingle().Subject;
        recorded.Should().BeEquivalentTo(new
        {
            Verdict = ChallengeVerdict.PublishAsRevised, MustCount = 1, ShouldCount = 1, ParkCount = 1,
            SessionId = (Guid?)spawn.SessionId, NodeId = node.NodeId,
        });
        recorded.Model.Value.Should().Be(AgentModel.ChallengeDefault);
        recorded.Effort.Should().Be(AgentEffort.High);
        stream.OfType<PublicationTokensRecorded>().Should().ContainSingle()
            .Which.Model!.Value.Should().Be(AgentModel.ChallengeDefault, "the spend lands under the model it ran on");
        stream.OfType<TaskRevised>().Should().BeEmpty("a challenge changes nothing about the draft");
        File.Exists(RunPaths.ChallengeReportFile(spawn.SessionId)).Should().BeTrue("the prose stays in the report file");

        await using IQuerySession query = store.QuerySession();
        TaskDetails details = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.State.Should().Be(TaskState.Draft);
        details.Objective.Should().Be("Challenge me");
        details.ChallengeRequested.Should().BeFalse();
        details.RunningChallengeSessionId.Should().BeNull();
        details.LatestChallengeVerdict.Should().NotBeNull();
    }

    /// <summary>A plan holding the draft, with a challenge it requested at <paramref name="requestedAt"/> and a sibling that is parked.</summary>
    private static async Task<Guid> StartParkedPlanAsync(
        DocumentStore store, NodeContext node, Guid projectId, Guid taskId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        Guid planId = DomainId.New();
        Guid sibling = DomainId.New();
        session.Events.StartStream<PlanAggregate>(
            planId,
            new PlanStarted(planId, projectId, [taskId, sibling], PlanPhase.Challenge, false, false, node.OwnerId, Now),
            new PlanChallengeRequested(planId, projectId, taskId, 1, requestedAt),
            new PlanParked(planId, projectId, sibling, PlanParkReason.Question, null, Now));
        await session.SaveChangesAsync(cancellationToken);
        return planId;
    }

    [Fact]
    public async Task A_challenge_a_parked_plan_requested_waits_until_the_plan_resumes()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid projectId) = await SeedAsync(store, node, cts.Token);
        Guid planId = await StartParkedPlanAsync(store, node, projectId, taskId, requestedAt: Now, cts.Token);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);
        DraftChallengeEngine engine = NewEngine(store, node, session, processes);

        DraftChallengeSweepResult parked = await engine.PollOnceAsync(cts.Token);

        parked.Should().Be(new DraftChallengeSweepResult(0, 0, 0, 0, 0));
        session.Spawns.Should().BeEmpty("a parked plan starts no new session for any of its drafts");
        (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeRecorded>().Should().BeEmpty("the request waits rather than failing");

        await using (IDocumentSession resume = store.LightweightSession())
        {
            resume.Events.Append(planId, new PlanResumed(planId, projectId, node.OwnerId, Now));
            await resume.SaveChangesAsync(cts.Token);
        }

        (await engine.PollOnceAsync(cts.Token)).Should().Be(new DraftChallengeSweepResult(1, 1, 0, 0, 0));
        session.Spawns.Should().ContainSingle();
    }

    [Fact]
    public async Task A_challenge_a_cancelled_plan_requested_is_withdrawn_with_no_session()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid projectId) = await SeedAsync(store, node, cts.Token);
        Guid planId = await StartParkedPlanAsync(store, node, projectId, taskId, requestedAt: Now, cts.Token);
        await using (IDocumentSession cancel = store.LightweightSession())
        {
            cancel.Events.Append(planId, new PlanCancelled(planId, projectId, "not wanted", node.OwnerId, Now));
            await cancel.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        session.Spawns.Should().BeEmpty();
        DraftChallengeRecorded recorded = (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeRecorded>().Should().ContainSingle().Subject;
        (recorded.SessionId, recorded.Failure).Should().Be(((Guid?)null, ChallengeFailureReason.PlanEnded));
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.ChallengeRequested.Should().BeFalse();
    }

    [Fact]
    public async Task A_persons_own_request_on_a_draft_a_parked_plan_holds_is_answered_as_usual()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, Guid projectId) = await SeedAsync(store, node, cts.Token);
        await StartParkedPlanAsync(store, node, projectId, taskId, requestedAt: Now.AddMinutes(-30), cts.Token);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);

        (await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token)).Should().Be(new DraftChallengeSweepResult(1, 1, 0, 0, 0));

        session.Spawns.Should().ContainSingle("the plan's record names another request, so this one is not the plan's to hold");
    }

    [Fact]
    public async Task A_second_sweep_starts_nothing_for_an_answered_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);
        DraftChallengeEngine engine = NewEngine(store, node, session, processes);

        await engine.PollOnceAsync(cts.Token);
        DraftChallengeSweepResult again = await engine.PollOnceAsync(cts.Token);

        again.Should().Be(new DraftChallengeSweepResult(0, 0, 0, 0, 0));
        session.Spawns.Should().ContainSingle();
        (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeDispatched>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_project_without_skip_permissions_gets_the_review_permissions_with_the_report_as_its_one_write()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        await SeedAsync(store, node, cts.Token, skipPermissions: false);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        AgentSpawnRequest spawn = session.Spawns.Should().ContainSingle().Subject;
        spawn.SkipPermissions.Should().BeFalse();
        spawn.UsesReviewPermissions.Should().BeTrue();
        spawn.AssessmentFile.Should().Be(RunPaths.ChallengeReportFile(spawn.SessionId));
        spawn.AllowMentionAnswer.Should().BeFalse("the report is the only file the session may write");
        spawn.Prompt.Should().Contain("No probe can run in this session").And.NotContain("At most five model sessions");
    }

    [Fact]
    public async Task A_project_with_skip_permissions_gets_the_build_shell()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        await SeedAsync(store, node, cts.Token, skipPermissions: true);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);

        await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        AgentSpawnRequest spawn = session.Spawns.Should().ContainSingle().Subject;
        spawn.SkipPermissions.Should().BeTrue();
        spawn.UsesReviewPermissions.Should().BeFalse();
        spawn.AssessmentFile.Should().BeNull();
    }

    [Theory]
    [InlineData("no block at all", Ending.Success, "UnreadableReport")]
    [InlineData(null, Ending.Success, "NoReport")]
    [InlineData("VERDICT: do not publish\nCHANGE: should - x - y\n", Ending.Success, "UnreadableReport")]
    [InlineData(GoodReport, Ending.ErrorResult, "SessionError")]
    [InlineData(GoodReport, Ending.NoResult, "NoResult")]
    public async Task A_session_that_ends_badly_records_a_failure_with_no_verdict_and_leaves_its_files(
        string? report, Ending ending, string expectedReason)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, report, ending);

        DraftChallengeSweepResult sweep = await NewEngine(store, node, session, processes).PollOnceAsync(cts.Token);

        sweep.Failed.Should().Be(1);
        IReadOnlyList<object> stream = await StreamAsync(store, taskId, cts.Token);
        DraftChallengeRecorded recorded = stream.OfType<DraftChallengeRecorded>().Should().ContainSingle().Subject;
        recorded.Verdict.Should().Be(ChallengeVerdict.Unknown);
        recorded.Failure.Value.Should().Be(expectedReason);
        (recorded.MustCount, recorded.ShouldCount, recorded.ParkCount).Should().Be((0, 0, 0));
        Directory.Exists(session.Spawns[0].RunDirectory).Should().BeTrue("the transcript and any report stay on disk");

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.LatestChallengeVerdict.Should().BeNull();
    }

    [Fact]
    public async Task A_session_that_cannot_start_records_a_start_failure()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        FakeProcessManager processes = new();

        await NewEngine(store, node, new ScriptedChallenge(processes, null, throwOnSpawn: true), processes).PollOnceAsync(cts.Token);

        (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeRecorded>().Should().ContainSingle()
            .Which.Failure.Should().Be(ChallengeFailureReason.StartFailed);
    }

    [Fact]
    public async Task A_session_past_its_bound_is_terminated_and_recorded_as_timed_out()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport, Ending.NeverEnds);

        await NewEngine(store, node, session, processes, timeout: TimeSpan.FromMilliseconds(300)).PollOnceAsync(cts.Token);

        processes.Terminations.Should().ContainSingle();
        (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeRecorded>().Should().ContainSingle()
            .Which.Failure.Should().Be(ChallengeFailureReason.TimedOut);
    }

    [Fact]
    public async Task A_task_that_stopped_being_a_draft_records_the_failure_and_spawns_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            session.Events.Append(taskId, TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, node.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedChallenge session2 = new(processes, GoodReport);
        await NewEngine(store, node, session2, processes).PollOnceAsync(cts.Token);

        session2.Spawns.Should().BeEmpty();
        DraftChallengeRecorded recorded = (await StreamAsync(store, taskId, cts.Token))
            .OfType<DraftChallengeRecorded>().Should().ContainSingle().Subject;
        recorded.Failure.Should().Be(ChallengeFailureReason.NoLongerDraft);
        recorded.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task A_dispatch_of_this_node_with_nothing_running_is_recorded_as_failed_and_never_dispatched_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token);
        Guid orphan = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(taskId, new DraftChallengeDispatched(
                taskId, orphan, node.NodeId, Now, AgentModel.ChallengeDefault, AgentEffort.High));
            await session.SaveChangesAsync(cts.Token);
        }

        // The identity a spawn writes beside the report: the previous daemon process died with the session still running.
        DateTimeOffset startedAt = Now.AddMinutes(-10);
        Directory.CreateDirectory(RunPaths.GlobalDirectory(orphan));
        await File.WriteAllTextAsync(RunPaths.ChallengeProcessFile(orphan), $"4242\n{startedAt.UtcTicks}\n", cts.Token);

        FakeProcessManager processes = new();
        processes.MarkAlive(4242);
        ScriptedChallenge session2 = new(processes, GoodReport);
        DraftChallengeSweepResult sweep = await NewEngine(store, node, session2, processes).PollOnceAsync(cts.Token);

        sweep.Recovered.Should().Be(1);
        session2.Spawns.Should().BeEmpty();
        processes.Terminations.Should().ContainSingle("a session the dead daemon left running is stopped, not just forgotten")
            .Which.Should().Be((4242, startedAt));
        DraftChallengeRecorded recorded = (await StreamAsync(store, taskId, cts.Token))
            .OfType<DraftChallengeRecorded>().Should().ContainSingle().Subject;
        recorded.Failure.Should().Be(ChallengeFailureReason.DaemonRestarted);
        recorded.SessionId.Should().Be(orphan);
    }

    [Fact]
    public async Task Another_nodes_dispatch_stands_until_the_foreign_ceiling_and_is_then_ended_so_the_draft_can_be_challenged_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token, requested: false);
        Guid otherNode = DomainId.New();
        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(taskId, new DraftChallengeDispatched(
                taskId, DomainId.New(), otherNode, clock.GetUtcNow().AddMinutes(-30), AgentModel.ChallengeDefault, AgentEffort.High));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        DraftChallengeEngine engine = NewEngine(
            store, node, new ScriptedChallenge(processes, GoodReport), processes, foreignCeiling: TimeSpan.FromHours(1), clock: clock);
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0, "inside the ceiling the other node is still the one to finish it");

        clock.Advance(TimeSpan.FromMinutes(40));
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0, "the first sweep that finds it overdue only starts the settle period");

        clock.Advance(new DaemonOptions().ForeignSessionSettlePeriod);
        DraftChallengeSweepResult expired = await engine.PollOnceAsync(cts.Token);

        expired.Expired.Should().Be(1);
        DraftChallengeRecorded recorded = (await StreamAsync(store, taskId, cts.Token))
            .OfType<DraftChallengeRecorded>().Should().ContainSingle().Subject;
        recorded.Failure.Should().Be(ChallengeFailureReason.DispatchExpired);
        recorded.NodeId.Should().Be(otherNode, "the node that holds whatever report exists is the one named");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.RunningChallengeSessionId.Should().BeNull();
    }

    [Fact]
    public async Task An_overdue_dispatch_whose_outcome_arrives_within_the_settle_period_is_never_expired()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid taskId, _) = await SeedAsync(store, node, cts.Token, requested: false);
        Guid otherNode = DomainId.New();
        Guid sessionId = DomainId.New();
        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(taskId, new DraftChallengeDispatched(
                taskId, sessionId, otherNode, clock.GetUtcNow().AddHours(-2), AgentModel.ChallengeDefault, AgentEffort.High));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        DraftChallengeEngine engine = NewEngine(
            store, node, new ScriptedChallenge(processes, GoodReport), processes, foreignCeiling: TimeSpan.FromHours(1), clock: clock);
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0, "a node just back from suspension may not have its inbox yet");
        (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeRecorded>().Should().BeEmpty();

        // The dispatching node's verdict, recorded long ago, arrives through replication.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(taskId, new DraftChallengeRecorded(
                taskId, sessionId, otherNode, ChallengeVerdict.PublishAsWritten, ChallengeFailureReason.Unknown, 0, 0, 0,
                AgentModel.ChallengeDefault, AgentEffort.High, clock.GetUtcNow().AddHours(-1.9)));
            await session.SaveChangesAsync(cts.Token);
        }

        clock.Advance(new DaemonOptions().ForeignSessionSettlePeriod + TimeSpan.FromMinutes(1));
        (await engine.PollOnceAsync(cts.Token)).Expired.Should().Be(0);

        (await StreamAsync(store, taskId, cts.Token)).OfType<DraftChallengeRecorded>()
            .Should().ContainSingle().Which.Failure.Should().Be(ChallengeFailureReason.Unknown);
    }

    [Fact]
    public async Task Two_requests_on_one_node_run_one_after_the_other_and_never_together()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        (Guid first, _) = await SeedAsync(store, node, cts.Token, objective: "First");
        (Guid second, _) = await SeedAsync(store, node, cts.Token, objective: "Second");
        FakeProcessManager processes = new();
        ScriptedChallenge session = new(processes, GoodReport);
        DraftChallengeEngine engine = NewEngine(store, node, session, processes);

        DraftChallengeSweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.Dispatched.Should().Be(2);
        session.Spawns.Select(spawn => spawn.TaskId).Should().BeEquivalentTo([first, second]);
        foreach (Guid taskId in new[] { first, second })
        {
            IReadOnlyList<object> stream = await StreamAsync(store, taskId, cts.Token);
            stream.OfType<DraftChallengeRecorded>().Should().ContainSingle();
        }
    }
}

internal static class SpawnRequestAssertions
{
    /// <summary>The session runs in the reading checkout the project registered, and nowhere else.</summary>
    public static void WorkingDirectoryIsTheCheckout(this AgentSpawnRequest spawn, string checkout) =>
        Path.GetFullPath(spawn.WorktreePath).Should().Be(Path.GetFullPath(checkout));
}
