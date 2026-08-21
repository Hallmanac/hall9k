using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Daemon.Worktrees;
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
                UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null));
        }

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
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

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken)
        {
            DeletedBranches.Add(branch);
            return Task.CompletedTask;
        }

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;
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
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

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
            NewSupervisor(store, node), NewContextAssembler(store), inspector, Options.Create(new DaemonOptions()),
            NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, nextRunId, node.NodeId, node.OwnerId, 3, cts.Token);

        inspector.Inspections.Should().Be(1, "the provider is consulted before any workspace or agent work");

        await using IQuerySession query = store.QuerySession();
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Done", "merged work closes out — it is never rebuilt");
        task.PullRequestUrl.Should().Be(PullRequestUrl);

        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("closing out releases the lease");
        (await query.Events.FetchStreamStateAsync(nextRunId, cts.Token)).Should().BeNull(
            "no run is ever dispatched for the merged pull request");
        worktrees.DeletedBranches.Should().ContainSingle(deleted => deleted == branch,
            "the merged branch is cleaned up like any closeout");
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

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteBranchEverywhereAsync(string repositoryPath, string branch, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) => Task.CompletedTask;
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
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

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
        RunLauncher launcher = new(store, new StubWorktreeManager(), executor,
            NewSupervisor(store, node), NewContextAssembler(store), new MergedInspector(), Options.Create(new DaemonOptions()),
            NullLogger<RunLauncher>.Instance);

        await launcher.LaunchAsync(taskId, runId, node.NodeId, node.OwnerId, 1, cts.Token);

        executor.Request!.Model.Value.Should().Be(
            "claude-opus-5[1m]", "the task override is the most specific level of the chain");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.Model.Value.Should().Be("claude-opus-5[1m]", "the run records what it was actually dispatched on");
        (await query.LoadAsync<RunListItem>(runId, cts.Token))!.Model.Value.Should().Be("claude-opus-5[1m]");
    }

    /// <summary>
    /// Context routing needs no seams here: both tests close out a merged pull request
    /// without reaching a dispatch, and a task with no BlockedBy edges assembles nothing
    /// anyway (Decisions Log #36).
    /// </summary>
    private static BlockerContextAssembler NewContextAssembler(DocumentStore store) =>
        new(store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance), new FakeProcessManager(),
            Options.Create(new DaemonOptions()), NullLogger<BlockerContextAssembler>.Instance);

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node)
    {
        FakeProcessManager processes = new();
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance);
        return new RunSupervisor(store, node, processes, verification, review,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            NullLogger<RunSupervisor>.Instance);
    }
}
