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
/// Scoped to what is actually reachable without shelling to a real <c>gh</c>: the mint path
/// (<c>CreateOneAsync</c>) always constructs its own <c>GitHubWorkItemProvider</c>/
/// <c>GitHubPullRequestProvider</c> through <c>WorkItemConnections.ImporterAsync</c>, which takes
/// no injectable <see cref="ProcessRunner"/> — a pre-existing gap this branch does not widen or
/// fix, since doing so would ripple into every other caller of that shared connectors entry
/// point. What is fully reachable: the dedup-timestamp comparison the re-mint-loop fix added
/// (<see cref="AutoPrReviewEngine.IsGenuineReRequestAsync"/>, made internal for exactly this), and
/// the withdrawal/recall half of the sweep (<c>ConcludeWithdrawnAsync</c>/<c>ConcludeOneAsync</c>),
/// which never imports anything and only ever calls back through the injected
/// <see cref="ProcessRunner"/> this engine already takes.
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
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance);
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        RunSupervisor supervisor = new(
            store, node, processes, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance), NullLogger<RunSupervisor>.Instance);
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
    /// The misattribution the independent pre-PR review found (adversarial lens, cycle 1): a
    /// timeline carrying only the original request event, no removal at all, must record
    /// RecalledByLogin as honestly null rather than naming the requester as the recaller.
    /// </summary>
    [Fact]
    public async Task A_withdrawal_with_no_removal_event_on_the_timeline_never_names_the_original_requester()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, Guid _, Guid taskId) = await SeedWatchedTaskAsync(TaskState.Queued, cts.Token);
        using DocumentStore _ = store;

        const string requestOnlyJson = """
            {"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[
              {"__typename":"ReviewRequestedEvent","createdAt":"2026-09-04T10:00:00Z",
               "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}}
            ]}}}}}
            """;
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node), ScriptedGh("brian", requestOnlyJson), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<JasperFx.Events.IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
        PullRequestReviewAssignmentRecalled recalled = stream.Select(recorded => recorded.Data)
            .OfType<PullRequestReviewAssignmentRecalled>().Single();
        recalled.RecalledByLogin.Should().BeNull("alice requested; nobody has recalled anything — a guess is worse than an honest gap");
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
}
