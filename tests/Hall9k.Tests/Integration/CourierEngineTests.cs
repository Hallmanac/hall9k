using FluentAssertions;
using Hall9k.Connectors.Orchestrator;
using Hall9k.Daemon;
using Hall9k.Daemon.Courier;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The feed courier's own ending, against a real event store (idea 89471598, piece 3): what the
/// unit tier cannot answer for <see cref="CourierEngine.RecordOutcomeAsync"/> — that a delivered
/// outcome really advances <see cref="OrchestratorFeedCursor"/> and a failed one really leaves it,
/// and that the run's own record and token spend really land on the store. No agent is spawned
/// anywhere here: this method starts after a session has already ended.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class CourierEngineTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly CourierBackoffWaits Waits = CourierBackoffWaits.For(null, TimeSpan.FromMinutes(30));

    private readonly PostgresFixture _postgres;

    public CourierEngineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync() => await _postgres.Store.Advanced.Clean.CompletelyRemoveAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> DispatchedRunAsync(Guid projectId, CancellationToken cancellationToken)
    {
        Guid runId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream(
            runId, new CourierRunDispatched(runId, projectId, DomainId.New(), AgentModel.CourierDefault, Now));
        await session.SaveChangesAsync(cancellationToken);
        return runId;
    }

    [Fact]
    public async Task A_delivered_outcome_advances_the_feed_cursor_and_records_the_run_as_delivered()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);
        AgentResult result = new(false, 500, 50, 0, 200, null, 4, "Sent it.");

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, CourierEnding.Delivered, "Delivered.",
            drainableThroughSequence: 42, result, Now.AddSeconds(30), Waits, failureReason: null, addressedLaunchAt: null, cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        long cursor = await OrchestratorFeedReader.CursorAsync(session, projectId, cts.Token);
        cursor.Should().Be(42);

        CourierRunDetails? run = await session.LoadAsync<CourierRunDetails>(runId, cts.Token);
        run.Should().NotBeNull();
        run!.Delivered.Should().BeTrue();
        run.CompletedAt.Should().Be(Now.AddSeconds(30));
        run.Outcome.Should().Be("Delivered.");
    }

    [Fact]
    public async Task A_failed_outcome_leaves_the_feed_cursor_exactly_where_it_stood()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, CourierEnding.Failed,
            "The session ended without a delivered marker.", drainableThroughSequence: 42, result: null,
            Now.AddSeconds(30), Waits, "ended without an outcome line", addressedLaunchAt: null, cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        long cursor = await OrchestratorFeedReader.CursorAsync(session, projectId, cts.Token);
        cursor.Should().Be(
            OrchestratorFeedCursor.NeverDrained, "a failed delivery must leave the same items for the next courier");

        CourierRunDetails? run = await session.LoadAsync<CourierRunDetails>(runId, cts.Token);
        run.Should().NotBeNull();
        run!.Delivered.Should().BeFalse();
        run.CompletedAt.Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_result_that_reported_usage_is_recorded_even_when_delivery_failed()
    {
        // Real tokens were spent whether or not the send landed — the failed outcome test above
        // passes a null result (the timeout/no-result path); this covers the other failure shape,
        // a session that ran, reported usage, and simply never said the delivered marker.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);
        AgentResult result = new(false, 300, 20, 0, 100, null, 2, "I couldn't find that tool.");

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, CourierEnding.Failed,
            "The session ended without a delivered marker. It said: I couldn't find that tool.",
            drainableThroughSequence: 7, result, Now.AddSeconds(10), Waits, "ended without an outcome line", addressedLaunchAt: null, cts.Token);

        await using IQuerySession session = _postgres.Store.QuerySession();
        Hall9k.Domain.Features.Run.PeriodSpend spend =
            await Hall9k.Domain.Features.Run.PeriodSpend.ReadAsync(session, Now.AddDays(-1), cts.Token);

        spend.TotalInputTokens.Should().Be(320, "300 fresh plus 20 cache-read input tokens, the same total every other role sums");
    }

    /// <summary>
    /// Drives <see cref="CourierEngine.SweepOnceAsync"/> itself, not just its own
    /// <see cref="CourierEngine.RecordOutcomeAsync"/> tail — the spawn-gate wiring inside
    /// <c>TickAsync</c> (stranded-run adoption, the settling-window item filter) had no test in
    /// the diff that introduced it (independent pre-PR review, cycle 2, conformance lens), which
    /// is exactly how the stale-<c>lastRun</c> regression the adversarial lens found in the same
    /// cycle shipped unnoticed: a daemon-crash recovery adopting a stranded run must still honor
    /// the ordinary batching wait for the very next spawn decision in that same tick, not read the
    /// adoption's own write as "no prior courier ever ran" and spawn again immediately.
    /// </summary>
    [Fact]
    public async Task A_stranded_run_adopted_as_failed_still_waits_out_the_batching_wait_in_the_same_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = _postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const int processId = 48_213;
        DateTimeOffset processStartedAt = Now;

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"project-{DomainId.Short(projectId)}", "/repo", null, null, Now));

            session.Events.StartStream<OrchestratorPresenceAggregate>(
                OrchestratorPresenceStreamId.For(node.NodeId, projectId),
                new OrchestratorLaunched(
                    node.NodeId, projectId, "orchestrator", processId, "claude-code", processStartedAt, Now));

            // Dispatched well past CourierTimeout (3 minutes) plus the engine's own stranded-run
            // grace (1 minute) with no CourierRunCompleted ever appended — the shape a daemon
            // that stopped existing outright (kill -9, a host power loss) leaves behind.
            Guid strandedRunId = DomainId.New();
            session.Events.StartStream(strandedRunId, new CourierRunDispatched(
                strandedRunId, projectId, node.NodeId, AgentModel.CourierDefault, DateTimeOffset.UtcNow.AddMinutes(-6)));

            // A message the feed admits at every band, including the project's own default
            // (Transitions) — the item the gate must still see and still wait on.
            session.Events.StartStream<MessageAggregate>(
                MessageStreamId.ForMessage(DomainId.New(), projectId, 1),
                new MessageReceived(
                    DomainId.New(), 1, Now, "abcdef0123456789", "project", null,
                    MessageKind.Note.Value, "are you still on the stacked pair?", Now, projectId));

            await session.SaveChangesAsync(cts.Token);
        }

        // OrchestratorFeedItem.At is the event's own real commit timestamp (OrchestratorFeedReader
        // reads e.Timestamp, never a domain-supplied "now"), so the item just seeded only clears
        // OrchestratorFeedSelection.SettlingWindow (ten seconds) once real wall-clock time actually
        // passes — there is no clock this engine takes that a test could set instead.
        await Task.Delay(OrchestratorFeedSelection.SettlingWindow + TimeSpan.FromSeconds(2), cts.Token);

        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(processId, processStartedAt);
        FakeProcessManager processManager = new();
        CountingExecutor executor = new();
        CourierEngine engine = new(
            store, node, executor, processManager, probe, new UnsupportedProcessElevationReader(),
            Options.Create(new DaemonOptions()), NullLogger<CourierEngine>.Instance);

        CourierSweepResult sweep = await engine.SweepOnceAsync(cts.Token);

        sweep.Should().Be(
            new CourierSweepResult(Delivered: 0, Failed: 0, NoAdapter: 0, DayCapHits: 0),
            "the stranded run's own adoption must not itself read as clearance to spawn a fresh "
            + "courier in the same tick");
        executor.Spawns.Should().BeEmpty(
            "a courier that just got marked failed must still wait out the ordinary batching wait, "
            + "the same as any other failed delivery — a stale, still-null CompletedAt read off the "
            + "adoption's own local copy is what used to let this spawn immediately instead");

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<CourierRunDetails> runs = await query.Query<CourierRunDetails>()
            .Where(run => run.ProjectId == projectId).ToListAsync(cts.Token);
        runs.Should().ContainSingle().Which.Should().Match<CourierRunDetails>(
            run => run.CompletedAt != null && !run.Delivered);
    }

    /// <summary>
    /// The capped-scan hole (independent pre-PR review, cycle 4, conformance lens): a project
    /// whose own item sits past <see cref="OrchestratorFeedReader.MaxEventsPerRead"/> worth of
    /// noise from the rest of this node's log must still have its cursor move forward on a tick
    /// that admits nothing, or every later tick re-reads the identical noise-only window forever
    /// and the real item is never reached.
    /// </summary>
    [Fact]
    public async Task A_capped_scan_that_admits_nothing_still_advances_the_cursor()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DocumentStore store = _postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        const int processId = 55_001;
        DateTimeOffset processStartedAt = Now;

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"project-{DomainId.Short(projectId)}", "/repo", null, null, Now));

            session.Events.StartStream<OrchestratorPresenceAggregate>(
                OrchestratorPresenceStreamId.For(node.NodeId, projectId),
                new OrchestratorLaunched(
                    node.NodeId, projectId, "orchestrator", processId, "claude-code", processStartedAt, Now));

            // Padding the raw log past MaxEventsPerRead with a type OrchestratorFeedInterest's own
            // Bands table has no entry for at all (a courier's own dispatch is never a feed item),
            // so every one of these is rejected before a project is ever resolved for it — cheap
            // noise that fills the scan's own cap ahead of the real item seeded below.
            for (int i = 0; i < OrchestratorFeedReader.MaxEventsPerRead + 50; i++)
            {
                Guid paddingId = DomainId.New();
                session.Events.StartStream(paddingId, new CourierRunDispatched(
                    paddingId, DomainId.New(), node.NodeId, AgentModel.CourierDefault, Now));
            }

            // The real item, written last and so past the cap: an ordinary message, which this
            // project's own default band admits regardless of which level it reads at.
            session.Events.StartStream<MessageAggregate>(
                MessageStreamId.ForMessage(DomainId.New(), projectId, 1),
                new MessageReceived(
                    DomainId.New(), 1, Now, "abcdef0123456789", "project", null,
                    MessageKind.Note.Value, "are you still on the stacked pair?", Now, projectId));

            await session.SaveChangesAsync(cts.Token);
        }

        await Task.Delay(OrchestratorFeedSelection.SettlingWindow + TimeSpan.FromSeconds(2), cts.Token);

        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe().Running(processId, processStartedAt);
        FakeProcessManager processManager = new();
        CountingExecutor executor = new();
        CourierEngine engine = new(
            store, node, executor, processManager, probe, new UnsupportedProcessElevationReader(),
            Options.Create(new DaemonOptions()), NullLogger<CourierEngine>.Instance);

        CourierSweepResult sweep = await engine.SweepOnceAsync(cts.Token);

        sweep.Should().Be(
            new CourierSweepResult(Delivered: 0, Failed: 0, NoAdapter: 0, DayCapHits: 0),
            "the capped scan admitted nothing for this project this tick, so there is nothing yet to "
            + "spawn a courier for");
        executor.Spawns.Should().BeEmpty();

        await using IQuerySession query = store.QuerySession();
        long cursor = await OrchestratorFeedReader.CursorAsync(query, projectId, cts.Token);
        cursor.Should().BeGreaterThan(
            OrchestratorFeedCursor.NeverDrained,
            "a capped scan admitting zero items for this project must still advance past the noise it "
            + "already considered, or the project's own item past the cap is never reached on any "
            + "future tick either");
    }

    private async Task<CourierFailureStreak?> StreakAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = _postgres.Store.QuerySession();
        return await query.LoadAsync<CourierFailureStreak>(projectId, cancellationToken);
    }

    [Fact]
    public async Task A_failed_outcome_starts_a_streak_that_names_the_reason_and_the_next_attempt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid runId = await DispatchedRunAsync(projectId, cts.Token);

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, CourierEnding.Failed,
            "The session ended without a delivered marker.", drainableThroughSequence: 42, result: null,
            Now.AddSeconds(30), Waits, "hall9k-9f is not reachable\nsecond line", addressedLaunchAt: Now.AddHours(-1), cts.Token);

        CourierFailureStreak? streak = await StreakAsync(projectId, cts.Token);
        streak.Should().NotBeNull();
        streak!.Count.Should().Be(1);
        streak.StartedAt.Should().Be(Now.AddSeconds(30));
        streak.LastFailureAt.Should().Be(Now.AddSeconds(30));
        streak.LastAddressedLaunchAt.Should().Be(Now.AddHours(-1), "the registration reset compares against the window the courier addressed");
        streak.LastFailureReason.Should().Be("hall9k-9f is not reachable second line");
        streak.HighestCarriedSequence.Should().Be(42);
        streak.NextAttemptAt.Should().Be(Now.AddSeconds(30).AddMinutes(1));

        await using IQuerySession query = _postgres.Store.QuerySession();
        CourierRunDetails? run = await query.LoadAsync<CourierRunDetails>(runId, cts.Token);
        run!.Outcome.Should().Be("The session ended without a delivered marker.", "the run's own Outcome is not the reason");
    }

    [Fact]
    public async Task Consecutive_failures_double_the_wait_and_a_delivery_resets_the_streak()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        for (int n = 1; n <= 4; n++)
        {
            Guid failedRun = await DispatchedRunAsync(projectId, cts.Token);
            await CourierEngine.RecordOutcomeAsync(
                _postgres.Store, projectId, failedRun, AgentModel.CourierDefault, CourierEnding.Failed, "failed",
                drainableThroughSequence: n, result: null, Now.AddMinutes(n * 10), Waits, "down", addressedLaunchAt: null, cts.Token);
        }

        CourierFailureStreak? streak = await StreakAsync(projectId, cts.Token);
        streak!.Count.Should().Be(4);
        streak.StartedAt.Should().Be(Now.AddMinutes(10));
        streak.NextAttemptAt.Should().Be(Now.AddMinutes(40).AddMinutes(8));

        Guid deliveredRun = await DispatchedRunAsync(projectId, cts.Token);
        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, deliveredRun, AgentModel.CourierDefault, CourierEnding.Delivered, "Delivered.",
            drainableThroughSequence: 9, result: null, Now.AddMinutes(60), Waits, failureReason: null, addressedLaunchAt: null, cts.Token);

        (await StreakAsync(projectId, cts.Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_courier_the_daemon_ended_rewrites_only_the_next_attempt_of_a_running_streak()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid failedRun = await DispatchedRunAsync(projectId, cts.Token);
        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, failedRun, AgentModel.CourierDefault, CourierEnding.Failed, "failed",
            drainableThroughSequence: 5, result: null, Now, Waits, "down", addressedLaunchAt: null, cts.Token);
        CourierFailureStreak before = (await StreakAsync(projectId, cts.Token))!;

        Guid stoppedRun = await DispatchedRunAsync(projectId, cts.Token);
        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, stoppedRun, AgentModel.CourierDefault, CourierEnding.EndedByDaemon,
            "The daemon stopped while this courier was running, and it was stopped with it.",
            drainableThroughSequence: 0, result: null, Now.AddMinutes(30), Waits, failureReason: null, addressedLaunchAt: null, cts.Token);

        CourierFailureStreak after = (await StreakAsync(projectId, cts.Token))!;
        after.Count.Should().Be(before.Count);
        after.StartedAt.Should().Be(before.StartedAt);
        after.LastFailureAt.Should().Be(before.LastFailureAt);
        after.LastFailureReason.Should().Be(before.LastFailureReason);
        after.HighestCarriedSequence.Should().Be(before.HighestCarriedSequence);
        after.NextAttemptAt.Should().Be(Now.AddMinutes(30).AddMinutes(1));
    }

    [Fact]
    public async Task A_courier_the_daemon_ended_with_no_streak_leaves_a_zero_count_hold()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        Guid projectId = DomainId.New();
        Guid stoppedRun = await DispatchedRunAsync(projectId, cts.Token);

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, projectId, stoppedRun, AgentModel.CourierDefault, CourierEnding.EndedByDaemon,
            "stopped", drainableThroughSequence: 0, result: null, Now, Waits, failureReason: null, addressedLaunchAt: null, cts.Token);

        CourierFailureStreak? hold = await StreakAsync(projectId, cts.Token);
        hold.Should().NotBeNull();
        hold!.Count.Should().Be(0);
        hold.NextAttemptAt.Should().Be(Now.AddMinutes(1));
    }

    /// <summary>
    /// One project with an orchestrator window registered on a fresh node and one feed item that has
    /// already cleared the settling window. The item is a message, which the feed admits at every
    /// band and treats as urgent, so a tick with no streak running would spawn at once.
    /// </summary>
    private sealed record Seeded(NodeContext Node, Guid ProjectId, int ProcessId, DateTimeOffset ProcessStartedAt);

    private async Task<Seeded> SeedProjectWithUrgentItemAsync(
        int processId, DateTimeOffset launchedAt, CancellationToken cancellationToken, bool withItem = true)
    {
        DocumentStore store = _postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewIsolatedNodeAsync(store, cancellationToken);
        Guid projectId = DomainId.New();
        DateTimeOffset processStartedAt = launchedAt;

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<ProjectAggregate>(projectId, ProjectDecider.Register(
                projectId, node.OwnerId, DomainId.New(), $"project-{DomainId.Short(projectId)}", "/repo", null, null, Now));
            session.Events.StartStream<OrchestratorPresenceAggregate>(
                OrchestratorPresenceStreamId.For(node.NodeId, projectId),
                new OrchestratorLaunched(
                    node.NodeId, projectId, "orchestrator", processId, "claude-code", processStartedAt, launchedAt));
            if (withItem)
            {
                session.Events.StartStream<MessageAggregate>(
                    MessageStreamId.ForMessage(DomainId.New(), projectId, 1),
                    new MessageReceived(
                        DomainId.New(), 1, Now, "abcdef0123456789", "project", null,
                        MessageKind.Note.Value, "are you still on the stacked pair?", Now, projectId));
            }

            await session.SaveChangesAsync(cancellationToken);
        }

        // The item's At is its real commit time, so only real time clears the settling window.
        if (withItem)
        {
            await Task.Delay(OrchestratorFeedSelection.SettlingWindow + TimeSpan.FromSeconds(2), cancellationToken);
        }

        return new Seeded(node, projectId, processId, processStartedAt);
    }

    private CourierEngine EngineFor(
        Seeded seeded, IExecutor executor, FakeProcessManager processManager, DaemonOptions? options = null) =>
        new(
            _postgres.Store, seeded.Node, executor, processManager,
            new FakeOrchestratorProcessProbe().Running(seeded.ProcessId, seeded.ProcessStartedAt),
            new UnsupportedProcessElevationReader(),
            Options.Create(options ?? new DaemonOptions()), NullLogger<CourierEngine>.Instance);

    private CourierEngine ElevationEngineFor(
        Seeded seeded,
        FakeOrchestratorProcessProbe probe,
        FakeProcessElevationReader reader,
        IExecutor executor,
        ILogger<CourierEngine>? logger = null) =>
        new(
            _postgres.Store, seeded.Node, executor, new FakeProcessManager(), probe, reader,
            Options.Create(new DaemonOptions()), logger ?? NullLogger<CourierEngine>.Instance);

    /// <summary>A new window registering over the seeded one, the way a re-opened orchestrator does.</summary>
    private async Task RegisterWindowAsync(
        Seeded seeded, int processId, DateTimeOffset processStartedAt, DateTimeOffset launchedAt,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.Append(
            OrchestratorPresenceStreamId.For(seeded.Node.NodeId, seeded.ProjectId),
            new OrchestratorLaunched(
                seeded.Node.NodeId, seeded.ProjectId, "orchestrator", processId, "claude-code", processStartedAt, launchedAt));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<CourierDaySpawnCounter?> DayCounterAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = _postgres.Store.QuerySession();
        return await session.LoadAsync<CourierDaySpawnCounter>(projectId, cancellationToken);
    }

    private async Task SeedStreakAsync(CourierFailureStreak streak, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Store(streak);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A run that already completed as a failure at <paramref name="completedAt"/>, for the gate's elapsed-since-last-courier read.</summary>
    private async Task SeedCompletedFailedRunAsync(
        Seeded seeded, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        Guid runId = DomainId.New();
        await using IDocumentSession session = _postgres.Store.LightweightSession();
        session.Events.StartStream(runId, new CourierRunDispatched(
            runId, seeded.ProjectId, seeded.Node.NodeId, AgentModel.CourierDefault, completedAt.AddSeconds(-30)));
        session.Events.Append(runId, new CourierRunCompleted(runId, false, "failed", completedAt));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static CourierFailureStreak RunningStreak(
        Guid projectId, int count, DateTimeOffset lastFailureAt, DateTimeOffset nextAttemptAt, long carried,
        DateTimeOffset? lastAddressedLaunchAt = null) => new()
    {
        LastAddressedLaunchAt = lastAddressedLaunchAt,
        Id = projectId,
        Count = count,
        StartedAt = lastFailureAt.AddHours(-2),
        LastFailureAt = lastFailureAt,
        LastFailureReason = "hall9k-9f is not reachable",
        HighestCarriedSequence = carried,
        NextAttemptAt = nextAttemptAt,
    };

    [Fact]
    public async Task A_courier_the_daemon_ended_is_followed_at_once_by_a_tick_that_does_not_spawn()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_301, DateTimeOffset.UtcNow.AddMinutes(-5), cts.Token);
        Guid stoppedRun = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream(stoppedRun, new CourierRunDispatched(
                stoppedRun, seeded.ProjectId, seeded.Node.NodeId, AgentModel.CourierDefault, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, seeded.ProjectId, stoppedRun, AgentModel.CourierDefault, CourierEnding.EndedByDaemon,
            "The daemon stopped while this courier was running, and it was stopped with it.",
            drainableThroughSequence: 0, result: null, DateTimeOffset.UtcNow, Waits, failureReason: null, addressedLaunchAt: null, cts.Token);

        CountingExecutor executor = new();
        CourierSweepResult sweep = await EngineFor(seeded, executor, new FakeProcessManager()).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().BeEmpty("an urgent item must not bypass the hold a daemon-ended courier leaves");
        sweep.Should().Be(new CourierSweepResult(0, 0, 0, 0));
    }

    [Fact]
    public async Task A_courier_the_daemon_ended_after_the_window_re_registered_keeps_its_hold_across_the_next_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DateTimeOffset launchedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_303, launchedAt, cts.Token);
        // A streak from the old window's couriers, then a courier dispatched before the window
        // re-registered that the daemon stops after it did.
        await SeedStreakAsync(
            RunningStreak(
                seeded.ProjectId, 3, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow, carried: long.MaxValue,
                lastAddressedLaunchAt: DateTimeOffset.UtcNow.AddMinutes(-31)),
            cts.Token);
        Guid stoppedRun = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream(stoppedRun, new CourierRunDispatched(
                stoppedRun, seeded.ProjectId, seeded.Node.NodeId, AgentModel.CourierDefault, DateTimeOffset.UtcNow.AddMinutes(-10)));
            await session.SaveChangesAsync(cts.Token);
        }

        DateTimeOffset endedAt = DateTimeOffset.UtcNow;
        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, seeded.ProjectId, stoppedRun, AgentModel.CourierDefault, CourierEnding.EndedByDaemon,
            "stopped", drainableThroughSequence: 0, result: null, endedAt, Waits, failureReason: null, addressedLaunchAt: null, cts.Token);

        CourierFailureStreak hold = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        hold.Count.Should().Be(0, "the re-registration already reset the failures the old window earned");
        hold.NextAttemptAt.Should().Be(endedAt.AddMinutes(1));

        CountingExecutor executor = new();
        await EngineFor(seeded, executor, new FakeProcessManager()).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().BeEmpty("the daemon-ended hold must outlive the restart tick, urgent item or not");
        (await StreakAsync(seeded.ProjectId, cts.Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_window_that_re_registers_after_a_daemon_ending_was_recorded_still_leaves_that_ending_its_hold()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_310, DateTimeOffset.UtcNow.AddMinutes(-40), cts.Token);
        await SeedStreakAsync(
            RunningStreak(
                seeded.ProjectId, 3, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow, carried: long.MaxValue,
                lastAddressedLaunchAt: DateTimeOffset.UtcNow.AddMinutes(-40)),
            cts.Token);
        Guid stoppedRun = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream(stoppedRun, new CourierRunDispatched(
                stoppedRun, seeded.ProjectId, seeded.Node.NodeId, AgentModel.CourierDefault, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await session.SaveChangesAsync(cts.Token);
        }

        // The shutdown is recorded while the old registration still stands, so the streak carries
        // its count into the hold; the window registers again only after that write.
        DateTimeOffset endedAt = DateTimeOffset.UtcNow;
        await CourierEngine.RecordOutcomeAsync(
            _postgres.Store, seeded.ProjectId, stoppedRun, AgentModel.CourierDefault, CourierEnding.EndedByDaemon,
            "stopped", drainableThroughSequence: 0, result: null, endedAt, Waits, failureReason: null,
            addressedLaunchAt: null, cts.Token);
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.Append(
                OrchestratorPresenceStreamId.For(seeded.Node.NodeId, seeded.ProjectId),
                new OrchestratorLaunched(
                    seeded.Node.NodeId, seeded.ProjectId, "orchestrator-renamed", seeded.ProcessId, "claude-code",
                    seeded.ProcessStartedAt, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cts.Token);
        }

        CountingExecutor executor = new();
        await EngineFor(seeded, executor, new FakeProcessManager()).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().BeEmpty("the registration resets the failures, not the hold the daemon-ended courier left");
        CourierFailureStreak hold = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        hold.Count.Should().Be(0);
        hold.NextAttemptAt.Should().Be(endedAt.AddMinutes(1));
    }

    [Fact]
    public async Task The_first_tick_after_a_start_pulls_the_next_attempt_in_to_the_base_and_leaves_the_count()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_302, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedCompletedFailedRunAsync(seeded, failedAt, cts.Token);
        // Carried past anything the feed holds, so the urgent item has no early attempt left and
        // only the pulled-in next attempt could ever let it through.
        await SeedStreakAsync(
            RunningStreak(seeded.ProjectId, 9, failedAt, DateTimeOffset.UtcNow.AddMinutes(29), carried: long.MaxValue), cts.Token);

        CountingExecutor executor = new();
        CourierEngine engine = EngineFor(seeded, executor, new FakeProcessManager());
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await engine.SweepOnceAsync(cts.Token);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        CourierFailureStreak pulled = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        pulled.Count.Should().Be(9);
        pulled.NextAttemptAt.Should().BeOnOrAfter(before.AddSeconds(60)).And.BeOnOrBefore(after.AddSeconds(60));
        executor.Spawns.Should().BeEmpty("the pulled-in time is a minute away");

        // Only the first tick since the engine started pulls in: a later one leaves a far time alone.
        DateTimeOffset far = DateTimeOffset.UtcNow.AddMinutes(25);
        await SeedStreakAsync(RunningStreak(seeded.ProjectId, 9, failedAt, far, carried: long.MaxValue), cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        (await StreakAsync(seeded.ProjectId, cts.Token))!.NextAttemptAt.Should().Be(far);

        // A fresh engine is a daemon restart, and pulls in again.
        await EngineFor(seeded, new CountingExecutor(), new FakeProcessManager()).SweepOnceAsync(cts.Token);
        (await StreakAsync(seeded.ProjectId, cts.Token))!.NextAttemptAt.Should().BeBefore(far);
    }

    [Fact]
    public async Task A_streak_built_up_after_the_first_tick_keeps_its_backoff()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(
            48_309, DateTimeOffset.UtcNow.AddHours(-3), cts.Token, withItem: false);
        CourierEngine engine = EngineFor(seeded, new CountingExecutor(), new FakeProcessManager());

        // The first tick finds no streak at all, which is the engine's one chance to pull one in.
        await engine.SweepOnceAsync(cts.Token);

        DateTimeOffset far = DateTimeOffset.UtcNow.AddMinutes(25);
        await SeedStreakAsync(
            RunningStreak(seeded.ProjectId, 6, DateTimeOffset.UtcNow.AddMinutes(-5), far, carried: long.MaxValue), cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        (await StreakAsync(seeded.ProjectId, cts.Token))!.NextAttemptAt.Should().Be(
            far, "a streak this daemon run built up is not the restart the pull-in is for");
    }

    [Fact]
    public async Task The_streak_survives_a_fresh_engine_over_the_same_store()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_303, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        await SeedCompletedFailedRunAsync(seeded, failedAt, cts.Token);
        await SeedStreakAsync(
            RunningStreak(seeded.ProjectId, 5, failedAt, failedAt.AddMinutes(16), carried: long.MaxValue), cts.Token);

        CountingExecutor executor = new();
        await EngineFor(seeded, executor, new FakeProcessManager()).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().BeEmpty("the restarted engine read the streak back and is still waiting");
        CourierFailureStreak streak = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        streak.Count.Should().Be(5);
        streak.LastFailureReason.Should().Be("hall9k-9f is not reachable");
    }

    [Fact]
    public async Task A_window_registered_after_the_last_failure_resets_the_streak_and_spawns()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_304, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await SeedStreakAsync(
            RunningStreak(seeded.ProjectId, 7, failedAt, DateTimeOffset.UtcNow.AddMinutes(25), carried: long.MaxValue), cts.Token);

        CountingExecutor executor = new();
        await EngineFor(seeded, executor, new FakeProcessManager()).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().ContainSingle("the new window's address was never tried, so the old streak's wait is gone");
        CourierFailureStreak streak = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        streak.Count.Should().Be(1, "the reset cleared the old streak, and this courier's own failure started a new one");
    }

    [Fact]
    public async Task A_window_registered_before_the_last_failure_leaves_the_streak_waiting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_305, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        await SeedCompletedFailedRunAsync(seeded, failedAt, cts.Token);
        await SeedStreakAsync(
            RunningStreak(seeded.ProjectId, 7, failedAt, failedAt.AddMinutes(30), carried: long.MaxValue), cts.Token);

        CountingExecutor executor = new();
        await EngineFor(seeded, executor, new FakeProcessManager()).SweepOnceAsync(cts.Token);

        executor.Spawns.Should().BeEmpty();
        (await StreakAsync(seeded.ProjectId, cts.Token))!.Count.Should().Be(7);
    }

    [Fact]
    public async Task An_urgent_item_no_failed_courier_carried_gets_one_early_attempt_and_no_second_bypass()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_306, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SeedCompletedFailedRunAsync(seeded, failedAt, cts.Token);
        // Six failures in: the next scheduled try is half an hour away, and no courier of the
        // streak ever carried the urgent item that has just landed.
        await SeedStreakAsync(
            RunningStreak(seeded.ProjectId, 6, failedAt, DateTimeOffset.UtcNow.AddMinutes(28), carried: 0), cts.Token);

        CountingExecutor executor = new();
        CourierEngine engine = EngineFor(seeded, executor, new FakeProcessManager());
        CourierSweepResult first = await engine.SweepOnceAsync(cts.Token);

        executor.Spawns.Should().ContainSingle("the uncarried urgent item bypasses the thirty minute wait once");
        first.Failed.Should().Be(1);
        CourierFailureStreak afterEarly = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        afterEarly.Count.Should().Be(7, "the early attempt does not reset the doubling, it took the next step up");
        afterEarly.StartedAt.Should().Be(failedAt.AddHours(-2));
        afterEarly.HighestCarriedSequence.Should().BeGreaterThan(0);

        CourierSweepResult second = await engine.SweepOnceAsync(cts.Token);

        executor.Spawns.Should().ContainSingle("the item was carried by the failed early attempt, so it has no second bypass");
        second.Should().Be(new CourierSweepResult(0, 0, 0, 0));
    }

    [Fact]
    public async Task A_courier_that_times_out_records_the_timeout_as_its_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_307, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        FakeProcessManager processManager = new();
        processManager.MarkAlive(60_000);

        CountingExecutor executor = new();
        await EngineFor(seeded, executor, processManager, new DaemonOptions { CourierTimeout = TimeSpan.FromSeconds(2) })
            .SweepOnceAsync(cts.Token);

        CourierFailureStreak streak = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        streak.Count.Should().Be(1);
        streak.LastFailureReason.Should().Be("timed out after 2s");
    }

    [Fact]
    public async Task A_courier_whose_session_left_no_result_records_that_it_ended_without_an_outcome_line()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        DateTimeOffset launchedAt = DateTimeOffset.UtcNow.AddHours(-3);
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_308, launchedAt, cts.Token);

        await EngineFor(seeded, new CountingExecutor(), new FakeProcessManager()).SweepOnceAsync(cts.Token);

        CourierFailureStreak streak = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        streak.Count.Should().Be(1);
        streak.LastAddressedLaunchAt.Should().Be(
            launchedAt, "the reset compares against the registration the courier took its address from, not its dispatch time");
        streak.LastFailureReason.Should().Be("ended without an outcome line");
        streak.NextAttemptAt.Should().Be(streak.LastFailureAt.AddMinutes(1));
    }

    [Fact]
    public async Task A_courier_whose_spawn_throws_records_a_daemon_error_as_its_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_311, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);

        await EngineFor(seeded, new ThrowingExecutor(), new FakeProcessManager()).SweepOnceAsync(cts.Token);

        CourierFailureStreak streak = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        streak.Count.Should().Be(1);
        streak.LastFailureReason.Should().StartWith(
            "daemon error: ", "a spawn that throws is the daemon's own failure, not the session's");
    }

    [Fact]
    public async Task An_elevated_window_under_a_non_elevated_daemon_spawns_nothing_and_spends_no_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_401, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        FakeProcessElevationReader reader = new FakeProcessElevationReader()
            .Self(ProcessElevation.NotElevated).With(seeded.ProcessId, ProcessElevation.Elevated);
        CountingExecutor executor = new();
        CourierEngine engine = ElevationEngineFor(
            seeded, new FakeOrchestratorProcessProbe().Running(seeded.ProcessId, seeded.ProcessStartedAt), reader, executor);

        CourierSweepResult sweep = await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        sweep.Should().Be(new CourierSweepResult(Delivered: 0, Failed: 0, NoAdapter: 0, DayCapHits: 0));
        executor.Spawns.Should().BeEmpty("an urgent item waits too, since the courier could not reach the window");
        (await DayCounterAsync(seeded.ProjectId, cts.Token)).Should().BeNull("a blocked tick spends none of the day cap");
        (await StreakAsync(seeded.ProjectId, cts.Token)).Should().BeNull("a blocked tick is not a failure");
    }

    [Theory]
    [InlineData(ProcessElevation.Elevated, ProcessElevation.NotElevated)]
    [InlineData(ProcessElevation.NotElevated, ProcessElevation.Unknown)]
    public async Task Every_pairing_but_the_blocked_one_spawns_as_before(ProcessElevation daemon, ProcessElevation window)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_402, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        FakeProcessElevationReader reader = new FakeProcessElevationReader().Self(daemon).With(seeded.ProcessId, window);
        CountingExecutor executor = new();

        await ElevationEngineFor(
                seeded, new FakeOrchestratorProcessProbe().Running(seeded.ProcessId, seeded.ProcessStartedAt), reader, executor)
            .SweepOnceAsync(cts.Token);

        executor.Spawns.Should().ContainSingle();
    }

    [Fact]
    public async Task A_blocked_first_tick_after_a_start_leaves_the_pull_in_to_the_first_unblocked_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_403, DateTimeOffset.UtcNow.AddHours(-3), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedCompletedFailedRunAsync(seeded, failedAt, cts.Token);
        DateTimeOffset far = DateTimeOffset.UtcNow.AddMinutes(29);
        await SeedStreakAsync(RunningStreak(seeded.ProjectId, 9, failedAt, far, carried: long.MaxValue), cts.Token);

        const int unelevatedWindow = 48_404;
        DateTimeOffset unelevatedStartedAt = DateTimeOffset.UtcNow.AddHours(-2);
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(seeded.ProcessId, seeded.ProcessStartedAt).Running(unelevatedWindow, unelevatedStartedAt);
        FakeProcessElevationReader reader = new FakeProcessElevationReader()
            .Self(ProcessElevation.NotElevated)
            .With(seeded.ProcessId, ProcessElevation.Elevated)
            .With(unelevatedWindow, ProcessElevation.NotElevated);
        CountingExecutor executor = new();
        CourierEngine engine = ElevationEngineFor(seeded, probe, reader, executor);

        await engine.SweepOnceAsync(cts.Token);
        (await StreakAsync(seeded.ProjectId, cts.Token))!.NextAttemptAt.Should().Be(
            far, "a blocked tick is not the first tick the restart pull-in is for");

        // Launched before the last failure, so this registration resets nothing; only the pull-in can move the time.
        await RegisterWindowAsync(seeded, unelevatedWindow, unelevatedStartedAt, unelevatedStartedAt, cts.Token);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await engine.SweepOnceAsync(cts.Token);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        CourierFailureStreak pulled = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        pulled.Count.Should().Be(9);
        pulled.NextAttemptAt.Should().BeOnOrAfter(before.AddSeconds(60)).And.BeOnOrBefore(after.AddSeconds(60));
        executor.Spawns.Should().BeEmpty();
    }

    [Fact]
    public async Task A_blocked_tick_leaves_a_new_window_s_reset_to_the_first_unblocked_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_405, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        DateTimeOffset nextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(25);
        await SeedStreakAsync(RunningStreak(seeded.ProjectId, 7, failedAt, nextAttemptAt, carried: long.MaxValue), cts.Token);

        const int unelevatedWindow = 48_406;
        DateTimeOffset unelevatedStartedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(seeded.ProcessId, seeded.ProcessStartedAt).Running(unelevatedWindow, unelevatedStartedAt);
        FakeProcessElevationReader reader = new FakeProcessElevationReader()
            .Self(ProcessElevation.NotElevated)
            .With(seeded.ProcessId, ProcessElevation.Elevated)
            .With(unelevatedWindow, ProcessElevation.NotElevated);
        CountingExecutor executor = new();
        CourierEngine engine = ElevationEngineFor(seeded, probe, reader, executor);

        await engine.SweepOnceAsync(cts.Token);
        CourierFailureStreak held = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        held.Count.Should().Be(7, "the elevated window registered after the failure, but a blocked tick resets nothing");
        held.NextAttemptAt.Should().Be(nextAttemptAt);
        executor.Spawns.Should().BeEmpty();

        await RegisterWindowAsync(seeded, unelevatedWindow, unelevatedStartedAt, unelevatedStartedAt, cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        executor.Spawns.Should().ContainSingle("the first unblocked tick applies the reset and spawns");
        (await StreakAsync(seeded.ProjectId, cts.Token))!.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_blocked_tick_leaves_a_stranded_run_s_adoption_to_the_first_unblocked_tick()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(48_411, DateTimeOffset.UtcNow.AddMinutes(-1), cts.Token);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        DateTimeOffset nextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(25);
        await SeedStreakAsync(RunningStreak(seeded.ProjectId, 7, failedAt, nextAttemptAt, carried: long.MaxValue), cts.Token);

        // Past CourierTimeout plus the stranded-run grace with no completion: what a daemon that
        // stopped mid-courier leaves for the restarted one to adopt.
        Guid strandedRunId = DomainId.New();
        await using (IDocumentSession session = _postgres.Store.LightweightSession())
        {
            session.Events.StartStream(strandedRunId, new CourierRunDispatched(
                strandedRunId, seeded.ProjectId, seeded.Node.NodeId, AgentModel.CourierDefault,
                DateTimeOffset.UtcNow.AddMinutes(-6)));
            await session.SaveChangesAsync(cts.Token);
        }

        const int unelevatedWindow = 48_412;
        DateTimeOffset unelevatedStartedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(seeded.ProcessId, seeded.ProcessStartedAt).Running(unelevatedWindow, unelevatedStartedAt);
        FakeProcessElevationReader reader = new FakeProcessElevationReader()
            .Self(ProcessElevation.NotElevated)
            .With(seeded.ProcessId, ProcessElevation.Elevated)
            .With(unelevatedWindow, ProcessElevation.NotElevated);
        CountingExecutor executor = new();
        CourierEngine engine = ElevationEngineFor(seeded, probe, reader, executor);

        await engine.SweepOnceAsync(cts.Token);
        CourierFailureStreak held = (await StreakAsync(seeded.ProjectId, cts.Token))!;
        held.Count.Should().Be(7, "adopting the stranded run would write its daemon-ended hold, which a blocked tick defers");
        held.NextAttemptAt.Should().Be(nextAttemptAt);
        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            CourierRunDetails? run = await query.LoadAsync<CourierRunDetails>(strandedRunId, cts.Token);
            run!.CompletedAt.Should().BeNull("the adoption waits for the first unblocked tick");
        }

        await RegisterWindowAsync(seeded, unelevatedWindow, unelevatedStartedAt, unelevatedStartedAt, cts.Token);
        await engine.SweepOnceAsync(cts.Token);

        await using (IQuerySession query = _postgres.Store.QuerySession())
        {
            CourierRunDetails? run = await query.LoadAsync<CourierRunDetails>(strandedRunId, cts.Token);
            run!.CompletedAt.Should().NotBeNull("the first unblocked tick adopts the stranded run");
        }

        (await StreakAsync(seeded.ProjectId, cts.Token))!.Count.Should().Be(
            0, "the adoption applies the new window's reset and keeps its own daemon-ended hold");
        executor.Spawns.Should().BeEmpty("the daemon-ended hold still stands");
    }

    [Fact]
    public async Task A_window_s_elevation_is_read_once_per_window_process()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(
            48_407, DateTimeOffset.UtcNow.AddHours(-3), cts.Token, withItem: false);
        const int nextWindow = 48_408;
        DateTimeOffset nextStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(seeded.ProcessId, seeded.ProcessStartedAt).Running(nextWindow, nextStartedAt);
        FakeProcessElevationReader reader = new FakeProcessElevationReader()
            .Self(ProcessElevation.NotElevated)
            .With(seeded.ProcessId, ProcessElevation.Elevated)
            .With(nextWindow, ProcessElevation.Elevated);
        CourierEngine engine = ElevationEngineFor(seeded, probe, reader, new CountingExecutor());

        await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        reader.ReadsOf(Environment.ProcessId).Should().Be(1, "the daemon's own elevation is read once, at start");
        reader.ReadsOf(seeded.ProcessId).Should().Be(1);

        await RegisterWindowAsync(seeded, nextWindow, nextStartedAt, nextStartedAt, cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        reader.ReadsOf(nextWindow).Should().Be(1, "a new window process is read once too");
        reader.ReadsOf(seeded.ProcessId).Should().Be(1);
    }

    [Fact]
    public async Task The_block_is_logged_once_per_window_process_whether_or_not_items_wait()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        Seeded seeded = await SeedProjectWithUrgentItemAsync(
            48_409, DateTimeOffset.UtcNow.AddHours(-3), cts.Token, withItem: false);
        const int nextWindow = 48_410;
        DateTimeOffset nextStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        FakeOrchestratorProcessProbe probe = new FakeOrchestratorProcessProbe()
            .Running(seeded.ProcessId, seeded.ProcessStartedAt).Running(nextWindow, nextStartedAt);
        FakeProcessElevationReader reader = new FakeProcessElevationReader()
            .Self(ProcessElevation.NotElevated)
            .With(seeded.ProcessId, ProcessElevation.Elevated)
            .With(nextWindow, ProcessElevation.Elevated);
        ListLogger<CourierEngine> logger = new();
        CourierEngine engine = ElevationEngineFor(seeded, probe, reader, new CountingExecutor(), logger);
        int BlockLines() => logger.Lines.Count(line => line.Contains("is elevated and this daemon is not"));

        await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        BlockLines().Should().Be(1);
        logger.Lines.Should().ContainSingle(line => line.Contains("is elevated and this daemon is not")).Which.Should()
            .Contain($"pid {seeded.ProcessId}").And.Contain("'orchestrator'")
            .And.Contain(CourierElevationFix.For(autostartRegistered: null));

        await RegisterWindowAsync(seeded, nextWindow, nextStartedAt, nextStartedAt, cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        await engine.SweepOnceAsync(cts.Token);
        BlockLines().Should().Be(2, "a new window at the same mismatch is a new episode");

        probe.Gone(nextWindow);
        await engine.SweepOnceAsync(cts.Token);
        probe.Running(nextWindow, nextStartedAt);
        await engine.SweepOnceAsync(cts.Token);
        BlockLines().Should().Be(3, "a tick that found the window no longer live ended the episode");

        ListLogger<CourierEngine> restarted = new();
        await ElevationEngineFor(seeded, probe, reader, new CountingExecutor(), restarted).SweepOnceAsync(cts.Token);
        restarted.Lines.Should().ContainSingle(line => line.Contains("is elevated and this daemon is not"));
    }

    /// <summary>Records every spawn request without ever completing one — any call here at all
    /// is itself the failure the test above exists to catch.</summary>
    private sealed class CountingExecutor : IExecutor
    {
        public List<AgentSpawnRequest> Spawns { get; } = [];

        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            return Task.FromResult(new SpawnedAgent(60_000, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Fails every spawn, the way a missing claude binary would.</summary>
    private sealed class ThrowingExecutor : IExecutor
    {
        public Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the agent binary could not be started");
    }
}
