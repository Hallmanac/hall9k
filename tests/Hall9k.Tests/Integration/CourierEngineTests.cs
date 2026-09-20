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
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, delivered: true, "Delivered.",
            drainableThroughSequence: 42, result, Now.AddSeconds(30), cts.Token);

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
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, delivered: false,
            "The session ended without a delivered marker.", drainableThroughSequence: 42, result: null,
            Now.AddSeconds(30), cts.Token);

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
            _postgres.Store, projectId, runId, AgentModel.CourierDefault, delivered: false,
            "The session ended without a delivered marker. It said: I couldn't find that tool.",
            drainableThroughSequence: 7, result, Now.AddSeconds(10), cts.Token);

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
            store, node, executor, processManager, probe, Options.Create(new DaemonOptions()),
            NullLogger<CourierEngine>.Instance);

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
            store, node, executor, processManager, probe, Options.Create(new DaemonOptions()),
            NullLogger<CourierEngine>.Instance);

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
}
