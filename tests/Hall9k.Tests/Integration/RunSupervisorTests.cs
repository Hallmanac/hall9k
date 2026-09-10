using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Hall9k.Tests.Fakes;
using Hall9k.Tests.TestSupport;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

// Both classes redirect the process-wide HALL9K_HOME; sharing a collection serializes
// them so one test's home is never yanked out from under the other's tail loop.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class RunSupervisorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // The non-terminal line most scripts below emit first, standing in for the assistant turn a
    // real session streams before its result.
    private const string AssistantLine = """{"type":"assistant"}""";

    // Shaped like a real cached session: nearly all input arrives as cache reads (log #30).
    private const string ResultLine =
        """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":1200,"cache_read_input_tokens":840000,"cache_creation_input_tokens":21000,"output_tokens":300},"total_cost_usd":0.0123}""";

    private readonly string _home = SetTempHome();

    // NewSupervisor always hands ReviewEngine (and PrReviewEngine) a real ClaudeExecutor,
    // regardless of whatever executor a test passes in for the primary/verification path — see
    // NewSupervisor's own construction below. SeedClaimedTaskWithProjectAsync's worktree is a
    // real directory (its own doc comment explains why), so on a machine where `claude` resolves
    // on PATH, any test here whose run actually reaches the review loop would launch a real,
    // billable agent session. Pinning this off-PATH for the whole class keeps the spawn's own
    // shell starting (preserving the race-closing timing that real directory exists for) while
    // `exec` always fails to find the binary, exactly as HeadlessLaunchTests pins it for the
    // same reason.
    private readonly string? _previousClaudePath = PinClaudeBinaryOffPath();
    private readonly List<string> _createdWorktreePaths = [];

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    private static string? PinClaudeBinaryOffPath()
    {
        string? previous = Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH");
        Environment.SetEnvironmentVariable("HALL9K_CLAUDE_PATH", "hall9k-test-binary-that-does-not-exist-xyz");
        return previous;
    }

    [Fact]
    public async Task Fake_agent_stream_is_tailed_to_completion_with_tokens_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        int processId = SpawnFakeAgent(runId,
            FakeAgentScript.New().Pause(0.3).Emit(AssistantLine).Pause(0.3).Emit(ResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);
        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        details.InputTokens.Should().Be(1200);
        details.CacheReadInputTokens.Should().Be(840_000, "cache reads are the bulk of a cached session's input");
        details.CacheCreationInputTokens.Should().Be(21_000);
        details.OutputTokens.Should().Be(300);
        details.CostUsd.Should().Be(0.0123m, "the cost is what the result reported");

        await using IQuerySession query = store.QuerySession();
        var activity = await query.LoadAsync<Hall9k.Domain.Features.Run.Documents.RunActivity>(runId, cts.Token);
        activity!.StreamBytesRead.Should().BeGreaterThan(0, "the tail cursor persists progress");
    }

    /// <summary>
    /// Decisions Log #163: the pull request the build session composed for itself
    /// is a second marked block on the same terminal result the handoff comes from, captured at
    /// the one moment that result is in hand.
    /// </summary>
    [Fact]
    public async Task A_results_pull_request_summary_block_lands_in_the_run_directory()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        const string resultWithSummary =
            """{"type":"result","subtype":"success","is_error":false,"result":"Did the work.\n\nPR SUMMARY:\nTitle: Resolve references in every host\n\nEvery host now uses the shared provider.\n\nHANDOFF:\nNothing surprising here.","usage":{"input_tokens":1,"output_tokens":1}}""";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Emit(resultWithSummary));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node)
            .StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);
        await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        string artifact = RunPaths.PrSummaryFile(RunPaths.GlobalDirectory(runId));
        File.ReadAllText(artifact).Should().Contain("Title: Resolve references in every host")
            .And.Contain("Every host now uses the shared provider.")
            .And.NotContain("Nothing surprising here.", "the handoff is its own artifact and its own parser");
        File.ReadAllText(RunPaths.HandoffFile(RunPaths.GlobalDirectory(runId)))
            .Should().Contain("Nothing surprising here.").And.NotContain("PR SUMMARY:");
    }

    /// <summary>
    /// A session that composed none writes none: unlike the handoff, whose empty file is the
    /// third of three observations closeout reads, nothing downstream of this artifact needs to
    /// tell an absent summary from an empty one.
    /// </summary>
    [Fact]
    public async Task A_result_with_no_summary_block_writes_no_pull_request_summary_at_all()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Emit(ResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node)
            .StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);
        await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        File.Exists(RunPaths.PrSummaryFile(RunPaths.GlobalDirectory(runId))).Should().BeFalse();
        File.Exists(RunPaths.HandoffFile(RunPaths.GlobalDirectory(runId)))
            .Should().BeTrue("the handoff's own three-state contract is untouched by this");
    }

    /// <summary>
    /// A reviewer's own review lap (<c>h9k pr review</c>, Decisions Log #149) is a human's, not
    /// this daemon's. Its run sits Dispatched under the ceiling-exempt <see cref="Guid.Empty"/>
    /// node sentinel with no agent process ever recorded — there is none; the reviewer pasted the
    /// briefing into a session they started themselves. It IS in adoption's candidate set, and
    /// deliberately so (<c>SentinelPrReviewCandidatesAsync</c> widens the sweep for pr-review
    /// runs so a delivered verdict can be finalized at all), which is exactly why the
    /// dispatched-but-never-started arm has to be taught to leave this one alone: without that,
    /// a daemon restart fails a lap a reviewer is in the middle of and deletes the worktree they
    /// were reading in.
    /// </summary>
    [Fact]
    public async Task Startup_adoption_leaves_an_open_review_lap_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedOpenReviewLapAsync(store, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node, new FakeProcessManager());
        await supervisor.AdoptOrphansAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(
            RunState.Dispatched,
            "the lap is still open — a restart must not fail it for having no agent process of its own");
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed, "the reviewer still holds this task");
    }

    /// <summary>
    /// The exclusion above is keyed on the RUN, not on the flag alone. A reviewer can leave a lap
    /// without ever posting a verdict — <c>h9k task release</c>, which a lap's own interactive
    /// claim accepts — and the verdict that closes <c>ReviewLapOpen</c> is unreachable from there
    /// (<c>h9k pr approve</c> refuses a task with no current run). The flag is therefore cleared
    /// wherever a task gives its claim back (<c>TaskAggregate.EndAnyOpenReviewLap</c>), and read
    /// against the run as a second fence — because a stale flag shielded a LATER, automated
    /// pr-review run of the same task from adoption entirely: a dead agent never failed, a live
    /// one never re-monitored, and no <c>TaskLease</c> to expire and rescue it (independent pre-PR
    /// review, cycle 1, adversarial lens). Both halves are asserted here, in the order they
    /// happen.
    /// </summary>
    [Fact]
    public async Task Startup_adoption_does_not_let_an_abandoned_laps_flag_shield_a_later_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid lapRunId) = await SeedOpenReviewLapAsync(store, cts.Token);

        // The reviewer walks away: h9k task release requeues the task, which is the only way to
        // leave a lap without posting a review.
        Guid automatedRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskRequeued requeued = TaskDecider.ReleaseInteractiveClaim(task, Now);
            session.Events.Append(taskId, requeued);
            await session.SaveChangesAsync(cts.Token);

            TaskAggregate requeuedTask = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            requeuedTask.ReviewLapOpen.Should().BeFalse(
                "the verdict that would close the lap is unreachable once the claim is gone, so the release closes it");

            // ... and the daemon then dispatches the automated pr-review run the requeue freed up,
            // which dies with the daemon before it ever records a process.
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                requeuedTask, node.OwnerId, automatedRunId, Now, dependencyOverrideAcknowledged: false);
            session.Events.Append(taskId, claimed);
            session.Events.StartStream<RunAggregate>(automatedRunId, new RunDispatched(
                automatedRunId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                Path.Combine(Path.GetTempPath(), $"hall9k-lap-later-wt-{automatedRunId:N}"), "pr/7",
                ExecutorMode.Subscription, Now,
                RunDirectory: RunPaths.GlobalDirectory(automatedRunId), DispatchingNodeId: node.NodeId));
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node, new FakeProcessManager());
        await supervisor.AdoptOrphansAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails automated = (await query.LoadAsync<RunDetails>(automatedRunId, cts.Token))!;
        automated.State.Should().Be(
            RunState.Failed,
            "this run has no process and no lap of its own — adoption must fail it honestly rather than read a dead lap's flag as 'a reviewer owns it'");
        RunDetails lapRun = (await query.LoadAsync<RunDetails>(lapRunId, cts.Token))!;
        lapRun.State.Should().Be(
            RunState.Superseded,
            "the lap's own run is no longer the task's current one, so adoption retires it as a stale candidate");
    }

    /// <summary>
    /// The good path (task: a do-now session launched by h9k task start is caught within
    /// seconds): a deliberate headless start's session exits with nobody watching, but the
    /// worktree it left behind is clean with a commit beyond its base branch — so the platform
    /// delivers it automatically instead of leaving the task reading "building" forever, exactly
    /// the origin incident (task ef2fefe5) this closes.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_with_a_clean_committed_tree_is_delivered_automatically()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, string worktreePath) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: false, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(ResultLine));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        RunDetails run = await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        run.ExitedUnattendedReason.Should().BeNull("a clean, committed tree needs no human flag");
        run.InputTokens.Should().Be(1200, "the session's own token spend must still be recorded");

        IReadOnlyList<object> events =
            [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunDeliveredAutomatically>().Should().ContainSingle(
            "the automatic delivery must be recorded on the stream, distinctly from a human's own h9k task deliver");
        events.OfType<AgentSessionCompleted>().Should().ContainSingle(
            e => e.DeliveredByNodeId == node.NodeId,
            "the same hand-off h9k task deliver records, carrying the delivering node's real id");

        Directory.Exists(worktreePath).Should().BeTrue();
    }

    /// <summary>
    /// The signal a killed or crashed process leaves behind is strictly weaker than an IsError
    /// result, not stronger (independent pre-PR review, cycle 1, conformance lens): the agent
    /// never got the chance to say whether it considered the work finished, so this must not be
    /// judged more permissively than a reported error just because a checkpoint commit happened to
    /// land on a clean tree moments before the process died. Regression for the origin finding:
    /// the first cut of this handler fell straight into the same tree check the clean-committed
    /// test above exercises whenever the process vanished with no result line at all, auto-
    /// delivering a session nobody ever confirmed had actually finished.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_whose_process_vanishes_without_a_result_is_flagged_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: false, cts.Token);

        // Exits clean with no result line at all — a killed or crashed process, not a reported
        // error — leaving behind the exact same clean, committed tree the good-path test above
        // auto-delivers, so this test can only pass if the flag fires regardless of the tree.
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Exit(0));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        await WaitForEventCountAsync<RunUnattendedExitFlagged>(store, runId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExitedUnattendedReason.Should().NotBeNull(
            "a process that never reported anything must never be judged on the tree it happens to leave behind");
        run.ExitedUnattendedReason.Should().Contain("without ever reporting a result");
        run.ExitedUnattendedDeliverConfirmedRefuses.Should().BeFalse(
            "the tree was never checked, so h9k task deliver is not confirmed to refuse here");
        run.State.Should().BeOneOf(RunState.Dispatched, RunState.Running,
            "the run must stay where h9k task deliver/work/release already expect it — no widened guard needed");

        IReadOnlyList<object> events =
            [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunDeliveredAutomatically>().Should().BeEmpty(
            "a process that vanished without reporting must never sail through auto-delivery on a clean tree alone");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed, "the flag's own levers all require Claimed");
    }

    /// <summary>
    /// The bad path, uncommitted shape: the session exited with nobody watching and left a
    /// modified, uncommitted file — the platform cannot honestly deliver that on the operator's
    /// behalf, so it flags the task needs-you instead of showing "building" indefinitely.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_with_a_dirty_tree_is_flagged_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, string worktreePath) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: true, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(ResultLine));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        await WaitForEventCountAsync<RunUnattendedExitFlagged>(store, runId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExitedUnattendedReason.Should().NotBeNull("the dirty file is exactly what a human needs told about");
        run.ExitedUnattendedReason.Should().Contain("uncommitted");
        run.ExitedUnattendedDeliverConfirmedRefuses.Should().BeTrue(
            "h9k task deliver runs this identical uncommitted-files check and refuses on the same ground");
        run.State.Should().BeOneOf(RunState.Dispatched, RunState.Running,
            "the run must stay where h9k task deliver/work/release already expect it — no widened guard needed");
        run.InputTokens.Should().Be(
            0,
            "a flagged run's tokens are recorded by whichever lever the human ends up using (deliver, work, "
            + "release, abandon, handback), never here too, or the node's spend budget double-counts this "
            + "session");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed, "the three levers this flag names all require Claimed");
    }

    /// <summary>
    /// The other bad-path shape the acceptance criteria name alongside the dirty-tree one
    /// (independent pre-PR review, cycle 1, conformance lens): a clean tree that never committed
    /// anything beyond its base at all. Distinct from the dirty-tree test above — that fixture
    /// has both a commit and a modified file, so it never exercises this arm of
    /// <c>DetectStrandedWorkAsync</c>. Without this test, a regression that mis-read the
    /// no-commit case (for instance treating <c>check.StrandedFiles.Count == 0</c> as "safe to
    /// auto-deliver" instead of <c>check.FailureReason is null</c>) would pass the whole suite
    /// green and auto-deliver a branch holding nothing beyond its base.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_with_no_commits_beyond_base_is_flagged_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: false, dirty: false, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(ResultLine));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        await WaitForEventCountAsync<RunUnattendedExitFlagged>(store, runId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExitedUnattendedReason.Should().NotBeNull(
            "a branch that holds nothing beyond its base is exactly what the no-commit check exists to catch");
        run.ExitedUnattendedReason.Should().Contain("no commits");
        run.ExitedUnattendedDeliverConfirmedRefuses.Should().BeTrue(
            "h9k task deliver runs this identical no-commits-beyond-base check and refuses on the same ground");
        run.State.Should().BeOneOf(RunState.Dispatched, RunState.Running,
            "the run must stay where h9k task deliver/work/release already expect it — no widened guard needed");

        IReadOnlyList<object> events =
            [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunDeliveredAutomatically>().Should().BeEmpty(
            "a branch holding nothing beyond its base must never be auto-delivered");
    }

    /// <summary>
    /// The branch-checkout guard (independent pre-PR review, cycle 1, both lenses):
    /// <c>DetectStrandedWorkAsync</c> counts commits and reads status against whatever HEAD
    /// happens to be, never confirming HEAD is actually checked out on <c>run.Branch</c> — so a
    /// session that died mid-recompose rebase (<c>GIT_SEQUENCE_EDITOR=: git rebase -i
    /// --autosquash</c>) can leave a clean, committed, but DETACHED tree that would otherwise
    /// sail through that check as "safe to auto-deliver" while the branch it actually publishes
    /// (<c>run.Branch</c>) still sits at its pre-rebase tip — gating one tree and shipping a
    /// different one. This proves the extra guard catches exactly that shape instead.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_with_a_clean_but_detached_tree_is_flagged_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) = await SeedDeliberateHeadlessStartTaskAsync(
            store, withTaskCommit: true, dirty: false, cts.Token, detached: true);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(ResultLine));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        await WaitForEventCountAsync<RunUnattendedExitFlagged>(store, runId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExitedUnattendedReason.Should().NotBeNull(
            "a clean but detached tree must never sail through as safe to auto-deliver");
        run.ExitedUnattendedReason.Should().Contain("detached commit");
        run.ExitedUnattendedReason.Should().Contain("task/test");
        run.ExitedUnattendedDeliverConfirmedRefuses.Should().BeTrue(
            "h9k task deliver runs this identical branch-checkout check and refuses on the same ground");
        run.State.Should().BeOneOf(RunState.Dispatched, RunState.Running,
            "the run must stay where h9k task deliver/work/release already expect it — no widened guard needed");

        IReadOnlyList<object> events =
            [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunDeliveredAutomatically>().Should().BeEmpty(
            "gating one tree while the pipeline later publishes a different, pre-rebase one is exactly what "
            + "this guard exists to prevent");
    }

    /// <summary>
    /// The error-result guard (independent pre-PR review, cycle 3, both lenses): AGENTS.md's own
    /// "commit as you go" rule means a checkpoint commit can already sit on a clean, committed
    /// tree by the time the provider reports a token-budget exhaustion — so judging this session
    /// on the tree alone, the way the good-path test above does, would auto-deliver half-finished
    /// work. This mirrors CompleteRunAsync's own identical park (backlog 40): external and
    /// clock-recoverable, not something to deliver or flag.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_with_a_budget_exhausted_result_parks_rather_than_delivers()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: false, cts.Token);

        const string budgetResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Claude AI usage limit reached|1762952400"}""";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Emit(budgetResultLine));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        RunDetails run = await WaitForStateAsync(store, runId, "BudgetParked", cts.Token);

        run.ParkedReason.Should().Be("token budget exhausted - resumes when the subscription window resets");
        run.ExitedUnattendedReason.Should().BeNull("this is a budget park, not the flagged-needs-you path");
        run.NodeId.Should().Be(node.NodeId,
            "moved off the ceiling-exempt sentinel so the ordinary TokenBudgetRetryEngine sweep can find it");

        IReadOnlyList<object> events =
            [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunDeliveredAutomatically>().Should().BeEmpty(
            "an error result must never be judged on the tree it happens to leave behind");
        events.OfType<TokensRecorded>().Should().ContainSingle(
            "the session's own spend is still recorded even though it never reached a human lever");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed, "a budget park keeps the claim intact, exactly like an ordinary dispatch");
    }

    /// <summary>
    /// The non-budget half of the same guard: a plain error result is flagged needs-you rather
    /// than judged on the worktree it left behind, exactly as a dirty or commit-less tree already
    /// is — never silently delivered just because the last checkpoint commit happened to land on
    /// a clean tree moments before the agent's own session errored out.
    /// </summary>
    [Fact]
    public async Task Deliberate_headless_start_with_a_generic_error_result_is_flagged_needs_you()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: false, cts.Token);

        const string errorResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Internal server error"}""";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Emit(errorResultLine));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);
        await WaitForEventCountAsync<RunUnattendedExitFlagged>(store, runId, 1, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExitedUnattendedReason.Should().Contain("error result");
        run.ExitedUnattendedDeliverConfirmedRefuses.Should().BeFalse(
            "this branch never looks at the tree at all, so h9k task deliver is not confirmed to refuse — the "
            + "tree left behind by an error result may well be perfectly clean and committed");
        run.State.Should().BeOneOf(RunState.Dispatched, RunState.Running,
            "the run must stay where h9k task work/handback/release already expect it — no widened guard needed");
        run.InputTokens.Should().Be(
            0, "a flagged run's tokens are recorded by whichever lever the human ends up using, never here too");

        IReadOnlyList<object> events =
            [.. (await store.QuerySession().Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunDeliveredAutomatically>().Should().BeEmpty(
            "an error result must never be judged on the tree it happens to leave behind");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Claimed, "the flag's own levers all require Claimed");
    }

    /// <summary>
    /// The re-entry guard (independent pre-PR review, cycle 1, both lenses): once a human attaches
    /// with <c>h9k task work</c>, <see cref="RunDetails.RegisteredInteractiveSessionName"/> stops
    /// reading null, and this sweep must never again treat that operator's own eventual "closed the
    /// terminal" as this run kind's unattended exit — the claim is attended now, exactly like an
    /// ordinary interactive one.
    /// </summary>
    [Fact]
    public async Task AdoptDeliberateHeadlessStartsAsync_leaves_a_reentered_claim_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: false, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Exit(0));
        await RecordInteractiveSessionStartedAsync(store, runId, taskId, processId, cts.Token);

        // The human re-enters with h9k task work before this sweep ever adopts the original
        // headless agent — the same event, under the operator's own interactive-claim session
        // name rather than the machine-composed build one.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), Now, processId, Environment.MachineName,
                $"{DomainId.Short(taskId)}-interactive-claim"));
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node, new FakeProcessManager());
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);

        supervisor.ActiveCount.Should().Be(0, "a human has re-entered — this sweep must leave the claim alone");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ExitedUnattendedReason.Should().BeNull("nothing has flagged this run — a human is simply attached");
    }

    /// <summary>
    /// The delegation re-entry guard (independent pre-PR review, cycle 3, both lenses):
    /// <c>h9k task delegate</c> records its contractor under this run's own machine-composed
    /// build-session name — the identical name <c>h9k task start</c>'s original agent used — so
    /// <see cref="RunDetails.RegisteredInteractiveSessionName"/> alone cannot tell a delegated
    /// contractor's exit apart from the unattended start's own. Without the
    /// <see cref="RunDetails.PhaseDelegations"/> filter, this sweep would adopt the contractor and
    /// treat its own exit as this run kind's unattended one — auto-delivering (or re-flagging) a
    /// claim <c>h9k task delegate</c> explicitly promises stays the operator's own to finish by
    /// hand with <c>h9k task work</c>.
    /// </summary>
    [Fact]
    public async Task AdoptDeliberateHeadlessStartsAsync_leaves_a_delegated_claim_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId, _) =
            await SeedDeliberateHeadlessStartTaskAsync(store, withTaskCommit: true, dirty: false, cts.Token);

        int contractorProcessId = SpawnFakeAgent(runId, FakeAgentScript.New().Exit(0));
        string buildSessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build);

        // Mirrors TaskDelegateCommand's own append: RunPhaseDelegated alongside
        // InteractiveSessionStarted, both under the run's own build-session name rather than a
        // human's own chosen one.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(
                runId,
                new RunPhaseDelegated(
                    runId, "Handing off for a phase.", Now, node.OwnerId, buildSessionName,
                    DomainId.New().ToString("N"), AgentModel.Unknown),
                new InteractiveSessionStarted(
                    runId, DomainId.New(), Now, contractorProcessId, Environment.MachineName, buildSessionName));
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node, new FakeProcessManager());
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);

        supervisor.ActiveCount.Should().Be(0,
            "a delegated claim stays the operator's own to finish by hand — this sweep must never adopt a "
            + "contractor's exit as the original unattended start's own");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.RegisteredInteractiveSessionName.Should().BeNull(
            "a delegated contractor is recorded under the same machine-composed build name h9k task start's "
            + "own agent used, so this guard alone cannot exclude it");
        run.PhaseDelegations.Should().ContainSingle("the delegation itself is what this sweep must key off instead");
    }

    /// <summary>
    /// The scoping guard (task: a do-now session launched by h9k task start is caught within
    /// seconds): an ordinary sentinel-claimed run that is NOT a deliberate headless start — an
    /// operator's own attended <c>h9k task work</c> claim shares the identical <see cref="Guid.Empty"/>
    /// NodeId sentinel — must never be swept up here. Nothing should attend, deliver, or flag a
    /// claim a human is sitting in front of.
    /// </summary>
    [Fact]
    public async Task AdoptDeliberateHeadlessStartsAsync_leaves_an_ordinary_interactive_claim_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedOpenReviewLapAsync(store, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node, new FakeProcessManager());
        await supervisor.AdoptDeliberateHeadlessStartsAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Dispatched, "a claim this sweep must never touch stays exactly where it was");
        run.ExitedUnattendedReason.Should().BeNull();
        supervisor.ActiveCount.Should().Be(0, "IsDeliberateHeadlessStart is false, so this run was never a candidate");
    }

    // Trimmed from run 01a07574-4db1's own stream.jsonl (discovery cc9b7aec): a build session
    // that spawned a background subagent wrote this 5-turn reaction leg's own result first —
    // before this fix, the daemon read this line and stopped, recording 1,814 output tokens
    // for a session that actually ran 302 turns over 76 minutes.
    private const string SubagentReactionResultLine =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":5,"duration_ms":29137,"total_cost_usd":24.780697800000002,"usage":{"input_tokens":10,"cache_creation_input_tokens":59074,"cache_read_input_tokens":324100,"output_tokens":1814}}""";

    // The same run's own true final line, immediately after the one above.
    private const string WholeSessionResultLine =
        """{"type":"result","subtype":"success","is_error":false,"num_turns":302,"duration_ms":4586641,"total_cost_usd":24.780697800000002,"usage":{"input_tokens":602,"cache_creation_input_tokens":454222,"cache_read_input_tokens":97887837,"output_tokens":177695}}""";

    [Fact]
    public async Task A_stream_holding_two_result_lines_records_the_whole_sessions_usage_and_the_completion_line_matches()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        // The process stays alive between the two lines, the way the real run this is trimmed
        // from does (a background subagent's own reaction leg completes minutes before the
        // session's real terminal line) — proves the monitor waits for the process to actually
        // die instead of acting on the first result line it sees (independent pre-PR review,
        // cycle 3, adversarial lens: a same-tick re-read can't tell these two shapes apart).
        int processId = SpawnFakeAgent(runId,
            FakeAgentScript.New().Emit(SubagentReactionResultLine).Pause(2).Emit(WholeSessionResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);
        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        details.CacheReadInputTokens.Should().Be(97_887_837, "the whole session's own cache reads, not the reaction leg's 324,100");
        details.OutputTokens.Should().Be(177_695, "the whole session's own output, not the reaction leg's 1,814");

        logger.Lines.Should().Contain(
            line => line.Contains("97887837") && line.Contains("177695"),
            "the completion log line must report the same figures TokensRecorded carries");
    }

    [Fact]
    public async Task Daemon_restart_mid_run_adopts_the_orphan_and_completes_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        // Agent outlives the "first daemon": takes ~4s, first monitor is killed after ~1s.
        int processId = SpawnFakeAgent(runId,
            FakeAgentScript.New().Emit(AssistantLine).Pause(4).Emit(ResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        using (CancellationTokenSource firstDaemon = new())
        {
            RunSupervisor doomed = NewSupervisor(store, node);
            doomed.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, firstDaemon.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            firstDaemon.Cancel();
        }

        // The "restarted daemon": adoption finds the live process and resumes tailing.
        RunSupervisor restarted = NewSupervisor(store, node);
        OrphanAdoption adoption = await restarted.AdoptOrphansAsync(cts.Token);
        adoption.RunsAdopted.Should().BeGreaterThanOrEqualTo(1,
            "the catch-up report (Decisions Log #31) counts this adoption");
        // GreaterThanOrEqualTo: this node is shared across the class's tests (node identity
        // is per machine name), so adoption may also resume another test's Verifying stray
        // as a background pipeline; the tail assertions below pin down THIS run's adoption.
        restarted.ActiveCount.Should().BeGreaterThanOrEqualTo(1, "the live orphan must be adopted, not killed (log #7)");

        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        details.InputTokens.Should().Be(1200);
        details.CacheReadInputTokens.Should().Be(840_000);
    }

    /// <summary>
    /// h9k task deliver pushes the branch and appends AgentSessionCompleted on an interactive
    /// run's stream with the delivering node's own id (Decisions Log #103), moving it to
    /// Verifying with no monitor. This proves the pickup half of that hand-off:
    /// ResumeStrandedPipelinesAsync notices the stranded run and starts driving it through the
    /// same pipeline a headless run's own completion would — matched here by the delivering
    /// node's own id, the ordinary <c>NodeId == nodeId</c> branch, exactly as
    /// <c>NodeLoad</c> counts it against that node's own ceiling from this point on.
    /// </summary>
    [Fact]
    public async Task Resume_stranded_pipelines_adopts_an_interactively_delivered_run_by_its_delivering_node()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        DocumentStore store = postgres.Store;

        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = new();
            (task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Interactive delivery test task", ["it completes"],
                    TaskType.Chore, null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, Now);
            session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            // Deliberately no TaskLease: an interactive claim holds no liveness lease.

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
                "/tmp/wt-interactive-test", "task/interactive-test", ExecutorMode.Subscription, Now));
            // The production shape: h9k task deliver stamps its own node id, not the sentinel.
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now, DeliveredByNodeId: node.NodeId));
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node);

        await supervisor.ResumeStrandedPipelinesAsync(cts.Token);

        // GreaterThanOrEqualTo, not equal to (same reasoning as
        // Daemon_restart_mid_run_adopts_the_orphan_and_completes_it above): this node identity
        // is shared across the class's tests, so the sweep may also pick up another test's own
        // stranded Verifying/UnderReview run in the same pass. What this test pins down is that
        // OUR run — now carrying this node's own real id, not the interactive sentinel — was
        // among them, matched by ordinary node ownership.
        supervisor.ActiveCount.Should().BeGreaterThanOrEqualTo(1,
            "delivery stamped this node's own id on the run, so the ordinary NodeId == nodeId match picks it up");

        await using IQuerySession query = store.QuerySession();
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.NodeId.Should().Be(node.NodeId,
            "the delivering node's id replaces the interactive sentinel from AgentSessionCompleted onward");
    }

    [Fact]
    public async Task Agent_dying_without_a_result_fails_run_and_task_and_releases_the_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Exit(1));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Failed", cts.Token);
        details.FailureReason.Should().Contain("without a result");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("failure releases the lease");
    }

    /// <summary>
    /// Catch-up's defect (backlog 39, origin incident 2026-08-21): every adopted case except
    /// ReviewParked used to skip the lease refresh, so the expiry sweep that runs one line
    /// later in startup order requeued the very task adoption had just reattached — two
    /// generations, one worktree, a full review cycle each. Adoption must win outright: the
    /// lease is refreshed before the sweep ever looks, so the same task is never both adopted
    /// and requeued.
    /// </summary>
    [Fact]
    public async Task Catch_up_adoption_of_a_live_process_refreshes_the_lease_so_the_sweep_never_requeues_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        // Long-running agent; by the time the "restarted daemon" adopts it the heartbeat
        // already reads as expired — exactly the sleep-through-restart shape.
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Pause(30));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Store(new TaskLease
            {
                Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1,
                HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            });
            await session.SaveChangesAsync(cts.Token);
        }

        RunSupervisor supervisor = NewSupervisor(store, node);
        OrphanAdoption adoption = await supervisor.AdoptOrphansAsync(cts.Token);
        adoption.RunsAdopted.Should().BeGreaterThanOrEqualTo(1);

        // The sweep gets a process manager that reports every pid dead (Copilot review, PR
        // #30): SweepExpiredLeasesAsync has its own local-liveness check
        // (DispatchEngine.LocalRunProcessIsAlive), and a real UnixProcessManager here would
        // see the still-sleeping agent alive and refresh the lease on that basis alone — the
        // assertions below would pass even with AdoptOrphansAsync's own
        // RefreshAdoptedLeaseAsync deleted. Denying the sweep that signal means the only
        // thing that can keep the lease fresh by the time it runs is adoption's own refresh.
        DaemonOptions options = new() { MaxConcurrentTaskRuns = 500, LeaseTimeout = TimeSpan.FromSeconds(60) };
        DispatchEngine engine = new(
            store, node, new DaemonConnection(postgres.ConnectionString), new FakeProcessManager(),
            Options.Create(options), NullLogger<DispatchEngine>.Instance);
        await engine.SweepExpiredLeasesAsync(cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            task.State.Value.Should().Be(
                "Claimed", "adoption already reattached this run — the sweep must not also requeue it");

            TaskLease lease = (await query.LoadAsync<TaskLease>(taskId, cts.Token))!;
            lease.HeartbeatAt.Should().BeAfter(
                DateTimeOffset.UtcNow.AddMinutes(-1), "adoption refreshed the heartbeat before the sweep ran");
        }

        try
        {
            Process.GetProcessById(processId).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// The generation fence (backlog 39): a stale generation's run — one a requeue-and-
    /// reclaim already superseded — must not fail the task the live generation is working,
    /// nor take that generation's lease with it. Origin incident (2026-08-21 evening): this
    /// exact path wrote the task Failed while the live generation's fix session was
    /// mid-flight, and a dependent's crying-wolf hold re-armed off the lie.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_run_dying_does_not_fail_the_live_generations_task_or_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid staleRunId) = await SeedClaimedTaskAsync(store, cts.Token);

        // A requeue-and-reclaim moved the task on to generation 2 under a fresh run while
        // the stale run (generation 1) is still the one this test's fake agent is attached to.
        Guid liveRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, liveRunId, Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        int processId = SpawnFakeAgent(staleRunId, FakeAgentScript.New().Emit(AssistantLine).Exit(1));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, staleRunId, processId, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        supervisor.StartMonitoring(staleRunId, RunPaths.GlobalDirectory(staleRunId), taskId, processId, startedAt, cts.Token);

        RunDetails staleDetails = await WaitForStateAsync(store, staleRunId, "Failed", cts.Token);
        staleDetails.FailureReason.Should().Contain("without a result");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim must survive the stale run's failure");
        task2.LeaseGeneration.Should().Be(2);

        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's failure must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));
    }

    /// <summary>
    /// The startup-adoption grouping fix itself (backlog 39, this task's headline acceptance
    /// criterion): a requeue-and-reclaim that landed while the daemon was down leaves one task
    /// with two non-terminal runs on this node — the stale generation this daemon was still
    /// tailing, and the fresh claim the live generation holds. AdoptOrphansAsync must adopt
    /// only the live one and retire the stale one, never both — adopting both double-books the
    /// task exactly like the live-process check a few lines above already exists to prevent.
    /// </summary>
    [Fact]
    public async Task Two_non_terminal_runs_on_one_task_adopt_the_live_generation_and_retire_the_stale_one()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid staleRunId) = await SeedClaimedTaskAsync(store, cts.Token);

        // A requeue-and-reclaim moved the task on to generation 2 under a fresh run while the
        // stale run (generation 1) is still recorded non-terminal for this node — exactly the
        // shape a catch-up running during downtime leaves behind.
        Guid liveRunId = DomainId.New();
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, liveRunId, Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });

            // "/tmp/wt-test-live" is left non-existent for the same reason
            // SeedClaimedTaskAsync's own "/tmp/wt-test" is (see the inline comment above that
            // method's own RunDispatched call): this task
            // still carries the unregistered project id SeedClaimedTaskAsync seeded above, so
            // nothing along AdoptOrphansAsync's path here ever loads real ProjectDetails and
            // reaches a spawn into this worktree — the live run stays a still-sleeping fake
            // agent for the whole test, never far enough along to try.
            session.Events.StartStream<RunAggregate>(liveRunId, new RunDispatched(
                liveRunId, taskId, node.NodeId, node.OwnerId, 2, DomainId.New(),
                "/tmp/wt-test-live", "task/test-live", ExecutorMode.Subscription, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        int staleProcessId = SpawnFakeAgent(staleRunId, FakeAgentScript.New().Emit(AssistantLine).Pause(30));
        await RecordProcessStartedAsync(store, staleRunId, staleProcessId, cts.Token);
        int liveProcessId = SpawnFakeAgent(liveRunId, FakeAgentScript.New().Emit(AssistantLine).Pause(30));
        await RecordProcessStartedAsync(store, liveRunId, liveProcessId, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        try
        {
            await supervisor.AdoptOrphansAsync(cts.Token);

            await using IQuerySession query = store.QuerySession();
            RunDetails staleDetails = (await query.LoadAsync<RunDetails>(staleRunId, cts.Token))!;
            staleDetails.State.Should().Be(RunState.Superseded,
                "the stale generation's run must be retired, not adopted alongside the live one");

            RunDetails liveDetails = (await query.LoadAsync<RunDetails>(liveRunId, cts.Token))!;
            liveDetails.State.IsLive.Should().BeTrue(
                "the live generation's own run is adopted and left running, not retired alongside its stale sibling");

            TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
            task2.State.Value.Should().Be("Claimed", "the live generation's claim is untouched");
            task2.LeaseGeneration.Should().Be(2);
            (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
                "the stale run's retirement must not release the live generation's lease");

            logger.Lines.Should().Contain(line =>
                line.Contains(staleRunId.ToString()) && line.Contains("retired instead of adopted"),
                "the grouping check must name the stale run it chose not to adopt");
        }
        finally
        {
            try { Process.GetProcessById(staleProcessId).Kill(entireProcessTree: true); } catch (ArgumentException) { }
            try { Process.GetProcessById(liveProcessId).Kill(entireProcessTree: true); } catch (ArgumentException) { }
        }
    }

    /// <summary>
    /// The usage-limit shape parks rather than fails (backlog 40): the run stream
    /// records what was observed, but the task stays Claimed — worktree and lease intact —
    /// instead of going through TaskDecider.Fail and releasing them.
    /// </summary>
    [Fact]
    public async Task A_budget_exhausted_result_parks_the_run_and_leaves_the_task_claimed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        const string budgetResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Claude AI usage limit reached|1762952400"}""";
        int processId = SpawnFakeAgent(runId,
            FakeAgentScript.New().Emit(AssistantLine).Emit(budgetResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "BudgetParked", cts.Token);
        details.ParkedReason.Should().Be("token budget exhausted - resumes when the subscription window resets");
        details.FailureReason.Should().BeNull("this is a wait, not a failure");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "the work is intact; nothing here demands a human retry");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "a budget park keeps the lease exactly the way a review park does");
    }

    /// <summary>
    /// The primary-session half of error-result retry (task: a session that reports an error
    /// result is retried once in place, measured 2026-09-05: bursty across only 18 distinct
    /// hours, the shape of a provider-side burst rather than a code defect): a generic error —
    /// distinct from the recognizable usage-limit shape the budget-park test above answers —
    /// is retried once, in the same worktree, rather than failing the run outright.
    /// </summary>
    [Fact]
    public async Task A_primary_sessions_error_result_is_retried_once_and_then_succeeds()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        const string errorResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Internal server error"}""";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Emit(errorResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        ScriptedResumeExecutor resumeExecutor = new(ResultLine);
        RunSupervisor supervisor = NewSupervisor(
            store, node, executor: resumeExecutor,
            options: new DaemonOptions { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) });
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        // Not WaitForStateAsync(..., "Verifying", ...): AgentSessionCompleted moves the run to
        // Verifying at the FIRST (errored) session's own completion, before the retry even
        // spawns — polling for that state alone races the retry and can pass without ever
        // observing it complete (adversarial pre-PR review, cycle 1). Waiting for the second
        // AgentSessionCompleted — the resumed session's own — is what actually proves the
        // retry ran to a clean result, regardless of whatever state the run moves to next.
        await WaitForEventCountAsync<AgentSessionCompleted>(store, runId, 2, cts.Token);
        resumeExecutor.Spawns.Should().ContainSingle("exactly one retry spawn for the primary session");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        RunSessionErrorRetried retry = events.OfType<RunSessionErrorRetried>().Single();
        retry.Leg.Should().Be(RunSessionLeg.Build);
        events.OfType<AgentSessionCompleted>().Should().HaveCount(
            2, "the original errored session and its resumed retry both completed");
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.FailureReason.Should().BeNull("the transient error was retried, not failed");
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Claimed", "the retried session is still doing the work");
    }

    /// <summary>
    /// The residue this task exists to narrow the failures down to: a second consecutive error
    /// on the primary session's own retry spends the one retry and fails the run exactly as
    /// before, with the identical reason text a genuinely broken session always got.
    /// </summary>
    [Fact]
    public async Task A_second_consecutive_error_on_the_primary_session_fails_the_run()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        const string errorResultLine =
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Internal server error"}""";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(AssistantLine).Emit(errorResultLine));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        ScriptedResumeExecutor resumeExecutor = new(errorResultLine);
        RunSupervisor supervisor = NewSupervisor(
            store, node, executor: resumeExecutor,
            options: new DaemonOptions { SessionErrorRetryBackoff = TimeSpan.FromMilliseconds(1) });
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Failed", cts.Token);
        details.FailureReason.Should().Be(
            "Agent reported an error result.", "the second consecutive error fails the run with today's reason text unchanged");
        resumeExecutor.Spawns.Should().ContainSingle("only the one retry is spent before the run fails");

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunSessionErrorRetried>().Should().ContainSingle(
            "only the first error earns a retry; the run fails outright on the second");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Failed");
    }

    /// <summary>
    /// AgentSessionCompleted moves the run to Verifying, and the retry decision
    /// (RunSessionErrorRetried) is durably saved, before the resumed spawn itself ever happens
    /// (RunSupervisor.RetryBuildSessionAsync's own doc comment) — so a daemon that dies in that
    /// gap and restarts must not read Verifying as "the work is done" and hand an unresumed
    /// build to the review loop. AdoptOrphansAsync has to finish the retry itself instead
    /// (independent pre-PR review, cycle 1, conformance finding).
    /// </summary>
    [Fact]
    public async Task Daemon_restart_mid_backoff_finishes_the_pending_build_session_retry_instead_of_treating_it_as_done()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        // The exact durable commit CompleteRunAsync makes before the backoff wait even starts —
        // simulating a crash landing right after it, before RetryBuildSessionAsync's own resumed
        // spawn ever ran.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            session.Events.Append(runId, new RunSessionErrorRetried(
                runId, RunSessionLeg.Build, Cycle: null, Lens: null, "Internal server error", Now));
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedResumeExecutor resumeExecutor = new(ResultLine);
        RunSupervisor restarted = NewSupervisor(store, node, executor: resumeExecutor);
        OrphanAdoption adoption = await restarted.AdoptOrphansAsync(cts.Token);

        resumeExecutor.Spawns.Should().ContainSingle(
            "the crash-stranded retry must be finished on restart, not silently dropped as already done");
        adoption.RunsAdopted.Should().BeGreaterThanOrEqualTo(1);

        await using IQuerySession query = store.QuerySession();
        List<object> events = [.. (await query.Events.FetchStreamAsync(runId, token: cts.Token)).Select(e => e.Data)];
        events.OfType<RunResumed>().Should().ContainSingle("the pending retry's own resumed spawn landed on restart");
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.PendingBuildSessionErrorRetry.Should().BeFalse("the resumed session actually spawned");
    }

    /// <summary>
    /// If the claim moved on during the backoff (an abandon, a lease-expiry requeue-and-reclaim,
    /// or any other release) there is nothing left here to resume — the same guard
    /// TokenBudgetRetryEngine.RetryOneAsync already applies before its own resume spawn. The run
    /// must be retired with RunSuperseded rather than left live at Verifying with no monitor
    /// (which would pin a NodeLoad slot forever) or failed with a reason implying the agent
    /// erred twice when it never got the chance to run again at all.
    /// </summary>
    [Fact]
    public async Task A_claim_that_moved_on_during_the_backoff_retires_the_run_instead_of_resuming_or_failing_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskWithProjectAsync(store, cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.Events.Append(runId, new AgentSessionCompleted(runId, Now));
            session.Events.Append(runId, new RunSessionErrorRetried(
                runId, RunSessionLeg.Build, Cycle: null, Lens: null, "Internal server error", Now));
            await session.SaveChangesAsync(cts.Token);

            // The claim moved on before the retry's own resumed spawn ran: a second generation
            // reclaimed the task under a different run.
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        ScriptedResumeExecutor resumeExecutor = new(ResultLine);
        RunSupervisor restarted = NewSupervisor(store, node, executor: resumeExecutor);
        await restarted.AdoptOrphansAsync(cts.Token);

        resumeExecutor.Spawns.Should().BeEmpty("the claim moved on; there is nothing left here to resume");

        await using IQuerySession query = store.QuerySession();
        RunDetails details = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        details.State.Value.Should().Be("Superseded", "retired explicitly rather than left live or reported as a second agent error");
        details.FailureReason.Should().BeNull("a superseded run was never actually failed");
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's own claim is untouched");
        task2.LeaseGeneration.Should().Be(2);
    }

    /// <summary>
    /// A follow-up that met a review thread it could not honestly judge parks for the human
    /// instead of pushing (Decisions Log #62): the never-loop rule the pre-PR fix session runs
    /// on, applied to a reviewer's thread. Both positions land beside the run, and the pipeline
    /// stops where it stands.
    /// </summary>
    [Fact]
    public async Task A_follow_up_that_disputes_a_review_thread_parks_instead_of_pushing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token, asFollowUp: true);

        const string disputed =
            "Answered three threads. The fourth asks for a different projection shape.\n"
            + "RESOLUTION: disputed";
        // The script writes the line verbatim: the JSON carries an escaped newline, and any
        // spelling that expanded it would split the result line in half and leave nothing
        // parseable (sh's own `echo` does exactly that, which is why FakeAgentScript uses
        // `printf` there).
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(disputed)));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "ReviewParked", cts.Token);
        details.ParkedReason.Should().Contain("disputed a review thread");
        details.ParkedReason.Should().Contain("h9k review resolve", "a park names the human's way back in");
        details.FailureReason.Should().BeNull("a park is a waiting state, not a failure");

        File.ReadAllText(RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId))).Should().Contain(
            "different projection shape", "the human reads the agent's position, not just the marker");
    }

    /// <summary>
    /// Every review thread on a pull request gets a triage disposition before any fix work (task:
    /// every review thread on a pull request gets a triage disposition before any fix work). A
    /// triage that also disputed one genuinely undecidable thread still records every other
    /// thread's disposition on the run stream — the triage and the park are independent facts,
    /// and <c>RecordThreadTriageAsync</c> runs ahead of <c>ParkedOnThreadDisputeAsync</c> for
    /// exactly that reason.
    /// </summary>
    [Fact]
    public async Task A_follow_ups_thread_triage_lands_on_the_stream_even_when_it_also_parks()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token, asFollowUp: true);

        const string summary =
            "THREAD DISPOSITION: thread=PRRC_1; disposition=decline; kind=bot; author=copilot\n"
            + "Reproduced in a scratch repo: git push does update the remote-tracking ref.\n"
            + "THREAD DISPOSITION: thread=PRRC_2; disposition=fix; kind=human; author=brianhallmanac\n"
            + "Renamed the limiter per the reviewer's suggestion.\n\n"
            + "A third thread asks for a different projection shape entirely.\n"
            + "RESOLUTION: disputed";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(summary)));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "ReviewParked", cts.Token);
        details.ReviewThreadOutcomes.Should().HaveCount(2);
        details.ReviewThreadOutcomes[0].ThreadId.Should().Be("PRRC_1");
        details.ReviewThreadOutcomes[0].Disposition.Should().Be(ReviewThreadDisposition.Decline);
        details.ReviewThreadOutcomes[0].IsHuman.Should().BeFalse("copilot's own kind tag says bot");
        details.ReviewThreadOutcomes[1].ThreadId.Should().Be("PRRC_2");
        details.ReviewThreadOutcomes[1].Disposition.Should().Be(ReviewThreadDisposition.Fix);
        details.ReviewThreadOutcomes[1].IsHuman.Should().BeTrue();
    }

    /// <summary>
    /// The rebase counterpart of the review-thread dispute above (backlog 44,
    /// AgentPromptBuilder.AppendRebaseDisputeRules): a rebase follow-up that hits a conflict it
    /// cannot honestly resolve parks the same way, but with its own artifact and reason text —
    /// pointing the human at <c>--needs-fixes</c> rather than the generic message, since a
    /// rebase dispute has no diff to sign off as merge-ready.
    /// </summary>
    [Fact]
    public async Task A_follow_up_that_disputes_a_rebase_conflict_parks_with_its_own_artifact_and_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(
            store, cts.Token, asFollowUp: true, followUpKind: FollowUpKind.Rebase);

        const string disputed =
            "Rebased cleanly except one file: both branches rewrote the same retry policy.\n"
            + "RESOLUTION: disputed";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(disputed)));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "ReviewParked", cts.Token);
        details.ParkedReason.Should().Contain("rebase conflict");
        details.ParkedReason.Should().Contain(
            "--needs-fixes", "merge-ready has no meaning here — nothing has been rebased yet");
        details.FailureReason.Should().BeNull("a park is a waiting state, not a failure");

        File.ReadAllText(RunPaths.RebaseConflictDisputeFile(RunPaths.GlobalDirectory(runId))).Should().Contain(
            "retry policy", "the human reads the agent's position, not just the marker");
        File.Exists(RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId))).Should().BeFalse(
            "a rebase dispute writes its own artifact, not the review-thread one");
    }

    /// <summary>
    /// The generation fence on the thread-dispute park (adversarial review, cycle 3): a
    /// requeue-and-reclaim moved the task on to generation 2 while this follow-up — still
    /// generation 1, the exact double-booking shape backlog 39 exists to close — was mid-run.
    /// Its agent session ends with a disputed verdict, so <c>ParkedOnThreadDisputeAsync</c>'s
    /// fence check rejects it; the rejection must retire the run with RunSuperseded, the same
    /// as every other fence rejection in this diff, rather than leaving it live in Verifying
    /// with no monitor watching it and a NodeLoad slot pinned until the next restart's orphan
    /// adoption sweep.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_thread_dispute_park_retires_the_run_instead_of_leaving_it_live()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token, asFollowUp: true);

        // A requeue-and-reclaim moved the task on to generation 2 under a different run while
        // this follow-up's agent session is still in flight — the same shape as backlog 39's
        // other stale-generation tests, applied to the thread-dispute park.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        const string disputed =
            "Answered three threads. The fourth asks for a different projection shape.\n"
            + "RESOLUTION: disputed";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(disputed)));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        ListLogger<RunSupervisor> logger = new();
        RunSupervisor supervisor = NewSupervisor(store, node, logger: logger);
        supervisor.StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Superseded", cts.Token);
        details.ParkedReason.Should().BeNull("the stale generation's dispute is never actually parked");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim is untouched by the stale run's park");
        task2.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's retirement must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains(runId.ToString()) && line.Contains("retired as superseded"),
            "the fence rejection must name the run it retired instead of parked");
    }

    /// <summary>The same marker from a first run is text, not an answer: only a follow-up was asked.</summary>
    [Fact]
    public async Task The_dispute_marker_is_read_only_from_follow_up_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(
            DisputedResultLine("Quoting the rules: RESOLUTION: disputed is how a follow-up parks.")));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node).StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        // Verifying is where a build run goes next; the gates then fail it on the missing
        // worktree, which is fine — what matters is that it was never parked.
        await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ParkedReason.Should().BeNull();
    }

    /// <summary>
    /// The marker is only an answer where the question was asked (Decisions Log #62). A CI-fix
    /// follow-up was never taught this vocabulary, so a summary of its own that happens to
    /// quote the line — the skill file is in the repo it is working in — is text, and parking
    /// on it would hand a human a "disputed review thread" whose position is about CI.
    /// </summary>
    [Fact]
    public async Task A_checks_follow_up_quoting_the_marker_is_not_read_as_a_dispute()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(
            store, cts.Token, asFollowUp: true, followUpKind: FollowUpKind.FailingChecks);

        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(
            "Fixed the flaky test. The skill file's park line reads RESOLUTION: disputed.")));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node).StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        // Verifying is where the run goes instead; the gates then fail it on the missing
        // worktree, which is fine — what matters is that it was never parked.
        await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ParkedReason.Should().BeNull();
        File.Exists(RunPaths.ReviewThreadDisputeFile(RunPaths.GlobalDirectory(runId))).Should().BeFalse(
            "nothing was disputed, so no position was written");
    }

    /// <summary>
    /// The triage marker contract is taught only to <c>BuildFollowUp</c>'s own prompt (Decisions
    /// Log #159): a CI-fix follow-up's summary that happens to quote it — the skill file lives in
    /// the repo it is working in — must not be read as this run's own triage.
    /// </summary>
    [Fact]
    public async Task A_checks_follow_ups_own_summary_quoting_the_triage_marker_is_not_recorded_as_a_triage()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(
            store, cts.Token, asFollowUp: true, followUpKind: FollowUpKind.FailingChecks);

        const string summary =
            "Fixed the flaky test. The skill file's own triage line reads:\n"
            + "THREAD DISPOSITION: thread=PRRC_1; disposition=fix; kind=bot; author=copilot";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(summary)));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node).StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ReviewThreadOutcomes.Should().BeEmpty();
    }

    /// <summary>
    /// The same guard as above, for the fourth follow-up kind the deny-list this replaced once
    /// missed (cycle-1 pre-PR review, adversarial finding): a stacked replay's prompt never
    /// teaches the triage marker either, so a summary quoting it — the skill file that teaches it
    /// lives in the same repo a replay works in — must not be recorded as this run's own triage.
    /// </summary>
    [Fact]
    public async Task A_stack_replay_follow_ups_own_summary_quoting_the_triage_marker_is_not_recorded_as_a_triage()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(
            store, cts.Token, asFollowUp: true, followUpKind: FollowUpKind.StackReplay);

        const string summary =
            "Replayed the stacked commits onto the new parent head. The skill file's own triage line reads:\n"
            + "THREAD DISPOSITION: thread=PRRC_1; disposition=fix; kind=bot; author=copilot";
        int processId = SpawnFakeAgent(runId, FakeAgentScript.New().Emit(DisputedResultLine(summary)));
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        NewSupervisor(store, node).StartMonitoring(runId, RunPaths.GlobalDirectory(runId), taskId, processId, startedAt, cts.Token);

        await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ReviewThreadOutcomes.Should().BeEmpty();
    }

    private static string DisputedResultLine(string summary) =>
        JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = summary,
            usage = new { input_tokens = 10, output_tokens = 10 },
        });


    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedClaimedTaskAsync(
        DocumentStore store, CancellationToken cancellationToken,
        bool asFollowUp = false, FollowUpKind? followUpKind = null)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, DomainId.New(), "Executor test task", ["it completes"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        // A follow-up's kind lives on the task, recorded by the reopen that dispatched it, so
        // a run that has one is seeded through the real edges: claim, complete, reopen, claim.
        object[] reopen = [];
        if (followUpKind is not null)
        {
            var firstClaim = TaskDecider.Claim(task, node.NodeId, node.OwnerId, DomainId.New(), Now);
            task.Apply(firstClaim);
            var completed = TaskDecider.Complete(task, DomainId.New(), "https://github.com/x/y/pull/1", Now);
            task.Apply(completed);
            var reopened = TaskDecider.Reopen(
                task, DomainId.New(), "task/test", "CI checks failing on the pull request.",
                followUpKind, automatic: true, Now, node.OwnerId,
                stackReplayUpstreamCommit: followUpKind == FollowUpKind.StackReplay ? "0000000000000000000000000000000000000a" : null,
                stackReplayOntoCommit: followUpKind == FollowUpKind.StackReplay ? "0000000000000000000000000000000000000b" : null);
            task.Apply(reopened);
            reopen = [firstClaim, completed, reopened];
        }

        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, .. reopen, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        // "/tmp/wt-test" is deliberately never created: this task's project id (above) is never
        // registered, so ReviewEngine.LoadContextAsync always finds no ProjectDetails and bails
        // out before ever spawning a real agent into this path — unlike
        // SeedClaimedTaskWithProjectAsync's own worktree, which a registered project lets the
        // review loop actually reach (see that method's own doc for the race that forces it to
        // be a real directory).
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
            "/tmp/wt-test", "task/test", ExecutorMode.Subscription, Now, IsFollowUp: asFollowUp));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    /// <summary>
    /// A pr-review task with a reviewer's own lap open on it: claimed interactively (the
    /// <see cref="Guid.Empty"/> sentinel node id), a run dispatched with no process of its own,
    /// and <c>PullRequestReviewLapOpened</c> on the task stream — which is the fact adoption
    /// reads. <c>DispatchingNodeId</c> is this node's, because that is what puts the run in
    /// adoption's candidate set in the first place.
    /// </summary>
    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedOpenReviewLapAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-lap-adopt-wt-{runId:N}");

        await using IDocumentSession session = store.LightweightSession();
        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, DomainId.New(), "Review pull request acme/web#7", ["the verdict is submitted"],
                TaskType.PrReview, null, null,
                new ExternalReference(WorkItemProvider.GitHubPullRequest, "acme/web#7"), Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(
            taskId,
            [
                .. lifecycle,
                claimed,
                new PullRequestReviewLapOpened(
                    taskId, runId, worktreePath, "https://github.com/acme/web/pull/7", Now, node.OwnerId),
            ]);
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            worktreePath, "pr/7", ExecutorMode.Subscription, Now,
            RunDirectory: RunPaths.GlobalDirectory(runId), DispatchingNodeId: node.NodeId));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    /// <summary>
    /// A <c>h9k task start</c> claim, exactly as <c>TaskStartCommand</c> itself records one:
    /// claimed deliberately under the ceiling-exempt <see cref="Guid.Empty"/> NodeId sentinel,
    /// <see cref="RunDispatched.IsDeliberateHeadlessStart"/> true, and
    /// <see cref="RunDispatched.DispatchingNodeId"/> this node's own — the field
    /// <see cref="RunSupervisor.AdoptDeliberateHeadlessStartsAsync"/> scopes its sweep by. The
    /// worktree is a REAL git repository (base branch <c>main</c>, task branch <c>task/test</c>
    /// checked out), because <c>VerificationRunner.DetectStrandedWorkAsync</c> runs actual `git
    /// status`/`git rev-list` against it — a plain empty directory (most other seeds in this file)
    /// would read as "git status unobservable" for every scenario alike, which is a real, distinct
    /// case of its own but not the one these tests exist to exercise.
    /// </summary>
    /// <param name="detached">
    /// When true, leaves the worktree's HEAD detached at the same commit <c>task/test</c> points
    /// to — exactly the shape a session that died mid-recompose rebase
    /// (<c>GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash</c>) can leave behind between applied
    /// picks: clean, committed, but checked out to no branch at all rather than its claim branch
    /// (independent pre-PR review, cycle 1, both lenses).
    /// </param>
    private async Task<(NodeContext Node, Guid TaskId, Guid RunId, string WorktreePath)> SeedDeliberateHeadlessStartTaskAsync(
        DocumentStore store, bool withTaskCommit, bool dirty, CancellationToken cancellationToken, bool detached = false)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-headless-start-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-headless-start-wt-{taskId:N}");
        _createdWorktreePaths.Add(worktreePath);

        Directory.CreateDirectory(worktreePath);
        await TestGit.RunAsync(worktreePath, ["init", "-q", "-b", "main"], cancellationToken);
        await TestGit.RunAsync(
            worktreePath, TestGit.CommitAs("commit", "--allow-empty", "-m", "init", "-q"), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "tracked.txt"), "original\n", cancellationToken);
        await TestGit.RunAsync(worktreePath, ["add", "tracked.txt"], cancellationToken);
        await TestGit.RunAsync(worktreePath, TestGit.CommitAs("commit", "-q", "-m", "seed"), cancellationToken);
        await TestGit.RunAsync(worktreePath, ["checkout", "-q", "-b", "task/test"], cancellationToken);
        if (withTaskCommit)
        {
            await TestGit.RunAsync(
                worktreePath, TestGit.CommitAs("commit", "--allow-empty", "-m", "work", "-q"), cancellationToken);
        }

        if (dirty)
        {
            await File.WriteAllTextAsync(Path.Combine(worktreePath, "tracked.txt"), "changed\n", cancellationToken);
        }

        if (detached)
        {
            await TestGit.RunAsync(worktreePath, ["checkout", "-q", "--detach", "HEAD"], cancellationToken);
        }

        await using IDocumentSession session = store.LightweightSession();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"headless-start-{taskId:N}", repositoryPath,
            new Uri("https://github.com/acme/web"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Headless start test task", ["it completes"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        TaskClaimed claimed = TaskDecider.ClaimDeliberately(
            task, node.OwnerId, runId, Now, dependencyOverrideAcknowledged: false, interactiveMode: false);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, Guid.Empty, node.OwnerId, claimed.LeaseGeneration, DomainId.New(),
            worktreePath, "task/test", ExecutorMode.Subscription, Now,
            RunDirectory: RunPaths.GlobalDirectory(runId), SessionName: $"{DomainId.Short(taskId)}-build",
            DispatchingNodeId: node.NodeId, IsDeliberateHeadlessStart: true));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId, worktreePath);
    }

    /// <summary>
    /// <see cref="SeedClaimedTaskAsync"/>'s task carries a project id that was never actually
    /// registered — fine for every other test here, since nothing along their paths ever loads
    /// <c>ProjectDetails</c> back. The error-result retry path does (<c>PrimarySessionResumer</c>
    /// needs the project's own <c>SkipPermissions</c>), so this variant registers a real project
    /// first and points the task at it.
    /// <para>
    /// A registered project also means <c>ReviewEngine.LoadContextAsync</c> can build a real
    /// <c>ReviewContext</c> once the primary session completes clean — where
    /// <see cref="SeedClaimedTaskAsync"/>'s unregistered project makes it bail out first. From
    /// there the review loop reaches <c>ClaudeExecutor.SpawnAsync</c> and, through it,
    /// <c>UnixProcessManager.Spawn</c>, which always sets this process's own working directory
    /// on the <c>ProcessStartInfo</c> it hands <c>Process.Start</c> — and unlike every git call
    /// this file's own gates make (all wrapped in a try/catch that degrades a missing directory
    /// to "unobservable"), that spawn's own <c>Process.Start</c> throws outright when its working
    /// directory does not exist. Origin incident (PR #235/#236, 2026-09-05): that throw raced
    /// A_primary_sessions_error_result_is_retried_once_and_then_succeeds's own post-retry
    /// assertions on Ubuntu CI, landing "Review loop failed: ... No such file or directory" and
    /// a task Failed under a test that never meant to exercise the review loop at all — confirmed
    /// by timing this worktree existing (safe for 2+ seconds) against it missing (fails within
    /// 100-250ms, well inside this method's callers' own assertion window). The worktree is
    /// therefore a real, task-unique temp directory, not just a plausible-looking path.
    /// </para>
    /// <para>
    /// A real working directory is exactly what makes that spawn's own shell start rather than throw —
    /// on a machine where <c>claude</c> resolves on <c>PATH</c>, that is a real agent session,
    /// not a race-closing no-op. This class's own <c>HALL9K_CLAUDE_PATH</c> pin (see the field
    /// above) is what keeps it inert: the shell still starts, exactly preserving the timing this
    /// method exists for, but its own <c>exec</c> can never find the pinned, nonexistent binary,
    /// so nothing this file's tests do ever launches a real, billable <c>claude</c> process.
    /// </para>
    /// </summary>
    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedClaimedTaskWithProjectAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);

        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        string repositoryPath = Path.Combine(Path.GetTempPath(), $"hall9k-session-error-retry-repo-{taskId:N}");
        string worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-session-error-retry-worktree-{taskId:N}");
        Directory.CreateDirectory(worktreePath);
        _createdWorktreePaths.Add(worktreePath);
        await using IDocumentSession session = store.LightweightSession();

        ProjectRegistered registered = ProjectDecider.Register(
            projectId, node.OwnerId, DomainId.New(), $"session-error-retry-{taskId:N}", repositoryPath,
            new Uri("https://github.com/acme/web"), "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        TaskAggregate task = new();
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Executor test task", ["it completes"],
                TaskType.Chore, null, null, null, Now, node.OwnerId),
            node.OwnerId, Now);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
            worktreePath, "task/test", ExecutorMode.Subscription, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    /// <summary>
    /// Scripted stand-in for the resumed process an error-result retry spawns (task: a session
    /// that reports an error result is retried once in place): writes the given result line
    /// straight into the run's main stream file, the same file a real `--resume` spawn's stdout
    /// redirect would truncate and rewrite.
    /// </summary>
    private sealed class ScriptedResumeExecutor(string resultLine) : IExecutor
    {
        private int _nextProcessId = 7_000;

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public async Task<SpawnedAgent> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken)
        {
            Spawns.Add(request);
            Directory.CreateDirectory(request.RunDirectory);
            await File.WriteAllTextAsync(RunPaths.StreamFile(request.RunDirectory), resultLine + "\n", cancellationToken);
            return new SpawnedAgent(_nextProcessId++, Now);
        }
    }

    private static int SpawnFakeAgent(Guid runId, FakeAgentScript script)
    {
        string runDirectory = RunPaths.GlobalDirectory(runId);
        Directory.CreateDirectory(runDirectory);
        Process process = script.Start(
            RunPaths.StreamFile(runDirectory), RunPaths.StandardErrorFile(runDirectory));
        return process.Id;
    }

    private static async Task<DateTimeOffset> RecordProcessStartedAsync(
        DocumentStore store, Guid runId, int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt;
        try
        {
            using Process process = Process.GetProcessById(processId);
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Very fast scripts can exit before we look them up; a nominal start time still
            // exercises the result-on-disk paths. Win32Exception is the zombie window the
            // production probes already guard (UnixProcessManager, DaemonProcess): the pid
            // still resolves after the child exits, but StartTime is no longer readable.
            startedAt = DateTimeOffset.UtcNow;
        }

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunProcessStarted(runId, processId, startedAt));
        await session.SaveChangesAsync(cancellationToken);
        return startedAt;
    }

    /// <summary>
    /// A deliberate headless start's own claim never appends <see cref="RunProcessStarted"/> — it
    /// records its agent with <see cref="InteractiveSessionStarted"/> instead, exactly as
    /// <c>TaskStartCommand.RunDeliberateStartAsync</c> does under the machine-composed build
    /// session name (independent pre-PR review, cycle 1, both lenses: a test seeded with
    /// <see cref="RecordProcessStartedAsync"/> here exercised a shape production never produces,
    /// since <see cref="RunDetails.ProcessId"/> is never set for this claim shape at all).
    /// </summary>
    private static async Task<DateTimeOffset> RecordInteractiveSessionStartedAsync(
        DocumentStore store, Guid runId, Guid taskId, int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt;
        try
        {
            using Process process = Process.GetProcessById(processId);
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            startedAt = DateTimeOffset.UtcNow;
        }

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new InteractiveSessionStarted(
            runId, DomainId.New(), startedAt, processId, Environment.MachineName,
            SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build)));
        await session.SaveChangesAsync(cancellationToken);
        return startedAt;
    }

    private static async Task<RunDetails> WaitForStateAsync(
        DocumentStore store, Guid runId, string state, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using IQuerySession query = store.QuerySession();
            RunDetails? details = await query.LoadAsync<RunDetails>(runId, cancellationToken);
            if (details?.State.Value == state)
            {
                return details;
            }

            await Task.Delay(250, cancellationToken);
        }

        await using IQuerySession final = store.QuerySession();
        RunDetails? reached = await final.LoadAsync<RunDetails>(runId, cancellationToken);
        throw new TimeoutException(
            $"Run {runId} never reached state {state}; it is {reached?.State.Value ?? "(no projection)"} "
            + $"(failure: {reached?.FailureReason ?? "none"}, park: {reached?.ParkedReason ?? "none"}).");
    }

    /// <summary>
    /// Polls the run's own stream, rather than its projected state, for at least
    /// <paramref name="count"/> events of type <typeparamref name="T"/> — a stable wait for a
    /// milestone that may not correspond to any single stable state (a resumed session's own
    /// completion, for instance, immediately hands off into whatever the pipeline does next).
    /// </summary>
    private static async Task WaitForEventCountAsync<T>(
        DocumentStore store, Guid runId, int count, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using IQuerySession query = store.QuerySession();
            int actual = (await query.Events.FetchStreamAsync(runId, token: cancellationToken))
                .Count(e => e.Data is T);
            if (actual >= count)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Run {runId} never recorded {count} {typeof(T).Name} event(s).");
    }

    private static RunSupervisor NewSupervisor(
        DocumentStore store, NodeContext node, IProcessManager? processManager = null, ILogger<RunSupervisor>? logger = null,
        IExecutor? executor = null, DaemonOptions? options = null)
    {
        processManager ??= ProcessManagers.ForCurrentPlatform();
        options ??= new DaemonOptions();
        IOptions<DaemonOptions> resolvedOptions = Options.Create(options);
        IExecutor resolvedExecutor =
            executor ?? new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processManager, resolvedOptions);
        VerificationRunner verification = new(
            store, resolvedOptions, NullLogger<VerificationRunner>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), resolvedExecutor, processManager);
        ReviewEngine review = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processManager, resolvedOptions), processManager, verification,
            resolvedOptions, NullLogger<ReviewEngine>.Instance,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), RecordingProcessRunner.NeverInvoked(),
            new Hall9k.Daemon.Closeout.StackedParentWatch(
                new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
                NullLogger<Hall9k.Daemon.Closeout.StackedParentWatch>.Instance));
        PrReviewEngine prReview = new(
            store, new ClaudeExecutor(NullLogger<ClaudeExecutor>.Instance, processManager, resolvedOptions), processManager,
            new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance),
            resolvedOptions, NullLogger<PrReviewEngine>.Instance);
        PrimarySessionResumer primarySessionResumer = new(resolvedExecutor);
        return new RunSupervisor(store, node, processManager, verification, review, prReview,
            new PullRequestOpener(store, NullLogger<PullRequestOpener>.Instance),
            primarySessionResumer, resolvedOptions, logger ?? NullLogger<RunSupervisor>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        Environment.SetEnvironmentVariable("HALL9K_CLAUDE_PATH", _previousClaudePath);
        TemporaryTree.TryDelete(_home);

        foreach (string worktreePath in _createdWorktreePaths)
        {
            TemporaryTree.TryDelete(worktreePath);
        }
    }
}
