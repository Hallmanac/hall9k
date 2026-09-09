using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Connectors.Worktrees;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The pr-review task's whole engine path, from the sweep that mints one to the retry that
/// resumes it, sharing one container across the three seams below. Each was its own class and so
/// its own container for three to eleven tests; every assertion here is scoped to the project or
/// run its own test seeded, so a sibling seam's rows are as invisible as a sibling test's already
/// were.
/// <para>
/// <see cref="AutoPrReviewEngine"/>'s own behavioral core (idea e5e98a33, PLAN.md §16 #34's
/// amendment, #128) had no test of any kind before this (independent pre-PR review, cycle 1,
/// conformance lens). The mint path (<c>CreateOneAsync</c>) used to be unreachable through a
/// scripted <c>gh</c> (independent pre-PR review, cycle 1, adversarial lens):
/// <c>WorkItemConnections.ImporterAsync</c> ignored this engine's own injected
/// <see cref="ProcessRunner"/> and always built its GitHub providers against the real one. Now
/// that it is threaded through, the mint path's speed dispatch (the immediate-launch cap in
/// particular) is reachable through <see cref="AutoPrReviewEngine.PollOnceAsync"/> like everything
/// else here. Also covered: the dedup-timestamp comparison the re-mint-loop fix added
/// (<see cref="AutoPrReviewEngine.IsGenuineReRequestAsync"/>, made internal for exactly this), and
/// the withdrawal/recall half of the sweep (<c>ConcludeWithdrawnAsync</c>/<c>ConcludeOneAsync</c>).
/// </para>
/// <para>
/// <c>PrReviewEngine</c>'s own re-entrancy and reclaim-safety guards (cycle-1 conformance finding,
/// <c>PrReviewEngine.cs:50</c>: the 525-line component that owns the whole pr-review completion
/// path had no coverage at all before this). Three shapes: a run reclaimed by a fresh generation
/// must retire as superseded rather than act under a stale name, whichever step of
/// <c>DriveAsync</c> discovers it; and a run finalizing after its task left Claimed some other way
/// completes the run without forcing a task transition that is no longer true.
/// </para>
/// <para>
/// A pr-review task's primary session is the adversarial lens reading another contributor's
/// pull-request head (adversarial review cycle-3 ride-along, on
/// <c>TokenBudgetRetryEngine.RetryParkedRunsAsync</c>'s own resume path): the resume spawn a
/// budget-exhaustion retry issues for that same session must carry
/// <see cref="AgentSpawnRequest.UntrustedWorkingDirectory"/> forward, exactly as the original
/// dispatch did, so the retried process never loads the foreign checkout's own <c>.claude/</c>
/// config or <c>.mcp.json</c> under the owner's credentials.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class PrReviewTaskEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);


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
        DocumentStore store = postgres.Store;
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
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            new StackedParentWatch(
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
                NullLogger<StackedParentWatch>.Instance));
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
        RefusingWorktreeManager closeoutWorktrees = new();
        CloseoutEngine closeout = new(
            store, node, new DaemonConnection(postgres.ConnectionString), inspector, closeoutWorktrees,
            new StackedParentWatch(closeoutWorktrees, NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner, FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);
        return new RunLauncher(
            store, new RefusingWorktreeManager(), new RefusingExecutor("The withdrawal/recall tests never dispatch a run — nothing here should ever spawn an agent."), supervisor, blockerContext, inspector,
            closeout, RecordingProcessRunner.NeverInvoked(), Options.Create(new DaemonOptions()),
            NullLogger<RunLauncher>.Instance);
    }

    /// <summary>
    /// An executor no test that takes it expects to be called, saying in its own words why. One
    /// type rather than one per seam: the message is the only thing that ever differed.
    /// </summary>
    private sealed class RefusingExecutor(string why) : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(why);
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

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
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

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
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

    /// <summary>
    /// A timeline carrying one <c>ReviewRequestedEvent</c> for <c>brian</c> at <see cref="Now"/> —
    /// the fact the no-backfill guard compares against a project's own cutoff (Decisions Log
    /// #159). A test that expects a mint has to script this: a timeline with no requested-at in it
    /// at all no longer mints anything, because nothing then proves the request postdates this
    /// install's own adoption of the on-by-default behaviour.
    /// </summary>
    private const string RequestedAtNowTimelineJson =
        """
        {"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[
          {"__typename":"ReviewRequestedEvent","createdAt":"2026-09-04T12:00:00Z",
           "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}}
        ]}}}}}
        """;

    /// <summary>
    /// This install's own on-by-default cutoff, recorded a day before <see cref="Now"/> so that a
    /// test project registered at <see cref="Now"/> is bounded by its own registration rather than
    /// by the real wall-clock moment <c>EnsureDefaultAdoptionAsync</c> would otherwise write
    /// (Decisions Log #159). Without it every seeded request in this file is older than the
    /// cutoff and mints nothing — which is the guard working, not a defect.
    /// </summary>
    private static async Task SeedDefaultAdoptionAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new AutoPrReviewDefaultAdoption { Id = node.NodeId, AdoptedAt = Now.AddDays(-1) });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether this gh invocation is <c>gh repo view --json url</c> — the read a project with no
    /// recorded repository URL falls back to in order to discover its own repository. Every
    /// scripted runner below has to refuse it now that a sweep reads every registered project
    /// rather than only the opted-in ones (Decisions Log #159): answered with any of the JSON a
    /// test scripts for its own pull requests, a sibling test's URL-less project would resolve to
    /// THIS test's repository and mint this test's own candidate under itself, starving this test
    /// via the canonical dedup check. Refusing is also the truthful answer — nothing here knows
    /// what repository that project is.
    /// </summary>
    private static bool IsRepositoryHostRead(IReadOnlyList<string> arguments) =>
        arguments.Count > 1 && arguments[0] == "repo" && arguments[1] == "view";

    /// <summary>
    /// Whether this gh invocation is asking about <paramref name="repository"/>, read off its own
    /// <c>--repo</c> argument. Every scripted <c>gh pr list</c> below needs the guard now that a
    /// sweep reads every registered project rather than only the opted-in ones (Decisions Log
    /// #159): this class shares one Postgres database across its test methods, so a sibling
    /// test's leftover project is swept inside this same PollOnceAsync call, and a list that
    /// answered for it too would hand it this test's own candidate — minting under the wrong
    /// project and starving this test's own via the canonical dedup check.
    /// </summary>
    private static bool AsksAbout(IReadOnlyList<string> arguments, string repository)
    {
        int repoIndex = arguments.ToList().IndexOf("--repo");
        return repoIndex >= 0
            && repoIndex + 1 < arguments.Count
            && string.Equals(arguments[repoIndex + 1], repository, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessRunner ScriptedGh(string login, string timelineJson) => (fileName, arguments, _, _) =>
    {
        if (IsRepositoryHostRead(arguments))
        {
            return Task.FromResult(new ProcessResult(1, string.Empty, "no repository this test knows"));
        }

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
        DocumentStore store = postgres.Store;
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
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
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
        (DocumentStore store, NodeContext node, Guid projectId, Guid taskId) = await SeedWatchedTaskAsync(TaskState.Queued, cts.Token);

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

        try
        {
            AutoPrReviewSweepResult sweep = await engine.PollOnceAsync(cts.Token);

            // The sweep does report its recall, but asserted as "at least one" rather than as
            // exactly one: PollOnceAsync sums its recalls across every opted-in project in the
            // database, and this class shares one (the mint-path tests' own note on the same
            // hazard), so a total of 1 would be a claim about which sibling test ran first. What
            // this recall actually did is asserted on the task's own stream below instead.
            sweep.AssignmentsRecalled.Should().BeGreaterThan(0);

            await using IQuerySession query = store.QuerySession();
            IReadOnlyList<JasperFx.Events.IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
            stream.Select(recorded => recorded.Data).OfType<PullRequestReviewAssignmentRecalled>().Should().ContainSingle(
                "alice's own withdrawal is recorded on the task whose assignment it recalled");
            TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token);
            task!.State.Should().Be(TaskState.Abandoned, "the go signal was recalled before the run ever dispatched");
            task.AutoPrReviewAssigneeLogin.Should().BeNull();
        }
        finally
        {
            // The task concluded, but the project it belongs to is still opted in at Normal —
            // left that way it is swept again by whichever sibling test runs next, with that
            // test's own gh script answering for it (see the mint-path tests' own note).
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
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
        (DocumentStore store, NodeContext node, Guid projectId, Guid taskId) = await SeedWatchedTaskAsync(TaskState.Claimed, cts.Token);

        const string removalJson = """
            {"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[
              {"__typename":"ReviewRequestRemovedEvent","createdAt":"2026-09-04T11:00:00Z",
               "actor":{"login":"alice"},"requestedReviewer":{"__typename":"User","login":"brian"}}
            ]}}}}}
            """;
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node), ScriptedGh("brian", removalJson), NullLogger<AutoPrReviewEngine>.Instance);

        try
        {
            AutoPrReviewSweepResult sweep = await engine.PollOnceAsync(cts.Token);

            // "At least one", and the recall itself read off this task's own stream, for the
            // reason the Queued sibling above states.
            sweep.AssignmentsRecalled.Should().BeGreaterThan(0);

            await using IQuerySession query = store.QuerySession();
            IReadOnlyList<JasperFx.Events.IEvent> stream = await query.Events.FetchStreamAsync(taskId, token: cts.Token);
            stream.Select(recorded => recorded.Data).OfType<PullRequestReviewAssignmentRecalled>().Should().ContainSingle(
                "alice's own withdrawal is recorded on the task whose assignment it recalled");
            TaskAggregate? task = await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token);
            task!.State.Should().Be(TaskState.Claimed, "findings already in flight are never discarded for a reviewer reshuffle");
            task.AutoPrReviewAssigneeLogin.Should().BeNull("the recall is still recorded, as an observation");
        }
        finally
        {
            // Still Claimed and still watched by its own project's opt-in, so this one has to be
            // taken out of later sweeps for the same reason the Queued sibling above does.
            await TurnOffAutoPrReviewAsync(store, projectId, node.OwnerId, cts.Token);
        }
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        await SeedDefaultAdoptionAsync(store, node, cts.Token);
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
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
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

        ProcessRunner gh = (fileName, arguments, _, _) =>
        {
            if (IsRepositoryHostRead(arguments))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "no repository this test knows"));
            }

            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(
                    0, AsksAbout(arguments, repository) ? listJson : "[]", string.Empty));
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

            // graphql — the actor-provenance timeline read, and since Decisions Log #159 also the
            // requested-at the no-backfill guard compares against this project's own cutoff. A
            // request GitHub recorded at this project's registration is inside its cutoff, so
            // both candidates are free to mint and the immediate-launch cap is what decides
            // which one starts.
            return Task.FromResult(new ProcessResult(0, RequestedAtNowTimelineJson, string.Empty));
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
            project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        await SeedDefaultAdoptionAsync(store, node, cts.Token);

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
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
                Optional<IReadOnlyList<ContextLink>>.None, Now, node.OwnerId,
                autoPrReview: Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Normal));
            session.Events.Append(projectId, optedIn);
            await session.SaveChangesAsync(cts.Token);
        }

        const string listJson = """
            [{"number":9201,"url":"https://github.com/acme/mint-linked-issue-test/pull/9201","title":"Add rate limiting","body":"Closes #9202."}]
            """;

        ProcessRunner gh = (fileName, arguments, _, _) =>
        {
            if (IsRepositoryHostRead(arguments))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "no repository this test knows"));
            }

            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(
                    0, AsksAbout(arguments, repository) ? listJson : "[]", string.Empty));
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

            return Task.FromResult(new ProcessResult(0, RequestedAtNowTimelineJson, string.Empty));
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
        DocumentStore store = postgres.Store;
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
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
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
            if (IsRepositoryHostRead(arguments))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "no repository this test knows"));
            }

            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(
                    0, AsksAbout(arguments, repository) ? listJson : "[]", string.Empty));
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        Guid firstReviewTaskId = DomainId.New();
        await SeedDefaultAdoptionAsync(store, node, cts.Token);
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
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
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
            if (IsRepositoryHostRead(arguments))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "no repository this test knows"));
            }

            if (arguments.Contains("user"))
            {
                return Task.FromResult(new ProcessResult(0, "brian\n", string.Empty));
            }

            if (arguments.Contains("list"))
            {
                return Task.FromResult(new ProcessResult(
                    0, AsksAbout(arguments, repository) ? listJson : "[]", string.Empty));
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

    // ── PrReviewEngine's re-entrancy and reclaim safety ──
    private static readonly DateTimeOffset PrReviewNow = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

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

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
    }


    /// <summary>Records the spawn request instead of starting anything.</summary>
    private sealed class CapturingExecutor(DateTimeOffset startedAt) : IExecutor
    {
        public AgentSpawnRequest? Request { get; private set; }

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new SpawnedAgent(4242, startedAt));
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
                return new SpawnedAgent(processId, PrReviewNow);
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
            return new SpawnedAgent(processId, PrReviewNow);
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, PrReviewNow));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new("Looked the pull request over; nothing further to add.");
        PrReviewEngine engine = NewPrReviewEngine(store, executor, executor.Processes, new NoOpWorktreeManager());
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, PrReviewNow));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Reviewed the pull request against its own title and description; it matches.\n\nVERDICT: merge-ready");
        PrReviewEngine engine = NewPrReviewEngine(store, executor, executor.Processes, new NoOpWorktreeManager());
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, PrReviewNow));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedExecutor executor = new(
            "Reviewed the pull request against its own title and description; it matches.\n\nVERDICT: merge-ready");
        PrReviewEngine engine = NewPrReviewEngine(store, executor, executor.Processes, new NoOpWorktreeManager());
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);
        await ReclaimUnderNewGenerationAsync(store, node, taskId, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, PrReviewNow));
            await session.SaveChangesAsync(cts.Token);
        }

        PrReviewEngine engine = NewPrReviewEngine(store, new RefusingExecutor("A reclaimed run must retire before dispatching anything."), new FakeProcessManager(), new NoOpWorktreeManager());
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, PrReviewNow));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new(PrReviewNow);
        PrReviewEngine engine = NewPrReviewEngine(store, executor, new FakeProcessManager(), new NoOpWorktreeManager());
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(store, node, cts.Token);
        Guid conformanceSessionId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId,
                new AgentSessionCompleted(runId, PrReviewNow),
                new PrReviewConformanceDispatched(runId, conformanceSessionId, 5_001, PrReviewNow, PrReviewNow, AgentModel.Sonnet),
                new PrReviewConformanceCompleted(runId, conformanceSessionId, PrReviewNow));
            await session.SaveChangesAsync(cts.Token);
        }

        PrReviewEngine engine = NewPrReviewEngine(store, new RefusingExecutor("A reclaimed run must retire before dispatching anything."), new FakeProcessManager(), new NoOpWorktreeManager());
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
    /// A reviewer's lap opened with <c>h9k pr review --no-worktree</c> (Decisions Log #149)
    /// records no worktree, so finalize has nothing to release — and must not try. Both cleanups
    /// would fail on it (<c>git worktree remove ""</c>, and an <c>update-ref -d</c> on a tracking
    /// ref that was never fetched because no checkout ever happened), and both failures are
    /// swallowed into warnings that read like a leaked worktree somebody then has to chase.
    /// </summary>
    [Fact]
    public async Task Finalize_releases_nothing_for_a_run_that_never_had_a_checkout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, _) = await SeedDeliveredPrReviewRunAsync(
            store, node, cts.Token, withoutWorktree: true);

        NoOpWorktreeManager worktrees = new();
        PrReviewEngine engine = NewPrReviewEngine(store, new RefusingExecutor("A reclaimed run must retire before dispatching anything."), new FakeProcessManager(), worktrees);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        worktrees.Removed.Should().BeEmpty("nothing was ever checked out, so there is nothing to remove");

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.State.Should().Be(RunState.Completed, "the task still finalizes — only the cleanups are skipped");
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Done");
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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedDeliveredPrReviewRunAsync(store, node, cts.Token);
        await ReclaimUnderNewGenerationAsync(store, node, taskId, cts.Token);

        NoOpWorktreeManager worktrees = new();
        PrReviewEngine engine = NewPrReviewEngine(store, new RefusingExecutor("A reclaimed run must retire before dispatching anything."), new FakeProcessManager(), worktrees);

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
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        (Guid taskId, Guid runId, string runDirectory) = await SeedDeliveredPrReviewRunAsync(store, node, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate aggregate = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskAbandoned abandoned = TaskDecider.Abandon(aggregate, "Superseded by hand.", PrReviewNow, node.OwnerId);
            session.Events.Append(taskId, abandoned);
            await session.SaveChangesAsync(cts.Token);
        }

        NoOpWorktreeManager worktrees = new();
        PrReviewEngine engine = NewPrReviewEngine(store, new RefusingExecutor("A reclaimed run must retire before dispatching anything."), new FakeProcessManager(), worktrees);

        await engine.ReviewAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunAggregate? run = await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token);
        run!.State.Should().Be(RunState.Completed, "the run itself still finishes even though the task moved on");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Abandoned", "finalize must not overwrite a state the owner already chose");

        worktrees.Removed.Should().ContainSingle(path => path.Contains(runId.ToString("N")));
    }


    private static PrReviewEngine NewPrReviewEngine(
        DocumentStore store, IExecutor executor, FakeProcessManager processes, IWorktreeManager worktrees) =>
        new(store, executor, processes, worktrees,
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);

    /// <summary>
    /// A pr-review task, published, assigned and claimed at generation 1 — exactly where
    /// <c>RunLauncher.LaunchAsync</c> hands off to <c>PrReviewEngine</c> once the adversarial
    /// lens (this run's own primary session) has been dispatched.
    /// </summary>
    /// <param name="withoutWorktree">
    /// Seeds the run with no recorded worktree — the shape a reviewer's own lap takes when it was
    /// opened with <c>h9k pr review --no-worktree</c> (Decisions Log #149). Nothing was ever
    /// checked out, so finalize has nothing to release.
    /// </param>
    private async Task<(Guid TaskId, Guid RunId, string RunDirectory)> SeedClaimedPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken, bool withoutWorktree = false)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-repo-{taskId:N}");
        string worktreePath = withoutWorktree
            ? string.Empty
            : Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-wt-{runId:N}");
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-pr-review-run-{runId:N}");
        Directory.CreateDirectory(runDirectory);

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"pr-review-{taskId:N}", repositoryPath, null, "main", PrReviewNow);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Review pull request acme/web#42", ["every finding names a file and line"], TaskType.PrReview,
                null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#42"),
                PrReviewNow, node.OwnerId),
            node.OwnerId, PrReviewNow);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, PrReviewNow);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = PrReviewNow });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/42",
            ExecutorMode.Subscription, PrReviewNow, RunDirectory: runDirectory));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId, runDirectory);
    }

    /// <summary>Extends <see cref="SeedClaimedPrReviewRunAsync"/> to a resolved park, ready for finalize.</summary>
    private async Task<(Guid TaskId, Guid RunId, string RunDirectory)> SeedDeliveredPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken, bool withoutWorktree = false)
    {
        (Guid taskId, Guid runId, string runDirectory) = await SeedClaimedPrReviewRunAsync(
            store, node, cancellationToken, withoutWorktree);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId,
            new AgentSessionCompleted(runId, PrReviewNow),
            new ReviewParked(runId, "Findings ready.", PrReviewNow),
            new PrReviewDelivered(runId, "Walked and directed.", PrReviewNow, node.OwnerId));
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
        TaskRequeued requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, PrReviewNow);
        task.Apply(requeued);
        TaskClaimed reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), PrReviewNow);
        task.Apply(reclaimed);
        session.Events.Append(taskId, requeued, reclaimed);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = task.LeaseGeneration, HeartbeatAt = PrReviewNow });
        await session.SaveChangesAsync(cancellationToken);
    }

    // ── a budget-exhaustion retry on a pr-review session ──
    private static readonly DateTimeOffset TokenBudgetNow = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Records the spawn request instead of starting anything.</summary>

    [Fact]
    public async Task Resuming_a_budget_parked_pr_review_run_marks_the_spawn_request_untrusted()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-wt-{runId:N}");
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-run-{runId:N}");
        Directory.CreateDirectory(runDirectory);

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"budget-retry-{taskId:N}", repositoryPath,
                new Uri("https://github.com/acme/web"), "main", TokenBudgetNow);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#42", ["every finding names a file and line"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#42"), TokenBudgetNow, node.OwnerId),
                node.OwnerId, TokenBudgetNow);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, TokenBudgetNow);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = TokenBudgetNow });

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/42",
                ExecutorMode.Subscription, TokenBudgetNow, RunDirectory: runDirectory));
            session.Events.Append(runId, new RunBudgetExhausted(runId, "usage limit reached", TokenBudgetNow));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new(TokenBudgetNow);
        TokenBudgetRetryEngine engine = new(
            store, node, new PrimarySessionResumer(executor), NewSupervisor(store, node), NullLogger<TokenBudgetRetryEngine>.Instance);

        int retried = await engine.RetryParkedRunsAsync(cts.Token);

        retried.Should().Be(1, "the run is budget-parked and its task is still claimed by this node");
        executor.Request.Should().NotBeNull();
        executor.Request!.UntrustedWorkingDirectory.Should().BeTrue(
            "the resumed session is the pr-review task's own adversarial lens over the same foreign checkout");
    }

    /// <summary>
    /// The high finding from independent pre-PR review cycle 10: a TokenBudgetNow-speed auto-pr-review
    /// sentinel run (<see cref="Guid.Empty"/> on <c>NodeId</c>, the real node on
    /// <c>DispatchingNodeId</c> — <c>AutoPrReviewEngine.CreateOneAsync</c>'s own claim shape,
    /// mirrored here with <see cref="TaskDecider.ClaimDeliberately"/>) never matched
    /// <c>RetryParkedRunsAsync</c>'s plain <c>NodeId == nodeId</c> filter, so it sat
    /// <c>BudgetParked</c> forever — nothing else on the node ever clears that state
    /// (<c>RunSupervisor.AdoptOrphansAsync</c> defers to this sweep for it by design). This pins
    /// down that the widened query now finds it and resumes it exactly as an ordinarily-claimed
    /// pr-review run does.
    /// </summary>
    [Fact]
    public async Task Resuming_a_budget_parked_sentinel_pr_review_run_retries_it_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-sentinel-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-sentinel-wt-{runId:N}");
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-sentinel-run-{runId:N}");
        Directory.CreateDirectory(runDirectory);

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"budget-retry-sentinel-{taskId:N}", repositoryPath,
                new Uri("https://github.com/acme/web"), "main", TokenBudgetNow);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#43", ["every finding names a file and line"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#43"), TokenBudgetNow, node.OwnerId),
                node.OwnerId, TokenBudgetNow);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, node.OwnerId, runId, TokenBudgetNow, dependencyOverrideAcknowledged: false);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            // Deliberately no TaskLease: a sentinel claim writes none (AutoPrReviewEngine.CreateOneAsync).

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, sessionId, worktreePath, "pr/43",
                ExecutorMode.Subscription, TokenBudgetNow, RunDirectory: runDirectory, DispatchingNodeId: node.NodeId));
            session.Events.Append(runId, new RunBudgetExhausted(runId, "usage limit reached", TokenBudgetNow));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new(TokenBudgetNow);
        TokenBudgetRetryEngine engine = new(
            store, node, new PrimarySessionResumer(executor), NewSupervisor(store, node), NullLogger<TokenBudgetRetryEngine>.Instance);

        int retried = await engine.RetryParkedRunsAsync(cts.Token);

        retried.Should().Be(1, "the sentinel run's DispatchingNodeId names this node, so the widened query must find it");
        executor.Request.Should().NotBeNull();
        executor.Request!.UntrustedWorkingDirectory.Should().BeTrue(
            "the resumed session is still the pr-review task's own adversarial lens over the same foreign checkout");
    }

    /// <summary>
    /// A reviewer's own review lap (<c>h9k pr review</c>, Decisions Log #149) may attach to a
    /// BudgetParked run deliberately — an exhausted token budget is the platform's problem, not a
    /// reason to refuse the human who wants to review by hand — and it reuses that run's
    /// worktree. So this sweep must not resume the automated review into the checkout the
    /// reviewer is reading: two sessions would share one working tree, and the reviewer's later
    /// verdict would have <c>PrReviewEngine.FinalizeAsync</c> delete it out from under the live
    /// one. The claim's own state cannot say this — the task stays Claimed and keeps naming this
    /// run for the lap's whole life — which is why the flag is read explicitly
    /// (independent pre-PR review, cycle 1, conformance lens: adoption had this exclusion, this
    /// sweep did not).
    /// </summary>
    [Fact]
    public async Task A_budget_parked_run_a_reviewers_lap_is_attached_to_is_not_resumed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-lap-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-lap-wt-{runId:N}");
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-lap-run-{runId:N}");
        Directory.CreateDirectory(runDirectory);

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"budget-retry-lap-{taskId:N}", repositoryPath,
                new Uri("https://github.com/acme/web"), "main", TokenBudgetNow);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#44", ["the verdict is submitted"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#44"), TokenBudgetNow, node.OwnerId),
                node.OwnerId, TokenBudgetNow);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, TokenBudgetNow);
            session.Events.StartStream<TaskAggregate>(
                taskId,
                [
                    .. lifecycle,
                    claimed,
                    new PullRequestReviewLapOpened(
                        taskId, runId, worktreePath, "https://github.com/acme/web/pull/44", TokenBudgetNow, node.OwnerId),
                ]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = TokenBudgetNow });

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(), worktreePath, "pr/44",
                ExecutorMode.Subscription, TokenBudgetNow, RunDirectory: runDirectory));
            session.Events.Append(runId, new RunBudgetExhausted(runId, "usage limit reached", TokenBudgetNow));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new(TokenBudgetNow);
        TokenBudgetRetryEngine engine = new(
            store, node, new PrimarySessionResumer(executor), NewSupervisor(store, node), NullLogger<TokenBudgetRetryEngine>.Instance);

        int retried = await engine.RetryParkedRunsAsync(cts.Token);

        retried.Should().Be(0, "a reviewer owns this run's checkout for as long as their lap is open");
        executor.Request.Should().BeNull("nothing was spawned into the worktree the reviewer is reading in");
    }

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node)
    {
        FakeProcessManager processes = new();
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            new Hall9k.Daemon.Closeout.StackedParentWatch(
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
                NullLogger<Hall9k.Daemon.Closeout.StackedParentWatch>.Instance));
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        PrimarySessionResumer primarySessionResumer = new(
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())));
        return new RunSupervisor(store, node, processes, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            primarySessionResumer, Options.Create(new DaemonOptions()), NullLogger<RunSupervisor>.Instance);
    }
}
