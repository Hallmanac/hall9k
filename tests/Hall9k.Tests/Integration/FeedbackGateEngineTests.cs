using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.AutoPrReview;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The membership gate where it meets the closeout poll (task: a changes-requested review or an
/// unresolved review thread from someone outside the project's trust-chain roster never dispatches a
/// build lap; it raises a needs-you item, and only the owner's explicit go dispatches a fix lap for
/// it): real task and run streams in a real store, a scripted inspector standing in for GitHub, and a
/// worktree manager that refuses to be reached, so nothing here touches git or the network. The gate's
/// own decisions are pinned over pure data in <c>FeedbackMembershipGateTests</c>; this is what proves
/// the engine honours them in the order the poll runs.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class FeedbackGateEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PullRequestUrl = "https://github.com/x/y/pull/7";
    private const string Head = "cafe1";
    private const long MemberId = 1001;
    private const long StrangerId = 2001;

    private static string ReviewUrl(int number) => $"{PullRequestUrl}#pullrequestreview-{number}";

    private static ChangesRequestedReview Review(string login, long accountId, int number) =>
        new(login, ReviewUrl(number), Now.AddMinutes(-5),
            [new ChangesRequestedFinding($"{login}'s finding.", "src/Limiter.cs:42", $"PRRT_{number}")], accountId);

    private static PullRequestSnapshot Public(params ChangesRequestedReview[] reviews) =>
        new(IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
            FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
            UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
            CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0)
        {
            RepositoryIsPrivate = false,
            HeadCommit = Head,
            ChangesRequestedReviews = reviews,
        };

    [Fact]
    public async Task A_non_members_review_dispatches_no_lap_and_is_held_once_across_polls()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 42)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId);

        await engine.PollOnceAsync(cts.Token);
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Done, "no lap was dispatched for a stranger's review");
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        NonMemberFeedbackItem item = task.NonMemberFeedback.Should().ContainSingle().Subject;
        item.Status.Should().Be(NonMemberFeedbackStatus.Undecided);
        item.Reviewer.Should().Be("stranger");
        item.Url.Should().Be(ReviewUrl(42));
        (await query.Events.FetchStreamAsync(taskId, token: cts.Token)).Count(e => e.Data is NonMemberFeedbackHeld)
            .Should().Be(1, "however many polls observe it, the item is raised once");
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.AwaitingReview);
        await RetireWatchAsync(runId, cts.Token);
    }

    [Fact]
    public async Task A_members_review_still_dispatches_its_lap_stamped_as_a_members_with_the_read_scope()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, _, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 43)));

        await NewEngine(node, inspector, roster: [MemberId], taskId).PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        task.ChangesRequestedReviews.Should().ContainSingle().Which.AdmittedAs.Should().Be(FeedbackAdmission.Member);
        task.ReviewReadScope.Should().NotBeNull("the lap is told which comments it may read");
        task.NonMemberFeedback.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mixed_poll_runs_the_members_lap_and_still_raises_the_strangers_item()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, _, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 44), Review("stranger", StrangerId, 45)));

        await NewEngine(node, inspector, roster: [MemberId], taskId).PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        task.ChangesRequestedReviews.Should().ContainSingle(
            review => review.ReviewUrl == ReviewUrl(44), "the lap carries only the member's feedback");
        task.NonMemberFeedback.Should().ContainSingle(
            item => item.Key == ReviewUrl(45) && item.Status == NonMemberFeedbackStatus.Undecided,
            "the stranger's item is raised in the same poll, before the lap's push can move the head");
    }

    [Fact]
    public async Task With_the_roster_not_computed_a_poll_dispatches_nothing_raises_nothing_and_does_not_merge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(
            cts.Token, preApproval: PreApprovalMode.On);
        FakeInspector inspector = new(Public(Review("teammate", MemberId, 46)));

        await NewEngine(node, inspector, roster: null, taskId).PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        task.NonMemberFeedback.Should().BeEmpty("it asks again on the next poll rather than guessing");
        inspector.MergeAttempts.Should().Be(0);
        await RetireWatchAsync(runId, cts.Token);
    }

    [Fact]
    public async Task A_pre_approved_task_holds_its_merge_while_an_item_is_undecided_and_merges_once_it_is_declined()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(
            cts.Token, preApproval: PreApprovalMode.On);
        FakeInspector inspector = new(Public(Review("stranger", StrangerId, 47)));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId);

        await engine.PollOnceAsync(cts.Token);
        await engine.PollOnceAsync(cts.Token);
        inspector.MergeAttempts.Should().Be(0, "a stranger can stall a pre-approved merge until the owner declines");

        await using (IDocumentSession decide = postgres.Store.LightweightSession())
        {
            TaskAggregate aggregate = (await decide.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            decide.Events.Append(
                taskId, TaskDecider.DeclineNonMemberFeedback(aggregate, ReviewUrl(47), Now, node.OwnerId));
            await decide.SaveChangesAsync(cts.Token);
        }

        await engine.PollOnceAsync(cts.Token);

        inspector.MergeAttempts.Should().Be(1, "declining a review releases the hold");
        await using IQuerySession query = postgres.Store.QuerySession();
        (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        await RetireWatchAsync(runId, cts.Token);
    }

    [Fact]
    public async Task The_owners_go_dispatches_one_lap_carrying_the_review_even_after_the_head_moved()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (Guid taskId, Guid runId, NodeContext node) = await SeedAwaitingReviewAsync(cts.Token);
        ChangesRequestedReview strangers = Review("stranger", StrangerId, 48);
        FakeInspector inspector = new(Public(strangers));
        CloseoutEngine engine = NewEngine(node, inspector, roster: [MemberId], taskId);
        await engine.PollOnceAsync(cts.Token);

        await using (IDocumentSession decide = postgres.Store.LightweightSession())
        {
            TaskAggregate aggregate = (await decide.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            decide.Events.Append(
                taskId, TaskDecider.AdmitNonMemberFeedback(aggregate, ReviewUrl(48), Now, node.OwnerId));
            await decide.SaveChangesAsync(cts.Token);
        }

        // The head has moved on: the review is no longer on it, though it still stands.
        inspector.Snapshot = Public() with
        {
            HeadCommit = "cafe2",
            StandingChangesRequestedReviews = [strangers],
        };
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = postgres.Store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewRequestedChanges);
        ChangesRequestedReview carried = task.ChangesRequestedReviews.Should().ContainSingle().Subject;
        carried.ReviewUrl.Should().Be(ReviewUrl(48));
        carried.AdmittedAs.Should().Be(FeedbackAdmission.OwnerApproved);
        (await query.Events.FetchStreamAsync(taskId, token: cts.Token)).Count(e => e.Data is TaskReopened)
            .Should().Be(1, "the go buys exactly one lap");
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.Superseded);
    }

    /// <summary>
    /// A task finished with its pull request open and its run awaiting review, with no worktree and no
    /// repository behind either: the poll only ever reads GitHub through the scripted inspector.
    /// </summary>
    private async Task<(Guid TaskId, Guid RunId, NodeContext Node)> SeedAwaitingReviewAsync(
        CancellationToken cancellationToken, PreApprovalMode? preApproval = null)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid projectId = DomainId.New();
        await using IDocumentSession session = postgres.Store.LightweightSession();

        TaskAdded added = TaskDecider.Add(
            taskId, projectId, "Bound the limiter", ["the limiter resets per window"], TaskType.Feature,
            null, null, null, Now, node.OwnerId);
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(added, node.OwnerId, Now);
        List<object> taskEvents = [.. lifecycle];
        if (preApproval is not null)
        {
            TaskPreApprovedSet set = TaskDecider.SetPreApproved(task, preApproval, Now, node.OwnerId, taskClosedOut: false);
            task.Apply(set);
            taskEvents.Add(set);
        }

        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);
        taskEvents.Add(TaskDecider.Complete(task, claimed.RunId, PullRequestUrl, Now));
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);

        string worktree = Path.Combine(Path.GetTempPath(), $"hall9k-feedback-gate-{claimed.RunId:N}");
        session.Events.StartStream<RunAggregate>(claimed.RunId,
            new RunDispatched(
                claimed.RunId, taskId, node.NodeId, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                worktree, "task/limiter", ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(claimed.RunId, Now),
            new VerificationPassed(claimed.RunId, Now),
            new PullRequestOpened(claimed.RunId, PullRequestUrl, 7, Now));

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"gate-{taskId:N}", "/tmp/feedback-gate-repo", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        await session.SaveChangesAsync(cancellationToken);
        return (taskId, claimed.RunId, node);
    }

    private CloseoutEngine NewEngine(NodeContext node, FakeInspector inspector, long[]? roster, Guid taskId)
    {
        EnrolledNodeSnapshots snapshots = new();
        if (roster is not null)
        {
            snapshots.Set(ProjectOf(taskId), new EnrolledFleetSnapshot([node.NodeId], roster, []));
        }

        StubWorktreeManager worktrees = new();
        return new CloseoutEngine(
            postgres.Store, node, new DaemonConnection(postgres.ConnectionString), inspector, worktrees,
            new StackedParentWatch(worktrees, new NoOpRemoteParentReader(), NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner,
            RecordingJiraRequester.Succeeding(200, "{}").Requester,
            Options.Create(new DaemonOptions
            {
                MaxAutomaticCloseoutRuns = 4,
                MaxCloseoutLapsPerObstruction = 2,
                CopilotReviewSettleWindow = TimeSpan.FromHours(24),
                ChecksRegistrationSettleWindow = TimeSpan.FromMinutes(15),
            }),
            NullLogger<CloseoutEngine>.Instance,
            enrolledNodes: snapshots);
    }

    private Guid ProjectOf(Guid taskId)
    {
        using IQuerySession query = postgres.Store.QuerySession();
        return query.LoadAsync<TaskDetails>(taskId).GetAwaiter().GetResult()!.ProjectId;
    }

    /// <summary>Leaves the watch set as the test found it, since one database backs every test in the class.</summary>
    private async Task RetireWatchAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = postgres.Store.LightweightSession();
        session.Events.Append(runId, new RunSuperseded(runId, 99, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A scripted GitHub: whatever snapshot the test sets, and a count of the merges attempted.</summary>
    private sealed class FakeInspector(PullRequestSnapshot snapshot) : IPullRequestInspector
    {
        public PullRequestSnapshot Snapshot { get; set; } = snapshot;

        public int MergeAttempts { get; private set; }

        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestStateSnapshot(Snapshot.IsMerged, Snapshot.IsClosed, Snapshot.MergedAt, Snapshot.ClosedAt));

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken)
        {
            MergeAttempts++;
            return Task.CompletedTask;
        }

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeletesHeadBranchOnMergeAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class NoOpLock : IAsyncDisposable
    {
        public static readonly NoOpLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Reaches no git: every operation either does nothing or says it should not have been called.</summary>
    private sealed class StubWorktreeManager : IWorktreeManager
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

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion,
            CancellationToken cancellationToken) =>
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
}
