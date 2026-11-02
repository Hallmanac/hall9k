using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Feedback;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Plan;
using Hall9k.Domain.Features.Plan.Events;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Feedback;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Notes;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The daemon's half of the feedback step, against a real store and scripted sessions: a held task begins its step on one node
/// only, releases at once when no blocker left a note the trust rule lets into a prompt, runs one check over the notes that did,
/// releases after a check that says no or fails, and, when the check says yes, goes back to Draft, is refined by a plan with one
/// challenge, and is queued again for the same owner and node, or parks for a person, with the step ending in one recorded outcome.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class FeedbackStepEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _checkout = Directory.CreateTempSubdirectory("h9k-feedback-checkout-").FullName;
    private readonly List<FeedbackStepEngine> _feedbackEngines = [];
    private readonly List<PlanEngine> _planEngines = [];
    private readonly List<Guid> _tasks = [];
    private NodeContext _node = null!;

    public async Task InitializeAsync() =>
        _node = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, CancellationToken.None);

    /// <summary>The store is shared by the tests of this class, and the engines scan every held task on the node, so each test abandons what it seeded and ends its plans.</summary>
    public async Task DisposeAsync()
    {
        foreach (FeedbackStepEngine engine in _feedbackEngines)
        {
            await engine.WhenIdleAsync();
        }

        foreach (PlanEngine engine in _planEngines)
        {
            await engine.WhenIdleAsync();
        }

        await using IDocumentSession session = postgres.Store.LightweightSession();
        foreach (PlanDetails plan in (await session.Query<PlanDetails>().ToListAsync()).Where(plan => plan.IsLive))
        {
            session.Events.Append(plan.Id, new PlanCancelled(plan.Id, plan.ProjectId, "test over", null, Now));
        }

        foreach (Guid task in _tasks)
        {
            session.Events.Append(task, new TaskAbandoned(task, "test over", Now, _node.OwnerId));
        }

        await session.SaveChangesAsync();
        _scopedHome.Dispose();
        if (Directory.Exists(_checkout))
        {
            Directory.Delete(_checkout, recursive: true);
        }
    }

    private enum CheckEnding
    {
        Answers,
        ErrorResult,
        NoResult,
        NeverEnds,
    }

    /// <summary>Plays the check session and every refinement session: the check answers as told, a refinement writes the next report and revision it was given.</summary>
    private sealed class ScriptedSessions(FakeProcessManager processes) : IExecutor
    {
        private int _nextPid = 7000;

        public string CheckText { get; set; } = "Nothing here touches the task.\n\nFEEDBACK CHECK: unaffected";

        public CheckEnding Ending { get; set; } = CheckEnding.Answers;

        public Action<AgentSpawnRequest>? OnCheck { get; set; }

        /// <summary>Runs as a refinement session is spawned, which is where a person can change the draft under it.</summary>
        public Action<AgentSpawnRequest>? OnRefinement { get; set; }

        public Queue<(string Report, string? Revision)> Refinements { get; } = new();

        public List<AgentSpawnRequest> Checks { get; } = [];

        public List<AgentSpawnRequest> Refinement { get; } = [];

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            Directory.CreateDirectory(request.RunDirectory);
            if (request.SessionArtifactName?.StartsWith("feedback-check-", StringComparison.Ordinal) == true)
            {
                lock (Checks)
                {
                    Checks.Add(request);
                }

                OnCheck?.Invoke(request);
                if (Ending == CheckEnding.NeverEnds)
                {
                    processes.MarkAlive(pid);
                }
                else if (Ending != CheckEnding.NoResult)
                {
                    WriteResult(
                        RunPaths.SessionStreamFile(request.RunDirectory, request.SessionArtifactName), CheckText, Ending == CheckEnding.ErrorResult);
                }

                return Task.FromResult(new SpawnedAgent(pid, Now));
            }

            lock (Refinement)
            {
                Refinement.Add(request);
            }

            OnRefinement?.Invoke(request);

            (string report, string? revision) = Refinements.Count > 0
                ? Refinements.Dequeue()
                : ("Nothing to do.\n\nREFINEMENT: unchanged\n", null);
            File.WriteAllText(RunPaths.RefinementReportFile(request.SessionId), report);
            if (revision is not null)
            {
                File.WriteAllText(RunPaths.RefinementRevisionFile(request.SessionId), revision);
            }

            WriteResult(RunPaths.StreamFile(request.RunDirectory), "done", isError: false);
            return Task.FromResult(new SpawnedAgent(pid, Now));
        }

        private static void WriteResult(string file, string text, bool isError) =>
            File.WriteAllText(
                file,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "result",
                    ["subtype"] = isError ? "error_during_execution" : "success",
                    ["is_error"] = isError,
                    ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 500, ["output_tokens"] = 40 },
                    ["result"] = text,
                }) + "\n");
    }

    /// <summary>The child <c>h9k</c> of a plan, played by a fake that does to the store what the real command would.</summary>
    private sealed class FakeCommands(DocumentStore store, Guid owner, TaskDependencyGraph graph) : IPlanTaskCommands
    {
        public List<string> Calls { get; } = [];

        public (bool NoExistingItem, bool Untracked)? LastAttestation { get; private set; }

        public async Task<TaskCommandResult> ReviseAsync(Guid planId, Guid sessionId, Guid taskId, string revisionFile, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add($"revise {taskId}");
            }

            await using IDocumentSession session = store.LightweightSession();
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
            await using IDocumentSession session = store.LightweightSession();
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
            session.Events.Append(taskId, TaskDecider.Publish(task, graph, Now, owner));
            await session.SaveChangesAsync(cancellationToken);
            return new TaskCommandResult(0, false, "ok.txt");
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

    private FeedbackStepEngine NewFeedbackEngine(
        IExecutor executor, FakeProcessManager processes, NodeContext? node = null, Action<DaemonOptions>? tweak = null)
    {
        DaemonOptions options = new() { FeedbackCheckTimeout = TimeSpan.FromSeconds(20) };
        tweak?.Invoke(options);
        FeedbackStepEngine engine = new(
            postgres.Store, node ?? _node, executor, processes, new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            new DaemonConnection(postgres.ConnectionString), Options.Create(options), NullLogger<FeedbackStepEngine>.Instance);
        _feedbackEngines.Add(engine);
        return engine;
    }

    private PlanEngine NewPlanEngine(IExecutor executor, FakeProcessManager processes, IPlanTaskCommands commands)
    {
        DaemonOptions options = new() { PlanRefinementTimeout = TimeSpan.FromSeconds(20), ForeignPublicationCeiling = TimeSpan.FromHours(1) };
        PlanEngine engine = new(
            postgres.Store, _node, executor, processes, new FixedWorktrees(),
            (_, _, _, _) => Task.FromResult(new ProcessResult(0, "0123abc\n", string.Empty)),
            new ReadingCheckoutLeases(), commands, new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance),
            new DaemonConnection(postgres.ConnectionString), Options.Create(options), NullLogger<PlanEngine>.Instance);
        _planEngines.Add(engine);
        return engine;
    }

    private async Task<Guid> SeedProjectAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        Guid projectId = DomainId.New();
        session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
            projectId, _node.OwnerId, DomainId.New(), $"feedback-{projectId:N}", _checkout, null, "main", Now, skipPermissions: true));
        await session.SaveChangesAsync(cancellationToken);
        return projectId;
    }

    /// <summary>A blocker task that has reached true closeout, carrying the notes it was given: (text, whether this node observed it being recorded).</summary>
    private async Task<(TaskDependency Dependency, Guid Id)> SeedBlockerAsync(
        Guid projectId, CancellationToken cancellationToken, params (string Text, bool Observed)[] notes)
    {
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            blockerId, projectId, "The blocker", ["it is done"], TaskType.Feature, "ctx", null, null, Now, _node.OwnerId);
        StreamAction stream = session.Events.StartStream<TaskAggregate>(blockerId, added);
        foreach ((string text, bool observed) in notes)
        {
            StreamAction noteStream = session.Events.Append(blockerId, new TaskNoteRecorded(
                blockerId, DomainId.New(), text, new RecordedProvenance(_node.OwnerId, runId, blockerId, HumanAttendance.Unattended),
                TaskNoteSource.ClosingBlock, Now));
            if (observed)
            {
                EventRecordingNode.StampAtAppend(noteStream, _node.NodeId);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        stream.Should().NotBeNull();

        // True closeout is read off the blocker's row and its current run, so the blocker is put where the store reads it as merged.
        await using (IDocumentSession close = postgres.Store.LightweightSession())
        {
            TaskListItem row = (await close.LoadAsync<TaskListItem>(blockerId, cancellationToken))!;
            row.State = TaskState.Done;
            row.CurrentRunId = runId;
            close.Store(row);
            close.Store(new RunDetails { Id = runId, TaskId = blockerId, State = RunState.Completed });
            await close.SaveChangesAsync(cancellationToken);
        }

        TaskDependency dependency = new(blockerId, "The blocker", TaskState.Done, true, RunState.Completed, null, TaskType.Feature, []);
        return (dependency, blockerId);
    }

    /// <summary>A task queued with every blocker closed out for a project that takes the step: Blocked and held, placed on this node unless told otherwise.</summary>
    private async Task<(Guid TaskId, string? Fingerprint)> SeedHeldTaskAsync(
        Guid projectId, TaskDependency blocker, CancellationToken cancellationToken, bool placed = true, bool untracked = false)
    {
        Guid taskId = DomainId.New();
        _tasks.Add(taskId);
        await using IDocumentSession session = postgres.Store.LightweightSession();
        string? fingerprint = await OwnerRootFingerprintResolver.ResolveAsync(session, _node.OwnerId, cancellationToken);
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Page through the results", ["The list endpoint returns every item"], TaskType.Feature, "Read the endpoint.",
            null, null, Now, _node.OwnerId, blockedBy: [blocker.Id]);
        TaskAggregate task = new();
        task.Apply(added);
        // The tracking choice is recorded as the earlier publish gave it; the project tracks no backlog, so the decider is not asked to vet it.
        TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph([blocker]), Now, _node.OwnerId) with
        {
            UntrackedAttested = untracked,
        };
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(
            task, _node.OwnerId, [blocker], Now, _node.OwnerId, fingerprint,
            placed ? Optional<Guid?>.Of(_node.NodeId) : default, projectTakesFeedbackStep: true);
        task.Apply(assigned);
        task.State.Should().Be(TaskState.Blocked);
        task.FeedbackStepHeld.Should().BeTrue();
        session.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, fingerprint);
    }

    private async Task<IReadOnlyList<object>> StreamAsync(Guid streamId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return [.. (await query.Events.FetchStreamAsync(streamId, token: cancellationToken)).Select(recorded => recorded.Data)];
    }

    private async Task<TaskDetails> TaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = postgres.Store.QuerySession();
        return (await query.LoadAsync<TaskDetails>(taskId, cancellationToken))!;
    }

    private async Task SweepUntilAsync(FeedbackStepEngine engine, Func<Task<bool>> condition, CancellationToken cancellationToken)
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

    private async Task<bool> EndedAsync(Guid taskId, CancellationToken cancellationToken) =>
        (await TaskAsync(taskId, cancellationToken)).FeedbackStepResult is not null;

    [Fact]
    public async Task When_no_blocker_left_a_note_the_task_is_released_at_once_and_no_session_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token);
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);

        await SweepUntilAsync(NewFeedbackEngine(sessions, processes), () => EndedAsync(taskId, cts.Token), cts.Token);

        sessions.Checks.Should().BeEmpty();
        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.Queued, "as it would have been without the step");
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.ReleasedWithoutCheck);
        task.FeedbackStepHeld.Should().BeFalse();
        (await StreamAsync(taskId, cts.Token)).OfType<FeedbackStepStarted>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_note_the_trust_rule_holds_back_is_never_offered_and_a_task_with_only_such_notes_runs_no_check()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token, ("A note from a node nobody observed.", false));
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);

        await SweepUntilAsync(NewFeedbackEngine(sessions, processes), () => EndedAsync(taskId, cts.Token), cts.Token);

        sessions.Checks.Should().BeEmpty("the only note was held back, so there was nothing to check");
        (await TaskAsync(taskId, cts.Token)).FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.ReleasedWithoutCheck);
    }

    [Fact]
    public async Task A_check_that_says_the_notes_do_not_affect_the_task_releases_it_and_runs_on_the_role_of_its_own_without_the_held_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(
            projectId, cts.Token, ("The API returns pages rather than a full list.", true), ("UNOBSERVED-NOTE", false));
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);

        await SweepUntilAsync(
            NewFeedbackEngine(sessions, processes, tweak: options =>
            {
                options.ModelByRole.FeedbackCheck = "claude-haiku-5-5";
                options.EffortByRole.FeedbackCheck = "low";
                options.ModelByRole.Refinement = "claude-opus-5-5";
            }),
            () => EndedAsync(taskId, cts.Token), cts.Token);

        AgentSpawnRequest check = sessions.Checks.Should().ContainSingle().Subject;
        check.NoTools.Should().BeTrue();
        check.MaxTurns.Should().Be(1);
        check.Model.Value.Should().Be("claude-haiku-5-5");
        check.Effort.Should().Be(AgentEffort.Low);
        check.TaskId.Should().Be(taskId);
        check.Prompt.Should().Contain("The API returns pages rather than a full list.")
            .And.Contain("Page through the results")
            .And.NotContain("UNOBSERVED-NOTE");

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.Queued);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.ReleasedAfterCheck);
        task.LatestFeedbackCheck!.Answer.Should().Be(FeedbackCheckAnswer.Unaffected);
        task.LatestFeedbackCheckDispatch!.Model.Value.Should().Be("claude-haiku-5-5");
        (await StreamAsync(taskId, cts.Token)).OfType<PublicationTokensRecorded>().Should().ContainSingle("the check's spend is recorded");
    }

    [Theory]
    [InlineData("error", "SessionError")]
    [InlineData("no-result", "NoResult")]
    [InlineData("unreadable", "UnreadableReport")]
    [InlineData("timed-out", "TimedOut")]
    public async Task A_check_that_fails_times_out_or_gives_no_readable_answer_releases_the_task_and_records_the_failure(string how, string failure)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token, ("A note worth reading.", true));
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes)
        {
            Ending = how switch
            {
                "error" => CheckEnding.ErrorResult,
                "no-result" => CheckEnding.NoResult,
                "timed-out" => CheckEnding.NeverEnds,
                _ => CheckEnding.Answers,
            },
            CheckText = how == "unreadable" ? "I think it probably does." : "ignored",
        };

        await SweepUntilAsync(
            NewFeedbackEngine(
                sessions, processes,
                tweak: options => options.FeedbackCheckTimeout = how == "timed-out"
                    ? TimeSpan.FromMilliseconds(400)
                    : TimeSpan.FromSeconds(30)),
            () => EndedAsync(taskId, cts.Token), cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.State.Should().Be(TaskState.Queued, "a failed check never stalls the tasks behind it");
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.ReleasedAfterFailedCheck);
        task.LatestFeedbackCheck!.Answer.Should().Be(FeedbackCheckAnswer.Failed);
        task.LatestFeedbackCheck.Failure.Value.Should().Be(failure, "the failure is recorded on the task");
    }

    [Fact]
    public async Task A_person_dequeuing_the_task_while_the_check_runs_ends_the_step_without_acting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token, ("The API returns pages.", true));
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes)
        {
            CheckText = "They change everything.\n\nFEEDBACK CHECK: affects",
            OnCheck = _ =>
            {
                using IDocumentSession session = postgres.Store.LightweightSession();
                TaskAggregate task = session.Events.AggregateStreamAsync<TaskAggregate>(taskId).GetAwaiter().GetResult()!;
                session.Events.Append(taskId, TaskDecider.Dequeue(task, "a person took it out", leaseHeld: false, Now, _node.OwnerId));
                session.SaveChangesAsync().GetAwaiter().GetResult();
            },
        };

        await SweepUntilAsync(NewFeedbackEngine(sessions, processes), () => EndedAsync(taskId, cts.Token), cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.EndedWithoutActing);
        task.State.Should().Be(TaskState.Published, "the person's dequeue stands, and the step acted on nothing");
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.Query<PlanDetails>().Where(plan => plan.FeedbackStepTaskId == taskId).CountAsync(cts.Token)).Should().Be(0);
    }

    [Fact]
    public async Task Two_nodes_that_observe_the_same_release_start_one_check_between_them()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, Guid blockerId) = await SeedBlockerAsync(projectId, cts.Token, ("The API returns pages.", true));
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token, placed: false);
        NodeContext other = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        other.OwnerId.Should().Be(_node.OwnerId, "two nodes of one owner");
        other.NodeId.Should().NotBe(_node.NodeId);

        // A note a node did not record itself needs the fleet to be trusted, so each node is given one it observed: whichever
        // begins the step has something to check, and the race is only over who begins it.
        await using (IDocumentSession noting = postgres.Store.LightweightSession())
        {
            StreamAction noteStream = noting.Events.Append(blockerId, new TaskNoteRecorded(
                blockerId, DomainId.New(), "The other node's note.",
                new RecordedProvenance(_node.OwnerId, DomainId.New(), blockerId, HumanAttendance.Unattended), TaskNoteSource.ClosingBlock, Now));
            EventRecordingNode.StampAtAppend(noteStream, other.NodeId);
            await noting.SaveChangesAsync(cts.Token);
        }
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);
        FeedbackStepEngine first = NewFeedbackEngine(sessions, processes);
        FeedbackStepEngine second = NewFeedbackEngine(sessions, processes, other);

        // Both read the same hold in the same moment, and both try to begin the step against the same stream version.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        do
        {
            await Task.WhenAll(first.PollOnceAsync(cts.Token), second.PollOnceAsync(cts.Token));
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the step should have ended on whichever node began it");
        }
        while (!await EndedAsync(taskId, cts.Token));

        await Task.WhenAll(first.PollOnceAsync(cts.Token), second.PollOnceAsync(cts.Token));

        IReadOnlyList<object> stream = await StreamAsync(taskId, cts.Token);
        stream.OfType<FeedbackStepStarted>().Should().ContainSingle("only one node begins the step");
        stream.OfType<FeedbackCheckDispatched>().Should().ContainSingle();
        sessions.Checks.Should().ContainSingle("one check between the two nodes");
        stream.OfType<FeedbackStepOutcomeRecorded>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_node_the_task_is_not_placed_on_and_a_teammates_node_start_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token, ("The API returns pages.", true));
        (Guid taskId, _) = await SeedHeldTaskAsync(projectId, blocker, cts.Token, placed: true);
        NodeContext otherNodeOfTheOwner = await NodeBootstrapSeed.NewIsolatedNodeAsync(postgres.Store, cts.Token);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);

        FeedbackStepResultFor(await NewFeedbackEngine(sessions, processes, otherNodeOfTheOwner).PollOnceAsync(cts.Token))
            .Should().Be(0, "the task is placed on another node of the same owner");

        (await TaskAsync(taskId, cts.Token)).FeedbackStepBegun.Should().BeFalse();
        (await TaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Blocked);
        sessions.Checks.Should().BeEmpty();
    }

    private static int FeedbackStepResultFor(FeedbackStepSweepResult sweep) => sweep.Steps;

    /// <summary>A task still waiting on its blocker, which then closes out; the dependency sweep is the door that releases it.</summary>
    private async Task<Guid> SeedWaitingTaskAsync(
        Guid projectId, TaskDependency blocker, bool? taskOverride, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        _tasks.Add(taskId);
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Waits for its blocker", ["It waits"], TaskType.Feature, "ctx", null, null, Now, _node.OwnerId, blockedBy: [blocker.Id]);
        TaskAggregate task = new();
        task.Apply(added);
        List<object> events = [added];
        if (taskOverride is { } value)
        {
            TaskRevised revised = TaskDecider.Revise(
                task, default, default, default, default, default, default, Now, _node.OwnerId, feedbackStep: Optional<bool?>.Of(value));
            task.Apply(revised);
            events.Add(revised);
        }

        TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph([blocker]), Now, _node.OwnerId);
        task.Apply(published);
        // Queued while the blocker was still open: the unmet set is the blocker, whichever the sweep then finds closed out.
        TaskDependency open = blocker with { State = TaskState.Claimed, IsClosedOut = false };
        TaskAssigned assigned = TaskDecider.Assign(task, _node.OwnerId, [open], Now, _node.OwnerId, null, default, projectTakesFeedbackStep: true);
        task.Apply(assigned);
        task.State.Should().Be(TaskState.Blocked);
        events.AddRange([published, assigned]);
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private async Task<Guid> SeedProjectTakingTheStepAsync(bool takes, CancellationToken cancellationToken)
    {
        Guid projectId = await SeedProjectAsync(cancellationToken);
        if (takes)
        {
            await using IDocumentSession settings = postgres.Store.LightweightSession();
            ProjectAggregate project = (await settings.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken))!;
            settings.Events.Append(projectId, ProjectDecider.ChangeSettings(
                project,
                verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.None,
                skipPermissions: Optional<bool>.None,
                contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
                changedAt: Now, changedByOwnerId: _node.OwnerId,
                takesFeedbackStep: Optional<bool>.Of(true)));
            await settings.SaveChangesAsync(cancellationToken);
        }

        return projectId;
    }

    private async Task SweepDependenciesAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        await TaskDependencyResolver.ForEveryBlockedTaskAsync(session, Now, cancellationToken);
    }

    [Theory]
    [InlineData(true, null, "Blocked", true)]
    [InlineData(false, null, "Queued", false)]
    [InlineData(true, false, "Queued", false)]
    [InlineData(false, true, "Blocked", true)]
    public async Task The_dependency_sweep_holds_the_task_whose_last_blocker_cleared_only_when_the_project_or_the_task_says_to(
        bool projectTakes, bool? taskOverride, string expectedState, bool expectedHeld)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectTakingTheStepAsync(projectTakes, cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token, ("A note.", true));
        Guid taskId = await SeedWaitingTaskAsync(projectId, blocker, taskOverride, cts.Token);

        await SweepDependenciesAsync(cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.State.Value.Should().Be(expectedState);
        task.FeedbackStepHeld.Should().Be(expectedHeld);
        (await StreamAsync(taskId, cts.Token)).OfType<TaskDependencyCompleted>().Single().HoldForFeedbackStep.Should().Be(expectedHeld);
    }

    [Fact]
    public async Task A_task_held_by_the_dependency_sweep_is_then_checked_and_released_by_the_step()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectTakingTheStepAsync(true, cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token, ("A note.", true));
        Guid taskId = await SeedWaitingTaskAsync(projectId, blocker, null, cts.Token);
        await using (IDocumentSession placing = postgres.Store.LightweightSession())
        {
            placing.Events.Append(taskId, new TaskPlacementChanged(taskId, _node.NodeId, Now, _node.OwnerId));
            await placing.SaveChangesAsync(cts.Token);
        }

        await SweepDependenciesAsync(cts.Token);
        (await TaskAsync(taskId, cts.Token)).FeedbackStepHeld.Should().BeTrue();
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);

        await SweepUntilAsync(NewFeedbackEngine(sessions, processes), () => EndedAsync(taskId, cts.Token), cts.Token);

        (await TaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Queued);
        sessions.Checks.Should().ContainSingle();
    }

    /// <summary>Plays the challenge engine: when the plan has asked for a challenge, records the verdict the test chose.</summary>
    private async Task AnswerChallengeAsync(Guid taskId, ChallengeVerdict verdict, int must, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not { ChallengeRequested: true })
        {
            return;
        }

        session.Events.Append(taskId, new DraftChallengeRecorded(
            taskId, DomainId.New(), _node.NodeId, verdict, ChallengeFailureReason.Unknown, must, 0, 0,
            AgentModel.FromInput(AgentModel.ChallengeDefault), AgentEffort.High, DateTimeOffset.UtcNow.AddSeconds(5)));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The two engines and the plan engine, taken round until the step has ended.</summary>
    private async Task DriveAsync(
        FeedbackStepEngine feedback, PlanEngine plan, Guid taskId, ChallengeVerdict verdict, int must, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (!await EndedAsync(taskId, cancellationToken))
        {
            await feedback.PollOnceAsync(cancellationToken);
            await plan.PollOnceAsync(cancellationToken);
            await AnswerChallengeAsync(taskId, verdict, must, cancellationToken);
            await feedback.WhenIdleAsync();
            await plan.WhenIdleAsync();
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the step should have ended");
            await Task.Delay(30, cancellationToken);
        }
    }

    private const string RevisedReport = "Rewrote the criteria.\n\nREFINEMENT: revised\n";
    private const string RevisionFile = "---\nobjective: Revised by the session\ncriteria:\n  - >-\n    It pages.\n---\nPointers.\n";

    private async Task<(Guid TaskId, string? Fingerprint, ScriptedSessions Sessions, FakeCommands Commands, FeedbackStepEngine Feedback, PlanEngine Plan)> AffectedAsync(
        CancellationToken cancellationToken, bool untracked = false)
    {
        Guid projectId = await SeedProjectAsync(cancellationToken);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cancellationToken, ("The API returns pages rather than a full list.", true));
        (Guid taskId, string? fingerprint) = await SeedHeldTaskAsync(projectId, blocker, cancellationToken, untracked: untracked);
        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes) { CheckText = "A criterion is now wrong.\n\nFEEDBACK CHECK: affects" };
        FakeCommands commands = new(postgres.Store, _node.OwnerId, new TaskDependencyGraph([blocker]));
        return (taskId, fingerprint, sessions, commands, NewFeedbackEngine(sessions, processes), NewPlanEngine(sessions, processes, commands));
    }

    [Fact]
    public async Task A_task_the_notes_affect_is_refined_challenged_once_judged_published_and_queued_again_for_the_same_owner_and_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, string? fingerprint, ScriptedSessions sessions, FakeCommands commands, FeedbackStepEngine feedback, PlanEngine plan) =
            await AffectedAsync(cts.Token, untracked: true);
        sessions.Refinements.Enqueue((RevisedReport, RevisionFile));
        sessions.Refinements.Enqueue((RevisedReport, RevisionFile));

        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.PublishAsRevised, must: 1, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.RevisedAndRequeued);
        task.State.Should().Be(TaskState.Queued, "its blockers have cleared, so it reaches Queued at once");
        task.AssignedOwnerId.Should().Be(_node.OwnerId);
        task.AssignedOwnerFingerprint.Should().Be(fingerprint);
        task.AssigneeOwnerId.Should().Be(_node.OwnerId);
        task.PlacedOnNodeId.Should().Be(_node.NodeId, "the placement the dequeue cleared is restored");
        task.Objective.Should().Be("Revised by the session");

        IReadOnlyList<object> stream = await StreamAsync(taskId, cts.Token);
        stream.OfType<DraftChallengeRequested>().Should().ContainSingle("there is exactly one challenge");
        stream.OfType<TaskReturnedToDraft>().Should().ContainSingle();
        stream.OfType<TaskUnassigned>().Should().ContainSingle().Which.KeepsAssignee.Should().BeTrue();
        stream.OfType<TaskAssigned>().Should().HaveCount(2).And.Subject.Last().Should().Match<TaskAssigned>(requeue => !requeue.HoldForFeedbackStep);
        stream.OfType<FeedbackStepOutcomeRecorded>().Should().ContainSingle();

        sessions.Refinement.Should().HaveCount(2);
        sessions.Refinement[0].Prompt.Should().Contain("The API returns pages rather than a full list.").And.Contain("There is no second challenge");
        sessions.Refinement[1].Prompt.Should().Contain("Judge each must change and each should change");
        commands.LastAttestation.Should().Be((false, true), "the earlier publish's tracking choice is passed on and --no-existing-item never is");

        await using IQuerySession query = postgres.Store.QuerySession();
        PlanDetails plans = (await query.Query<PlanDetails>().Where(p => p.FeedbackStepTaskId == taskId).ToListAsync(cts.Token)).Single();
        plans.State.Should().Be(PlanState.Published);
        plans.Drafts.Single().ChallengesRequested.Should().Be(1);
    }

    [Fact]
    public async Task A_do_not_publish_verdict_parks_the_task_for_a_person_and_nothing_requeues_it_or_challenges_it_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, _, ScriptedSessions sessions, FakeCommands commands, FeedbackStepEngine feedback, PlanEngine plan) = await AffectedAsync(cts.Token);
        sessions.Refinements.Enqueue((RevisedReport, RevisionFile));

        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.DoNotPublish, must: 2, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.Parked);
        task.FeedbackStepResult.Reason.Should().Contain("do not publish");
        task.State.Should().Be(TaskState.Draft, "a person settles it by hand");
        commands.Calls.Should().NotContain(call => call.StartsWith("publish", StringComparison.Ordinal));

        // The plan ended with the step, so a sweep later changes nothing and asks for no second challenge.
        await plan.PollOnceAsync(cts.Token);
        await feedback.PollOnceAsync(cts.Token);
        (await StreamAsync(taskId, cts.Token)).OfType<DraftChallengeRequested>().Should().ContainSingle();
        (await TaskAsync(taskId, cts.Token)).State.Should().Be(TaskState.Draft);
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.Query<PlanDetails>().Where(p => p.FeedbackStepTaskId == taskId).ToListAsync(cts.Token)).Single().State.Should().Be(PlanState.Cancelled);
    }

    [Fact]
    public async Task A_session_that_concludes_the_task_should_not_run_parks_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, _, ScriptedSessions sessions, _, FeedbackStepEngine feedback, PlanEngine plan) = await AffectedAsync(cts.Token);
        sessions.Refinements.Enqueue(("The notes remove the reason for it.\n\nREFINEMENT: should-not-run\n", null));

        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.PublishAsWritten, must: 0, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.Parked);
        task.FeedbackStepResult.Reason.Should().Contain("should not run");
        (await StreamAsync(taskId, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty("no challenge for a task that should not run");
    }

    [Theory]
    [InlineData("type: spike")]
    [InlineData("model: claude-opus-5-5")]
    [InlineData("blocked-by:\n  - 01a09285-6a21-7259-a883-c5ebe5482c3e")]
    public async Task A_revision_that_names_a_dependency_a_type_or_a_model_parks_the_task_and_is_not_applied(string extra)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, _, ScriptedSessions sessions, FakeCommands commands, FeedbackStepEngine feedback, PlanEngine plan) = await AffectedAsync(cts.Token);
        sessions.Refinements.Enqueue((RevisedReport, $"---\nobjective: Revised by the session\n{extra}\n---\nPointers.\n"));

        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.PublishAsWritten, must: 0, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.Parked);
        task.FeedbackStepResult.Reason.Should().Contain("a field other than the objective, criteria, or context");
        commands.Calls.Should().BeEmpty("the revision was refused before the child ran");
        task.Objective.Should().Be("Page through the results");
    }

    [Fact]
    public async Task A_person_revising_the_task_while_the_refinement_ran_ends_the_step_without_acting_and_the_revision_is_not_applied()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, _, ScriptedSessions sessions, FakeCommands commands, FeedbackStepEngine feedback, PlanEngine plan) = await AffectedAsync(cts.Token);
        sessions.Refinements.Enqueue((RevisedReport, RevisionFile));
        sessions.OnRefinement = _ =>
        {
            using IDocumentSession session = postgres.Store.LightweightSession();
            session.Events.Append(taskId, new TaskRevised(
                taskId, Optional<string>.Of("A person's own objective"), default, default, default, default, default, Now, _node.OwnerId));
            session.SaveChangesAsync().GetAwaiter().GetResult();
        };

        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.PublishAsWritten, must: 0, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.EndedWithoutActing);
        task.FeedbackStepResult.Reason.Should().Contain("revised");
        task.Objective.Should().Be("A person's own objective", "the person's revision stands and the session's was not applied");
        commands.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refinement_session_that_fails_parks_the_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, _, ScriptedSessions sessions, _, FeedbackStepEngine feedback, PlanEngine plan) = await AffectedAsync(cts.Token);
        sessions.Refinements.Enqueue(("I ran out of road and wrote no closing line.", null));

        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.PublishAsWritten, must: 0, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.Parked);
        task.State.Should().Be(TaskState.Draft);
    }

    [Fact]
    public async Task A_person_publishing_the_refined_task_by_hand_ends_the_step_without_the_daemon_queueing_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        (Guid taskId, _, ScriptedSessions sessions, FakeCommands commands, FeedbackStepEngine feedback, PlanEngine plan) = await AffectedAsync(cts.Token);
        sessions.Refinements.Enqueue((RevisedReport, RevisionFile));
        sessions.CheckText = "They affect it.\n\nFEEDBACK CHECK: affects";

        // Take it round until the plan is waiting on its challenge, then publish by hand.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!(await StreamAsync(taskId, cts.Token)).OfType<DraftChallengeRequested>().Any())
        {
            await feedback.PollOnceAsync(cts.Token);
            await plan.PollOnceAsync(cts.Token);
            await plan.WhenIdleAsync();
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the plan should have asked for its challenge");
            await Task.Delay(30, cts.Token);
        }

        await commands.PublishAsync(Guid.Empty, taskId, false, false, cts.Token);
        await DriveAsync(feedback, plan, taskId, ChallengeVerdict.PublishAsWritten, must: 0, cts.Token);

        TaskDetails task = await TaskAsync(taskId, cts.Token);
        task.FeedbackStepResult!.Outcome.Should().Be(FeedbackStepOutcome.EndedWithoutActing);
        task.State.Should().Be(TaskState.Published, "a person who got there first publishes and queues it by hand");
    }

    [Fact]
    public async Task A_feedback_plan_never_asks_for_a_second_challenge_even_when_the_draft_comes_back_to_challenge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Guid projectId = await SeedProjectAsync(cts.Token);
        (TaskDependency blocker, _) = await SeedBlockerAsync(projectId, cts.Token);
        Guid taskId = DomainId.New();
        _tasks.Add(taskId);
        await using (IDocumentSession seed = postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, projectId, "Page through the results", ["It pages"], TaskType.Feature, "ctx", null, null, Now, _node.OwnerId,
                blockedBy: [blocker.Id]));
            await seed.SaveChangesAsync(cts.Token);
        }

        // A plan that already asked for its one challenge and is back at challenge, as a person's resume would leave it.
        Guid planId = DomainId.New();
        await using (IDocumentSession seed = postgres.Store.LightweightSession())
        {
            seed.Events.StartStream<PlanAggregate>(
                planId,
                new PlanStarted(planId, projectId, [taskId], PlanPhase.Challenge, false, false, _node.OwnerId, Now, FeedbackStepTaskId: taskId),
                new PlanChallengeRequested(planId, projectId, taskId, 1, Now),
                new PlanDraftMoved(planId, projectId, taskId, PlanPhase.Challenge, 1, PlanOutcome.Revised, RefinementPurpose.Unknown, false, null, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        FakeProcessManager processes = new();
        ScriptedSessions sessions = new(processes);
        PlanEngine plan = NewPlanEngine(sessions, processes, new FakeCommands(postgres.Store, _node.OwnerId, new TaskDependencyGraph([blocker])));

        await plan.PollOnceAsync(cts.Token);

        (await StreamAsync(taskId, cts.Token)).OfType<DraftChallengeRequested>().Should().BeEmpty("the plan was not allowed a second challenge");
        await using IQuerySession query = postgres.Store.QuerySession();
        PlanDetails details = (await query.LoadAsync<PlanDetails>(planId, cts.Token))!;
        details.Draft(taskId)!.ParkReason.Should().Be(PlanParkReason.OneChallengeOnly);
    }
}
