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
/// never counting the run being compared against itself, never pooling a failed or
/// differently-scoped sample in with a comparable one, and never letting undispatched drafts
/// crowd a project's genuinely dispatched history out of the window.
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
            query, projectId, "build", TimeSpan.FromMinutes(10), ranFullScope: true, DomainId.New(), cts.Token);

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
            query, projectId, "build", TimeSpan.FromSeconds(150), ranFullScope: true, DomainId.New(), cts.Token);

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
            query, projectId, "build", TimeSpan.FromSeconds(65), ranFullScope: true, DomainId.New(), cts.Token);

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
            query, projectId, "build", TimeSpan.FromMinutes(10), ranFullScope: true, DomainId.New(), cts.Token);

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
            query, projectId, "build", TimeSpan.FromMinutes(10), ranFullScope: true, excludedRunId, cts.Token);

        comparison.Should().BeNull("excluding the run itself leaves only four samples, below the minimum");
    }

    [Fact]
    public async Task A_failed_gates_own_duration_never_pools_into_the_passing_average()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 0; i < 5; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "test", TimeSpan.FromSeconds(60));
            }

            // Three failed samples, much shorter (a fail-fast compile error) — these must never
            // drag the average down, and must never count toward the sample size either.
            for (int i = 5; i < 8; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "test", TimeSpan.FromSeconds(5), passed: false);
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "test", TimeSpan.FromSeconds(150), ranFullScope: true, DomainId.New(), cts.Token);

        comparison.Should().NotBeNull();
        comparison!.RecentAverage.Should().Be(
            TimeSpan.FromSeconds(60), "the failed 5s samples must never pool into the passing average");
        comparison.SampleCount.Should().Be(5, "only the five passing samples count, not the three failed ones");
    }

    [Fact]
    public async Task A_differently_scoped_samples_history_never_pools_into_the_average()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            // Five full-scope passes at 4 minutes.
            for (int i = 0; i < 5; i++)
            {
                SeedRun(
                    session, projectId, ownerId, Now.AddMinutes(-i), "test", TimeSpan.FromMinutes(4),
                    ranFullScope: true);
            }

            // Three scoped fix-cycle passes at 20 seconds — comparable to each other, not to the
            // full-scope samples above.
            for (int i = 5; i < 8; i++)
            {
                SeedRun(
                    session, projectId, ownerId, Now.AddMinutes(-i), "test", TimeSpan.FromSeconds(20),
                    ranFullScope: false);
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();

        GateDurationComparison? fullScopeComparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "test", TimeSpan.FromMinutes(4), ranFullScope: true, DomainId.New(), cts.Token);
        fullScopeComparison.Should().BeNull("a full-scope 4-minute pass is exactly the full-scope average, not an anomaly");

        GateDurationComparison? inflatedByScopedSamples = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "test", TimeSpan.FromMinutes(2), ranFullScope: true, DomainId.New(), cts.Token);
        inflatedByScopedSamples.Should().BeNull(
            "the full-scope average must stay 4 minutes, never dragged down toward the scoped 20s samples");

        GateDurationComparison? scopedComparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "test", TimeSpan.FromSeconds(21), ranFullScope: false, DomainId.New(), cts.Token);
        scopedComparison.Should().BeNull(
            "only three scoped samples were recorded, below the minimum — the full-scope samples must not fill the gap");
    }

    [Fact]
    public async Task Draft_tasks_never_crowd_dispatched_runs_out_of_the_history_window()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using DocumentStore store = NewStore();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            // The project's genuine dispatched history, recorded first.
            for (int i = 0; i < 5; i++)
            {
                SeedRun(session, projectId, ownerId, Now.AddMinutes(-i), "build", TimeSpan.FromSeconds(60));
            }

            // More drafts than the history window holds, added AFTER the dispatched runs above —
            // under a window ordered by task-add recency (rather than run-dispatch recency),
            // these alone would push every dispatched task out of it.
            for (int i = 0; i < 60; i++)
            {
                SeedDraftTask(session, projectId, ownerId, Now.AddMinutes(i + 1));
            }

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        GateDurationComparison? comparison = await GateDurationHistoryQuery.CompareAsync(
            query, projectId, "build", TimeSpan.FromSeconds(150), ranFullScope: true, DomainId.New(), cts.Token);

        comparison.Should().NotBeNull(
            "the dispatched runs' own history must still be found; drafts never dispatched carry no runs to crowd it out with");
        comparison!.SampleCount.Should().Be(5);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static void SeedRun(
        IDocumentSession session, Guid projectId, Guid ownerId, DateTimeOffset at, string gateName,
        TimeSpan duration, bool passed = true, bool ranFullScope = true, Guid? runId = null)
    {
        Guid taskId = DomainId.New();
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, projectId, "Verify the gate", ["gates pass"], TaskType.Chore,
                null, null, null, at, ownerId),
            ownerId, at));

        Guid resolvedRunId = runId ?? DomainId.New();
        GateDuration gateDuration = new(gateName, duration, Passed: passed, RanFullScope: ranFullScope);
        object verificationEvent = passed
            ? new VerificationPassed(resolvedRunId, at, null, false, null, null, [gateDuration])
            : new VerificationFailed(resolvedRunId, [gateName], at, [gateDuration]);

        session.Events.StartStream<RunAggregate>(resolvedRunId,
            new RunDispatched(
                resolvedRunId, taskId, DomainId.New(), ownerId, 1, DomainId.New(),
                $"/tmp/hall9k-{resolvedRunId:N}", $"task/{resolvedRunId:N}", ExecutorMode.Subscription, at),
            verificationEvent);
    }

    /// <summary>A task that was added but never published or dispatched — no RunListItem row of its own.</summary>
    private static void SeedDraftTask(IDocumentSession session, Guid projectId, Guid ownerId, DateTimeOffset at)
    {
        Guid taskId = DomainId.New();
        session.Events.StartStream<TaskAggregate>(
            taskId,
            TaskDecider.Add(taskId, projectId, "Just an idea, not yet dispatched", [], TaskType.Chore, null, null, null, at, ownerId));
    }
}
