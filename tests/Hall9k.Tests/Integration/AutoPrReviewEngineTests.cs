using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Daemon.Review;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// AutoPrReviewEngine's own behavioral core (idea e5e98a33, PLAN.md §16 #34's amendment, #128)
/// had no test of any kind before this (independent pre-PR review, cycle 1, conformance lens).
/// The mint path (<c>CreateOneAsync</c>) used to be unreachable through a scripted <c>gh</c>
/// (independent pre-PR review, cycle 1, adversarial lens): <c>WorkItemConnections.ImporterAsync</c>
/// ignored this engine's own injected <see cref="ProcessRunner"/> and always built its GitHub
/// providers against the real one. Now that it is threaded through, the mint path's speed
/// dispatch (the immediate-launch cap in particular) is reachable through
/// <see cref="AutoPrReviewEngine.PollOnceAsync"/> like everything else here. Also covered: the
/// dedup-timestamp comparison the re-mint-loop fix added
/// (<see cref="AutoPrReviewEngine.IsGenuineReRequestAsync"/>, made internal for exactly this), and
/// the withdrawal/recall half of the sweep (<c>ConcludeWithdrawnAsync</c>/<c>ConcludeOneAsync</c>).
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class AutoPrReviewEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    // -------------------------------------------------------------------------------------
    // IsGenuineReRequestAsync: the re-mint-loop fix (independent pre-PR review, cycle 1, both
    // lenses) — a Done pr-review task no longer blocks a fresh mint, so the only thing telling
    // a genuine re-request apart from the same standing request GitHub never cleared is whether
    // the currently-observed request timestamp postdates the one the earlier task was minted
    // from.
    // -------------------------------------------------------------------------------------

    private async Task<(DocumentStore Store, TaskListItem PreviousReview)> SeedDoneReviewAsync(
        DateTimeOffset? requestedAt, CancellationToken cancellationToken)
    {
        DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"auto-pr-review-{taskId:N}", "/tmp/auto-pr-review-repo",
                new Uri("https://github.com/acme/widgets"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Review pull request acme/widgets#42", ["every finding is directed"],
                TaskType.PrReview, null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#42"),
                Now.AddDays(-1), node.OwnerId);
            TaskAggregate task = new();
            task.Apply(added);

            PullRequestReviewAssignmentObserved observed = new(
                taskId, "https://github.com/acme/widgets/pull/42", "brian", "alice", Now.AddDays(-1), requestedAt);
            task.Apply(observed);

            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddDays(-1), node.OwnerId, BacklogPolicy.None);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddDays(-1), node.OwnerId);
            task.Apply(assigned);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now.AddDays(-1));
            task.Apply(claimed);
            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, null, Now.AddHours(-23));
            task.Apply(completed);

            session.Events.StartStream<TaskAggregate>(
                taskId, [added, observed, published, assigned, claimed, completed]);
            await session.SaveChangesAsync(cancellationToken);
        }

        await using IQuerySession query = store.QuerySession();
        TaskListItem previousReview = (await query.LoadAsync<TaskListItem>(taskId, cancellationToken))!;
        return (store, previousReview);
    }

    [Fact]
    public async Task No_currently_observed_timestamp_is_treated_as_the_same_standing_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, TaskListItem previousReview) = await SeedDoneReviewAsync(Now.AddDays(-1), cts.Token);
        using DocumentStore _ = store;

        await using IDocumentSession session = store.LightweightSession();
        bool genuine = await AutoPrReviewEngine.IsGenuineReRequestAsync(
            session, previousReview, currentRequestedAt: null, cts.Token);

        genuine.Should().BeFalse(
            "no evidence this is a fresh request — the conservative side of the infinite re-mint loop this check exists to close");
    }

    [Fact]
    public async Task A_currently_observed_timestamp_older_than_the_previous_reviews_own_is_the_same_standing_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DateTimeOffset previousRequestedAt = Now.AddHours(-2);
        (DocumentStore store, TaskListItem previousReview) = await SeedDoneReviewAsync(previousRequestedAt, cts.Token);
        using DocumentStore _ = store;

        await using IDocumentSession session = store.LightweightSession();
        bool genuine = await AutoPrReviewEngine.IsGenuineReRequestAsync(
            session, previousReview, currentRequestedAt: previousRequestedAt, cts.Token);

        genuine.Should().BeFalse("an identical timestamp is the same request GitHub never cleared, not a re-request");
    }

    [Fact]
    public async Task A_currently_observed_timestamp_newer_than_the_previous_reviews_own_is_a_genuine_re_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DateTimeOffset previousRequestedAt = Now.AddHours(-2);
        (DocumentStore store, TaskListItem previousReview) = await SeedDoneReviewAsync(previousRequestedAt, cts.Token);
        using DocumentStore _ = store;

        await using IDocumentSession session = store.LightweightSession();
        bool genuine = await AutoPrReviewEngine.IsGenuineReRequestAsync(
            session, previousReview, currentRequestedAt: Now, cts.Token);

        genuine.Should().BeTrue("alice requested again after the earlier review closed — a real re-review");
    }

    [Fact]
    public async Task No_baseline_on_the_previous_review_lets_any_currently_observed_timestamp_count_as_fresh()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        // A stream predating PullRequestReviewAssignmentObserved.RequestedAt.
        (DocumentStore store, TaskListItem previousReview) = await SeedDoneReviewAsync(requestedAt: null, cts.Token);
        using DocumentStore _ = store;

        await using IDocumentSession session = store.LightweightSession();
        bool genuine = await AutoPrReviewEngine.IsGenuineReRequestAsync(
            session, previousReview, currentRequestedAt: Now, cts.Token);

        genuine.Should().BeTrue(
            "nothing to compare against on a task this field predates — re-review must not be permanently blocked");
    }

    // -------------------------------------------------------------------------------------
    // Withdrawal/recall (ConcludeWithdrawnAsync / ConcludeOneAsync): never imports anything, so
    // it is fully reachable through PollOnceAsync with a scripted gh.
    // -------------------------------------------------------------------------------------

    /// <summary>Mirrors RunLauncherTests' own NewSupervisor/NewCloseoutEngine/NewContextAssembler exactly — nothing here is ever exercised by a withdrawal/recall test, since ConcludeWithdrawnAsync never calls launcher.LaunchAsync; these exist only to satisfy AutoPrReviewEngine's constructor.</summary>
    private RunLauncher NewLauncher(DocumentStore store, NodeContext node)
    {
        FakeProcessManager processes = new();
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance));
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance);
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        PrimarySessionResumer primarySessionResumer = new(
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())));
        RunSupervisor supervisor = new(
            store, node, processes, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance), primarySessionResumer,
            Options.Create(new DaemonOptions()), NullLogger<RunSupervisor>.Instance);
        BlockerContextAssembler blockerContext = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())),
            processes, Options.Create(new DaemonOptions()), NullLogger<BlockerContextAssembler>.Instance);
        RefusingInspector inspector = new();
        CloseoutEngine closeout = new(
            store, node, new DaemonConnection(postgres.ConnectionString), inspector, new RefusingWorktreeManager(),
            RecordingProcessRunner.Succeeding(string.Empty).Runner, FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);
        return new RunLauncher(
            store, new RefusingWorktreeManager(), new RefusingExecutor(), supervisor, blockerContext, inspector,
            closeout, RecordingProcessRunner.NeverInvoked(), Options.Create(new DaemonOptions()),
            NullLogger<RunLauncher>.Instance);
    }

    private sealed class RefusingExecutor : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The withdrawal/recall tests never dispatch a run — nothing here should ever spawn an agent.");
    }

    private sealed class RefusingInspector : IPullRequestInspector
    {
        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached by these tests.");

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Not reached by these tests.");
    }

    private sealed class RefusingWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "not a real repository"));

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
    }

    private sealed class NoOpLock : IAsyncDisposable
    {
        public static readonly NoOpLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------------------------
    // Full sweep coverage for the withdrawal/recall path, through the public PollOnceAsync
    // entry point: an empty gh pr list means "nothing currently requested", the recall trigger
    // for every previously auto-created task this sweep watches.
    // -------------------------------------------------------------------------------------

    private static ProcessRunner ScriptedGh(string login, string timelineJson) => (fileName, arguments, _, _) =>
    {
        if (arguments.Contains("user"))
        {
            return Task.FromResult(new ProcessResult(0, login + "\n", string.Empty));
        }

        if (arguments.Contains("list"))
        {
            return Task.FromResult(new ProcessResult(0, "[]", string.Empty));
        }

        // "graphql" — the timeline read FindMostRecentRequestActorAsync makes.
        return Task.FromResult(new ProcessResult(0, timelineJson, string.Empty));
    };

    private async Task<(DocumentStore Store, NodeContext Node, Guid ProjectId, Guid TaskId)> SeedWatchedTaskAsync(
        TaskState leaveAt, CancellationToken cancellationToken)
    {
        DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"auto-pr-review-sweep-{taskId:N}", "/tmp/auto-pr-review-sweep-repo",
                new Uri("https://github.com/acme/widgets"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
            ProjectAggregate project = new();
            project.Apply(registered);
            ProjectSettingsChanged optedIn = ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None, Optional<int>.None,
                Optional<IReadOnlyList<ContextLink>>.None, Now, node.OwnerId,
                autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Normal));
            session.Events.Append(projectId, optedIn);

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Review pull request acme/widgets#42", ["every finding is directed"],
                TaskType.PrReview, null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/widgets#42"),
                Now.AddHours(-1), node.OwnerId);
            TaskAggregate task = new();
            task.Apply(added);
            PullRequestReviewAssignmentObserved observed = new(
                taskId, "https://github.com/acme/widgets/pull/42", "brian", "alice", Now.AddHours(-1), Now.AddHours(-1));
            task.Apply(observed);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-1), node.OwnerId, BacklogPolicy.None);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-1), node.OwnerId);
            task.Apply(assigned);
            List<object> events = [added, observed, published, assigned];

            if (leaveAt == TaskState.Claimed)
            {
                TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now.AddHours(-1));
                task.Apply(claimed);
                events.Add(claimed);
            }

            session.Events.StartStream<TaskAggregate>(taskId, [.. events]);
            await session.SaveChangesAsync(cancellationToken);
        }

        return (store, node, projectId, taskId);
    }

    /// <summary>
    /// A run still Queued (never dispatched) whose reviewer assignment is withdrawn — the go
    /// signal recalled by the same authority that gave it (PLAN.md §16 #34's amendment): the
    /// task concludes honestly rather than dispatching on a request nobody stands behind any more.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_assignment_concludes_the_task_before_it_ever_dispatches()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, Guid _, Guid taskId) = await SeedWatchedTaskAsync(TaskState.Queued, cts.Token);
        using DocumentStore _ = store;

        const string removalJson = """
            {"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[
              {"__typename":"ReviewRequestedEvent","createdAt":"2026-09-04T10:00:00Z",
               "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}},
              {"__typename":"ReviewRequestRemovedEvent","createdAt":"2026-09-04T11:00:00Z",
               "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}}
            ]}}}}}
            """;
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node), ScriptedGh("brian", removalJson), NullLogger<AutoPrReviewEngine>.Instance);

        AutoPrReviewSweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.AssignmentsRecalled.Should().Be(1);
        await using IQuerySession query = store.QuerySession();
        TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token);
        task!.State.Should().Be(TaskState.Abandoned, "the go signal was recalled before the run ever dispatched");
        task.AutoPrReviewAssigneeLogin.Should().BeNull();
    }

    /// <summary>
    /// A deeper defect than the misattribution it started as (independent pre-PR review, cycle 1,
    /// adversarial lens): absence from the review-requested search alone — a merge, a submitted
    /// review that cleared the request, or a transient gh failure — is not proof of an actual
    /// recall. A timeline carrying only the original request event, no removal at all, must
    /// record nothing rather than concluding a withdrawal nobody actually made.
    /// </summary>
    [Fact]
    public async Task A_missing_removal_event_on_the_timeline_concludes_nothing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, Guid projectId, Guid taskId) = await SeedWatchedTaskAsync(TaskState.Queued, cts.Token);
        using DocumentStore _ = store;

        const string requestOnlyJson = """
            {"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[
              {"__typename":"ReviewRequestedEvent","createdAt":"2026-09-04T10:00:00Z",
               "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}}
            ]}}}}}
            """;
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node), ScriptedGh("brian", requestOnlyJson), NullLogger<AutoPrReviewEngine>.Instance);

        try
        {
            AutoPrReviewSweepResult sweep = await engine.PollOnceAsync(cts.Token);

            sweep.AssignmentsRecalled.Should().Be(0, "no removal event was observed — absence from the search alone is not proof of a recall");
            await using IQuerySession query = store.QuerySession();
            IReadOnlyList<JasperFx.Events.IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
            stream.Select(recorded => recorded.Data).OfType<PullRequestReviewAssignmentRecalled>().Should().BeEmpty(
                "alice requested; nobody has recalled anything — recording an unattributed recall on absence alone was itself the defect");
            TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token);
            task!.State.Should().Be(TaskState.Queued, "nothing concludes this task without positive evidence of a withdrawal");
        }
        finally
        {
            // Unlike every sibling withdrawal test, this one's own task is never concluded — it
            // deliberately stays Queued with its AutoPrReviewAssigneeLogin still set (the fix
            // under test: absence alone must not conclude anything), so it would otherwise remain
            // watched forever and get swept — with whatever a later sibling test's own gh script
            // reports — by any test that runs after it in this shared-database class (see the
            // mint-path tests' own note on why).
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
    }

    /// <summary>
    /// A run already Claimed when the assignment withdraws is recorded as an observation only:
    /// the work, and any findings already produced, are never discarded for a reviewer reshuffle.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_assignment_after_the_run_is_claimed_is_recorded_without_ending_the_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, Guid _, Guid taskId) = await SeedWatchedTaskAsync(TaskState.Claimed, cts.Token);
        using DocumentStore _ = store;

        const string removalJson = """
            {"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[
              {"__typename":"ReviewRequestRemovedEvent","createdAt":"2026-09-04T11:00:00Z",
               "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}}
            ]}}}}}
            """;
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node), ScriptedGh("brian", removalJson), NullLogger<AutoPrReviewEngine>.Instance);

        AutoPrReviewSweepResult sweep = await engine.PollOnceAsync(cts.Token);

        sweep.AssignmentsRecalled.Should().Be(1);
        await using IQuerySession query = store.QuerySession();
        TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token);
        task!.State.Should().Be(TaskState.Claimed, "findings already in flight are never discarded for a reviewer reshuffle");
        task.AutoPrReviewAssigneeLogin.Should().BeNull("the recall is still recorded, as an observation");
    }

    // -------------------------------------------------------------------------------------
    // The mint path (CreateOneAsync), now reachable through a scripted gh (WorkItemConnections
    // .ImporterAsync's own processRunner threading fix, independent pre-PR review, cycle 1,
    // adversarial lens).
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A sweep offering two candidates at Now speed: the first takes the sweep's one immediate
    /// ceiling-exempt launch (MaxImmediateLaunchesPerSweep), the second is not silently dropped —
    /// it still takes the queue-first marker First speed uses, so it takes the next free ordinary
    /// dispatch slot rather than waiting a full poll interval for nothing to happen. A regression
    /// in this exact dispatch (the review's own named risk: the cap's off-by-one inverting, or
    /// the queue-first fallback silently dropped) would have compiled and passed dotnet test
    /// green before this test existed.
    /// </summary>
    [Fact]
    public async Task Now_speed_immediate_launch_cap_defers_the_second_candidate_to_queue_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        // A repository and pull request numbers found nowhere else in this file: every other
        // test's seed hardcodes acme/widgets#42, and CreateOneAsync's own dedup queries key on
        // the canonical external reference alone, unscoped by project — a collision there would
        // read a same-class sibling's leftover task as this sweep's own previous review.
        const string repository = "acme/mint-cap-test";

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), "auto-pr-review-now-cap", "/tmp/auto-pr-review-now-cap-repo",
                new Uri($"https://github.com/{repository}"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
            ProjectAggregate project = new();
            project.Apply(registered);
            ProjectSettingsChanged optedIn = ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None, Optional<int>.None,
                Optional<IReadOnlyList<ContextLink>>.None, Now, node.OwnerId,
                autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Now));
            session.Events.Append(projectId, optedIn);
            await session.SaveChangesAsync(cts.Token);
        }

        const string listJson = """
            [
              {"number":9101,"url":"https://github.com/acme/mint-cap-test/pull/9101","title":"First","body":"no links here"},
              {"number":9102,"url":"https://github.com/acme/mint-cap-test/pull/9102","title":"Second","body":"no links here"}
            ]
            """;
        const string emptyTimelineJson = """{"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[]}}}}}""";

        ProcessRunner gh = (fileName, arguments, _, _) =>
        {
            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(0, listJson, string.Empty));
            }

            if (arguments.Contains("view"))
            {
                // The requested repository is echoed back into the response's own url (exactly
                // as a real gh pr view --repo <repo> would answer for that repo) rather than
                // hardcoded to this test's own repository: this class shares one Postgres
                // database across every test method (see the finally block below), so an
                // already-opted-in leftover project from a sibling test sweeps in this same
                // PollOnceAsync call too, and a hardcoded url would hand it this test's own
                // canonical reference — minting under the wrong project and starving this
                // project's own candidate via the dedup check.
                int number = arguments
                    .Select(argument => int.TryParse(argument, out int parsed) ? parsed : (int?)null)
                    .First(parsed => parsed.HasValue)!.Value;
                int repoIndex = arguments.ToList().IndexOf("--repo");
                string requestRepository = repoIndex >= 0 && repoIndex + 1 < arguments.Count
                    ? arguments[repoIndex + 1]
                    : repository;
                string json = $$"""
                    {"number":{{number}},"title":"Pull request #{{number}}","body":"no links here",
                     "state":"OPEN","url":"https://github.com/{{requestRepository}}/pull/{{number}}","baseRefName":"main"}
                    """;
                return Task.FromResult(new ProcessResult(0, json, string.Empty));
            }

            // graphql — the actor-provenance timeline read; empty means unattributed, which
            // never blocks minting a fresh candidate (no previous review exists to compare against).
            return Task.FromResult(new ProcessResult(0, emptyTimelineJson, string.Empty));
        };

        AutoPrReviewEngine engine = new(store, node, NewLauncher(store, node), gh, NullLogger<AutoPrReviewEngine>.Instance);

        try
        {
            // Not asserted on sweep.TasksCreated: this class shares one Postgres database across
            // every test method, and an already-opted-in leftover project from a sibling test
            // (never turned off, since that is not this test's job to police) sweeps in this same
            // call too and can mint its own unrelated task — a global total would make this test
            // depend on which sibling tests happened to run first. Every assertion below is scoped
            // to this test's own projectId instead, which only this project's own candidates can
            // ever satisfy.
            await engine.PollOnceAsync(cts.Token);

            await using IQuerySession query = store.QuerySession();
            IReadOnlyList<TaskListItem> minted = await query.Query<TaskListItem>()
                .Where(task => task.ProjectId == projectId)
                .ToListAsync(cts.Token);
            minted.Should().HaveCount(2);
            TaskListItem first = minted.Single(task => task.ExternalReference!.EndsWith("#9101"));
            TaskListItem second = minted.Single(task => task.ExternalReference!.EndsWith("#9102"));

            IReadOnlyList<JasperFx.Events.IEvent> firstStream = await query.Events.FetchStreamAsync(first.Id, token: cts.Token);
            firstStream.Select(recorded => recorded.Data).OfType<TaskClaimed>().Should().ContainSingle(
                "the sweep's one immediate ceiling-exempt launch went to the first candidate");

            IReadOnlyList<JasperFx.Events.IEvent> secondStream = await query.Events.FetchStreamAsync(second.Id, token: cts.Token);
            secondStream.Select(recorded => recorded.Data).OfType<TaskClaimed>().Should().BeEmpty(
                "the second candidate is beyond this sweep's own immediate-launch cap");
            secondStream.Select(recorded => recorded.Data).OfType<TaskRevised>().Should().ContainSingle(
                revised => revised.QueuePriority.HasValue && revised.QueuePriority.Value,
                "a Now candidate beyond the cap still takes the queue-first marker rather than waiting a full poll interval");
        }
        finally
        {
            // This class shares one Postgres database across every test method (PostgresFixture's
            // own doc: "one Postgres container per test class"), and PollOnceAsync's own outer
            // loop sweeps every opted-in project regardless of which test created it — left
            // opted-in, this project's still-Published/Claimed tasks would be swept, and
            // mis-recalled, by whichever sibling test happens to run next.
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
    }

    /// <summary>Restores a test-created project to AutoPrReview.Off so PollOnceAsync's later, unrelated sweeps in this same shared-database test class never revisit it.</summary>
    private static async Task TurnOffAutoPrReviewAsync(DocumentStore store, Guid projectId, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        ProjectAggregate? project = await session.Events.AggregateStreamAsync<ProjectAggregate>(projectId, token: cancellationToken);
        if (project is null)
        {
            return;
        }

        ProjectSettingsChanged turnedOff = ProjectDecider.ChangeSettings(
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None, Optional<int>.None,
            Optional<IReadOnlyList<ContextLink>>.None, DateTimeOffset.UtcNow, ownerId,
            autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Off));
        session.Events.Append(projectId, turnedOff);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The task type/pull-request contract's own imported-context clause (AGENTS.md): an
    /// auto-created review's agent context carries a linked issue's own content exactly as
    /// h9k task add --from-pr's context does, now that CreateOneAsync composes it through the
    /// same shared LinkedWorkItemImport.TryImportContextAsync (independent pre-PR review, cycle
    /// 1, conformance lens — the two adoption paths had silently drifted apart).
    /// </summary>
    [Fact]
    public async Task A_linked_issue_referenced_by_the_pull_request_is_imported_into_the_agent_context()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();

        // A repository, pull request and issue number found nowhere else in this file — see the
        // Now-speed cap test's own note on why: CreateOneAsync's dedup queries key on the
        // canonical external reference alone, unscoped by project.
        const string repository = "acme/mint-linked-issue-test";

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), "auto-pr-review-linked-issue", "/tmp/auto-pr-review-linked-issue-repo",
                new Uri($"https://github.com/{repository}"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
            ProjectAggregate project = new();
            project.Apply(registered);
            ProjectSettingsChanged optedIn = ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None, Optional<int>.None,
                Optional<IReadOnlyList<ContextLink>>.None, Now, node.OwnerId,
                autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Normal));
            session.Events.Append(projectId, optedIn);
            await session.SaveChangesAsync(cts.Token);
        }

        const string listJson = """
            [{"number":9201,"url":"https://github.com/acme/mint-linked-issue-test/pull/9201","title":"Add rate limiting","body":"Closes #9202."}]
            """;
        const string emptyTimelineJson = """{"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[]}}}}}""";

        ProcessRunner gh = (fileName, arguments, _, _) =>
        {
            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(0, listJson, string.Empty));
            }

            // The requested repository is echoed back into each response's own url rather than
            // hardcoded (see the Now-speed cap test's own note): this class shares one Postgres
            // database across every test method, so an already-opted-in leftover project from a
            // sibling test sweeps in this same PollOnceAsync call too, and a hardcoded url would
            // hand it this test's own canonical reference — minting under the wrong project and
            // starving this project's own candidate via the dedup check.
            int repoIndex = arguments.ToList().IndexOf("--repo");
            string requestRepository = repoIndex >= 0 && repoIndex + 1 < arguments.Count
                ? arguments[repoIndex + 1]
                : repository;

            if (arguments.Contains("issue"))
            {
                string issueJson = $$"""
                    {"number":9202,"title":"Auth endpoints have no rate limiting","body":"An attacker can hammer login.",
                     "state":"OPEN","url":"https://github.com/{{requestRepository}}/issues/9202"}
                    """;
                return Task.FromResult(new ProcessResult(0, issueJson, string.Empty));
            }

            if (arguments.Contains("view"))
            {
                string prJson = $$"""
                    {"number":9201,"title":"Add rate limiting","body":"Closes #9202.","state":"OPEN",
                     "url":"https://github.com/{{requestRepository}}/pull/9201","baseRefName":"main"}
                    """;
                return Task.FromResult(new ProcessResult(0, prJson, string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, emptyTimelineJson, string.Empty));
        };

        AutoPrReviewEngine engine = new(store, node, NewLauncher(store, node), gh, NullLogger<AutoPrReviewEngine>.Instance);

        try
        {
            // Not asserted on sweep.TasksCreated: see the Now-speed cap test's own note — a
            // sibling test's still-opted-in leftover project sweeps in this same call too.
            await engine.PollOnceAsync(cts.Token);

            await using IQuerySession query = store.QuerySession();
            TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
            IReadOnlyList<JasperFx.Events.IEvent> stream = await query.Events.FetchStreamAsync(minted.Id, token: cts.Token);
            TaskAdded added = stream.Select(recorded => recorded.Data).OfType<TaskAdded>().Single();

            added.AgentContext.Should().NotBeNull().And.Contain(
                "Auth endpoints have no rate limiting",
                "the linked issue #9202 is imported alongside the pull request, exactly as h9k task add --from-pr does");
        }
        finally
        {
            // See the Now-speed cap test's own note: this class shares one Postgres database
            // across every test method, so a project left opted-in here would be revisited by
            // whichever sibling test's own sweep runs next.
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
    }

    /// <summary>
    /// The gh pr view subprocess the import always pays is skipped for the overwhelmingly common
    /// case — a live task already covers this pull request — via a cheap case-insensitive match
    /// against the reference guessed from the project's own repository casing, never gh's own
    /// canonical casing (independent pre-PR review, cycle 1, conformance lens, low). The
    /// project's own recorded repository casing deliberately differs from the candidate's, so a
    /// plain case-sensitive guess would miss it and pay the subprocess anyway.
    /// </summary>
    [Fact]
    public async Task An_already_covered_candidate_is_recognized_without_ever_calling_gh_pr_view()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid existingTaskId = DomainId.New();
        const string repository = "Acme/Mint-FastPath-Test";

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), "auto-pr-review-fastpath", "/tmp/auto-pr-review-fastpath-repo",
                new Uri($"https://github.com/{repository}"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
            ProjectAggregate project = new();
            project.Apply(registered);
            ProjectSettingsChanged optedIn = ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None, Optional<int>.None,
                Optional<IReadOnlyList<ContextLink>>.None, Now, node.OwnerId,
                autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Normal));
            session.Events.Append(projectId, optedIn);

            TaskAdded added = TaskDecider.Add(
                existingTaskId, projectId, "Review pull request acme/mint-fastpath-test#7001",
                ["every finding is directed"], TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/mint-fastpath-test#7001"),
                Now, node.OwnerId);
            session.Events.StartStream<TaskAggregate>(existingTaskId, added);
            await session.SaveChangesAsync(cts.Token);
        }

        const string listJson = """
            [{"number":7001,"url":"https://github.com/acme/mint-fastpath-test/pull/7001","title":"Already covered","body":"no links here"}]
            """;
        List<IReadOnlyList<string>> unexpectedCalls = [];
        ProcessRunner gh = (fileName, arguments, _, _) =>
        {
            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(0, listJson, string.Empty));
            }

            // view/issue/graphql for THIS test's own repository should never be reached — the
            // fast path recognizes this candidate as already covered before any of them would
            // run. A call for some other repository is a sibling test's own still-opted-in
            // leftover project sweeping in this same call too (see the Now-speed cap test's own
            // note) and is legitimately reached — not this assertion's concern.
            int repoIndex = arguments.ToList().IndexOf("--repo");
            string? requestRepository = repoIndex >= 0 && repoIndex + 1 < arguments.Count ? arguments[repoIndex + 1] : null;
            if (string.Equals(requestRepository, repository, StringComparison.OrdinalIgnoreCase))
            {
                unexpectedCalls.Add(arguments);
            }

            return Task.FromResult(new ProcessResult(0, "{}", string.Empty));
        };

        AutoPrReviewEngine engine = new(store, node, NewLauncher(store, node), gh, NullLogger<AutoPrReviewEngine>.Instance);

        try
        {
            await engine.PollOnceAsync(cts.Token);

            unexpectedCalls.Should().BeEmpty(
                "a live task already covers this pull request — the fast path must recognize that without shelling out to gh pr view");

            await using IQuerySession query = store.QuerySession();
            IReadOnlyList<TaskListItem> matching = await query.Query<TaskListItem>()
                .Where(task => task.ProjectId == projectId)
                .ToListAsync(cts.Token);
            matching.Should().ContainSingle("the fast path must skip minting, not merely skip the subprocess");
        }
        finally
        {
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
    }

    /// <summary>
    /// The defect independent pre-PR review cycle 2's adversarial lens found: a task recalled
    /// mid-run with "the work continues" (<c>Concluded</c> false — the run was already Claimed)
    /// has its <see cref="TaskListItem.AutoPrReviewAssigneeLogin"/> nulled by that same recall,
    /// even though the task later finishes normally to Done. Reusing that transient field as
    /// <c>CreateOneAsync</c>'s own previousReview provenance check would make a later genuine
    /// re-request mint a fresh task with no re-review note and no reference back to this one,
    /// exactly as though auto-pr-review had never touched this pull request before —
    /// <see cref="TaskListItem.WasAutoPrReviewCreated"/> is the permanent field that must survive
    /// the recall instead.
    /// </summary>
    [Fact]
    public async Task A_genuine_re_request_after_a_mid_run_recall_still_carries_a_re_review_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid firstReviewTaskId = DomainId.New();
        const string repository = "acme/mint-rereview-test";
        DateTimeOffset firstRequestedAt = Now.AddDays(-2);
        DateTimeOffset secondRequestedAt = Now;

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), "auto-pr-review-rereview", "/tmp/auto-pr-review-rereview-repo",
                new Uri($"https://github.com/{repository}"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
            ProjectAggregate project = new();
            project.Apply(registered);
            ProjectSettingsChanged optedIn = ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None, Optional<int>.None,
                Optional<IReadOnlyList<ContextLink>>.None, Now, node.OwnerId,
                autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Normal));
            session.Events.Append(projectId, optedIn);

            // T1: auto-created, dispatched (Claimed), then its GitHub reviewer assignment is
            // recalled while the run is already in flight — Concluded: false, "the work
            // continues" — and the run finishes normally to Done regardless.
            TaskAdded added = TaskDecider.Add(
                firstReviewTaskId, projectId, $"Review pull request {repository}#9301", ["every finding is directed"],
                TaskType.PrReview, null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#9301"),
                Now.AddDays(-2), node.OwnerId);
            TaskAggregate task = new();
            task.Apply(added);
            PullRequestReviewAssignmentObserved observed = new(
                firstReviewTaskId, $"https://github.com/{repository}/pull/9301", "brian", "alice", Now.AddDays(-2), firstRequestedAt);
            task.Apply(observed);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddDays(-2), node.OwnerId, BacklogPolicy.None);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddDays(-2), node.OwnerId);
            task.Apply(assigned);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now.AddDays(-2));
            task.Apply(claimed);
            PullRequestReviewAssignmentRecalled recalled = new(
                firstReviewTaskId, $"https://github.com/{repository}/pull/9301", "alice", Now.AddDays(-1), Concluded: false);
            task.Apply(recalled);
            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, null, Now.AddHours(-23));
            task.Apply(completed);

            session.Events.StartStream<TaskAggregate>(
                firstReviewTaskId, [added, observed, published, assigned, claimed, recalled, completed]);
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IQuerySession verifySeed = store.QuerySession())
        {
            TaskListItem seeded = (await verifySeed.LoadAsync<TaskListItem>(firstReviewTaskId, cts.Token))!;
            seeded.AutoPrReviewAssigneeLogin.Should().BeNull(
                "the mid-run recall nulls the transient field even though the task went on to finish normally");
            seeded.WasAutoPrReviewCreated.Should().BeTrue(
                "the permanent provenance field must survive the recall — this is exactly the task under test");
        }

        const string listJson = """
            [{"number":9301,"url":"https://github.com/acme/mint-rereview-test/pull/9301","title":"Add rate limiting","body":"no links here"}]
            """;
        string timelineJson =
            "{\"data\":{\"repository\":{\"pullRequest\":{\"timelineItems\":{\"nodes\":["
            + "{\"__typename\":\"ReviewRequestedEvent\",\"createdAt\":\"" + secondRequestedAt.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\","
            + "\"actor\":{\"login\":\"alice\"},\"requestedReviewer\":{\"__typename\":\"User\",\"login\":\"brian\"}}"
            + "]}}}}}";

        ProcessRunner gh = (fileName, arguments, _, _) =>
        {
            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(0, listJson, string.Empty));
            }

            if (arguments.Contains("view"))
            {
                int repoIndex = arguments.ToList().IndexOf("--repo");
                string requestRepository = repoIndex >= 0 && repoIndex + 1 < arguments.Count
                    ? arguments[repoIndex + 1]
                    : repository;
                string json = $$"""
                    {"number":9301,"title":"Add rate limiting","body":"no links here","state":"OPEN",
                     "url":"https://github.com/{{requestRepository}}/pull/9301","baseRefName":"main"}
                    """;
                return Task.FromResult(new ProcessResult(0, json, string.Empty));
            }

            // graphql — the actor-provenance timeline read, a genuinely fresh request postdating
            // the one T1 was minted from.
            return Task.FromResult(new ProcessResult(0, timelineJson, string.Empty));
        };

        AutoPrReviewEngine engine = new(store, node, NewLauncher(store, node), gh, NullLogger<AutoPrReviewEngine>.Instance);

        try
        {
            await engine.PollOnceAsync(cts.Token);

            await using IQuerySession query = store.QuerySession();
            IReadOnlyList<TaskListItem> matching = await query.Query<TaskListItem>()
                .Where(task => task.ProjectId == projectId)
                .ToListAsync(cts.Token);
            TaskListItem secondReview = matching.Single(task => task.Id != firstReviewTaskId);

            IReadOnlyList<JasperFx.Events.IEvent> stream = await query.Events.FetchStreamAsync(secondReview.Id, token: cts.Token);
            TaskAdded secondAdded = stream.Select(recorded => recorded.Data).OfType<TaskAdded>().Single();

            secondAdded.AgentContext.Should().NotBeNull().And.Contain(
                "This is a re-review",
                "a genuine re-request must still be recognized as one even though the earlier task's "
                + "AutoPrReviewAssigneeLogin was already nulled by its own mid-run recall");
            secondAdded.AgentContext.Should().Contain(
                DomainId.Short(firstReviewTaskId),
                "the re-review note must reference the earlier task by id");
        }
        finally
        {
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
    }
}
