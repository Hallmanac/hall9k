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
                Optional<int>.None,
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
                Optional<int>.None,
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
            RecordingProcessRunner.Succeeding(string.Empty).Runner, FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions()), NullLogger<CloseoutEngine>.Instance);

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node)
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
        return new RunSupervisor(store, node, processes, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            NullLogger<RunSupervisor>.Instance);
    }

    /// <summary>
    /// A gh runner these tests must never actually reach: nothing here exercises a pr-review
    /// task, so RunLauncher's own <c>ProcessRunner</c> is wired but always unused — a real
    /// <c>gh</c> invocation would mean either a hidden pr-review path or a test running gh for
    /// real, and this makes either one fail loudly instead of hanging on a live network call.
    /// </summary>
    private static readonly ProcessRunner UnusedProcessRunner =
        RecordingProcessRunner.Failing("this test never reviews a pull request").Runner;
}
