using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="LaunchHoldEngine"/>'s own node-stream bookkeeping (task: a session that exits at
/// once with no work done is treated as the node failing to launch sessions), and
/// <see cref="NodeLaunchHoldEpisodes"/>'s replay of it — apart from <c>RunSupervisor</c>/
/// <c>ReviewEngine</c>'s own classification, which <c>RunSupervisorTests</c> and
/// <c>ReviewEngineTests</c> cover end to end.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class LaunchHoldEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 4, 22, 0, TimeSpan.Zero);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// This class's every test shares one reused node (<c>NodeBootstrapSeed.NewNodeAsync</c>'s
    /// own doc: bootstrap finds the one existing node row for this machine name rather than
    /// minting a fresh one), and the launch hold is genuinely node-scoped, durable state — unlike
    /// a spend budget's own period-keyed sum or a task's own fresh id, there is no per-test
    /// discriminator that isolates it. A test that raises a hold and does not itself clear it
    /// (proving the raised or ongoing shape is the point of several of these) would otherwise leak
    /// into whichever sibling xUnit's own test-case orderer schedules next — this runs after
    /// every test, pass or fail, so none of them has to remember to clean up after itself.
    /// </summary>
    public async Task DisposeAsync()
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(postgres.Store, CancellationToken.None);
        await new LaunchHoldEngine(postgres.Store, NullLogger<LaunchHoldEngine>.Instance)
            .ClearIfActiveAsync(node.NodeId, CancellationToken.None);
    }

    [Fact]
    public async Task Raising_twice_for_different_runs_logs_and_appends_the_raise_only_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        // Read before this test's own actions rather than asserted as an absolute count: this
        // class's tests all share one reused node stream (NodeBootstrapSeed's own doc), so a
        // sibling test's own already-cleared episode still leaves its own NodeLaunchHoldRaised
        // event in this same stream's history — DisposeAsync clears the ACTIVE doc between
        // tests, never the past.
        int raisedBefore;
        await using (IQuerySession before = store.QuerySession())
        {
            raisedBefore = (await before.Events.FetchStreamAsync(node.NodeId, token: cts.Token))
                .Select(e => e.Data).OfType<NodeLaunchHoldRaised>().Count();
        }

        Guid firstRun = DomainId.New();
        Guid secondRun = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, firstRun, "Failed to authenticate", cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, secondRun, "GitHub fetch timed out", cts.Token);

        NodeDetails? details = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        details.Should().NotBeNull();
        details!.LaunchHoldActive.Should().BeTrue();
        details.LaunchHoldCauseText.Should().Be(
            "Failed to authenticate", "the cause is fixed at the raise; a second, different cause joining does not overwrite it");
        details.LaunchHoldRunCount.Should().Be(2);
        details.LaunchHoldProbeCount.Should().Be(0);

        await using IQuerySession query = store.QuerySession();
        List<object> nodeEvents = [.. (await query.Events.FetchStreamAsync(node.NodeId, token: cts.Token)).Select(e => e.Data)];
        nodeEvents.OfType<NodeLaunchHoldRaised>().Count().Should().Be(
            raisedBefore + 1, "the second zero-work session joins the standing hold; it must not raise a second episode");
        nodeEvents.OfType<NodeLaunchHoldRunHeld>().Where(e => e.RunId == firstRun || e.RunId == secondRun).Should().HaveCount(2);

        IReadOnlyList<RunDetails> held = await engine.HeldRunsAsync(node.NodeId, cts.Token);
        held.Should().BeEmpty("LaunchHoldEngine only ever appends to the node stream — the run's own RunLaunchHeld event, which RunDetails.State keys on, is the caller's job");
    }

    /// <summary>
    /// "raising and clearing each log exactly one warn line in h9kd.log" (acceptance): three runs
    /// joining the same episode must never turn into three warn lines, and a probe in between
    /// must not add one of its own.
    /// </summary>
    [Fact]
    public async Task Raising_and_clearing_each_log_exactly_one_warn_line()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        ListLogger<LaunchHoldEngine> logger = new();
        LaunchHoldEngine engine = new(store, logger);

        Guid firstRun = DomainId.New();
        Guid secondRun = DomainId.New();
        Guid thirdRun = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, firstRun, "Failed to authenticate", cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, secondRun, "Failed to authenticate", cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, thirdRun, "Failed to authenticate", cts.Token);
        await engine.RecordProbeAsync(node.NodeId, firstRun, cts.Token);
        await engine.ClearIfActiveAsync(node.NodeId, cts.Token);

        logger.Entries.Count(entry => entry.Level == LogLevel.Warning).Should().Be(
            2, "one warn line for the raise, whatever else joins it, and one for the clear — never one per run or per probe");
    }

    [Fact]
    public async Task Clearing_when_nothing_is_active_is_a_silent_no_op()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        (await engine.ClearIfActiveAsync(node.NodeId, cts.Token)).Should().BeFalse();

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamAsync(node.NodeId, token: cts.Token))
            .Select(e => e.Data).OfType<NodeLaunchHoldCleared>().Should().BeEmpty();
    }

    [Fact]
    public async Task Clearing_an_active_hold_resets_the_published_state_for_the_next_episode()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        Guid runId = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, runId, "Failed to authenticate", cts.Token);
        await engine.RecordProbeAsync(node.NodeId, runId, cts.Token);

        (await engine.ClearIfActiveAsync(node.NodeId, cts.Token)).Should().BeTrue();

        NodeDetails? details = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        details!.LaunchHoldActive.Should().BeFalse();
        details.LaunchHoldCauseText.Should().BeEmpty();
        details.LaunchHoldRaisedAt.Should().BeNull();

        // A second, later episode starts clean rather than inheriting the first's counts.
        Guid laterRun = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, laterRun, "GitHub fetch timed out", cts.Token);
        NodeDetails? second = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        second!.LaunchHoldRunCount.Should().Be(1, "the second episode's own count, not carried over from the first");
        second.LaunchHoldProbeCount.Should().Be(0);
    }

    [Fact]
    public async Task The_oldest_held_run_is_the_one_the_probe_would_relaunch_next()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid older = DomainId.New();
        Guid newer = DomainId.New();
        await SeedLaunchHeldRunAsync(store, node, older, Now, cts.Token);
        await SeedLaunchHeldRunAsync(store, node, newer, Now.AddMinutes(5), cts.Token);

        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);
        RunDetails? oldest = await engine.OldestHeldRunAsync(node.NodeId, cts.Token);
        oldest.Should().NotBeNull();
        oldest!.Id.Should().Be(older, "the longest-waiting held run is probed first, so one persistently dead run never starves the others");
    }

    /// <summary>
    /// Reconstructs a completed episode's start, end, and counts from the raw node stream —
    /// the "queryable from the node stream afterwards" half of the feature: the published
    /// <see cref="NodeDetails"/> doc resets on the next raise, so only the stream itself still
    /// answers for a hold that already cleared.
    /// </summary>
    [Fact]
    public async Task A_cleared_episode_is_still_answerable_from_the_node_stream_afterwards()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        // This class's tests all share one reused node stream (NodeBootstrapSeed's own doc), so a
        // sibling test's own already-cleared episode still stays in this same stream's history —
        // counted here, before this test's own two episodes, rather than asserted as an absolute
        // position.
        int episodesBefore;
        await using (IQuerySession before = store.QuerySession())
        {
            episodesBefore = (await NodeLaunchHoldEpisodes.ReadAsync(before, node.NodeId, cts.Token)).Count;
        }

        Guid firstRun = DomainId.New();
        Guid secondRun = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, firstRun, "Failed to authenticate", cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, secondRun, "Failed to authenticate", cts.Token);
        await engine.RecordProbeAsync(node.NodeId, firstRun, cts.Token);
        await engine.RecordProbeAsync(node.NodeId, firstRun, cts.Token);
        await engine.ClearIfActiveAsync(node.NodeId, cts.Token);

        // A second episode, still standing when the stream is read — proves an ongoing episode
        // reports no end rather than being silently dropped from the list.
        Guid thirdRun = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, thirdRun, "GitHub fetch timed out", cts.Token);

        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<NodeLaunchHoldEpisode> episodes =
            [.. (await NodeLaunchHoldEpisodes.ReadAsync(query, node.NodeId, cts.Token)).Skip(episodesBefore)];

        episodes.Should().HaveCount(2);
        NodeLaunchHoldEpisode first = episodes[0];
        first.CauseText.Should().Be("Failed to authenticate");
        first.RunsHeld.Should().Be(2, "two distinct runs joined the first episode");
        first.Probes.Should().Be(2);
        first.IsOngoing.Should().BeFalse();
        first.ClearedAt.Should().NotBeNull();

        NodeLaunchHoldEpisode second = episodes[1];
        second.CauseText.Should().Be("GitHub fetch timed out");
        second.RunsHeld.Should().Be(1);
        second.Probes.Should().Be(0);
        second.IsOngoing.Should().BeTrue("the second episode is still standing when the stream was read");
        second.ClearedAt.Should().BeNull();
    }

    /// <summary>
    /// The CLI-side reader (task: a session that exits at once with no work done is treated as
    /// the node failing to launch sessions — the "h9k status shows one needs-you line naming the
    /// cause text and the likely fix" and "h9k daemon status shows the hold with its start time
    /// and probe count" acceptance criteria): <see cref="Hall9k.Cli.Commands.LaunchHoldStatus"/>
    /// reads the same <see cref="NodeDetails"/> doc <see cref="LaunchHoldEngine"/> writes, finding
    /// this machine's own node by name exactly as <c>DispatchPressure</c> does.
    /// </summary>
    [Fact]
    public async Task The_cli_reads_the_same_hold_state_the_engine_published()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        Guid runId = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, runId, "Failed to authenticate", cts.Token);
        await engine.RecordProbeAsync(node.NodeId, runId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        Hall9k.Cli.Commands.LaunchHoldStatus? status = await Hall9k.Cli.Commands.LaunchHoldStatus.ReadAsync(query, cts.Token);

        status.Should().NotBeNull();
        status!.Active.Should().BeTrue();
        status.CauseText.Should().Be("Failed to authenticate");
        status.ProbeCount.Should().Be(1);
        status.HeldRunCount.Should().Be(1);
        status.NeedsYouLine.Should().Contain("NEEDS YOU").And.Contain("Failed to authenticate").And.Contain(
            "Sign in to the agent CLI on this node", "the acceptance criterion's own example fix for an authentication failure");
        status.DaemonStatusLine.Should().Contain("launch hold:").And.Contain("1 probe(s)").And.Contain("1 run(s) waiting");
    }

    private static async Task SeedLaunchHeldRunAsync(
        DocumentStore store, NodeContext node, Guid runId, DateTimeOffset heldAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, DomainId.New(), node.NodeId, node.OwnerId, 1, DomainId.New(),
            "/wt/held", "task/held", ExecutorMode.Subscription, heldAt));
        session.Events.Append(runId, new RunProcessStarted(runId, 4482, heldAt));
        session.Events.Append(runId, new RunLaunchHeld(runId, "Failed to authenticate", heldAt));
        await session.SaveChangesAsync(cancellationToken);
    }
}
