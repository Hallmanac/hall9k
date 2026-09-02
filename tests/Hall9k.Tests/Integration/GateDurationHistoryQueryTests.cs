using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Queries;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A gate's own comparison against its project's recent history (task: gate wall-clock duration
/// is recorded and surfaced), against a real store: enough samples to say something honest,
/// too few to say anything at all, scoped to the project rather than every task ever recorded,
/// and never counting the run being compared against itself.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class GateDurationHistoryQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Too_few_recorded_runs_says_nothing_rather_than_inventing_a_norm()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            // Only two prior samples — below MinimumSamplesForComparison(5).
            for (int i = 0; i < 2; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "build", TimeSpan.FromSeconds(60));
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "build", TimeSpan.FromMinutes(10), DomainId.New(), cts.Token);

        comparison.Should().BeNull("two recorded samples is not enough to compare against honestly");
    }

    [Fact]
    public async Task A_duration_well_above_the_recent_average_is_flagged_with_the_comparison()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 0; i < 5; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "build", TimeSpan.FromSeconds(60));
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "build", TimeSpan.FromSeconds(150), DomainId.New(), cts.Token);

        comparison.Should().NotBeNull("150s is 2.5x the recent 60s average, well past the anomaly multiplier");
        comparison!.Gate.Should().Be("build");
        comparison.Observed.Should().Be(TimeSpan.FromSeconds(150));
        comparison.RecentAverage.Should().Be(TimeSpan.FromSeconds(60));
        comparison.SampleCount.Should().Be(5);
    }

    [Fact]
    public async Task A_duration_close_to_the_recent_average_is_not_flagged()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 0; i < 5; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "build", TimeSpan.FromSeconds(60));
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "build", TimeSpan.FromSeconds(65), DomainId.New(), cts.Token);

        comparison.Should().BeNull("65s over a 60s recent average is ordinary drift, not an anomaly");
    }

    [Fact]
    public async Task A_sibling_projects_history_never_counts_toward_this_projects_average()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid otherProjectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            // The other project has plenty of history, but it must never be read for this one.
            for (int i = 0; i < 5; i++)
            {
                SeedRun(session, otherProjectId, ownerId, Now.AddMinutes(-i), "build", TimeSpan.FromSeconds(60));
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "build", TimeSpan.FromMinutes(10), DomainId.New(), cts.Token);

        comparison.Should().BeNull("this project has recorded no history of its own for this gate");
    }

    [Fact]
    public async Task The_run_being_compared_never_counts_toward_its_own_baseline()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid excludedRunId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 0; i < 4; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "build", TimeSpan.FromSeconds(60));
            }

            // A fifth sample under the very run id the caller is comparing against — it must be
            // excluded, leaving only four genuine samples, below the minimum.
            SeedRun(session, projectId, ownerId, Now, "build", TimeSpan.FromSeconds(60), runId: excludedRunId);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "build", TimeSpan.FromMinutes(10), excludedRunId, cts.Token);

        comparison.Should().BeNull("excluding the run itself leaves only four samples, below the minimum");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static void SeedRun(
        IDocumentSession session, Guid projectId, Guid ownerId, DateTimeOffset at, string gateName,
        TimeSpan duration, Guid? runId = null)
    {
        Guid taskId = DomainId.New();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, projectId, "Verify the gate", ["gates pass"], TaskType.Chore,
                null, null, null, at, ownerId),
            ownerId, at));

        Guid resolvedRunId = runId ?? DomainId.New();
        session.Events.StartStream<RunAggregate>(resolvedRunId,
            new RunDispatched(
                resolvedRunId, taskId, DomainId.New(), ownerId, 1, DomainId.New(),
                $"/tmp/hall9k-{resolvedRunId:N}", $"task/{resolvedRunId:N}", ExecutorMode.Subscription, at),
            new VerificationPassed(
                resolvedRunId, at, null, false, null, null, [new GateDuration(gateName, duration, Passed: true)]));
    }
}
