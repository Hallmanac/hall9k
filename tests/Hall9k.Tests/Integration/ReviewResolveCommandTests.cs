using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="ReviewResolveCommand.ResolvePrReviewAsync"/> against a real store, called
/// directly rather than through <c>CliStore.Open</c>'s ambient connection (this codebase's CLI
/// commands have no other test seam) — the pr-review verdict rules had no coverage at all before
/// this (cycle-1 conformance finding, `PrReviewEngine.cs:50`).
/// </summary>
// The merge-ready path rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell), which resolves
// its connection off HALL9K_CONNECTION_STRING rather than this fixture, so that one test points
// it at the fixture for its duration. That is process-wide state, same as DatabaseDoctorTests and
// BacklogTrackingTests, so this joins the same collection to serialize against every other test
// that redirects it.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ReviewResolveCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Needs_fixes_is_refused_outright_on_a_pr_review_tasks_park()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;

        Func<Task> act = () => ReviewResolveCommand.ResolvePrReviewAsync(
            session, runId, taskId, fence,
            new ReviewResolveCommand.Settings { NeedsFixes = "Fix the thing" }, cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*nothing here for a fix session to apply*");
    }

    [Fact]
    public async Task A_missing_merge_ready_verdict_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;

        Func<Task> act = () => ReviewResolveCommand.ResolvePrReviewAsync(
            session, runId, taskId, fence, new ReviewResolveCommand.Settings(), cts.Token);

        (await act.Should().ThrowAsync<DomainValidationException>())
            .WithMessage("*Pass --merge-ready*");
    }

    [Fact]
    public async Task Merge_ready_delivers_the_review_without_ever_opening_a_pull_request()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = new();
        await node.InitializeAsync(store, cts.Token);
        (Guid taskId, Guid runId) = await SeedParkedPrReviewRunAsync(store, node, cts.Token);

        // Resolving merge-ready rings the doorbell, which resolves its connection off
        // HALL9K_CONNECTION_STRING rather than this fixture, so it has to be pointed at the
        // fixture for the duration of the call.
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            StreamState fence = (await session.Events.FetchStreamStateAsync(runId, cts.Token))!;
            int result = await ReviewResolveCommand.ResolvePrReviewAsync(
                session, runId, taskId, fence,
                new ReviewResolveCommand.Settings { MergeReady = true, Reason = "Walked and directed by hand." },
                cts.Token);

            result.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }

        await using IQuerySession query = store.QuerySession();
        RunAggregate run = (await query.Events.AggregateStreamAsync<RunAggregate>(runId, token: cts.Token))!;
        run.PrReviewDelivered.Should().BeTrue();
        run.State.Should().Be(RunState.UnderReview,
            "the run leaves its park the moment the verdict lands — PrReviewEngine's own resume finalizes it from here");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>A pr-review task whose adversarial and conformance lenses both ran and parked their report.</summary>
    private async Task<(Guid TaskId, Guid RunId)> SeedParkedPrReviewRunAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid sessionId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-resolve-wt-{runId:N}");

        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"resolve-{taskId:N}", "/tmp/resolve-repo", null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Review pull request acme/web#7", ["every finding names a file and line"],
                TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#7"), Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(
                runId, taskId, node.NodeId, node.OwnerId, 1, sessionId, worktreePath, "pr/7",
                ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new ReviewParked(runId, "Findings ready.", Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }
}
