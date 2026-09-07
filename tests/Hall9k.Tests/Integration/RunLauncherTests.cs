using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Daemon.Review;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
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
/// The dispatch path's last look before spawning: a requeued or reopened task whose pull
/// request already merged closes out instead of redispatching (origin incident,
/// 2026-08-18: after PR #11 merged, a lease-expiry requeue spawned generation 6 to
/// rebuild the feature that was already on main).
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class RunLauncherTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/11";

    private const string ParentPullRequestUrl = "https://github.com/x/y/pull/7";

    private sealed class MergedInspector : IPullRequestInspector
    {
        public int Inspections { get; private set; }

        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
        {
            Inspections++;
            return Task.FromResult(new PullRequestSnapshot(
                IsMerged: true, IsClosed: false, MergedAt: Now.AddMinutes(-30), ClosedAt: null,
                FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
                UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
                CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0));
        }

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
        {
            Inspections++;
            return Task.FromResult(new PullRequestStateSnapshot(
                IsMerged: true, IsClosed: false, MergedAt: Now.AddMinutes(-30), ClosedAt: null));
        }

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>The ordinary case: a pull request that is still open, so dispatch proceeds.</summary>
    private sealed class NotMergedInspector : IPullRequestInspector
    {
        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestSnapshot(
                IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
                FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
                UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
                CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0));

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestStateSnapshot(IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null));

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Refuses to prepare a workspace: closing out must never reach the checkout step.</summary>
    private sealed class RefusingWorktreeManager : IWorktreeManager
    {
        public List<string> DeletedBranches { get; } = [];

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A merged pull request must not get a fresh worktree.");

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A merged pull request must not get a follow-up worktree.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A merged pull request must not get a pr-review worktree.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
        {
            DeletedBranches.Add(branch);
            return Task.CompletedTask;
        }

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(
            string checkoutPath, string branch, CancellationToken cancellationToken) =>
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "nothing here is a real repository"));

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

    private sealed class RefusingExecutor : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A merged pull request must not spawn an agent.");
    }

    [Fact]
    public async Task A_requeued_task_whose_pull_request_already_merged_closes_out_instead_of_redispatching()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        // The incident shape: the task completed with a PR, a follow-up was queued, its
        // generation died mid-flight, the lease expired, and the requeue reclaimed the
        // task — while the PR quietly merged.
        Guid taskId = DomainId.New();
        Guid deadRunId = DomainId.New();
        Guid nextRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string branch = "task/merged-already";
        await using (IDocumentSession session = store.LightweightSession())
        {
            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"launcher-{taskId:N}", "/tmp/launcher-repo", null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Already on main", ["merged"], TaskType.Chore,
                    null, null, null, Now.AddHours(-2), node.OwnerId),
                node.OwnerId, Now.AddHours(-2));
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed firstClaim =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, DomainId.New(), Now.AddHours(-2));
            aggregate.Apply(firstClaim);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted completed =
                TaskDecider.Complete(aggregate, aggregate.CurrentRunId!.Value, PullRequestUrl, Now.AddHours(-1));
            aggregate.Apply(completed);
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                aggregate, aggregate.CurrentRunId!.Value, branch,
                "Copilot threads.", FollowUpKind.ReviewFeedback, automatic: true, Now.AddMinutes(-90), node.OwnerId);
            aggregate.Apply(reopened);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed deadClaim =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, deadRunId, Now.AddMinutes(-80));
            aggregate.Apply(deadClaim);
            Hall9k.Domain.Features.Tasks.Events.TaskRequeued requeued =
                TaskDecider.Requeue(aggregate, RequeueReason.LeaseExpired, Now.AddMinutes(-10));
            aggregate.Apply(requeued);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed nextClaim =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, nextRunId, Now);
            aggregate.Apply(nextClaim);
            session.Events.StartStream<TaskAggregate>(taskId,
                [.. lifecycle, firstClaim, completed, reopened, deadClaim, requeued, nextClaim]);
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
            });

            // The dead generation's run stream: its retained worktree path is long gone.
            session.Events.StartStream<RunAggregate>(deadRunId,
                new RunDispatched(deadRunId, taskId, node.NodeId, node.OwnerId, 2, DomainId.New(),
                    $"/tmp/hall9k-gone-{deadRunId:N}", branch, ExecutorMode.Subscription, Now.AddMinutes(-80),
                    IsFollowUp: true));
            await session.SaveChangesAsync(cts.Token);
        }

        MergedInspector inspector = new();
        RefusingWorktreeManager worktrees = new();
        RunLauncher launcher = new(store, worktrees, new RefusingExecutor(),
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, nextRunId, node.NodeId, node.OwnerId, 3, cts.Token);

        inspector.Inspections.Should().Be(1, "the provider is consulted before any workspace or agent work");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Done", "merged work closes out — it is never rebuilt");
        task.PullRequestUrl.Should().Be(PullRequestUrl);

        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("closing out releases the lease");
        worktrees.DeletedBranches.Should().ContainSingle(deleted => deleted == branch,
            "the merged branch is cleaned up like any closeout");

        // nextRunId itself never spawned an agent (RefusingExecutor would have thrown), but its
        // stream is no longer bare: the declined-dispatch path now reconstructs a minimal run
        // record and runs it through the same closeout every merged run gets, instead of
        // stopping at TaskCompleted and leaving nothing to watch it (origin incident, 2026-08-28
        // needs-you cleanup — task 98ac05ef's exact shape).
        RunDetails reconstructed = (await query.LoadAsync<RunDetails>(nextRunId, cts.Token))!;
        reconstructed.State.Should().Be(RunState.Completed, "the reconstructed run reaches true closeout too");
        reconstructed.PullRequestMergedAt.Should().Be(Now.AddMinutes(-30));
    }

    /// <summary>
    /// The sibling of the missing-run sweep's own defect (routed pre-PR review finding fixed
    /// alongside this one, self-review blast-radius sweep): whatever channel put an unsafe
    /// PullRequestUrl on the task stream — a URL naming a different repository than the
    /// project's own — this dispatch-time recheck must never resolve that foreign number inside
    /// the project's own repository the way <c>gh pr view &lt;number&gt;</c> below would. Without
    /// the guard, <see cref="MergedInspector"/>'s own "always merged" answer would wrongly close
    /// this task out on an unrelated pull request instead of dispatching the real follow-up.
    /// </summary>
    [Fact]
    public async Task A_reopened_tasks_foreign_repository_pull_request_never_reaches_the_merge_check()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid previousRunId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        const string branch = "task/foreign-pr";
        const string foreignPullRequestUrl = "https://github.com/other-org/other-repo/pull/24";
        await using (IDocumentSession session = store.LightweightSession())
        {
            var registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"launcher-foreign-{taskId:N}", "/tmp/launcher-foreign-repo",
                new Uri("https://github.com/x/y"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Reopened onto a foreign PR", ["never closes out on the wrong pull request"],
                    TaskType.Chore, null, null, null, Now.AddHours(-1), node.OwnerId),
                node.OwnerId, Now.AddHours(-1));
            var firstClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, previousRunId, Now.AddHours(-1));
            aggregate.Apply(firstClaim);
            // Whatever recorded this — TaskDecider.Complete stands in here for the CLI path this
            // test's own doc comment describes; the point under test is what LaunchAsync does with
            // an unsafe URL already on the stream, not how it got there.
            var completed = TaskDecider.Complete(aggregate, previousRunId, foreignPullRequestUrl, Now.AddMinutes(-40));
            aggregate.Apply(completed);
            var reopened = TaskDecider.Reopen(
                aggregate, previousRunId, branch, "Copilot threads.", FollowUpKind.ReviewFeedback,
                automatic: true, Now.AddMinutes(-30), node.OwnerId);
            aggregate.Apply(reopened);
            var claimed = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
            aggregate.Apply(claimed);
            session.Events.StartStream<TaskAggregate>(
                taskId, [.. lifecycle, firstClaim, completed, reopened, claimed]);
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        StubWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        inspector.Inspections.Should().Be(0,
            "a foreign repository's pull request must never reach gh through the project's own repository path");
        executor.Request.Should().NotBeNull(
            "the guard skips the merge check, so the follow-up dispatches normally instead of wrongly closing out");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "the real dispatch proceeded — this never closed out on the foreign pull request");
        task.PullRequestUrl.Should().Be(foreignPullRequestUrl, "recording it on the task is exactly the pre-existing gap under test");
    }

    /// <summary>
    /// The seed for a follow-up run's own opening Discovery cycle (task: a lap reviews only what it
    /// changed): a ReviewFeedback reopen's own recorded pull request head lands on the follow-up
    /// run's own RunDispatched, forwarded verbatim from TaskAggregate.FollowUpPullRequestHeadSha —
    /// the fact CloseoutEngine observed and TaskDecider.Reopen carried.
    /// </summary>
    [Fact]
    public async Task A_review_feedback_reopens_pull_request_head_seeds_the_follow_up_runs_opening_review_scope()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid previousRunId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        const string branch = "task/opening-seed";
        const string pullRequestUrl = "https://github.com/x/y/pull/9";
        await using (IDocumentSession session = store.LightweightSession())
        {
            var registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"launcher-seed-{taskId:N}", "/tmp/launcher-seed-repo",
                new Uri("https://github.com/x/y"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Seeds the opening review scope", ["carries the head sha"],
                    TaskType.Chore, null, null, null, Now.AddHours(-1), node.OwnerId),
                node.OwnerId, Now.AddHours(-1));
            var firstClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, previousRunId, Now.AddHours(-1));
            aggregate.Apply(firstClaim);
            var completed = TaskDecider.Complete(aggregate, previousRunId, pullRequestUrl, Now.AddMinutes(-40));
            aggregate.Apply(completed);
            var reopened = TaskDecider.Reopen(
                aggregate, previousRunId, branch, "Unresolved review comments.", FollowUpKind.ReviewFeedback,
                automatic: true, Now.AddMinutes(-30), node.OwnerId, pullRequestHeadSha: "cafe1234");
            aggregate.Apply(reopened);
            var claimed = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
            aggregate.Apply(claimed);
            session.Events.StartStream<TaskAggregate>(
                taskId, [.. lifecycle, firstClaim, completed, reopened, claimed]);
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        StubWorktreeManager worktrees = new();
        NotMergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.OpeningReviewSinceSha.Should().Be(
            "cafe1234", "the follow-up's own RunDispatched carries the reopen's recorded pull request head");
    }

    /// <summary>
    /// The origin incident this feature guards against (2026-09-06): an orchestrator retried
    /// five stale rebase follow-ups with <c>h9k task retry --reason "rebase onto origin/main
    /// first"</c>, and the text never reached any dispatched session — RunLauncher's follow-up
    /// prompt never read <see cref="TaskDetails.RetryReason"/> at all, so each retry ran the
    /// plain follow-up template, found nothing to rebase, and failed the same gate again. This
    /// exercises the real path end to end — reopen, fail, retry with a reason, dispatch — and
    /// reads the prompt actually written to disk by <see cref="ClaudeExecutor"/> rather than the
    /// in-memory string <c>AgentPromptBuilderTests</c> already covers.
    /// </summary>
    [Fact]
    public async Task A_retried_follow_up_tasks_operator_reason_reaches_the_dispatched_prompt_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid failedFollowUpRunId = DomainId.New();
        Guid retriedRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string branch = "task/retry-reason-reaches-prompt";
        const string retryReason = "rebase onto origin/main first — main's own gate went red under PR #239";
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-retry-reason-{DomainId.Short(taskId)}");

        try
        {
            TaskAggregate aggregate = new();
            await using (IDocumentSession session = store.LightweightSession())
            {
                var registered = ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), $"retry-reason-{taskId:N}", "/tmp/retry-reason-repo",
                    null, "main", Now, ProjectHome.Parse(home));
                session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

                var added = TaskDecider.Add(
                    taskId, projectId, "Carries a retry reason into a follow-up prompt",
                    ["the dispatched prompt file on disk carries the operator's retry reason"],
                    TaskType.Chore, null, null, null, Now.AddHours(-3), node.OwnerId);
                aggregate.Apply(added);
                var published = TaskDecider.Publish(aggregate, TaskDependencyGraph.Empty, Now.AddHours(-3), node.OwnerId);
                aggregate.Apply(published);
                var assigned = TaskDecider.Assign(aggregate, node.OwnerId, [], Now.AddHours(-3), node.OwnerId);
                aggregate.Apply(assigned);
                var firstClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, firstRunId, Now.AddHours(-3));
                aggregate.Apply(firstClaim);
                var completed = TaskDecider.Complete(aggregate, firstRunId, PullRequestUrl, Now.AddHours(-2));
                aggregate.Apply(completed);
                var reopened = TaskDecider.Reopen(
                    aggregate, firstRunId, branch, "main's own gate went red", FollowUpKind.Rebase,
                    automatic: true, Now.AddHours(-1), node.OwnerId);
                aggregate.Apply(reopened);
                var followUpClaim = TaskDecider.Claim(
                    aggregate, node.NodeId, node.OwnerId, failedFollowUpRunId, Now.AddMinutes(-50));
                aggregate.Apply(followUpClaim);
                var failed = TaskDecider.Fail(aggregate, failedFollowUpRunId, "still conflicts with main", Now.AddMinutes(-40));
                aggregate.Apply(failed);
                var retried = TaskDecider.Retry(
                    aggregate, failedFollowUpRunId, branch, retryReason, Now.AddMinutes(-10), node.OwnerId);
                aggregate.Apply(retried);
                var retryClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, retriedRunId, Now);
                aggregate.Apply(retryClaim);

                session.Events.StartStream<TaskAggregate>(
                    taskId,
                    [added, published, assigned, firstClaim, completed, reopened, followUpClaim, failed, retried, retryClaim]);
                session.Store(new TaskLease
                {
                    Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
                });
                await session.SaveChangesAsync(cts.Token);
            }

            FakeProcessManager processes = new();
            ClaudeExecutor executor = new(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions()));
            StubWorktreeManager worktrees = new();
            NotMergedInspector inspector = new();
            RunLauncher launcher = new(store, worktrees, executor,
                NewSupervisor(store, node), NewContextAssembler(store), inspector,
                NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
                Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

            await launcher.LaunchAsync(
                taskId, retriedRunId, node.NodeId, node.OwnerId, aggregate.LeaseGeneration, cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(retriedRunId, cts.Token))!;
            string prompt = await File.ReadAllTextAsync(RunPaths.PromptFile(run.RunDirectory), cts.Token);

            prompt.Should().Contain("## Operator guidance");
            prompt.Should().Contain(retryReason);
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// The sibling of the test above: an ordinary follow-up dispatch that was never retried
    /// carries no operator-guidance section at all — the section only ever appears with a
    /// recorded reason behind it, never as boilerplate every follow-up prompt always shows.
    /// </summary>
    [Fact]
    public async Task A_follow_up_dispatched_without_a_retry_reason_carries_no_operator_guidance_section()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid followUpRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string branch = "task/no-retry-reason-reaches-prompt";
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-no-retry-reason-{DomainId.Short(taskId)}");

        try
        {
            TaskAggregate aggregate = new();
            await using (IDocumentSession session = store.LightweightSession())
            {
                var registered = ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), $"no-retry-reason-{taskId:N}", "/tmp/no-retry-reason-repo",
                    null, "main", Now, ProjectHome.Parse(home));
                session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

                var added = TaskDecider.Add(
                    taskId, projectId, "Never retried follow-up", ["carries no operator guidance section"],
                    TaskType.Chore, null, null, null, Now.AddHours(-2), node.OwnerId);
                aggregate.Apply(added);
                var published = TaskDecider.Publish(aggregate, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
                aggregate.Apply(published);
                var assigned = TaskDecider.Assign(aggregate, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
                aggregate.Apply(assigned);
                var firstClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, firstRunId, Now.AddHours(-2));
                aggregate.Apply(firstClaim);
                var completed = TaskDecider.Complete(aggregate, firstRunId, PullRequestUrl, Now.AddHours(-1));
                aggregate.Apply(completed);
                var reopened = TaskDecider.Reopen(
                    aggregate, firstRunId, branch, "main's own gate went red", FollowUpKind.Rebase,
                    automatic: true, Now.AddMinutes(-30), node.OwnerId);
                aggregate.Apply(reopened);
                var followUpClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, followUpRunId, Now);
                aggregate.Apply(followUpClaim);

                session.Events.StartStream<TaskAggregate>(
                    taskId, [added, published, assigned, firstClaim, completed, reopened, followUpClaim]);
                session.Store(new TaskLease
                {
                    Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
                });
                await session.SaveChangesAsync(cts.Token);
            }

            FakeProcessManager processes = new();
            ClaudeExecutor executor = new(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions()));
            StubWorktreeManager worktrees = new();
            NotMergedInspector inspector = new();
            RunLauncher launcher = new(store, worktrees, executor,
                NewSupervisor(store, node), NewContextAssembler(store), inspector,
                NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
                Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

            await launcher.LaunchAsync(
                taskId, followUpRunId, node.NodeId, node.OwnerId, aggregate.LeaseGeneration, cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails run = (await query.LoadAsync<RunDetails>(followUpRunId, cts.Token))!;
            string prompt = await File.ReadAllTextAsync(RunPaths.PromptFile(run.RunDirectory), cts.Token);

            prompt.Should().NotContain("## Operator guidance", "this follow-up was never retried");
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// The generation fence (backlog 39): a launch dispatched under a generation the task
    /// has already moved past — the shape a catch-up double-booking or a claim-then-
    /// requeue-then-reclaim race leaves behind — must not close the task out from under
    /// the live generation, even though its pull request really did merge. Checked before
    /// any merged-PR inspection, worktree checkout, or spawn now (Copilot review, PR #30):
    /// the fence used to be reachable only from inside the merged-PR branch, so a stale
    /// launch with no PR yet, or an unmerged one, fell through to
    /// CheckoutFreshOrRetryAsync and spawned a second live agent for the task.
    /// </summary>
    [Fact]
    public async Task A_launch_under_a_stale_generation_does_not_close_out_the_live_generations_task()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid staleRunId = DomainId.New();
        Guid liveRunId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"launcher-fence-{taskId:N}", "/tmp/launcher-fence-repo",
                null, "main", Now);
            session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);

            // Generation 1 (staleRunId) already completed and was reopened for a follow-up;
            // generation 2 (liveRunId) is the live claim. A launch for generation 1 arriving
            // late — the double-booking shape — must not act under generation 2's name.
            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Stale generation launch", ["never closes out as generation 1"],
                    TaskType.Chore, null, null, null, Now.AddHours(-1), node.OwnerId),
                node.OwnerId, Now.AddHours(-1));
            var staleClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, staleRunId, Now.AddMinutes(-30));
            aggregate.Apply(staleClaim);
            var completed = TaskDecider.Complete(aggregate, staleRunId, PullRequestUrl, Now.AddMinutes(-20));
            aggregate.Apply(completed);
            var reopened = TaskDecider.Reopen(
                aggregate, staleRunId, "task/stale-launch", "Copilot threads.", FollowUpKind.ReviewFeedback,
                automatic: true, Now.AddMinutes(-15), node.OwnerId);
            aggregate.Apply(reopened);
            var liveClaim = TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, liveRunId, Now);
            aggregate.Apply(liveClaim);
            session.Events.StartStream<TaskAggregate>(
                taskId, [.. lifecycle, staleClaim, completed, reopened, liveClaim]);
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        ListLogger<RunLauncher> logger = new();
        MergedInspector inspector = new();
        RefusingWorktreeManager worktrees = new();
        RunLauncher launcher = new(store, worktrees, new RefusingExecutor(),
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), logger);

        // staleRunId, at its own generation (1) — while the task has already moved on to
        // generation 2 under liveRunId.
        await launcher.LaunchAsync(taskId, staleRunId, node.NodeId, node.OwnerId, 1, cts.Token);

        inspector.Inspections.Should().Be(0,
            "the fence now runs before the merged-PR check, so a stale generation's launch never reaches the inspector");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "the live generation's claim survives the stale generation's launch");
        task.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale generation's launch must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));

        // The fenced write is only half the guarantee: a stale generation must never fall
        // through to CheckoutFreshOrRetryAsync either, merged PR or not. If it did,
        // RefusingWorktreeManager/RefusingExecutor would throw and LaunchAsync's catch-all
        // would swallow it — so the real guard is that dispatch was never attempted in the
        // first place, not merely that the exception went unobserved.
        logger.Lines.Should().NotContain(line => line.Contains("Launch failed for run"));
        (await query.Events.FetchStreamStateAsync(staleRunId, cts.Token)).Should().BeNull(
            "a stale generation's launch must never start a run stream, dispatched or failed");
    }

    /// <summary>Prepares a workspace without touching git; the launcher only needs a path and a branch.</summary>
    private sealed class StubWorktreeManager : IWorktreeManager
    {
        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Worktree(
                Path.Combine(Path.GetTempPath(), $"hall9k-wt-{request.RunId:N}"), "task/model-policy", request.BaseBranch));

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Worktree(
                Path.Combine(Path.GetTempPath(), $"hall9k-wt-{request.RunId:N}"), request.Branch, request.Branch));

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

    /// <summary>
    /// The model a run is spawned on and the model its dispatch records are one fact
    /// (Decisions Log #33): resolved once through the chain, handed to the executor, and
    /// written to the stream, so a later question about spend has an answer instead of a
    /// guess. Origin incident (2026-08-20): runs drifted from Fable 5 to Opus 5 1M when the
    /// owner changed a personal setting, and nothing on the platform recorded that it happened.
    /// </summary>
    [Fact]
    public async Task A_dispatched_run_spawns_on_the_resolved_model_and_records_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"model-{taskId:N}", "/tmp/model-repo", null, "main", Now);
            ProjectAggregate project = new();
            project.Apply(registered);

            // The project asks for sonnet; the task overrides it, because the task is the
            // most specific level of the chain.
            ProjectSettingsChanged chose = ProjectDecider.ChangeSettings(
                project,
                Optional<IReadOnlyList<VerifyCommand>>.None,
                Optional<bool>.None,
                Optional<IReadOnlyList<ContextLink>>.None,
                Now, node.OwnerId,
                model: Optional<AgentModel>.Of(AgentModel.Sonnet));
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered, chose);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Record what I ran on", ["the run says so"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId, model: "claude-opus-5[1m]"),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        StubWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        executor.Request!.Model.Value.Should().Be(
            "claude-opus-5[1m]", "the task override is the most specific level of the chain");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.Model.Value.Should().Be("claude-opus-5[1m]", "the run records what it was actually dispatched on");
        (await query.LoadAsync<RunListItem>(runId, cts.Token))!.Model.Value.Should().Be("claude-opus-5[1m]");
    }

    /// <summary>
    /// A stacked child's dispatch (task: a stacked pull-request edge exists as an explicit opt-in
    /// dependency): the branch is cut from the PARENT's branch head rather than from the project's
    /// base, and that base is recorded once on <c>RunDispatched</c> so the pull request's own
    /// <c>--base</c>, the review packet's diff range, and the pre-final-pass rebase all read the
    /// same branch. Recording it wrongly here is the one-directional hazard: everything downstream
    /// trusts this value, so a child cut from main would open a pull request whose diff contains
    /// the parent's already-reviewed work.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_is_cut_from_its_parents_branch_and_records_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid parentTaskId = DomainId.New();
        Guid parentRunId = DomainId.New();
        Guid childTaskId = DomainId.New();
        Guid childRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string parentBranch = "task/parent-slice-one";
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"stacked-launch-{childTaskId:N}",
                "/tmp/stacked-launch-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            // The parent, Delivered: its run carries the branch a stacked child builds on, which is
            // where the launcher reads it from — the branch name lives on the run, never the task.
            (TaskAggregate parent, object[] parentLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    parentTaskId, projectId, "Parent slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed parentClaimed =
                TaskDecider.Claim(parent, node.NodeId, node.OwnerId, parentRunId, Now);
            parent.Apply(parentClaimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted parentCompleted =
                TaskDecider.Complete(parent, parentRunId, "https://github.com/x/y/pull/7", Now);
            session.Events.StartStream<TaskAggregate>(
                parentTaskId, [.. parentLifecycle, parentClaimed, parentCompleted]);
            session.Events.StartStream<RunAggregate>(parentRunId,
                new RunDispatched(parentRunId, parentTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/parent-wt", parentBranch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(parentRunId, Now),
                new VerificationPassed(parentRunId, Now),
                new PullRequestOpened(parentRunId, "https://github.com/x/y/pull/7", 7, Now));

            // The child, declared stacked on it and claimed — which the parent's Delivered allows.
            TaskDependencyGraph graph = new([
                new TaskDependency(
                    parentTaskId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.AwaitingReview,
                    "https://github.com/x/y/pull/7", TaskType.Feature, []),
            ]);
            (TaskAggregate child, object[] childLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    childTaskId, projectId, "Child slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId,
                    blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId),
                node.OwnerId, Now, graph);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed childClaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, childRunId, Now);
            session.Events.StartStream<TaskAggregate>(childTaskId, [.. childLifecycle, childClaimed]);
            session.Store(new TaskLease
            {
                Id = childTaskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(childTaskId, childRunId, node.NodeId, node.OwnerId, 1, cts.Token);

        worktrees.CreateRequests.Should().ContainSingle().Which.BaseBranch.Should().Be(
            parentBranch, "the child's branch is cut from the parent's branch head, not from main");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(childRunId, cts.Token))!;
        run.BaseBranch.Should().Be(parentBranch);
        run.StackedOnBranch.Should().Be(parentBranch);
        run.BaseBranchOr("main").Should().Be(parentBranch, "the pull request opens against the parent's branch");

        executor.Request!.Prompt.Should().Contain($"git diff origin/{parentBranch}...HEAD",
            "the session's own self-review hunt reads this branch's delta against the parent");
    }

    /// <summary>
    /// The same dispatch for a child stacked on a pull request another install owns (task: a
    /// stacked child can stand on a pull request another install owns). Everything downstream is
    /// the same machinery — the cut, the recorded base the pull request's own <c>--base</c> reads,
    /// the diff range the review packet scopes to — reached from a recorded observation instead of
    /// from a parent run this install does not have. Two things make it worth its own test rather
    /// than trusting the local one: there is no parent task in the store at all here, and the base
    /// comes off <c>RemoteStackedParentHeadBranch</c>, which nothing in the local path reads.
    /// </summary>
    [Fact]
    public async Task A_child_stacked_on_a_remote_pull_request_is_cut_from_its_head_branch_and_records_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid childTaskId = DomainId.New();
        Guid childRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const int parentNumber = 264;
        const string parentHeadBranch = "feature/teammate-slice";
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"remote-stacked-launch-{childTaskId:N}",
                "/tmp/remote-stacked-launch-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            // No parent task, no parent run — a reviewer's node holds neither. The child's whole
            // knowledge of its parent is the observation the closeout watcher's sweep recorded.
            (TaskAggregate child, object[] childLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    childTaskId, projectId, "Playwright coverage for the teammate's slice", ["it works"],
                    TaskType.Feature, null, null, null, Now, node.OwnerId,
                    stackedOnPullRequestNumber: parentNumber),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.RemoteStackedParentObserved observed = new(
                childTaskId, parentNumber, RemoteParentState.Open, parentHeadBranch, "abc1234", "main",
                $"https://github.com/x/y/pull/{parentNumber}", null, "open", Now);
            child.Apply(observed);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed childClaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, childRunId, Now);
            session.Events.StartStream<TaskAggregate>(
                childTaskId, [.. childLifecycle, observed, childClaimed]);
            session.Store(new TaskLease
            {
                Id = childTaskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(childTaskId, childRunId, node.NodeId, node.OwnerId, 1, cts.Token);

        worktrees.CreateRequests.Should().ContainSingle().Which.BaseBranch.Should().Be(
            parentHeadBranch,
            "the cut starts from origin's copy of the pull request's head branch, not from main");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(childRunId, cts.Token))!;
        run.BaseBranch.Should().Be(parentHeadBranch);
        run.StackedOnBranch.Should().Be(parentHeadBranch);
        run.BaseBranchOr("main").Should().Be(parentHeadBranch,
            "the child's own pull request opens against the parent's head branch");

        executor.Request!.Prompt.Should().Contain($"git diff origin/{parentHeadBranch}...HEAD",
            "the review packet and the session's own hunt read this branch's delta against the parent");
    }

    /// <summary>
    /// The unstacked half of the same fact: an ordinary task records a BLANK base branch, which is
    /// what <c>RunDetails.BaseBranch</c> means by "the project's own" — the invariant that lets the
    /// CLI's composers tell a stacked run from an ordinary one with no project in hand.
    /// </summary>
    [Fact]
    public async Task An_unstacked_dispatch_records_no_base_branch_of_its_own()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"unstacked-launch-{taskId:N}",
                "/tmp/unstacked-launch-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Ordinary work", ["it works"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        worktrees.CreateRequests.Should().ContainSingle().Which.BaseBranch.Should().Be("main");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.BaseBranch.Should().BeEmpty("blank means the project's own base — nothing to back-fill, ever");
        run.StackedOnBranch.Should().BeNull();
        run.BaseBranchOr("main").Should().Be("main");
    }

    /// <summary>
    /// <see cref="StubWorktreeManager"/> with the requests kept, so a test can assert what the
    /// launcher actually asked to cut from rather than only what the run stream records afterwards.
    /// </summary>
    private sealed class RequestCapturingWorktreeManager : IWorktreeManager
    {
        public List<WorktreeRequest> CreateRequests { get; } = [];

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken)
        {
            CreateRequests.Add(request);
            return Task.FromResult(new Worktree(
                Path.Combine(Path.GetTempPath(), $"hall9k-wt-{request.RunId:N}"),
                "task/child-slice-two", request.BaseBranch));
        }

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Worktree(
                Path.Combine(Path.GetTempPath(), $"hall9k-wt-{request.RunId:N}"), request.Branch, request.Branch));

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
            Task.FromResult(new CheckoutRefresh(UpToDate: true, "nothing here is a real repository"));

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(NoOpLock.Instance);
    }

    /// <summary>
    /// A follow-up that resumes a stacked child's branch carries the fork point forward. Resuming a
    /// branch does not move where it forked from, and nothing can re-derive that point later — so a
    /// follow-up that blanked it would leave the NEXT replay with no observable boundary, and the
    /// child would silently stop being replayed at all.
    /// <para>
    /// Pinned because the first cut of this did exactly that: it read <c>task.CurrentRunId</c> for
    /// "the previous run", which by launch time already names the run being launched — whose stream
    /// does not exist yet — so every follow-up recorded a blank fork point and nothing failed.
    /// Found in round two of this task's own self-review.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_follow_up_on_a_stacked_child_carries_the_recorded_fork_point_forward()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid followUpRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string parentBranch = "task/parent-slice-one";
        const string childBranch = "task/child-slice-two";
        const string forkPoint = "abc1234def5678";
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"carry-forward-{taskId:N}",
                "/tmp/carry-forward-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Child slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, firstRunId, Now);
            aggregate.Apply(claimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted completed =
                TaskDecider.Complete(aggregate, firstRunId, PullRequestUrl, Now);
            aggregate.Apply(completed);

            // The review-feedback reopen: an ordinary follow-up, carrying no replay commits of its
            // own, which is exactly the shape that has to inherit the fork point rather than
            // re-observe it.
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                aggregate, firstRunId, childBranch, "review feedback", FollowUpKind.ReviewFeedback,
                automatic: true, Now, node.OwnerId);
            aggregate.Apply(reopened);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed reclaimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, followUpRunId, Now);

            session.Events.StartStream<TaskAggregate>(
                taskId, [.. lifecycle, claimed, completed, reopened, reclaimed]);

            // The first run recorded the fork point its cut observed.
            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(firstRunId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/child-wt", childBranch, ExecutorMode.Subscription, Now,
                    BaseBranch: parentBranch, BaseCommit: forkPoint),
                new AgentSessionCompleted(firstRunId, Now),
                new VerificationPassed(firstRunId, Now),
                new PullRequestOpened(firstRunId, PullRequestUrl, 11, Now),
                new RunSuperseded(firstRunId, 2, Now));

            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        NotMergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), ForkPointContainmentRunner(contained: true),
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, followUpRunId, node.NodeId, node.OwnerId, 2, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails followUp = (await query.LoadAsync<RunDetails>(followUpRunId, cts.Token))!;
        followUp.BaseCommit.Should().Be(forkPoint,
            "resuming a branch does not move its fork point, and nothing can re-derive it later — "
            + "a blank here would leave the next replay with no observable boundary");
        worktrees.CreateRequests.Should().BeEmpty("a follow-up resumes the branch rather than cutting one");
    }

    /// <summary>
    /// The other half of that fact, and the harder one (adversarial review, cycle 6): a recorded
    /// fork point is carried forward only while the branch actually SITS on it. A stacked replay
    /// records <c>RunDispatched.BaseCommit</c> as the commit it was dispatched to land on — a
    /// prediction, written before the session rebases anything — and its own prompt sanctions
    /// `git rebase --abort` on a conflict it cannot honestly resolve, which leaves the branch where
    /// it was with that prediction on the record. Re-asserting it here would hand the next rebase
    /// session `git rebase --onto origin/&lt;parent&gt; &lt;a commit this branch never landed on&gt;`,
    /// whose replay range still holds the parent's own commits, and scope a review lap's diff from
    /// the same place. Blank is the honest record: the prompts already dispute an unobserved
    /// boundary rather than guessing one.
    /// </summary>
    [Fact]
    public async Task A_follow_up_on_a_stacked_child_blanks_a_fork_point_its_branch_never_landed_on()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid replayRunId = DomainId.New();
        Guid followUpRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string parentBranch = "task/parent-slice-one";
        const string childBranch = "task/child-slice-two";
        const string neverLanded = "9999888877776666555544443333222211110000";
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"aborted-replay-{taskId:N}",
                "/tmp/aborted-replay-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Child slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, replayRunId, Now);
            aggregate.Apply(claimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted completed =
                TaskDecider.Complete(aggregate, replayRunId, PullRequestUrl, Now);
            aggregate.Apply(completed);

            // The lap after the replay: GitHub reports the child conflicting, so closeout dispatches
            // an ordinary judgment rebase rather than another replay — the dispatch that would
            // otherwise be told to replay from a commit the aborted rebase never landed on.
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                aggregate, replayRunId, childBranch, "the pull request conflicts with its base branch",
                FollowUpKind.Rebase, automatic: true, Now, node.OwnerId);
            aggregate.Apply(reopened);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed reclaimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, followUpRunId, Now);

            session.Events.StartStream<TaskAggregate>(
                taskId, [.. lifecycle, claimed, completed, reopened, reclaimed]);

            // The replay run's own record: the commit it was TOLD to land on, not one it reached.
            session.Events.StartStream<RunAggregate>(replayRunId,
                new RunDispatched(replayRunId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/child-wt", childBranch, ExecutorMode.Subscription, Now,
                    BaseBranch: parentBranch, BaseCommit: neverLanded),
                new AgentSessionCompleted(replayRunId, Now),
                new VerificationPassed(replayRunId, Now),
                new PullRequestOpened(replayRunId, PullRequestUrl, 14, Now),
                new RunSuperseded(replayRunId, 2, Now));

            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        NotMergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), ForkPointContainmentRunner(contained: false),
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, followUpRunId, node.NodeId, node.OwnerId, 2, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails followUp = (await query.LoadAsync<RunDetails>(followUpRunId, cts.Token))!;
        followUp.BaseCommit.Should().BeEmpty(
            "the branch does not contain the recorded commit, so this run records no fork point rather "
            + "than re-asserting one the branch never landed on");
        followUp.BaseBranch.Should().Be(parentBranch,
            "the base is a fact about the branch and is untouched by this — the retarget is still owed");
        followUp.StackedForkPoint("main").Should().BeNull(
            "which is what keeps every consumer of the record off a boundary nothing observed");

        string prompt = executor.Request!.Prompt;
        prompt.Should().NotContain(neverLanded,
            "no instruction may name a commit this branch never landed on");
        prompt.Should().Contain("do not rebase this branch",
            "with no observed boundary the stacked rebase prompt disputes instead of guessing one");
    }

    /// <summary>
    /// A stacked child's conflict follow-up is told to REPLAY, not to rebase (independent pre-PR
    /// review, cycle 2, adversarial lens). Closeout reaches this dispatch when it could not observe
    /// the parent that sweep (<c>StackedParentVerdict.Unobservable</c>) and GitHub reports the child
    /// CONFLICTING anyway: the mechanical rebase refuses a base that is not the project's own, so a
    /// <c>FollowUpKind.Rebase</c> judgment session is dispatched — and a plain
    /// <c>git rebase origin/&lt;parent&gt;</c> there is the same operation the pre-final-pass gate
    /// refuses outright, since a force-pushed parent collapses the merge base below this branch's
    /// own fork point. This is the whole path, launcher through prompt, rather than the builder
    /// alone: the fork point has to survive the follow-up's own dispatch to reach the instruction.
    /// </summary>
    [Fact]
    public async Task A_rebase_follow_up_on_a_stacked_child_is_told_to_replay_from_its_fork_point()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid parentTaskId = DomainId.New();
        Guid parentRunId = DomainId.New();
        Guid childTaskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid followUpRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string parentBranch = "task/parent-slice-one";
        const string childBranch = "task/child-slice-two";
        const string forkPoint = "abc1234def5678";
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"stacked-rebase-{childTaskId:N}",
                "/tmp/stacked-rebase-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            // The parent, still Delivered rather than closed out — which is what keeps the stack
            // edge live, so the follow-up's own base resolves to the parent's branch again.
            (TaskAggregate parent, object[] parentLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    parentTaskId, projectId, "Parent slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed parentClaimed =
                TaskDecider.Claim(parent, node.NodeId, node.OwnerId, parentRunId, Now);
            parent.Apply(parentClaimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted parentCompleted =
                TaskDecider.Complete(parent, parentRunId, "https://github.com/x/y/pull/7", Now);
            session.Events.StartStream<TaskAggregate>(
                parentTaskId, [.. parentLifecycle, parentClaimed, parentCompleted]);
            session.Events.StartStream<RunAggregate>(parentRunId,
                new RunDispatched(parentRunId, parentTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/parent-wt", parentBranch, ExecutorMode.Subscription, Now),
                new AgentSessionCompleted(parentRunId, Now),
                new VerificationPassed(parentRunId, Now),
                new PullRequestOpened(parentRunId, "https://github.com/x/y/pull/7", 7, Now));

            TaskDependencyGraph graph = new([
                new TaskDependency(
                    parentTaskId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.AwaitingReview,
                    "https://github.com/x/y/pull/7", TaskType.Feature, []),
            ]);
            (TaskAggregate child, object[] childLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    childTaskId, projectId, "Child slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId,
                    blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId),
                node.OwnerId, Now, graph);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed childClaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, firstRunId, Now);
            child.Apply(childClaimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted childCompleted =
                TaskDecider.Complete(child, firstRunId, PullRequestUrl, Now);
            child.Apply(childCompleted);
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                child, firstRunId, childBranch, "the pull request conflicts with its base branch",
                FollowUpKind.Rebase, automatic: true, Now, node.OwnerId);
            child.Apply(reopened);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed reclaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, followUpRunId, Now);
            session.Events.StartStream<TaskAggregate>(
                childTaskId, [.. childLifecycle, childClaimed, childCompleted, reopened, reclaimed]);

            // The child's first run: cut from the parent's branch, at the fork point it observed.
            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(firstRunId, childTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/child-wt", childBranch, ExecutorMode.Subscription, Now,
                    BaseBranch: parentBranch, BaseCommit: forkPoint),
                new AgentSessionCompleted(firstRunId, Now),
                new VerificationPassed(firstRunId, Now),
                new PullRequestOpened(firstRunId, PullRequestUrl, 12, Now),
                new RunSuperseded(firstRunId, 2, Now));

            session.Store(new TaskLease
            {
                Id = childTaskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        NotMergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), ForkPointContainmentRunner(contained: true),
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(childTaskId, followUpRunId, node.NodeId, node.OwnerId, 2, cts.Token);

        string prompt = executor.Request!.Prompt;
        prompt.Should().Contain($"git rebase --onto origin/{parentBranch} {forkPoint} {childBranch}",
            "the replay is keyed to the recorded fork point, which is what keeps the parent's own commits out");
        prompt.Should().NotContain($"`git rebase origin/{parentBranch}`, resolving each conflict",
            "a merge-base rebase onto a force-pushed parent replays this branch's copies of the parent's commits");
        prompt.Should().NotContain($"--autosquash origin/{parentBranch}",
            "and a gate fix folds back to an observed commit, never to the parent's own branch ref");
    }

    /// <summary>
    /// A RETRY that resumes the branch inherits the fork point too (conformance review, cycle 4).
    /// <c>h9k task retry</c> on a stacked child whose run failed resumes <c>task.RetryBranch</c>
    /// through the same <c>CheckoutExistingAsync</c> a follow-up uses, and that path reports no
    /// start point at all — a resumed checkout performs no fresh cut to observe one from. Reading it
    /// there recorded a BLANK fork point over one the failed run had already observed, which leaves
    /// <c>StackedParentWatch</c> permanently unobservable for the rest of the branch's life: no
    /// replay when the parent is force-pushed, and no retarget when it merges.
    /// </summary>
    [Fact]
    public async Task A_retry_that_resumes_a_stacked_childs_branch_carries_the_recorded_fork_point_forward()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid parentTaskId = DomainId.New();
        Guid parentRunId = DomainId.New();
        Guid childTaskId = DomainId.New();
        Guid failedRunId = DomainId.New();
        Guid retriedRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string parentBranch = "task/parent-slice-one";
        const string childBranch = "task/child-slice-two";
        const string forkPoint = "abc1234def5678";
        int leaseGeneration;
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"stacked-retry-{childTaskId:N}",
                "/tmp/stacked-retry-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            SeedDeliveredParent(session, node, projectId, parentTaskId, parentRunId, parentBranch, closedOut: false);

            (TaskAggregate child, object[] childLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    childTaskId, projectId, "Child slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId,
                    blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId),
                node.OwnerId, Now, StackedOnDeliveredParent(parentTaskId));
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed childClaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, failedRunId, Now);
            child.Apply(childClaimed);
            // The run failed with work already committed on the branch, which is exactly why the
            // retry resumes it rather than cutting a fresh one.
            Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
                TaskDecider.Fail(child, failedRunId, "the gates went red", Now);
            child.Apply(failed);
            Hall9k.Domain.Features.Tasks.Events.TaskRetried retried = TaskDecider.Retry(
                child, failedRunId, childBranch, "the gates should pass on a second look", Now, node.OwnerId);
            child.Apply(retried);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed reclaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, retriedRunId, Now);
            child.Apply(reclaimed);
            leaseGeneration = child.LeaseGeneration;
            session.Events.StartStream<TaskAggregate>(
                childTaskId, [.. childLifecycle, childClaimed, failed, retried, reclaimed]);

            // The failed run recorded where its cut actually forked from.
            session.Events.StartStream<RunAggregate>(failedRunId,
                new RunDispatched(failedRunId, childTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/child-wt", childBranch, ExecutorMode.Subscription, Now,
                    BaseBranch: parentBranch, BaseCommit: forkPoint),
                new RunFailed(failedRunId, "the gates went red", Now));

            session.Store(new TaskLease
            {
                Id = childTaskId, NodeId = node.NodeId, LeaseGeneration = leaseGeneration, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        NotMergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), ForkPointContainmentRunner(contained: true),
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(childTaskId, retriedRunId, node.NodeId, node.OwnerId, leaseGeneration, cts.Token);

        worktrees.CreateRequests.Should().BeEmpty("the retry resumes the branch rather than cutting a fresh one");

        await using IQuerySession query = store.QuerySession();
        RunDetails retriedRun = (await query.LoadAsync<RunDetails>(retriedRunId, cts.Token))!;
        retriedRun.BaseCommit.Should().Be(forkPoint,
            "resuming a branch does not move its fork point, and nothing can re-derive it later");
        retriedRun.BaseBranch.Should().Be(parentBranch, "the branch still sits on the parent's commits");
        executor.Request!.Prompt.Should().Contain($"git diff {forkPoint}...HEAD",
            "and the carried-forward commit is what the resumed session's own hunt reads its delta from");
    }

    /// <summary>
    /// A follow-up that resumes a stacked child's branch inherits the base that branch already sits
    /// on, rather than re-resolving it against the parent's CURRENT state (adversarial review,
    /// cycle 4). The parent closing out between the reopen and this launch is the case that proves
    /// it: the resolver answers "the project's base" — right for a fresh cut, since the parent's
    /// branch is gone — while this branch still physically carries the parent's commits with its
    /// pull request still aimed at the parent's branch. Recording it as unstacked would disarm
    /// <c>StackedParentWatch.IsStackedChild</c>, the merge-bar guard and every stacked prompt
    /// variant for the rest of the branch's life, and the replay actually owed could never dispatch.
    /// </summary>
    [Fact]
    public async Task A_follow_up_on_a_stacked_child_keeps_its_recorded_base_after_the_parent_closed_out()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid parentTaskId = DomainId.New();
        Guid parentRunId = DomainId.New();
        Guid childTaskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid followUpRunId = DomainId.New();
        Guid projectId = DomainId.New();
        const string parentBranch = "task/parent-slice-one";
        const string childBranch = "task/child-slice-two";
        const string forkPoint = "abc1234def5678";
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"stacked-closed-parent-{childTaskId:N}",
                "/tmp/stacked-closed-parent-repo", null, "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            // The parent, merged and closed out — so the resolver's own answer for this child is
            // now the project's base branch.
            SeedDeliveredParent(session, node, projectId, parentTaskId, parentRunId, parentBranch, closedOut: true);

            (TaskAggregate child, object[] childLifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    childTaskId, projectId, "Child slice", ["it works"], TaskType.Feature,
                    null, null, null, Now, node.OwnerId,
                    blockedBy: [parentTaskId], stackedOnTaskId: parentTaskId),
                node.OwnerId, Now, StackedOnDeliveredParent(parentTaskId));
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed childClaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, firstRunId, Now);
            child.Apply(childClaimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted childCompleted =
                TaskDecider.Complete(child, firstRunId, PullRequestUrl, Now);
            child.Apply(childCompleted);
            // A human granting another attempt (h9k pr resolve) reopens with an ordinary kind —
            // never StackReplay — so nothing here re-derives the stacked base for the launcher.
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                child, firstRunId, childBranch, "another attempt granted by hand",
                FollowUpKind.ReviewFeedback, automatic: false, Now, node.OwnerId);
            child.Apply(reopened);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed reclaimed =
                TaskDecider.Claim(child, node.NodeId, node.OwnerId, followUpRunId, Now);
            session.Events.StartStream<TaskAggregate>(
                childTaskId, [.. childLifecycle, childClaimed, childCompleted, reopened, reclaimed]);

            session.Events.StartStream<RunAggregate>(firstRunId,
                new RunDispatched(firstRunId, childTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                    "/tmp/child-wt", childBranch, ExecutorMode.Subscription, Now,
                    BaseBranch: parentBranch, BaseCommit: forkPoint),
                new AgentSessionCompleted(firstRunId, Now),
                new VerificationPassed(firstRunId, Now),
                new PullRequestOpened(firstRunId, PullRequestUrl, 13, Now),
                new RunSuperseded(firstRunId, 2, Now));

            session.Store(new TaskLease
            {
                Id = childTaskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now,
            });
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        RequestCapturingWorktreeManager worktrees = new();
        NotMergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), ForkPointContainmentRunner(contained: true),
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(childTaskId, followUpRunId, node.NodeId, node.OwnerId, 2, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails followUp = (await query.LoadAsync<RunDetails>(followUpRunId, cts.Token))!;
        followUp.BaseBranch.Should().Be(parentBranch,
            "the branch still carries the parent's commits and its pull request is still aimed at the "
            + "parent's branch — the retarget and replay are owed, not spent");
        followUp.AwaitsStackedRetarget("main").Should().BeTrue(
            "so the merge bar still refuses it and the parent watch still watches it");
        followUp.BaseCommit.Should().Be(forkPoint);
    }

    /// <summary>
    /// A parent at the top of the stack: Delivered onto its own pull request, and — when
    /// <paramref name="closedOut"/> — merged and completed, which is the state that makes
    /// <see cref="StackedBaseResolver"/> answer "the project's base" for its children.
    /// </summary>
    private static void SeedDeliveredParent(
        IDocumentSession session, NodeContext node, Guid projectId, Guid parentTaskId, Guid parentRunId,
        string parentBranch, bool closedOut)
    {
        (TaskAggregate parent, object[] parentLifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                parentTaskId, projectId, "Parent slice", ["it works"], TaskType.Feature,
                null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        Hall9k.Domain.Features.Tasks.Events.TaskClaimed parentClaimed =
            TaskDecider.Claim(parent, node.NodeId, node.OwnerId, parentRunId, Now);
        parent.Apply(parentClaimed);
        Hall9k.Domain.Features.Tasks.Events.TaskCompleted parentCompleted =
            TaskDecider.Complete(parent, parentRunId, ParentPullRequestUrl, Now);
        session.Events.StartStream<TaskAggregate>(
            parentTaskId, [.. parentLifecycle, parentClaimed, parentCompleted]);

        List<object> parentRun =
        [
            new RunDispatched(parentRunId, parentTaskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
                "/tmp/parent-wt", parentBranch, ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(parentRunId, Now),
            new VerificationPassed(parentRunId, Now),
            new PullRequestOpened(parentRunId, ParentPullRequestUrl, 7, Now),
        ];
        if (closedOut)
        {
            parentRun.Add(new PullRequestMerged(parentRunId, Now, Now));
            parentRun.Add(new RunCompleted(parentRunId, Now));
        }

        session.Events.StartStream<RunAggregate>(parentRunId, [.. parentRun]);
    }

    /// <summary>The dependency graph a child declared stacked on a Delivered parent is published against.</summary>
    private static TaskDependencyGraph StackedOnDeliveredParent(Guid parentTaskId) => new([
        new TaskDependency(
            parentTaskId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.AwaitingReview,
            ParentPullRequestUrl, TaskType.Feature, []),
    ]);

    /// <summary>
    /// The pr-review dispatch branch itself (cycle-1 conformance finding, `PrReviewEngine.cs:50`
    /// — before this, LaunchAsync's own isPrReview branch had no coverage at all): a fresh read
    /// of the open pull request resolves the base branch and the checkout, the run's primary
    /// session is the adversarial lens rather than a build session, it resolves the Review role's
    /// model rather than Build's, and — the security fix this same cycle added — the spawn is
    /// marked as an untrusted working directory so the checkout's own settings and MCP config
    /// never load (`RunLauncher.cs:228`).
    /// </summary>
    [Fact]
    public async Task A_pr_review_task_dispatches_the_adversarial_lens_into_an_untrusted_checkout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"pr-review-launch-{taskId:N}", "/tmp/pr-review-launch-repo",
                new Uri("https://github.com/acme/web"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#42", ["every finding names a file and line"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#42"), Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        const string pullRequestJson = """
            {
              "number": 42,
              "title": "Add rate limiting to auth endpoints",
              "body": "Fixes an incident.",
              "state": "OPEN",
              "url": "https://github.com/acme/web/pull/42",
              "baseRefName": "release/2.0"
            }
            """;
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(pullRequestJson);
        CapturingExecutor executor = new();
        StubWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), gh.Runner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        executor.Request.Should().NotBeNull("the fresh gh read found the pull request open, so dispatch must proceed");
        executor.Request!.UntrustedWorkingDirectory.Should().BeTrue(
            "the checkout is another contributor's pull-request head, never this platform's own worktree");
        executor.Request!.Prompt.Should().Contain("read-only, detached checkout",
            "the primary session is the adversarial lens's own pr-review prompt");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.Branch.Should().Be("pr/42", "the checkout's own branch name records which pull request this run reviewed");
        run.Model.Should().Be(new DaemonOptions().ResolveModel(AgentRole.Review, null, null),
            "a pr-review run's primary session resolves the Review role, never Build");
    }

    /// <summary>
    /// TaskDecider.Add/Revise refuse a task-level review-stage-composition override on a pr-review
    /// task, but that refusal cannot reach a project- or node-level one — neither is task-type-aware
    /// — so without this, a project set to a reduced composition would still resolve one onto a
    /// pr-review task's own RunDispatched (class sweep, independent pre-PR review, cycle 1,
    /// adversarial lens's PrReviewEngine finding: the same "h9k task show states a pipeline shape
    /// the run never honors" defect, reached through the project level instead of the task level).
    /// PrReviewEngine's primary session is always the adversarial lens and its
    /// DispatchConformanceAsync always dispatches the conformance lens second, unconditionally, so
    /// FullPipeline is the only value ever actually true here regardless of what the project set.
    /// </summary>
    [Fact]
    public async Task A_pr_review_task_always_records_full_pipeline_regardless_of_the_projects_own_reduced_composition()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"pr-review-composition-{taskId:N}",
                "/tmp/pr-review-composition-repo", new Uri("https://github.com/acme/web"), "main", Now);
            ProjectAggregate project = new();
            project.Apply(registered);

            // The project drops both lenses entirely — a setting no task-level refusal on this
            // pr-review task can see or override.
            ProjectSettingsChanged reduced = ProjectDecider.ChangeSettings(
                project,
                Optional<IReadOnlyList<VerifyCommand>>.None,
                Optional<bool>.None,
                Optional<IReadOnlyList<ContextLink>>.None,
                Now, node.OwnerId,
                reviewStageComposition: Optional<string?>.Of("none"), reviewStageCompositionAcknowledged: true);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered, reduced);

            (TaskAggregate aggregate, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#43", ["every finding names a file and line"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#43"), Now, node.OwnerId),
                node.OwnerId, Now);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        const string pullRequestJson = """
            {
              "number": 43,
              "title": "Add rate limiting to auth endpoints",
              "body": "Fixes an incident.",
              "state": "OPEN",
              "url": "https://github.com/acme/web/pull/43",
              "baseRefName": "release/2.0"
            }
            """;
        RecordingProcessRunner gh = RecordingProcessRunner.Succeeding(pullRequestJson);
        CapturingExecutor executor = new();
        StubWorktreeManager worktrees = new();
        MergedInspector inspector = new();
        RunLauncher launcher = new(store, worktrees, executor,
            NewSupervisor(store, node), NewContextAssembler(store), inspector,
            NewCloseoutEngine(store, node, inspector, worktrees), gh.Runner,
            Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunListItem run = (await query.LoadAsync<RunListItem>(runId, cts.Token))!;
        run.ReviewStageComposition.Should().Be(ReviewStageComposition.FullPipeline,
            "both lenses always dispatch on a pr-review run, so this is the only value that is ever "
            + "actually true here — never the project's own None");
    }

    /// <summary>
    /// The doorbell-woken render sweep, not dispatch, owns renaming a task's on-disk directory
    /// when a revision changes its slug — and it runs on its own schedule, never synchronously
    /// with an assign (adversarial review, backlog 49 cycle 1). A run dispatched between a
    /// revision and the sweep catching up must not invent the not-yet-renamed directory itself:
    /// doing so would create a fresh, empty directory under the new name while the task's real,
    /// already-populated one sat under its old name — an orphan the next reconciliation pass
    /// only marks, never merges, undermining "the task directory is the whole story."
    /// </summary>
    [Fact]
    public async Task A_run_dispatched_ahead_of_the_render_sweep_lands_under_the_tasks_existing_directory()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-race-{DomainId.Short(taskId)}");
        string oldDirectoryName = ProjectHomePaths.EntryDirectoryName(taskId, "Old objective text");
        string newDirectoryName = ProjectHomePaths.EntryDirectoryName(taskId, "New objective text");
        newDirectoryName.Should().NotBe(oldDirectoryName, "the revision below must actually change the slug");

        try
        {
            // The sweep's own prior render, before the revision below runs — the task's real
            // directory, exactly as HomeEntryWriter always leaves one.
            HomeEntryWriter.Write(
                ProjectHomePaths.TasksDirectory(home), taskId, oldDirectoryName, "task.md", "old contract");

            await using (IDocumentSession session = store.LightweightSession())
            {
                var registered = ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), $"race-{taskId:N}", "/tmp/race-repo",
                    null, "main", Now, ProjectHome.Parse(home));
                session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

                TaskAggregate aggregate = new();
                Hall9k.Domain.Features.Tasks.Events.TaskAdded added = TaskDecider.Add(
                    taskId, projectId, "Old objective text", ["criteria"], TaskType.Chore,
                    null, null, null, Now.AddHours(-1), node.OwnerId);
                aggregate.Apply(added);

                // The revision the render sweep has not caught up to yet when Assign below
                // dispatches — the sweep's own doorbell wakeup has not run in this test at all.
                Hall9k.Domain.Features.Tasks.Events.TaskRevised revised = TaskDecider.Revise(
                    aggregate, Optional<string>.Of("New objective text"), Optional<IReadOnlyList<string>>.None,
                    Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
                    Optional<AgentModel>.None, Now.AddMinutes(-50), node.OwnerId);
                aggregate.Apply(revised);

                Hall9k.Domain.Features.Tasks.Events.TaskPublished published =
                    TaskDecider.Publish(aggregate, TaskDependencyGraph.Empty, Now.AddMinutes(-40), node.OwnerId);
                aggregate.Apply(published);

                Hall9k.Domain.Features.Tasks.Events.TaskAssigned assigned =
                    TaskDecider.Assign(aggregate, node.OwnerId, [], Now.AddMinutes(-30), node.OwnerId);
                aggregate.Apply(assigned);

                Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                    TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, runId, Now);
                aggregate.Apply(claimed);

                session.Events.StartStream<TaskAggregate>(taskId, [added, revised, published, assigned, claimed]);
                session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });
                await session.SaveChangesAsync(cts.Token);
            }

            CapturingExecutor executor = new();
            StubWorktreeManager worktrees = new();
            MergedInspector inspector = new();
            RunLauncher launcher = new(store, worktrees, executor,
                NewSupervisor(store, node), NewContextAssembler(store), inspector,
                NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
                Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

            await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

            string expectedDirectory = Path.Combine(
                ProjectHomePaths.TasksDirectory(home), oldDirectoryName, "runs", runId.ToString());
            executor.Request!.RunDirectory.Should().Be(expectedDirectory,
                "the run belongs under whatever directory the task actually has on disk, " +
                "not a name the render sweep has not moved to yet");

            Directory.Exists(Path.Combine(ProjectHomePaths.TasksDirectory(home), newDirectoryName)).Should().BeFalse(
                "dispatch must never invent the not-yet-renamed directory and orphan the real one");
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// The archive half of the same race (backlog 51): a task the render sweep already moved
    /// into tasks/_archive/ on true closeout, then reopened for a follow-up, can still be
    /// sitting there when the follow-up's run launches — the sweep that would move it back to
    /// tasks/ runs on its own doorbell-woken schedule, not synchronously with the reopen. The
    /// follow-up's run directory must land beside the task's real directory wherever it
    /// currently is, not under a tasks/&lt;name&gt;/ path the sweep has not created.
    /// </summary>
    [Fact]
    public async Task A_reopened_task_still_sitting_in_the_archive_directory_redispatches_beside_its_real_directory()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid firstRunId = DomainId.New();
        Guid followUpRunId = DomainId.New();
        Guid projectId = DomainId.New();
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-archive-race-{DomainId.Short(taskId)}");
        string directoryName = ProjectHomePaths.EntryDirectoryName(taskId, "Task closed out then reopened");
        const string branch = "task/archive-race";

        try
        {
            // The render sweep already moved this task's directory into tasks/_archive/ on a
            // prior sweep, before the reopen below — exactly what a true-closeout task gets.
            HomeEntryWriter.Write(
                ProjectHomePaths.ArchivedTasksDirectory(home), taskId, directoryName, "task.md", "closed out");

            TaskAggregate aggregate = new();
            await using (IDocumentSession session = store.LightweightSession())
            {
                var registered = ProjectDecider.Register(
                    projectId, node.OwnerId, DomainId.New(), $"archive-race-{taskId:N}", "/tmp/archive-race-repo",
                    null, "main", Now, ProjectHome.Parse(home));
                session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

                Hall9k.Domain.Features.Tasks.Events.TaskAdded added = TaskDecider.Add(
                    taskId, projectId, "Task closed out then reopened", ["criteria"], TaskType.Chore,
                    null, null, null, Now.AddHours(-2), node.OwnerId);
                aggregate.Apply(added);
                Hall9k.Domain.Features.Tasks.Events.TaskPublished published =
                    TaskDecider.Publish(aggregate, TaskDependencyGraph.Empty, Now.AddHours(-2), node.OwnerId);
                aggregate.Apply(published);
                Hall9k.Domain.Features.Tasks.Events.TaskAssigned assigned =
                    TaskDecider.Assign(aggregate, node.OwnerId, [], Now.AddHours(-2), node.OwnerId);
                aggregate.Apply(assigned);
                Hall9k.Domain.Features.Tasks.Events.TaskClaimed firstClaim =
                    TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, firstRunId, Now.AddHours(-2));
                aggregate.Apply(firstClaim);
                Hall9k.Domain.Features.Tasks.Events.TaskCompleted completed =
                    TaskDecider.Complete(aggregate, firstRunId, PullRequestUrl, Now.AddHours(-1));
                aggregate.Apply(completed);
                Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                    aggregate, firstRunId, branch, "one more look", FollowUpKind.ReviewFeedback, automatic: false,
                    Now, node.OwnerId);
                aggregate.Apply(reopened);
                Hall9k.Domain.Features.Tasks.Events.TaskClaimed followUpClaim =
                    TaskDecider.Claim(aggregate, node.NodeId, node.OwnerId, followUpRunId, Now);
                aggregate.Apply(followUpClaim);

                session.Events.StartStream<TaskAggregate>(
                    taskId, [added, published, assigned, firstClaim, completed, reopened, followUpClaim]);
                session.Store(new TaskLease
                {
                    Id = taskId, NodeId = node.NodeId, LeaseGeneration = aggregate.LeaseGeneration, HeartbeatAt = Now,
                });
                await session.SaveChangesAsync(cts.Token);
            }

            CapturingExecutor executor = new();
            StubWorktreeManager worktrees = new();
            NotMergedInspector inspector = new();
            RunLauncher launcher = new(store, worktrees, executor,
                NewSupervisor(store, node), NewContextAssembler(store), inspector,
                NewCloseoutEngine(store, node, inspector, worktrees), UnusedProcessRunner,
                Options.Create(new DaemonOptions()), NullLogger<RunLauncher>.Instance);

            await launcher.LaunchAsync(
                taskId, followUpRunId, node.NodeId, node.OwnerId, aggregate.LeaseGeneration, cts.Token);

            string expectedDirectory = Path.Combine(
                ProjectHomePaths.ArchivedTasksDirectory(home), directoryName, "runs", followUpRunId.ToString());
            executor.Request!.RunDirectory.Should().Be(expectedDirectory,
                "the follow-up belongs beside the task's real directory, still under tasks/_archive/ until " +
                "the render sweep itself moves it back out");
            Directory.Exists(Path.Combine(ProjectHomePaths.TasksDirectory(home), directoryName)).Should().BeFalse(
                "dispatch must never invent a fresh tasks/ directory ahead of the sweep");
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    /// <summary>
    /// Context routing needs no seams here: both tests close out a merged pull request
    /// without reaching a dispatch, and a task with no BlockedBy edges assembles nothing
    /// anyway (Decisions Log #36).
    /// </summary>
    private static BlockerContextAssembler NewContextAssembler(DocumentStore store)
    {
        FakeProcessManager processes = new();
        return new(store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes, Options.Create(new DaemonOptions())), processes,
            Options.Create(new DaemonOptions()), NullLogger<BlockerContextAssembler>.Instance);
    }

    /// <summary>
    /// RunLauncher's declined-dispatch closeout now runs through the same
    /// CloseoutEngine.ReconstructAndCompleteAsync every merged-but-unrecorded run does, so the
    /// launcher needs one to hand it. No test here carries an external reference, so
    /// TellTheCardAsync always returns before touching either seam — these stubs exist only to
    /// satisfy the constructor.
    /// </summary>
    private CloseoutEngine NewCloseoutEngine(
        DocumentStore store, NodeContext node, IPullRequestInspector inspector, IWorktreeManager worktrees) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), inspector, worktrees,
            new StackedParentWatch(worktrees, NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner, FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);

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
            new StackedParentWatch(
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
                NullLogger<StackedParentWatch>.Instance));
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

    /// <summary>
    /// A gh runner these tests must never actually reach: nothing here exercises a pr-review
    /// task, so RunLauncher's own <c>ProcessRunner</c> is wired but always unused — a real
    /// <c>gh</c> invocation would mean either a hidden pr-review path or a test running gh for
    /// real, and this makes either one fail loudly instead of hanging on a live network call.
    /// </summary>
    private static readonly ProcessRunner UnusedProcessRunner =
        RecordingProcessRunner.Failing("this test never reviews a pull request").Runner;

    /// <summary>
    /// The one git question a stacked resume asks: does the branch still contain the fork point the
    /// previous run recorded (adversarial review, cycle 6 — a replay that aborted its rebase leaves
    /// a recorded commit the branch never landed on)? <paramref name="contained"/> is git's own
    /// answer: exit 0 for yes, 1 for no. Everything else still fails loudly, for the reason
    /// <see cref="UnusedProcessRunner"/> gives.
    /// </summary>
    private static ProcessRunner ForkPointContainmentRunner(bool contained) =>
        (fileName, arguments, _, _) => fileName == "git" && arguments.Contains("--is-ancestor")
            ? Task.FromResult(new ProcessResult(contained ? 0 : 1, string.Empty, string.Empty))
            : throw new InvalidOperationException(
                $"this test only answers the fork-point containment check, but '{fileName}' was invoked "
                + $"with {arguments.Count} argument(s)");
}
