using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.ValueObjects;
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

    /// <summary>
    /// <see cref="LaunchHoldEngine.ClearIfEvidencedAsync"/> must ignore a completion whose own
    /// session started before the hold's own raise (independent pre-PR review, cycle 1,
    /// adversarial lens): that session was already running when the outage began, so its eventual
    /// completion — whatever it reports — proves nothing about whether a fresh launch works right
    /// now. Only a session started at or after the raise counts as fresh evidence.
    /// </summary>
    [Fact]
    public async Task ClearIfEvidencedAsync_ignores_a_session_that_predates_the_holds_own_raise()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        Guid runId = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, runId, "Failed to authenticate", cts.Token);
        NodeDetails? hold = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        DateTimeOffset raisedAt = hold!.LaunchHoldRaisedAt!.Value;

        (await engine.ClearIfEvidencedAsync(node.NodeId, raisedAt.AddSeconds(-30), cts.Token)).Should().BeFalse(
            "a session that started before the hold's own raise is not fresh evidence");
        (await engine.CurrentHoldAsync(node.NodeId, cts.Token))!.LaunchHoldActive.Should().BeTrue();

        (await engine.ClearIfEvidencedAsync(node.NodeId, raisedAt.AddSeconds(1), cts.Token)).Should().BeTrue(
            "a session started after the hold's own raise is fresh evidence and clears it");
        (await engine.CurrentHoldAsync(node.NodeId, cts.Token))!.LaunchHoldActive.Should().BeFalse();
    }

    /// <summary>
    /// The wedge-open fix (independent pre-PR review, cycle 3, both lenses): once every held run's
    /// own claim moved on, nothing could ever supply evidence again, so the hold must clear rather
    /// than stand forever. And never before that: a run still held keeps it standing.
    /// </summary>
    [Fact]
    public async Task ClearIfNothingLeftHeldAsync_clears_once_every_held_run_moved_on_and_not_before()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);
        await RetireEveryHeldRunAsync(store, engine, node, cts.Token);

        Guid runId = DomainId.New();
        await SeedLaunchHeldRunAsync(store, node, runId, Now, cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, runId, "Failed to authenticate", cts.Token);

        (await engine.ClearIfNothingLeftHeldAsync(node.NodeId, cts.Token)).Should().BeFalse(
            "the run is still LaunchHeld; the probe can still relaunch it");
        (await engine.CurrentHoldAsync(node.NodeId, cts.Token))!.LaunchHoldActive.Should().BeTrue();

        await SupersedeAsync(store, runId, cts.Token);

        (await engine.ClearIfNothingLeftHeldAsync(node.NodeId, cts.Token)).Should().BeTrue(
            "the only held run's claim moved on, so nothing is left that could ever clear this hold with evidence");
        (await engine.CurrentHoldAsync(node.NodeId, cts.Token))!.LaunchHoldActive.Should().BeFalse();
    }

    /// <summary>
    /// The race the force-clear must not lose (independent pre-PR review, cycle 4, adversarial
    /// lens): an unrelated run joins the hold on the node stream before its own
    /// <see cref="RunLaunchHeld"/> lands on the run stream, so for that window no
    /// <see cref="RunState.LaunchHeld"/> query sees it, yet the hold already names it. Clearing
    /// then would reopen the claim gate into a node that just failed another launch.
    /// </summary>
    [Fact]
    public async Task ClearIfNothingLeftHeldAsync_keeps_a_hold_a_run_joined_before_its_own_hold_event_landed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);
        await RetireEveryHeldRunAsync(store, engine, node, cts.Token);

        Guid movedOn = DomainId.New();
        await SeedLaunchHeldRunAsync(store, node, movedOn, Now, cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, movedOn, "Failed to authenticate", cts.Token);
        await SupersedeAsync(store, movedOn, cts.Token);

        // The unrelated run: its session just completed the zero-work way and joined the hold
        // (RunSupervisor.CompleteRunAsync's node-stream step), but its own RunLaunchHeld has not
        // been appended yet, so RunDetails still reads the Running state its session ran in.
        Guid joining = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.StartStream<RunAggregate>(joining, new RunDispatched(
                joining, DomainId.New(), node.NodeId, node.OwnerId, 1, DomainId.New(),
                "/wt/joining", "task/joining", ExecutorMode.Subscription, Now));
            session.Events.Append(joining, new RunProcessStarted(joining, 4485, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        await engine.RaiseOrJoinAsync(node.NodeId, joining, "Failed to authenticate", cts.Token);
        (await engine.HeldRunsAsync(node.NodeId, cts.Token)).Should().BeEmpty(
            "the precondition for the race: no LaunchHeld query can see the joining run yet");

        (await engine.ClearIfNothingLeftHeldAsync(node.NodeId, cts.Token)).Should().BeFalse(
            "the hold names a run that is still live, so its own join is still landing");
        (await engine.CurrentHoldAsync(node.NodeId, cts.Token))!.LaunchHoldActive.Should().BeTrue();

        await SupersedeAsync(store, joining, cts.Token);
        (await engine.ClearIfNothingLeftHeldAsync(node.NodeId, cts.Token)).Should().BeTrue(
            "once that run's own claim moves on too, nothing is left to wait on");
    }

    /// <summary>
    /// The versioned raise (independent pre-PR review, cycle 3, conformance lens): runs finishing
    /// within milliseconds of the same outage used to each read the hold inactive and each append
    /// a raise, logging two warn lines and resetting the run count so an earlier join vanished.
    /// Whatever order these concurrent calls actually interleave in, the outcome must be one
    /// episode naming every run. This is a regression guard, not a deterministic reproduction of
    /// the race: the calls may happen to serialize on a given run.
    /// </summary>
    [Fact]
    public async Task Concurrent_raises_record_one_episode_and_count_every_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        int raisedBefore;
        await using (IQuerySession before = store.QuerySession())
        {
            raisedBefore = (await before.Events.FetchStreamAsync(node.NodeId, token: cts.Token))
                .Select(e => e.Data).OfType<NodeLaunchHoldRaised>().Count();
        }

        Guid[] runs = [DomainId.New(), DomainId.New(), DomainId.New(), DomainId.New()];
        await Task.WhenAll(runs.Select(runId =>
            engine.RaiseOrJoinAsync(node.NodeId, runId, "Failed to authenticate", cts.Token)));

        NodeDetails? details = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        details!.LaunchHoldActive.Should().BeTrue();
        details.LaunchHoldRunIds.Should().BeEquivalentTo(runs, "no concurrent join may be dropped by a second raise resetting the count");

        await using IQuerySession query = store.QuerySession();
        (await query.Events.FetchStreamAsync(node.NodeId, token: cts.Token))
            .Select(e => e.Data).OfType<NodeLaunchHoldRaised>().Count().Should().Be(
                raisedBefore + 1, "concurrent launch failures still raise exactly one episode");
    }

    /// <summary>
    /// A genuine error that is not the zero-work shape waits on a different run's standing hold
    /// rather than spending its one retry, but it is no launch failure itself, so it must never
    /// raise a hold of its own (independent pre-PR review, cycle 4: deciding from a separate read
    /// and then calling <see cref="LaunchHoldEngine.RaiseOrJoinAsync"/> turned that join into a
    /// raise whenever a clear landed in between).
    /// </summary>
    [Fact]
    public async Task JoinIfActiveAsync_joins_a_standing_hold_but_never_raises_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        Guid genuineError = DomainId.New();
        (await engine.JoinIfActiveAsync(node.NodeId, genuineError, cts.Token)).Should().BeFalse(
            "no hold stands, so this is an ordinary error with its own retry to spend");
        (await engine.CurrentHoldAsync(node.NodeId, cts.Token))!.LaunchHoldActive.Should().BeFalse(
            "a genuine error is never itself a launch failure's cause");

        Guid launchFailure = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, launchFailure, "Failed to authenticate", cts.Token);

        (await engine.JoinIfActiveAsync(node.NodeId, genuineError, cts.Token)).Should().BeTrue();
        NodeDetails? hold = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        hold!.LaunchHoldRunIds.Should().BeEquivalentTo([launchFailure, genuineError]);
        hold.LaunchHoldCauseText.Should().Be("Failed to authenticate", "joining never rewrites the episode's own cause");
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
    /// A run left <see cref="RunState.LaunchHeld"/> under its own task's open review lap is one
    /// <c>RunSupervisor.ResumeLaunchHeldRunAsync</c> always refuses to resume while the lap stays
    /// open, so its <see cref="RunDetails.LaunchHeldAt"/> never advances (Copilot review, PR #317):
    /// without this skip, that run would keep winning the plain oldest-first sort forever, starving
    /// every genuinely dead run behind it of its own probe and never letting the hold clear even
    /// once the real outage is gone.
    /// </summary>
    [Fact]
    public async Task OldestHeldRunAsync_skips_a_run_whose_task_has_an_open_review_lap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        // A much older heldAt than the shared Now constant, exactly as
        // A_sentinel_pr_review_runs_own_hold_is_found_by_the_probe does: this class's tests all
        // share one reused node stream and never retire what they seed, so a sibling test's own
        // "older"/"newer" pair at Now/Now.AddMinutes(5) would otherwise race this one for which
        // run the plain LaunchHeldAt sort returns first.
        DateTimeOffset blockedHeldAt = Now.AddYears(-2);
        Guid blockedRun = DomainId.New();
        Guid resumableRun = DomainId.New();
        await SeedLaunchHeldRunUnderOpenReviewLapAsync(store, node, blockedRun, blockedHeldAt, cts.Token);
        await SeedLaunchHeldRunAsync(store, node, resumableRun, blockedHeldAt.AddMinutes(5), cts.Token);

        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);
        RunDetails? oldest = await engine.OldestHeldRunAsync(node.NodeId, cts.Token);
        oldest.Should().NotBeNull();
        oldest!.Id.Should().Be(resumableRun,
            "the older run cannot be resumed while its own review lap stays open, so the probe must skip past it to the run it can actually relaunch");
    }

    /// <summary>
    /// A Now-speed auto-pr-review run carries the ceiling-exempt <see cref="Guid.Empty"/> on
    /// <see cref="RunDetails.NodeId"/>, so the plain <c>NodeId == nodeId</c> filter alone would
    /// never find it once it is held — <see cref="LaunchHoldEngine.HeldRunsAsync"/> must widen for
    /// it exactly as <c>RunSupervisor.SentinelPrReviewCandidatesAsync</c> and
    /// <c>TokenBudgetRetryEngine.SentinelPrReviewCandidatesAsync</c> already do for adoption and
    /// the budget retry sweep (independent pre-PR review, cycle 1, both lenses): without the
    /// widening, such a run is held but the probe never finds it again, and if it is the only run
    /// held, the hold never clears and the dispatcher never claims again.
    /// </summary>
    [Fact]
    public async Task A_sentinel_pr_review_runs_own_hold_is_found_by_the_probe()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        // This class's tests all share one reused node stream (NodeBootstrapSeed's own doc), and
        // a sibling test's own seeded LaunchHeld run is never retired afterward, so a much older
        // heldAt is what makes this run unambiguously the oldest rather than racing whatever a
        // sibling test happened to seed at the shared `Now` constant.
        Guid runId = await SeedSentinelLaunchHeldPrReviewRunAsync(store, node, Now.AddYears(-1), cts.Token);

        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);
        IReadOnlyList<RunDetails> held = await engine.HeldRunsAsync(node.NodeId, cts.Token);
        held.Should().Contain(run => run.Id == runId, "a sentinel pr-review run's own hold must still be found by the probe");

        RunDetails? oldest = await engine.OldestHeldRunAsync(node.NodeId, cts.Token);
        oldest.Should().NotBeNull();
        oldest!.Id.Should().Be(runId, "the sentinel run is the oldest held run, so the probe must find it, not skip past it");
    }

    /// <summary>
    /// Seeds a PrReview task claimed deliberately (the shape <c>AutoPrReviewEngine</c>'s own
    /// "now" speed produces) with its run dispatched under the ceiling-exempt
    /// <see cref="Guid.Empty"/> <c>NodeId</c> sentinel and this node's own <c>DispatchingNodeId</c>,
    /// then held exactly like <see cref="SeedLaunchHeldRunAsync"/> does for an ordinary run.
    /// </summary>
    private static async Task<Guid> SeedSentinelLaunchHeldPrReviewRunAsync(
        DocumentStore store, NodeContext node, DateTimeOffset heldAt, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, DomainId.New(), "Review pull request acme/web#9", ["the verdict is submitted"],
                TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#9"), heldAt, node.OwnerId),
            node.OwnerId, heldAt);
        TaskClaimed claimed = TaskDecider.ClaimDeliberately(
            task, node.OwnerId, runId, heldAt, dependencyOverrideAcknowledged: false);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            "/wt/sentinel-held", "pr/9", ExecutorMode.Subscription, heldAt,
            RunDirectory: "/tmp/sentinel-held-run-dir", DispatchingNodeId: node.NodeId));
        session.Events.Append(runId, new RunProcessStarted(runId, 4483, heldAt));
        session.Events.Append(runId, new RunLaunchHeld(runId, "Failed to authenticate", heldAt));
        await session.SaveChangesAsync(cancellationToken);
        return runId;
    }

    /// <summary>
    /// A run rejoining the same standing episode after a failed probe must not inflate
    /// <see cref="NodeDetails.LaunchHoldRunCount"/> past the number of distinct runs actually
    /// waiting (independent pre-PR review, cycle 1, both lenses): the count is documented as
    /// "distinct runs" and printed as "N run(s) waiting" by both CLI surfaces, and
    /// <see cref="NodeLaunchHoldEpisodes"/>'s own replay already tracks it with a
    /// <c>HashSet&lt;Guid&gt;</c> for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_run_rejoining_after_a_failed_probe_is_not_counted_twice()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);
        LaunchHoldEngine engine = new(store, NullLogger<LaunchHoldEngine>.Instance);

        Guid firstRun = DomainId.New();
        Guid secondRun = DomainId.New();
        await engine.RaiseOrJoinAsync(node.NodeId, firstRun, "Failed to authenticate", cts.Token);
        await engine.RecordProbeAsync(node.NodeId, firstRun, cts.Token);
        // The probe relaunched firstRun and it failed the same way again — the same run
        // rejoining the still-standing episode, not a second distinct run.
        await engine.RaiseOrJoinAsync(node.NodeId, firstRun, "Failed to authenticate", cts.Token);
        await engine.RaiseOrJoinAsync(node.NodeId, secondRun, "Failed to authenticate", cts.Token);

        NodeDetails? details = await engine.CurrentHoldAsync(node.NodeId, cts.Token);
        details!.LaunchHoldRunCount.Should().Be(
            2, "firstRun rejoined once after a failed probe; only two distinct runs are actually waiting");
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

    /// <summary>
    /// This class's tests all share one reused node (<see cref="DisposeAsync"/>'s own doc), and
    /// several seed LaunchHeld runs they never retire, since proving the held shape is their
    /// point. A test asserting what happens when nothing is left held retires them first rather
    /// than depending on which siblings xUnit happened to run before it.
    /// </summary>
    private static async Task RetireEveryHeldRunAsync(
        DocumentStore store, LaunchHoldEngine engine, NodeContext node, CancellationToken cancellationToken)
    {
        foreach (RunDetails held in await engine.HeldRunsAsync(node.NodeId, cancellationToken))
        {
            await SupersedeAsync(store, held.Id, cancellationToken);
        }
    }

    /// <summary>The claim-moved-on retirement <c>RunSupervisor.ResumeLaunchHeldRunAsync</c> applies to a held run whose task was abandoned or reclaimed.</summary>
    private static async Task SupersedeAsync(DocumentStore store, Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunSuperseded(runId, SupersededByGeneration: 2, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
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

    /// <summary>Like <see cref="SeedLaunchHeldRunAsync"/>, but the held run's own task carries a real, open <c>h9k pr review</c> lap riding on it, the shape <c>RunSupervisor.ResumeLaunchHeldRunAsync</c> refuses to resume.</summary>
    private static async Task SeedLaunchHeldRunUnderOpenReviewLapAsync(
        DocumentStore store, NodeContext node, Guid runId, DateTimeOffset heldAt, CancellationToken cancellationToken)
    {
        Guid taskId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, DomainId.New(), "Review pull request acme/web#11", ["the verdict is submitted"],
                TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#11"), heldAt, node.OwnerId),
            node.OwnerId, heldAt);
        TaskClaimed claimed = TaskDecider.ClaimDeliberately(
            task, node.OwnerId, runId, heldAt, dependencyOverrideAcknowledged: false);
        PullRequestReviewLapOpened lapOpened = new(
            taskId, runId, "/wt/review-lap", "https://github.com/acme/web/pull/11", heldAt, node.OwnerId);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed, lapOpened]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            "/wt/review-lap", "task/review-lap", ExecutorMode.Subscription, heldAt));
        session.Events.Append(runId, new RunProcessStarted(runId, 4484, heldAt));
        session.Events.Append(runId, new RunLaunchHeld(runId, "Failed to authenticate", heldAt));
        await session.SaveChangesAsync(cancellationToken);
    }
}
