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
/// pull-request head (adversarial review cycle-3 ride-along, `TokenBudgetRetryEngine.cs:113`):
/// the resume spawn a budget-exhaustion retry issues for that same session must carry
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
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);

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
        TokenBudgetRetryEngine engine = new(store, node, executor, NewSupervisor(store, node), NullLogger<TokenBudgetRetryEngine>.Instance);

        int retried = await engine.RetryParkedRunsAsync(cts.Token);

        retried.Should().Be(1, "the run is budget-parked and its task is still claimed by this node");
        executor.Request.Should().NotBeNull();
        executor.Request!.UntrustedWorkingDirectory.Should().BeTrue(
            "the resumed session is the pr-review task's own adversarial lens over the same foreign checkout");
    }

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node)
    {
        FakeProcessManager processes = new();
        VerificationRunner verification = new(
            store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes), processes, verification,
            Options.Create(new DaemonOptions()), NullLogger<ReviewEngine>.Instance);
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processes), processes,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            Options.Create(new DaemonOptions()), NullLogger<PrReviewEngine>.Instance);
        return new RunSupervisor(store, node, processes, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            NullLogger<RunSupervisor>.Instance);
    }
}
