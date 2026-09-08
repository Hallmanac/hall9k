using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Run.Queries;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The query surfaces whose Marten read has to be exercised against a real store, sharing one
/// container between them. Each of the three seams below was its own class and so its own
/// container, for one to twelve tests each; nothing in any of them asks for a database its
/// siblings cannot see, because every one seeds its own task and run ids and asks the query only
/// about those — which is the same isolation each already relied on between its own tests, since
/// <see cref="PostgresFixture"/> has always shared one database across a class.
/// <para>
/// <c>GateDurationHistoryQuery</c> — a gate's own comparison against its project's recent history
/// (task: gate wall-clock duration is recorded and surfaced): enough samples to say something
/// honest, too few to say anything at all, scoped to the project rather than every task ever
/// recorded, never counting the run being compared against itself, never pooling a failed or
/// differently-scoped sample in with a comparable one, and never letting undispatched drafts crowd
/// a project's genuinely dispatched history out of the window.
/// </para>
/// <para>
/// <see cref="TaskDependencyQuery"/> — its own read of <see cref="RunDetails.PullRequestNumber"/>
/// and <see cref="RunDetails.FailureReason"/>, at the layer the orphan-sweep fix actually lives in
/// (independent pre-PR review, cycle 1: the unit tests in <c>TaskDependencyClosureTests</c>
/// construct <see cref="TaskDependency"/> by hand and so never exercise the Marten <c>Select</c>
/// projection this query runs — a regression there, such as a member-mapping change that silently
/// materialized either scalar as null, would pass every one of them while every dependency went
/// back to reading dead exactly as before the fix).
/// </para>
/// <para>
/// <see cref="BlockerHandoffQuery"/> — assembling a dependent's starting context from its
/// BlockedBy edges (Decisions Log #36): depth one and no further, the successful run's handoff
/// after a retry, and an honest fallback for every blocker that handed nothing down.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class StoreBackedQueryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Too_few_recorded_runs_says_nothing_rather_than_inventing_a_norm()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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
        DocumentStore store = postgres.Store;
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

    /// <summary>
    /// The raw material for a clean-base comparison's own budget (task: the clean-base comparison
    /// can actually finish — origin incident 2026-09-05/06, this project's own 11-12 minute test
    /// gate against a fixed 5-minute cap alone): the newest recorded duration for a gate on the
    /// SAME node, not an average, and never a duration recorded on a different node.
    /// </summary>
    [Fact]
    public async Task The_most_recent_duration_on_a_node_is_returned_rather_than_an_average()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid nodeId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            SeedRun(session, projectId, ownerId, Now.AddMinutes(-2), "test", TimeSpan.FromMinutes(8), nodeId: nodeId);
            SeedRun(session, projectId, ownerId, Now.AddMinutes(-1), "test", TimeSpan.FromMinutes(12), nodeId: nodeId);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TimeSpan? recentDuration = await GateDurationHistoryQuery.MostRecentDurationOnNodeAsync(
            query, projectId, nodeId, "test", cts.Token);

        recentDuration.Should().Be(
            TimeSpan.FromMinutes(12), "the newest recorded run's own duration, not an average of the two");
    }

    /// <summary>
    /// A clean-base comparison always spawns the gate's raw, unscoped command (never a fix
    /// cycle's own <c>--filter</c>), so a scoped sample's duration says nothing comparable about
    /// how long that unscoped spawn will actually take (independent pre-PR review, cycle 1, both
    /// lenses, medium: a scoped 90-second sample budgeting a comparison that then runs the full
    /// 11-12 minute suite reproduces the origin incident this method exists to fix). The newest
    /// full-scope sample must be found even when a newer scoped one exists in between.
    /// </summary>
    [Fact]
    public async Task A_scoped_samples_duration_never_counts_toward_the_clean_base_budget()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid nodeId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            SeedRun(
                session, projectId, ownerId, Now.AddMinutes(-2), "test", TimeSpan.FromMinutes(12),
                ranFullScope: true, nodeId: nodeId);
            SeedRun(
                session, projectId, ownerId, Now.AddMinutes(-1), "test", TimeSpan.FromSeconds(90),
                ranFullScope: false, nodeId: nodeId);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TimeSpan? recentDuration = await GateDurationHistoryQuery.MostRecentDurationOnNodeAsync(
            query, projectId, nodeId, "test", cts.Token);

        recentDuration.Should().Be(
            TimeSpan.FromMinutes(12),
            "the newest full-scope sample, not the newer-but-scoped one a comparison's own unscoped spawn cannot be budgeted from");
    }

    [Fact]
    public async Task A_duration_recorded_on_a_different_node_never_counts()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid recordingNodeId = DomainId.New();
        Guid comparisonNodeId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            SeedRun(session, projectId, ownerId, Now, "test", TimeSpan.FromMinutes(12), nodeId: recordingNodeId);
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        TimeSpan? recentDuration = await GateDurationHistoryQuery.MostRecentDurationOnNodeAsync(
            query, projectId, comparisonNodeId, "test", cts.Token);

        recentDuration.Should().BeNull("this node has recorded nothing for this gate — a sibling node's duration is not this node's own");
    }

    [Fact]
    public async Task No_recorded_duration_for_the_gate_on_this_node_returns_null()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;
        Guid projectId = DomainId.New();
        Guid nodeId = DomainId.New();

        await using IQuerySession query = store.QuerySession();
        TimeSpan? recentDuration = await GateDurationHistoryQuery.MostRecentDurationOnNodeAsync(
            query, projectId, nodeId, "test", cts.Token);

        recentDuration.Should().BeNull();
    }


    private static void SeedRun(
        IDocumentSession session, Guid projectId, Guid ownerId, DateTimeOffset at, string gateName,
        TimeSpan duration, bool passed = true, bool ranFullScope = true, Guid? runId = null, Guid? nodeId = null)
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
                resolvedRunId, taskId, nodeId ?? DomainId.New(), ownerId, 1, DomainId.New(),
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

    // ── TaskDependencyQuery ──
    private static readonly DateTimeOffset DependencyNow = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private const string DependencyPullRequestUrl = "https://github.com/x/y/pull/7";

    /// <summary>
    /// Mirrors what <c>h9k task resolve --pr</c> appends: TaskFailed then TaskResolved on the
    /// task stream, RunFailed then PullRequestRecordedOnFailedRun on the run stream. The orphan
    /// sweep is still watching this pull request, so the dependency must read alive.
    /// </summary>
    [Fact]
    public async Task A_done_blocker_the_orphan_sweep_still_watches_reads_alive_through_the_real_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();

        Guid blockerId = await SeedResolvedFailedBlockerAsync(
            store, ownerId, recordPullRequestOnRun: true, closedWithoutMerge: false, cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskDependency> dependencies =
            await TaskDependencyQuery.LoadAsync(query, [blockerId], cts.Token);

        dependencies.Should().ContainSingle();
        dependencies[0].IsDead.Should().BeFalse(
            "the run's own PullRequestNumber, read off RunDetails, still puts it in the orphan "
            + "sweep's candidate set");
    }

    /// <summary>
    /// The sweep's own exclusion, read through the same store: a run the monitor already
    /// observed closed without merging carries <see cref="RunDetails.PullRequestClosedWithoutMerge"/>
    /// as its FailureReason, and the dependency must read dead rather than still watched.
    /// </summary>
    [Fact]
    public async Task A_done_blocker_whose_pull_request_closed_without_merging_reads_dead_through_the_real_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid ownerId = DomainId.New();

        Guid blockerId = await SeedResolvedFailedBlockerAsync(
            store, ownerId, recordPullRequestOnRun: true, closedWithoutMerge: true, cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<TaskDependency> dependencies =
            await TaskDependencyQuery.LoadAsync(query, [blockerId], cts.Token);

        dependencies.Should().ContainSingle();
        dependencies[0].IsDead.Should().BeTrue(
            "the run's own FailureReason, read off RunDetails, already excludes it from the "
            + "orphan sweep's candidate set");
    }


    private static async Task<Guid> SeedResolvedFailedBlockerAsync(
        DocumentStore store, Guid ownerId, bool recordPullRequestOnRun, bool closedWithoutMerge,
        CancellationToken cancellationToken)
    {
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                blockerId, DomainId.New(), "Ship the schema", ["merged"], TaskType.Chore,
                null, null, null, DependencyNow, ownerId),
            ownerId, DependencyNow);
        List<object> taskEvents = [.. lifecycle];

        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, DomainId.New(), ownerId, runId, DependencyNow);
        task.Apply(claimed);
        taskEvents.Add(claimed);

        Hall9k.Domain.Features.Tasks.Events.TaskFailed failed =
            TaskDecider.Fail(task, runId, "the gates never went green", DependencyNow);
        task.Apply(failed);
        taskEvents.Add(failed);

        Hall9k.Domain.Features.Tasks.Events.TaskResolved resolved = TaskDecider.Resolve(
            task, "the work merged; only the gate failed", DependencyPullRequestUrl, DependencyNow, ownerId);
        task.Apply(resolved);
        taskEvents.Add(resolved);

        session.Events.StartStream<TaskAggregate>(blockerId, [.. taskEvents]);

        List<object> runEvents =
        [
            new RunDispatched(
                runId, blockerId, DomainId.New(), ownerId, 1, DomainId.New(),
                "/tmp/worktree", "task/ship-the-schema", ExecutorMode.Subscription, DependencyNow),
            new RunFailed(runId, "the gates never went green", DependencyNow),
        ];
        if (recordPullRequestOnRun)
        {
            runEvents.Add(new PullRequestRecordedOnFailedRun(runId, DependencyPullRequestUrl, 7, DependencyNow));
        }

        if (closedWithoutMerge)
        {
            runEvents.Add(new PullRequestClosed(runId, DependencyNow, DependencyNow));
        }

        session.Events.StartStream<RunAggregate>(runId, [.. runEvents]);

        await session.SaveChangesAsync(cancellationToken);
        return blockerId;
    }

    // ── BlockerHandoffQuery ──
    private static readonly DateTimeOffset HandoffNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private const string HandoffPullRequestUrl = "https://github.com/x/y/pull/42";

    /// <summary>
    /// The dead-blocker case #34 left open, pinned. A blocker whose first run died and was
    /// retried to a merge has two runs on one task, and only one of them describes work that
    /// exists — so the handoff the dependent reads must come from the run that merged. The
    /// query never inspects the failed run's text at all: selection is on the run's own
    /// terminal state, which the closeout monitor alone produces.
    /// </summary>
    [Fact]
    public async Task A_retried_blocker_hands_down_the_successful_runs_summary_never_the_failed_ones()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid failedRunId = DomainId.New();
        Guid mergedRunId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    blockerId, DomainId.New(), "Ship the schema", ["the migration applies"],
                    TaskType.Chore, null, null, null, HandoffNow, ownerId),
                ownerId, HandoffNow));

            // Attempt one died. It wrote a handoff before failing, and that handoff describes
            // work nobody can build on.
            session.Events.StartStream<RunAggregate>(failedRunId,
                Dispatch(failedRunId, blockerId, ownerId, HandoffNow),
                new RunHandoffRecorded(
                    failedRunId, HandoffOutcome.Captured, "I renamed the column to Legacy.", HandoffNow),
                new RunFailed(failedRunId, "The gates never passed.", HandoffNow));

            // The retry merged an hour later.
            session.Events.StartStream<RunAggregate>(mergedRunId,
                Dispatch(mergedRunId, blockerId, ownerId, HandoffNow.AddHours(1)),
                new PullRequestOpened(mergedRunId, HandoffPullRequestUrl, 42, HandoffNow.AddHours(1)),
                new PullRequestMerged(mergedRunId, HandoffNow.AddHours(2), HandoffNow.AddHours(2)),
                new RunHandoffRecorded(
                    mergedRunId, HandoffOutcome.Captured, "The column is named Canonical.", HandoffNow.AddHours(2)),
                new RunCompleted(mergedRunId, HandoffNow.AddHours(2)));

            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [blockerId], cts.Token);

        handoffs.Should().ContainSingle();
        handoffs[0].Summary.Should().Be("The column is named Canonical.",
            "only the run that reached true closeout describes work the dependent can build on");
        handoffs[0].Summary.Should().NotContain("Legacy", "the failed attempt's handoff never travels");
        handoffs[0].HasSummary.Should().BeTrue();
    }

    /// <summary>
    /// The depth-one rule, enforced where context is assembled rather than where the graph is
    /// walked. TaskDependencyQuery still loads the transitive closure for cycle detection at
    /// publish; this reads the first hop and stops (Decisions Log #36).
    /// </summary>
    [Fact]
    public async Task Context_stops_at_the_first_hop_even_when_the_chain_runs_deeper()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid grandparentId = DomainId.New();
        Guid parentId = DomainId.New();
        Guid childId = DomainId.New();

        // Each hop is committed before the next declares an edge to it: Publish reads the
        // committed graph, and a human declaring a dependency does the same.
        await using (IDocumentSession session = store.LightweightSession())
        {
            SeedClosedOutBlocker(
                session, grandparentId, ownerId, "Ship the schema", TaskDependencyGraph.Empty, [], "Two hops back.");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskDependencyGraph graph =
                await TaskSeed.DependencyGraphAsync(session, [grandparentId], cts.Token);
            SeedClosedOutBlocker(
                session, parentId, ownerId, "Ship the projection", graph, [grandparentId], "One hop back.");
            await session.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskDependencyGraph graph = await TaskSeed.DependencyGraphAsync(session, [parentId], cts.Token);
            session.Events.StartStream<TaskAggregate>(childId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    childId, DomainId.New(), "Ship the CLI surface", ["it prints"], TaskType.Chore,
                    null, null, null, HandoffNow, ownerId, blockedBy: [parentId]),
                ownerId, HandoffNow, graph));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [parentId], cts.Token);

        handoffs.Should().ContainSingle("the child's own edge names one blocker");
        handoffs[0].Summary.Should().Be("One hop back.");

        string document = BlockerContextDocument.Render(handoffs)!;
        document.Should().NotContain("Two hops back.",
            "a needed two-hop fact is evidence of a missing edge, not a context gap to paper over");
        document.Should().NotContain("Ship the schema");
    }

    [Fact]
    public async Task A_blocker_still_in_flight_reports_a_recorded_absence_and_its_own_intent()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    blockerId, DomainId.New(), "Ship the schema", ["the migration applies", "it is indexed"],
                    TaskType.Chore, null, null, null, HandoffNow, ownerId),
                ownerId, HandoffNow));
            session.Events.StartStream<RunAggregate>(runId, Dispatch(runId, blockerId, ownerId, HandoffNow));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [blockerId], cts.Token);

        handoffs.Should().ContainSingle();
        handoffs[0].Outcome.Should().Be(HandoffOutcome.NotClosedOut,
            "no run carried it to true closeout, which is a different absence from one that closed out with nothing to say");
        handoffs[0].HasSummary.Should().BeFalse();
        BlockerContextDocument.Render(handoffs).Should().NotContain("the run that closed this out",
            "a blocker still in flight has no such run, and the document may not imply one");
        handoffs[0].AcceptanceCriteria.Should().Equal("the migration applies", "it is indexed");
    }

    /// <summary>
    /// A run that merged before handoffs existed is the historical case: it closed out, it
    /// unblocks its dependents, and it carries no handoff event. It must read as an absence
    /// with the blocker's intent behind it, not as a broken context assembly.
    /// </summary>
    [Fact]
    public async Task A_pre_handoff_stream_closes_out_and_reads_as_unknown_rather_than_failing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        Guid ownerId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid runId = DomainId.New();

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<TaskAggregate>(blockerId, TaskSeed.Dispatchable(
                TaskDecider.Add(
                    blockerId, DomainId.New(), "Ship the schema", ["the migration applies"],
                    TaskType.Chore, null, null, null, HandoffNow, ownerId),
                ownerId, HandoffNow));
            session.Events.StartStream<RunAggregate>(runId,
                Dispatch(runId, blockerId, ownerId, HandoffNow),
                new PullRequestOpened(runId, HandoffPullRequestUrl, 42, HandoffNow),
                new PullRequestMerged(runId, HandoffNow, HandoffNow),
                new RunCompleted(runId, HandoffNow));
            await session.SaveChangesAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [blockerId], cts.Token);

        handoffs[0].Outcome.Should().Be(HandoffOutcome.Unknown,
            "a stream written before handoffs existed says it does not know");
        handoffs[0].HasSummary.Should().BeFalse();
        BlockerContextDocument.Render(handoffs).Should().Contain("the migration applies",
            "the fallback is the blocker's own intent, which every blocker has");
    }

    [Fact]
    public async Task An_edge_naming_no_known_task_is_skipped_rather_than_invented()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = postgres.Store;

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<BlockerHandoff> handoffs =
            await BlockerHandoffQuery.LoadAsync(query, [DomainId.New()], cts.Token);

        handoffs.Should().BeEmpty("a dangling edge is the dependency query's story; inventing a blocker here would be a guess");
    }


    private static void SeedClosedOutBlocker(
        IDocumentSession session, Guid taskId, Guid ownerId, string objective,
        TaskDependencyGraph graph, IReadOnlyList<Guid> blockedBy, string handoff)
    {
        session.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(
                taskId, DomainId.New(), objective, ["merged"], TaskType.Chore,
                null, null, null, HandoffNow, ownerId, blockedBy: blockedBy),
            ownerId, HandoffNow, graph));

        Guid runId = DomainId.New();
        session.Events.StartStream<RunAggregate>(runId,
            Dispatch(runId, taskId, ownerId, HandoffNow),
            new PullRequestOpened(runId, HandoffPullRequestUrl, 42, HandoffNow),
            new PullRequestMerged(runId, HandoffNow, HandoffNow),
            new RunHandoffRecorded(runId, HandoffOutcome.Captured, handoff, HandoffNow),
            new RunCompleted(runId, HandoffNow));
    }

    private static RunDispatched Dispatch(Guid runId, Guid taskId, Guid ownerId, DateTimeOffset at) => new(
        runId, taskId, DomainId.New(), ownerId, 1, DomainId.New(),
        $"/tmp/hall9k-{runId:N}", $"task/{runId:N}", ExecutorMode.Subscription, at);
}
