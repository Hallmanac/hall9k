using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Review;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
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
/// A pr-review task's primary session is the adversarial lens reading another contributor's
/// pull-request head (adversarial review cycle-3 ride-along, on <see cref="TokenBudgetRetryEngine.RetryParkedRunsAsync"/>'s
/// own resume path): the resume spawn a budget-exhaustion retry issues for that same session must carry
/// <see cref="AgentSpawnRequest.UntrustedWorkingDirectory"/> forward, exactly as the original
/// dispatch did, so the retried process never loads the foreign checkout's own `.claude/`
/// config or `.mcp.json` under the owner's credentials.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class TokenBudgetRetryEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task Resuming_a_budget_parked_pr_review_run_marks_the_spawn_request_untrusted()
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
        Guid sessionId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-wt-{runId:N}");
        string runDirectory = Path.Combine(Path.GetTempPath(), $"hall9k-budget-retry-run-{runId:N}");
        Directory.CreateDirectory(runDirectory);

        await using (IDocumentSession session = store.LightweightSession())
        {
            ProjectRegistered registered = ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"budget-retry-{taskId:N}", repositoryPath,
                new Uri("https://github.com/acme/web"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#42", ["every finding names a file and line"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#42"), Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/42",
                ExecutorMode.Subscription, Now, RunDirectory: runDirectory));
            session.Events.Append(runId, new RunBudgetExhausted(runId, "usage limit reached", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
        TokenBudgetRetryEngine engine = new(
            store, node, new PrimarySessionResumer(executor), NewSupervisor(store, node), NullLogger<TokenBudgetRetryEngine>.Instance);

        int retried = await engine.RetryParkedRunsAsync(cts.Token);

        retried.Should().Be(1, "the run is budget-parked and its task is still claimed by this node");
        executor.Request.Should().NotBeNull();
        executor.Request!.UntrustedWorkingDirectory.Should().BeTrue(
            "the resumed session is the pr-review task's own adversarial lens over the same foreign checkout");
    }

    /// <summary>
    /// The high finding from independent pre-PR review cycle 10: a Now-speed auto-pr-review
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
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
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
                new Uri("https://github.com/acme/web"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#43", ["every finding names a file and line"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#43"), Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, node.OwnerId, runId, Now, dependencyOverrideAcknowledged: false);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            // Deliberately no TaskLease: a sentinel claim writes none (AutoPrReviewEngine.CreateOneAsync).

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, sessionId, worktreePath, "pr/43",
                ExecutorMode.Subscription, Now, RunDirectory: runDirectory, DispatchingNodeId: node.NodeId));
            session.Events.Append(runId, new RunBudgetExhausted(runId, "usage limit reached", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
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
        using DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
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
                new Uri("https://github.com/acme/web"), "main", Now);
            session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(
                    taskId, projectId, "Review pull request acme/web#44", ["the verdict is submitted"],
                    TaskType.PrReview, null, null,
                    new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#44"), Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(
                taskId,
                [
                    .. lifecycle,
                    claimed,
                    new PullRequestReviewLapOpened(
                        taskId, runId, worktreePath, "https://github.com/acme/web/pull/44", Now, node.OwnerId),
                ]);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(), worktreePath, "pr/44",
                ExecutorMode.Subscription, Now, RunDirectory: runDirectory));
            session.Events.Append(runId, new RunBudgetExhausted(runId, "usage limit reached", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        CapturingExecutor executor = new();
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
