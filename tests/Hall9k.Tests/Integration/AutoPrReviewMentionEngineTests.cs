using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.AutoPrReview;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
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
/// Auto-pr-review's second trigger (idea 2f079bcd): a GitHub comment mentioning the install's own
/// login on a pull request in a registered project mints or extends the same pr-review task a
/// review request does. Every test here scripts its own repository, distinct from every other test
/// file's, for the reason <see cref="PrReviewTaskEngineTests"/>'s own class doc gives: this suite
/// shares one Postgres database across its test methods, and the dedup queries key on the
/// canonical external reference alone, unscoped by project.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class AutoPrReviewMentionEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------------------
    // Fakes and seeding — deliberately this file's own rather than shared with
    // PrReviewTaskEngineTests, mirroring how RunLauncherTests and PrReviewTaskEngineTests each
    // already roll their own rather than share a fixture library.
    // -------------------------------------------------------------------------------------

    private static bool IsRepositoryHostRead(IReadOnlyList<string> arguments) =>
        arguments.Count > 1 && arguments[0] == "repo" && arguments[1] == "view";

    private static bool AsksAbout(IReadOnlyList<string> arguments, string repository)
    {
        int repoIndex = arguments.ToList().IndexOf("--repo");
        return repoIndex >= 0
            && repoIndex + 1 < arguments.Count
            && string.Equals(arguments[repoIndex + 1], repository, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A gh that answers for exactly one repository: no review-requested pull requests at all
    /// (isolating the mentions search), one open pull request mentioning <paramref name="login"/>,
    /// and <paramref name="comments"/> as the comments <c>FindMentionCommentsAsync</c>'s own
    /// GraphQL query would find on it. The two GraphQL shapes (the review-request timeline and the
    /// mention comments) are told apart by a substring of the query text itself, since both travel
    /// as a plain <c>-f query=...</c> argument.
    /// </summary>
    private static ProcessRunner MentionScriptedGh(
        string repository, int number, string login,
        IReadOnlyList<(string Id, string Author, string Body, DateTimeOffset CreatedAt)> comments) =>
        (fileName, arguments, _, _) =>
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
                bool isMentionsSearch = arguments.Any(argument => argument.StartsWith("mentions:", StringComparison.Ordinal));
                string listJson = isMentionsSearch && AsksAbout(arguments, repository)
                    ? $$"""
                        [{"number":{{number}},"url":"https://github.com/{{repository}}/pull/{{number}}",
                          "title":"Add rate limiting","body":"no links here"}]
                        """
                    : "[]";
                return Task.FromResult(new ProcessResult(0, listJson, string.Empty));
            }

            if (arguments.Contains("view"))
            {
                int repoIndex = arguments.ToList().IndexOf("--repo");
                string requestRepository = repoIndex >= 0 && repoIndex + 1 < arguments.Count
                    ? arguments[repoIndex + 1]
                    : repository;
                string prJson = $$"""
                    {"number":{{number}},"title":"Add rate limiting","body":"no links here","state":"OPEN",
                     "url":"https://github.com/{{requestRepository}}/pull/{{number}}","baseRefName":"main"}
                    """;
                return Task.FromResult(new ProcessResult(0, prJson, string.Empty));
            }

            if (arguments.Contains("graphql") && arguments.Any(argument => argument.Contains("reviewThreads", StringComparison.Ordinal)))
            {
                string nodes = string.Join(",", comments.Select(comment => $$"""
                    {"id":"{{comment.Id}}","author":{"login":"{{comment.Author}}"},
                     "body":"{{comment.Body.Replace("\"", "\\\"", StringComparison.Ordinal)}}",
                     "url":"https://github.com/{{repository}}/pull/{{number}}#issuecomment-{{comment.Id}}",
                     "createdAt":"{{comment.CreatedAt:yyyy-MM-ddTHH:mm:ss}}Z"}
                    """));
                string json =
                    """{"data":{"repository":{"pullRequest":{"comments":{"nodes":[""" + nodes
                    + """]},"reviewThreads":{"nodes":[]},"reviews":{"nodes":[]}}}}}""";
                return Task.FromResult(new ProcessResult(0, json, string.Empty));
            }

            // The review-requested timeline query (IsGenuineReRequestAsync's own read, or the
            // withdrawal sweep's) — never exercised by these mention-only tests, so an honestly
            // empty timeline rather than a crash.
            return Task.FromResult(new ProcessResult(
                0, """{"data":{"repository":{"pullRequest":{"timelineItems":{"nodes":[]}}}}}""", string.Empty));
        };

    private static async Task SeedProjectAsync(
        DocumentStore store, NodeContext node, Guid projectId, string name, string repository,
        DateTimeOffset registeredAt, DateTimeOffset adoptedAt, Optional<AutoPrReviewSpeed> speed,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new AutoPrReviewDefaultAdoption { Id = node.NodeId, AdoptedAt = adoptedAt });
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), name, $"/tmp/{name}-repo",
            new Uri($"https://github.com/{repository}"), "main", registeredAt);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);
        if (speed.HasValue)
        {
            ProjectAggregate project = new();
            project.Apply(registered);
            session.Events.Append(projectId, ProjectDecider.ChangeSettings(
                project, Optional<IReadOnlyList<VerifyCommand>>.None, Optional<bool>.None,
                Optional<IReadOnlyList<ContextLink>>.None, registeredAt, node.OwnerId, autoPrReview: speed));
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A live pr-review task already waiting on its pull request (AwaitingAuthor) — the shape a mention attaches to instead of minting a second task.</summary>
    private static async Task<Guid> SeedWaitingReviewAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-2), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddHours(-2));
        task.Apply(claimed);
        PullRequestReviewFollowThroughOpened opened = TaskDecider.OpenPrReviewFollowThrough(
            task, runId, $"https://github.com/{repository}/pull/{number}", headSha: null, Now.AddHours(-1));
        task.Apply(opened);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed, opened]);
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    private sealed class NoOpLock : IAsyncDisposable
    {
        public static readonly NoOpLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Prepares a workspace without touching git; the launcher only needs a path and a branch.</summary>
    private sealed class StubWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Worktree(
                Path.Combine(Path.GetTempPath(), $"hall9k-wt-{request.RunId:N}"),
                $"pr/{request.PullRequestNumber}", $"pr/{request.PullRequestNumber}"));

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "nothing here is a real repository"));

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
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

    private sealed class RefusingExecutor(string why) : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(why);
    }

    private sealed class RefusingWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests: no mention here should ever dispatch a follow-up.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached by these tests.");

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
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

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node)
    {
        FakeProcessManager processes = new();
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes);
        LaunchHoldEngine launchHold = new(store, NullLogger<LaunchHoldEngine>.Instance);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            new StackedParentWatch(
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
                NullLogger<StackedParentWatch>.Instance),
            launchHold);
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), launchHold,
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        PrimarySessionResumer primarySessionResumer = new(
            new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())));
        return new RunSupervisor(store, node, processes, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            primarySessionResumer, launchHold, Options.Create(new DaemonOptions()), NullLogger<RunSupervisor>.Instance);
    }

    /// <summary>
    /// A real <see cref="RunLauncher"/> with the worktree manager and executor swapped for the
    /// fakes a test needs — refusing ones for a test that must never actually dispatch, capturing
    /// ones for a test asserting a follow-up really was dispatched.
    /// </summary>
    private RunLauncher NewLauncher(
        DocumentStore store, NodeContext node, IWorktreeManager worktrees, IExecutor executor, ProcessRunner gh)
    {
        RefusingInspector inspector = new();
        CloseoutEngine closeout = new(
            store, node, new DaemonConnection(postgres.ConnectionString), inspector, new RefusingWorktreeManager(),
            new StackedParentWatch(new RefusingWorktreeManager(), NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner, FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);
        BlockerContextAssembler blockerContext = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, new FakeProcessManager(), Options.Create(new DaemonOptions())),
            new FakeProcessManager(), Options.Create(new DaemonOptions()), NullLogger<BlockerContextAssembler>.Instance);
        return new RunLauncher(
            store, worktrees, executor, NewSupervisor(store, node), blockerContext, inspector, closeout, gh,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);
    }

    // -------------------------------------------------------------------------------------
    // The tests themselves.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mention_on_a_pull_request_with_no_live_task_mints_a_fresh_pr_review_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-mint-test";
        const int number = 4201;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-mint", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what do you think of this approach?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem minted = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single();
        minted.Type.Should().Be(TaskType.PrReview);
        minted.LatestMentionCommentId.Should().Be("IC_1");
        minted.LatestMentionAuthorLogin.Should().Be("ryan");

        TaskDetails details = (await query.LoadAsync<TaskDetails>(minted.Id, cts.Token))!;
        details.LatestMentionBody.Should().Be("@brian what do you think of this approach?");
        details.LatestMentionCreatedAt.Should().Be(Now.AddMinutes(5));

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.TaskCreated);
        observed.TaskId.Should().Be(minted.Id);
    }

    /// <summary>
    /// A fresh mint's own primary session must be told about the tagged comment too, not only the
    /// task's own stream (idea 2f079bcd, decision 3): the findings report it produces is the ONE
    /// place a mint-triggered task's "You were asked" section can come from, since no separate
    /// follow-up lap ever runs for a task that had no live coverage to attach to.
    /// </summary>
    [Fact]
    public async Task A_fresh_mint_from_a_mention_asks_its_primary_session_to_answer_the_comment_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-mint-prompt-test";
        const int number = 4208;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-mint-prompt", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Now), cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does this handle the empty-list case?", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new StubWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        executor.Request.Should().NotBeNull("Now speed launches the primary review session immediately");
        executor.Request!.Prompt.Should().Contain("@brian does this handle the empty-list case?");
        executor.Request.Prompt.Should().Contain("ryan");
        // The session's own working directory is the pull request checkout, a different directory
        // entirely from where its findings files land — the prompt must therefore name the exact
        // absolute path to write mention-answer.md to, never just the bare filename, or
        // PrReviewEngine.ComposeReportAndParkAsync's own read of Path.Combine(runDirectory,
        // "mention-answer.md") finds nothing (independent pre-PR review, cycle 1, conformance
        // lens, high).
        executor.Request.Prompt.Should().Contain(
            Path.Combine(executor.Request.RunDirectory, "mention-answer.md"),
            "the prompt must name the exact path PrReviewEngine reads back, not just the bare filename");
    }

    [Fact]
    public async Task A_comment_written_by_the_installs_own_login_is_never_counted_as_a_mention()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-own-comment-test";
        const int number = 4202;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-own-comment", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        // The connector's own filter already excludes an authorLogin matching the mentioned
        // login — this comment is here only so the fake gh has SOMETHING to answer, and the
        // assertion is that nothing at all was minted from it.
        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "brian", "@brian noting this for my own records", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("never dispatches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("the install's own comment is never a trigger");
    }

    [Fact]
    public async Task A_mention_predating_the_cutoff_never_mints()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-cutoff-test";
        const int number = 4203;

        // Registered now, with the mention two weeks old — the no-backfill guard applies to a
        // mention's own comment timestamp exactly as it does to a review request's.
        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-cutoff", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian could you take a look?", Now.AddDays(-14))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("held mentions never dispatch"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("the comment predates this project's own cutoff — no backfill");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.HeldBeforeCutoff);
        observed.TaskId.Should().BeNull();
    }

    [Fact]
    public async Task Auto_pr_review_off_silences_mentions_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-off-test";
        const int number = 4204;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-off", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Off), cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian could you take a look?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("off means off"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().BeEmpty("--auto-pr-review off silences mentions exactly as it silences review requests");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.HeldSettingOff);
    }

    /// <summary>
    /// Off silences a follow-up dispatch onto an existing, already-covered task exactly as it
    /// silences a mint (independent pre-PR review, cycle 1, both lenses: the attach path used to
    /// launch before ever checking the setting). The mention still attaches — it costs nothing and
    /// keeps the record honest — but no session is spawned.
    /// </summary>
    [Fact]
    public async Task Auto_pr_review_off_silences_a_follow_up_onto_an_already_covered_task_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-attach-off-test";
        const int number = 4208;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-attach-off", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.Of(AutoPrReviewSpeed.Off), cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question about this", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("off silences the follow-up too"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1", "the mention is still recorded on the task, off or not");
        watched.State.Should().Be(TaskState.AwaitingAuthor, "no follow-up claim was made, so the task never moved off its waiting state");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    /// <summary>
    /// The no-backfill guard applies to a follow-up dispatch onto an existing task exactly as it
    /// applies to a mint (independent pre-PR review, cycle 1, both lenses).
    /// </summary>
    [Fact]
    public async Task A_stale_mention_never_dispatches_a_follow_up_onto_an_already_covered_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-attach-cutoff-test";
        const int number = 4209;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-attach-cutoff", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian could you take a look?", Now.AddDays(-14))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("stale mentions never dispatch"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1");
        watched.State.Should().Be(TaskState.AwaitingAuthor, "no follow-up claim was made — the comment predates this project's own cutoff");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    [Fact]
    public async Task A_comment_id_already_handled_never_fires_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-dedup-test";
        const int number = 4205;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-dedup", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian what do you think?", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);
        Guid mintedTaskId;
        await using (IQuerySession query = store.QuerySession())
        {
            mintedTaskId = (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token)).Single().Id;
        }

        // Abandon the minted task so a second mint would be visible if the dedup ever failed —
        // the comment id is what must stop it, not "a live task still covers it".
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(mintedTaskId, token: cts.Token))!;
            session.Events.Append(mintedTaskId, TaskDecider.Abandon(task, "test cleanup", Now, node.OwnerId));
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession recheck = store.QuerySession();
        (await recheck.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().HaveCount(1, "the same comment id must never mint a second time, even once its own task is gone");
    }

    [Fact]
    public async Task A_mention_on_a_pull_request_whose_only_task_is_done_mints_a_fresh_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-done-test";
        const int number = 4206;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-done", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        // A Done pr-review task already covers this pull request — a prior review that reached
        // Done (merged/closed) does not block a fresh mint the way a live one does.
        Guid doneTaskId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                doneTaskId, projectId, $"Review pull request {repository}#{number}", ["done"], TaskType.PrReview,
                null, null, new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"),
                Now.AddDays(-2), node.OwnerId);
            TaskAggregate task = new();
            task.Apply(added);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddDays(-2), node.OwnerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddDays(-2), node.OwnerId);
            task.Apply(assigned);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now.AddDays(-2));
            task.Apply(claimed);
            TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, null, Now.AddDays(-2).AddHours(1));
            task.Apply(completed);
            session.Events.StartStream<TaskAggregate>(doneTaskId, [added, published, assigned, claimed, completed]);
            await session.SaveChangesAsync(cts.Token);
        }

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question now that this merged", Now.AddMinutes(5))]);
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("normal speed never launches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskListItem> tasks = await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token);
        tasks.Should().HaveCount(2, "the Done task stays, and a fresh one mints beside it");
        tasks.Should().Contain(task => task.Id == doneTaskId && task.State == TaskState.Done);
        tasks.Should().Contain(task => task.Id != doneTaskId && task.LatestMentionCommentId == "IC_1");
    }

    /// <summary>
    /// The mint-or-attach decision, decision 2's own core: a mention on a pull request a live task
    /// already covers attaches to it — recording <see cref="PullRequestReviewMentionObserved"/> on
    /// that task's own stream — and, because the task is waiting on its pull request
    /// (AwaitingAuthor, eligible for a follow-up), the daemon claims it and dispatches the bounded
    /// follow-up lap through <see cref="RunLauncher.LaunchPrReviewMentionFollowUpAsync"/>.
    /// </summary>
    [Fact]
    public async Task A_mention_on_a_pull_request_a_live_task_already_covers_attaches_and_dispatches_a_follow_up()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-attach-test";
        const int number = 4207;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-attach", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedWaitingReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian one more question about this", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new StubWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.Query<TaskListItem>().Where(task => task.ProjectId == projectId).ToListAsync(cts.Token))
            .Should().HaveCount(1, "the mention attaches to the live task rather than minting a second one");

        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1");
        watched.State.Should().Be(TaskState.Claimed, "claimed for the bounded follow-up lap");

        executor.Request.Should().NotBeNull("a bounded follow-up session was dispatched");
        executor.Request!.Prompt.Should().Contain("@brian one more question about this");
        executor.Request.Prompt.Should().Contain("ryan");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    /// <summary>
    /// A task whose review already parked but is not yet resolved sits Claimed, never
    /// AwaitingAuthor/NeedsHuman — the park lives entirely on the run stream
    /// (<c>RunState.ReviewParked</c>), never the task's own. Both independent review lenses (cycle
    /// 1) found <see cref="TaskDecider.AwaitsPrReviewFollowThrough"/> alone blind to this, so a
    /// mention arriving here used to just sit recorded with no follow-up ever dispatched — exactly
    /// the criterion's own "when that task's report is already parked" case.
    /// </summary>
    private static async Task<Guid> SeedParkedReviewAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-2), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddHours(-2));
        task.Apply(claimed);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, LeaseGeneration: 1, SessionId: DomainId.New(),
            WorktreePath: "/tmp/does-not-exist", Branch: $"pr/{number}", ExecutorMode.Subscription, Now.AddHours(-2)));
        session.Events.Append(runId, new ReviewParked(
            runId, "Pull request review complete. Findings: /tmp/does-not-exist/review-1-findings.md.", Now.AddHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
        return taskId;
    }

    [Fact]
    public async Task A_mention_on_a_task_whose_report_is_parked_and_unresolved_attaches_and_dispatches_a_follow_up()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-parked-report-test";
        const int number = 5301;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-parked", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        Guid watchedTaskId = await SeedParkedReviewAsync(store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does the retry logic look right to you?", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new StubWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1");
        watched.State.Should().Be(
            TaskState.Claimed, "reclaimed for the bounded follow-up lap, exactly as the AwaitingAuthor case is");

        executor.Request.Should().NotBeNull(
            "a report parked and not yet resolved is one of the two states the criterion names for a dispatched follow-up");
        executor.Request!.Prompt.Should().Contain("@brian does the retry logic look right to you?");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached);
        observed.TaskId.Should().Be(watchedTaskId);
    }

    /// <summary>
    /// A human reviewer's own <c>h9k pr review</c> lap re-enters the identical Claimed/ReviewParked
    /// shape <see cref="SeedParkedReviewAsync"/> leaves a task in, without moving either off it
    /// (<c>PullRequestReviewCommand</c>'s own re-entry path). A mention landing mid-lap must not
    /// claim the task out from under that live human review — the worktree cleanup a mention
    /// follow-up's own launch runs would delete the checkout the reviewer is sitting in
    /// (independent pre-PR review, cycle 1, adversarial lens, high).
    /// </summary>
    [Fact]
    public async Task A_mention_on_a_task_with_an_open_human_review_lap_attaches_without_claiming_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-open-lap-test";
        const int number = 5302;

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-open-lap", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);
        (Guid watchedTaskId, Guid lapRunId) = await SeedParkedReviewWithOpenLapAsync(
            store, node, projectId, repository, number, cts.Token);

        ProcessRunner gh = MentionScriptedGh(
            repository, number, "brian",
            [("IC_1", "ryan", "@brian does the retry logic look right to you?", Now.AddMinutes(5))]);
        CapturingExecutor executor = new();
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), executor, gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskDetails watched = (await query.LoadAsync<TaskDetails>(watchedTaskId, cts.Token))!;
        watched.LatestMentionCommentId.Should().Be("IC_1", "the mention is still recorded on the task's own stream");
        watched.State.Should().Be(TaskState.Claimed, "the lap's own claim, never reclaimed for a follow-up");
        watched.CurrentRunId.Should().Be(
            lapRunId, "the reviewer's own lap run is still current — a follow-up would have repointed it");
        watched.ReviewLapOpen.Should().BeTrue("the human reviewer's lap is still open");

        executor.Request.Should().BeNull(
            "a mention must never dispatch a follow-up onto a task a live human review lap already owns");

        ObservedReviewMention observed = (await query.LoadAsync<ObservedReviewMention>(
            ObservedReviewMention.ComputeId(node.NodeId, projectId, repository, number, "brian", "IC_1"), cts.Token))!;
        observed.Outcome.Should().Be(ReviewMentionOutcome.Attached, "the mention is still recorded, just without a claim");
        observed.TaskId.Should().Be(watchedTaskId);
    }

    private static async Task<(Guid TaskId, Guid RunId)> SeedParkedReviewWithOpenLapAsync(
        DocumentStore store, NodeContext node, Guid projectId, string repository, int number,
        CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        TaskAdded added = TaskDecider.Add(
            taskId, projectId, $"Review pull request {repository}#{number}",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, null, null,
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{repository}#{number}"), Now.AddHours(-2), node.OwnerId);
        TaskAggregate task = new();
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
        task.Apply(assigned);
        Guid runId = DomainId.New();
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now.AddHours(-2));
        task.Apply(claimed);
        PullRequestReviewLapOpened lapOpened = new(
            taskId, runId, "/tmp/does-not-exist", $"https://github.com/{repository}/pull/{number}",
            Now.AddMinutes(-30), node.OwnerId);
        task.Apply(lapOpened);

        session.Events.StartStream<TaskAggregate>(taskId, [added, published, assigned, claimed, lapOpened]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, LeaseGeneration: 1, SessionId: DomainId.New(),
            WorktreePath: "/tmp/does-not-exist", Branch: $"pr/{number}", ExecutorMode.Subscription, Now.AddHours(-2)));
        session.Events.Append(runId, new ReviewParked(
            runId, "Pull request review complete. Findings: /tmp/does-not-exist/review-1-findings.md.", Now.AddHours(-1)));
        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId);
    }

    /// <summary>
    /// The second search itself: <c>gh</c> is asked for <c>mentions:&lt;login&gt;</c>, a distinct
    /// call from the review-requested search the sweep already made — proof the sweep runs both,
    /// not that a single search happens to answer both trigger names.
    /// </summary>
    [Fact]
    public async Task The_sweep_runs_a_second_search_for_mentions_beside_review_requested()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const string repository = "acme/mention-second-search-test";

        await SeedProjectAsync(
            store, node, projectId, "auto-pr-review-mention-second-search", repository, Now, Now.AddDays(-1),
            Optional<AutoPrReviewSpeed>.None, cts.Token);

        List<IReadOnlyList<string>> searches = [];
        ProcessRunner gh = (fileName, arguments, workingDirectory, cancellationToken) =>
        {
            if (arguments.Contains("--search"))
            {
                searches.Add(arguments);
            }

            return MentionScriptedGh(repository, 1, "brian", [])(fileName, arguments, workingDirectory, cancellationToken);
        };
        AutoPrReviewEngine engine = new(
            store, node, NewLauncher(store, node, new RefusingWorktreeManager(), new RefusingExecutor("no mention here dispatches"), gh),
            gh, new LaunchHoldEngine(store, NullLogger<LaunchHoldEngine>.Instance), NullLogger<AutoPrReviewEngine>.Instance);

        await engine.PollOnceAsync(cts.Token);

        searches.Should().Contain(arguments => arguments.Contains("review-requested:brian"));
        searches.Should().Contain(arguments => arguments.Contains("mentions:brian"), "the second, independent search idea 2f079bcd adds");
    }
}
