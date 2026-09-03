using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The second surface (Decisions Log #66): what the machinery is doing right now, from the run's
/// records plus one observation of the recorded process. The rule the whole surface rests on is
/// that a phase never claims a session is doing something without observing the process.
/// </summary>
public sealed class TaskPhaseSurfaceTests
{
    [Fact]
    public void A_phase_that_names_a_session_says_what_was_observed_of_it()
    {
        // Origin incident (2026-08-22): the board said the lane was quiet while a fix agent was
        // editing the worktree, and the orchestrator nearly rewrote history under it. Both
        // readings are on the phase line now, and neither is inferred from the run state.
        Guid runId = DomainId.New();
        RunDetails fixing = StatusFixtures.Run(runId, RunState.UnderReview, sessionRole: AgentRole.Fix);
        fixing.ReviewCycle = 2;

        TaskStatusRow alive = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, runId), fixing);
        alive.Phase.Text.Should().Be("review cycle 2");
        alive.Phase.Detail.Should().Be("fix session running");
        alive.Phase.Liveness.Should().Be(SessionLiveness.Alive);
        alive.Phase.Markup.Should().Contain("session alive");

        TaskStatusRow gone = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId), fixing, liveness: SessionLiveness.Gone);
        gone.Phase.Liveness.Should().Be(SessionLiveness.Gone);
        gone.Phase.Markup.Should().Contain("recorded process is gone");
    }

    /// <summary>
    /// The blocker-context session runs inside the launch itself, before the run's own process
    /// starts, so the run is still Dispatched while it reads. Origin incident (pre-PR review,
    /// 2026-08-22): the phase was composed from the run state alone, so a live condensing pass
    /// was described as worktree preparation and the branch written to name it correctly sat in
    /// the review leg, where the role can never appear.
    /// </summary>
    [Fact]
    public void A_condensing_pass_is_named_by_its_role_rather_than_by_the_launch_it_runs_inside()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Dispatched, sessionRole: AgentRole.Synthesis));

        row.Phase.Text.Should().Be("condensing blocker context");
        row.Phase.Detail.Should().Be("context synthesis running");
        row.Phase.Liveness.Should().Be(SessionLiveness.Alive);
    }

    [Fact]
    public void A_dispatch_with_no_condensing_pass_still_reads_as_the_launch_it_is()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Dispatched, sessionProcessId: null));

        row.Phase.Text.Should().Be("starting up");
        row.Phase.Detail.Should().Be("worktree and prompt being prepared");
    }

    /// <summary>
    /// An interactive claim's own Dispatched window is no longer necessarily brief (Decisions Log
    /// #124): by default h9k task work returns as soon as it prints the prompt, before anyone has
    /// pasted it anywhere, so a claim can sit Dispatched — with no session ever having registered —
    /// for as long as the operator takes to get to it. The generic "worktree and prompt being
    /// prepared" wording above describes a headless run's own brief pre-launch window, not this
    /// one, so an interactive claim gets its own honest phase instead.
    /// </summary>
    [Fact]
    public void An_interactive_claim_awaiting_its_pasted_session_reads_honestly_rather_than_as_still_being_prepared()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            StatusFixtures.Run(runId, RunState.Dispatched, sessionProcessId: null));

        row.Phase.Text.Should().Be("awaiting a pasted session");
        row.Phase.Detail.Should().Contain("self-registers");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable);
    }

    [Fact]
    public void A_run_that_records_no_session_never_reads_as_one_that_is_running()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null));

        row.Phase.Text.Should().Be("building");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable);
        row.Phase.Detail.Should().Be("no session recorded");
        row.Phase.Markup.Should().NotContain("session alive");
    }

    [Fact]
    public void A_session_on_another_machine_is_unobserved_rather_than_assumed_either_way()
    {
        Guid runId = DomainId.New();
        RunDetails elsewhere = StatusFixtures.Run(runId, RunState.Running);
        elsewhere.NodeId = DomainId.New();

        TaskStatusContext context = StatusFixtures.Context(elsewhere) with
        {
            // The node the run belongs to is not this one, so its pid names a process in a
            // process table this machine cannot see. The real observer is used here on purpose:
            // this is its rule, not the composer's.
            NodeMachines = new Dictionary<Guid, string> { [elsewhere.NodeId] = "some-other-node" },
            Sessions = ProcessSessionObserver.Instance,
        };

        TaskStatusRow row = TaskStatusComposer.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId), context, StatusFixtures.Now);

        row.Phase.Liveness.Should().Be(SessionLiveness.Unobserved);
        row.Phase.Markup.Should().Contain("not observed here");
    }

    [Fact]
    public void An_interactive_sessions_own_machine_name_is_observable_even_though_its_run_carries_no_node()
    {
        // An interactive claim's RunDispatched records NodeId as the Guid.Empty sentinel, which
        // is never a key in NodeMachines (no NodeDetails document is ever written for it) — so
        // the run-level onThisMachine lookup reads false for every interactive session, however
        // real the process is. ActiveSession.MachineName is what InteractiveSessionLiveness
        // already reads instead (adversarial review, cycle 2); the phase line must agree with it
        // rather than calling the very session that guard can see "not observed here"
        // (adversarial review, cycle 3).
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.ActiveSessions =
        [
            new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, Environment.ProcessId,
                StatusFixtures.Now, StatusFixtures.ThisMachine),
        ];

        TaskStatusContext context = StatusFixtures.Context(interactive) with
        {
            // Empty rather than the fixture's usual [run.NodeId -> ThisMachine]: a real
            // NodeMachines table never carries an entry for the Guid.Empty sentinel, so the
            // run-level lookup must read false here for the test to mean anything.
            NodeMachines = new Dictionary<Guid, string>(),
            Sessions = ProcessSessionObserver.Instance,
        };

        TaskStatusRow row = TaskStatusComposer.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId), context, StatusFixtures.Now);

        row.Phase.Liveness.Should().NotBe(SessionLiveness.Unobserved,
            "the session's own machine name says this machine can check it");
    }

    [Fact]
    public void A_resumed_session_records_only_a_pid_so_liveness_stays_unobserved()
    {
        // RunResumed carries no process start time (log #5's exit-and-resume), and a bare pid is
        // a lie waiting to happen (log #2). The real observer is the one enforcing this.
        ProcessSessionObserver.Instance.Observe(4711, startedAt: null, onThisMachine: true)
            .Should().Be(SessionLiveness.Unobserved);
        ProcessSessionObserver.Instance.Observe(processId: null, startedAt: null, onThisMachine: true)
            .Should().Be(SessionLiveness.NotApplicable);
    }

    [Fact]
    public void The_review_loop_says_which_leg_and_which_lens_is_still_reading()
    {
        Guid runId = DomainId.New();
        RunDetails reviewing = StatusFixtures.Run(runId, RunState.UnderReview, sessionProcessId: null);
        reviewing.ReviewCycle = 2;
        reviewing.ActiveSessions = [StatusFixtures.Session(AgentRole.Review, 5002, ReviewLens.Adversarial)];

        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, runId), reviewing)
            .Phase.Detail.Should().Be("adversarial pending");

        // Between passes: the run is still UnderReview and nothing is running, which is exactly
        // the ambiguity the run state alone could never resolve.
        RunDetails between = StatusFixtures.Run(runId, RunState.UnderReview, sessionProcessId: null);
        between.ReviewCycle = 3;
        TaskStatusRow idle = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, runId), between);
        idle.Phase.Text.Should().Be("review cycle 3");
        idle.Phase.Detail.Should().Be("no session recorded as running");
    }

    [Fact]
    public void The_cycle_is_named_without_a_cap_the_cli_cannot_read()
    {
        // The caps live in DaemonOptions, which the CLI has no access to; "cycle 2 of 3" would
        // be a number nobody here observed (the never-guess rule).
        Guid runId = DomainId.New();
        RunDetails reviewing = StatusFixtures.Run(runId, RunState.UnderReview, sessionRole: AgentRole.Review);
        reviewing.ReviewCycle = 2;

        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, runId), reviewing)
            .Phase.Text.Should().Be("review cycle 2").And.NotContain(" of ");
    }

    [Fact]
    public void A_cycle_whose_faster_pass_has_exited_is_not_a_run_that_lost_its_process()
    {
        // The cycle's passes are dispatched together and exit in whichever order their diffs
        // allow, so a pass that has already finished sits beside one still reading until the
        // engine collects it. Reading that as "the recorded process is gone" turns a healthy run
        // red and files it under Stalled — the inverse of the incident this surface exists for.
        Guid runId = DomainId.New();
        RunDetails cycle = StatusFixtures.Run(runId, RunState.UnderReview, sessionProcessId: null);
        cycle.ReviewCycle = 1;
        cycle.ActiveSessions =
        [
            StatusFixtures.Session(AgentRole.Review, 5001, ReviewLens.Conformance),
            StatusFixtures.Session(AgentRole.Review, 5002, ReviewLens.Adversarial),
        ];

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            cycle,
            livenessByProcess: new Dictionary<int, SessionLiveness>
            {
                [5001] = SessionLiveness.Alive,
                [5002] = SessionLiveness.Gone,
            });

        row.Phase.Liveness.Should().Be(SessionLiveness.Alive, "a pass is still reading");
        row.Phase.Detail.Should().Be("conformance and adversarial pending");
        row.Stalled.Should().BeFalse();
        row.Group.Should().Be(AttentionBucket.Working);

        // Both gone is the honest Gone: nothing the run believes in is there any more.
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, runId), cycle, liveness: SessionLiveness.Gone)
            .Phase.Liveness.Should().Be(SessionLiveness.Gone);
    }

    [Fact]
    public void A_gone_interactive_session_is_not_reported_as_a_stalled_process()
    {
        // An interactive claim has no lease or heartbeat (Decisions Log #103): closing the
        // terminal is a normal way to leave, and h9k task work re-enters the same claim. The
        // dead pid it leaves behind must not misfile the row as a stalled machine failure —
        // the lever that finding would print (h9k logs) is unusable, since an interactive
        // session runs attached to the tty and never writes a stream file (adversarial
        // review, cycle 4).
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.ActiveSessions =
        [
            new ActiveSession(
                AgentRole.Interactive, ReviewLens.Unknown, 4711, StatusFixtures.Now, StatusFixtures.ThisMachine),
        ];

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            liveness: SessionLiveness.Gone);

        row.Stalled.Should().BeFalse();
        row.Attention.NeedsYou.Should().BeFalse();
        row.Group.Should().Be(AttentionBucket.Working);

        // The phase line is what an operator actually reads, and it drew the identical
        // misfiling one field over: "building · the recorded process is gone" in red reports a
        // normal terminal close as a machine failure, for a state PLAN.md #103 defines as normal
        // and re-enterable with h9k task work (adversarial review, cycle 1).
        row.Phase.Text.Should().Be("building");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable);
        row.Phase.Markup.Should().NotContain("recorded process is gone");
        row.Phase.Markup.Should().Contain("h9k task work re-enters this claim");
    }

    [Fact]
    public void An_interactive_claim_unobserved_on_another_machine_is_not_read_as_reenterable()
    {
        // Unobserved (a session recorded live on another machine this one cannot check) must not
        // fall into the same branch as Gone/NotApplicable: telling this machine's reader "closing
        // the terminal is a normal way to leave" about a claim that, as far as anyone here can
        // honestly say, is still attached would invite exactly the collision h9k task work
        // --force exists to gate behind a human attestation (adversarial review, cycle 1).
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running);

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            liveness: SessionLiveness.Unobserved);

        row.Phase.Liveness.Should().Be(SessionLiveness.Unobserved);
        row.Phase.Markup.Should().Contain("not observed here");
        row.Phase.Markup.Should().NotContain("h9k task work re-enters this claim");
    }

    [Fact]
    public void An_interactive_claim_that_exited_normally_still_reads_as_reenterable()
    {
        // InteractiveSessionEnded (a normal /exit or Ctrl+D) clears ActiveSessions entirely, so
        // ActiveRole(run) reads Unknown and the observed session reads NotApplicable rather than
        // Gone — neither of which the old guard (session == Gone && ActiveRole == Interactive)
        // could ever see, so the row fell through to the ordinary Running case and read as an
        // unattended headless run with no session recorded (adversarial review, cycle 4). The
        // discriminator has to be task.ClaimedByNodeId, the Guid.Empty sentinel every other
        // command on this surface reads, because it is the only thing that survives the session
        // ending.
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty), interactive);

        row.Phase.Text.Should().Be("building");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable);
        row.Phase.Markup.Should().NotContain("no session recorded");
        row.Phase.Markup.Should().Contain("h9k task work re-enters this claim");
    }

    [Fact]
    public void An_interactive_claim_untouched_past_the_configured_days_nudges_needs_you()
    {
        // A human-held claim has no lease or heartbeat by design (Decisions Log #103), so the
        // only remedy for a forgotten one is the attention pane asking about it — never an
        // automatic reclaim (idea 3ba186b6).
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.LastInteractiveActivityAt = StatusFixtures.Now.AddDays(-4);

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            interactiveClaimStaleAfterDays: 3);

        row.Attention.NeedsYou.Should().BeTrue();
        row.Group.Should().Be(AttentionBucket.NeedsYou);
        row.Attention.Cause.Should().Contain("last recorded activity").And.NotContain("untouched for");
        row.Attention.Lever.Should().Contain("h9k task work").And.Contain("h9k task handback");
    }

    /// <summary>
    /// The run-state guard alone (Dispatched/Running) is not enough: a claim's interactive
    /// session this machine can see is genuinely alive right now still read run.State ==
    /// Running, so the nudge fired anyway (conformance review, cycle 2) and advertised two
    /// levers that both refuse — <c>InteractiveSessionLiveness.EnsureNotAttachedElsewhere</c>
    /// refuses <c>h9k task work</c> re-entry and <c>h9k task handback</c> alike for a session it
    /// observes alive, and does not honour <c>--force</c> for that case. Gating on
    /// <c>phase.Liveness</c> — which is what that guard itself would observe — closes it.
    /// </summary>
    [Fact]
    public void An_interactive_claim_with_a_session_observed_alive_right_now_does_not_nudge()
    {
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.LastInteractiveActivityAt = StatusFixtures.Now.AddDays(-4);
        interactive.ActiveSessions =
        [
            new ActiveSession(
                AgentRole.Interactive, ReviewLens.Unknown, 4711, StatusFixtures.Now, StatusFixtures.ThisMachine),
        ];

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            liveness: SessionLiveness.Alive,
            interactiveClaimStaleAfterDays: 3);

        row.Phase.Liveness.Should().Be(SessionLiveness.Alive);
        row.Attention.Cause.Should().NotContain("last recorded activity").And.NotContain("was claimed");
        row.Attention.NeedsYou.Should().BeFalse();
    }

    /// <summary>
    /// Unobserved (a session recorded on another machine this one cannot check) must not nudge
    /// either, for the identical reason Alive does not: the phase line above already reads
    /// "liveness not observed here" rather than claiming the claim is re-enterable
    /// (<c>TaskPhaseComposer</c>'s own interactive branch admits only Gone or NotApplicable), and
    /// <c>InteractiveSessionLiveness.EnsureNotAttachedElsewhere</c> refuses both
    /// <c>h9k task work</c> and <c>h9k task handback</c> for exactly this observation without
    /// <c>--force</c> — advertising them free would be the same "never advise a lever the
    /// platform will refuse" defect the Alive guard above exists to prevent (conformance and
    /// adversarial review, cycle 4).
    /// </summary>
    [Fact]
    public void An_interactive_claim_unobserved_on_another_machine_does_not_nudge()
    {
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.LastInteractiveActivityAt = StatusFixtures.Now.AddDays(-4);
        interactive.ActiveSessions =
        [
            new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, 4711, StatusFixtures.Now, "machine-a"),
        ];

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            liveness: SessionLiveness.Unobserved,
            interactiveClaimStaleAfterDays: 3);

        row.Phase.Liveness.Should().Be(SessionLiveness.Unobserved);
        row.Attention.Cause.Should().NotContain("last recorded activity").And.NotContain("was claimed");
        row.Attention.NeedsYou.Should().BeFalse();
    }

    /// <summary>
    /// A blank <c>MachineName</c> (a stream written before the field existed) is not the same
    /// unobservable fact as a name that names a real other machine: it also reads Unobserved here
    /// (<c>SessionOnThisMachine</c> falls back to the run-level answer, which is always false for
    /// an interactive claim's <see cref="Guid.Empty"/> sentinel node), but
    /// <c>InteractiveSessionLiveness.EnsureNotAttachedElsewhere</c> proceeds without <c>--force</c>
    /// for exactly this claim, so both advertised levers would actually work — the nudge has to
    /// reach it too, or the oldest claims (the population it exists for) could never be nudged
    /// (adversarial review, cycle 6).
    /// <para>
    /// <see cref="RunDetails.LastInteractiveActivityAt"/> is null here rather than set, and
    /// <see cref="RunDetails.InteractiveSessionCount"/> is positive: that is the only shape a real
    /// blank-<c>MachineName</c> document can actually have (conformance review, cycle 7) —
    /// <c>RunDetailsProjection.StartSession</c> writes <c>MachineName</c> and
    /// <c>LastInteractiveActivityAt</c> together on every <c>InteractiveSessionStarted</c>/
    /// <c>Ended</c>, so a still-blank name is itself proof no touch has landed since machine-name
    /// tracking began, while <c>InteractiveSessionCount</c> was already being incremented before
    /// that tracking existed. A non-null timestamp beside a blank name cannot occur on a real
    /// projection and would silently hide a regression where the guard below (correctly) still
    /// suppresses the nudge because it saw a timestamp, not because this arm actually worked.
    /// </para>
    /// </summary>
    [Fact]
    public void An_interactive_claim_unobserved_with_a_blank_machine_name_still_nudges()
    {
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(
            runId, RunState.Running, sessionProcessId: null, dispatchedAt: StatusFixtures.Now.AddDays(-4));
        interactive.InteractiveSessionCount = 1;
        interactive.ActiveSessions =
        [
            new ActiveSession(AgentRole.Interactive, ReviewLens.Unknown, 4711, StatusFixtures.Now),
        ];

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            liveness: SessionLiveness.Unobserved,
            interactiveClaimStaleAfterDays: 3);

        row.Phase.Liveness.Should().Be(SessionLiveness.Unobserved);
        row.Attention.NeedsYou.Should().BeTrue();
        // InteractiveSessionCount is positive here, so the wording must not claim no touch was ever
        // recorded (conformance review, cycle 1 following cycle 7's own reading of that field) — it
        // says a touch happened before this machine tracked exact touch times instead.
        row.Attention.Cause.Should().Contain("was claimed")
            .And.Contain("activity recorded before this machine tracked exact touch times")
            .And.NotContain("has not recorded a touch since");
    }

    /// <summary>
    /// Once <c>h9k task deliver</c> hands the run to the standard pipeline, the task can still
    /// read Claimed+interactive for the whole review loop — <c>TaskHandbackCommand</c> and
    /// <c>TaskWorkCommand</c> both refuse once <c>run.State</c> is past Dispatched/Running
    /// (adversarial pre-PR review, cycle 1) — so the nudge must not fire there either: both levers
    /// it would advertise are ones the platform refuses.
    /// </summary>
    [Fact]
    public void A_delivered_interactive_claim_does_not_nudge_even_when_long_untouched()
    {
        Guid runId = DomainId.New();
        RunDetails delivered = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null);
        delivered.LastInteractiveActivityAt = StatusFixtures.Now.AddDays(-4);

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            delivered,
            interactiveClaimStaleAfterDays: 3);

        row.Attention.Cause.Should().NotContain("last recorded activity");
    }

    [Fact]
    public void An_interactive_claim_touched_within_the_configured_days_does_not_nudge()
    {
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.LastInteractiveActivityAt = StatusFixtures.Now.AddDays(-1);

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            interactiveClaimStaleAfterDays: 3);

        row.Attention.NeedsYou.Should().BeFalse();
    }

    [Fact]
    public void An_interactive_claim_with_no_recorded_touch_falls_back_to_when_it_was_claimed()
    {
        // LastInteractiveActivityAt is null until the claim's first InteractiveSessionStarted —
        // never guessed at as more recent than the claim's own DispatchedAt.
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(
            runId, RunState.Running, sessionProcessId: null, dispatchedAt: StatusFixtures.Now.AddDays(-4));

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            interactiveClaimStaleAfterDays: 3);

        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Cause.Should().Contain("was claimed").And.NotContain("last recorded activity");
    }

    /// <summary>
    /// <c>LastInteractiveActivityAt</c> null does not by itself mean never touched: a document
    /// written before the field existed reads that way forever, until its claim's next attach or
    /// detach rewrites it for real (<c>RunDetails.cs</c>'s own doc comment). A positive
    /// <c>InteractiveSessionCount</c> is the tell that touches did happen, and
    /// <c>DispatchedAt</c> — the claim's own start, not a fallback for its last touch — can
    /// understate the real last touch by however long the claim has been open. Asserting
    /// staleness off it would be the exact unobserved-fact guess this nudge otherwise refuses to
    /// make, so it stays silent on this claim rather than risk telling an operator who was here
    /// recently that they have not been seen in days (conformance review, cycle 6).
    /// </summary>
    [Fact]
    public void An_interactive_claim_touched_before_the_timestamp_field_existed_does_not_nudge()
    {
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(
            runId, RunState.Running, sessionProcessId: null, dispatchedAt: StatusFixtures.Now.AddDays(-10));
        interactive.InteractiveSessionCount = 2;

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty),
            interactive,
            interactiveClaimStaleAfterDays: 3);

        row.Attention.NeedsYou.Should().BeFalse();
    }

    [Fact]
    public void A_check_name_from_github_cannot_repaint_the_board_it_is_printed_on()
    {
        // FailingChecks is read off gh pr view, so a workflow job is named by whoever named it.
        // EscapeMarkup alone neutralises Spectre's syntax and not the terminal's, and a newline
        // in the detail would break the one-line guarantee the layout tests measure.
        Guid runId = DomainId.New();
        RunDetails failing = StatusFixtures.Run(
            runId, RunState.ChecksFailing, sessionProcessId: null, pullRequestNumber: 24);
        failing.FailingChecks = ["build\u001b[31m (ubuntu)\nfake row"];

        string markup = StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), failing)
            .Phase.Markup;

        markup.Should().NotContain("\u001b", "an escape sequence would repaint the rows above this one");
        markup.Should().NotContain("\n", "a newline would break the one-line guarantee the layout is measured against");
        markup.Should().Contain("build[[31m (ubuntu) fake row",
            "the escape is dropped, the newline folds to a space, and Spectre's syntax is escaped");
    }

    [Fact]
    public void The_two_meanings_of_the_old_closing_out_read_differently()
    {
        // Origin incident (2026-08-22, PR 24). Same lifecycle state, opposite answers to
        // "is it my turn?", and the phase line is what separates them.
        Guid runId = DomainId.New();
        string pullRequest = "https://github.com/x/y/pull/24";

        TaskStatusRow followUp = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId, pullRequest),
            StatusFixtures.Run(runId, RunState.Running, pullRequestNumber: 24));
        TaskStatusRow yours = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, pullRequest),
            StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24));

        followUp.State.Should().Be(yours.State, "both are Delivered — the state is not what tells them apart");
        followUp.Phase.Text.Should().Be("follow-up on PR #24: building");
        followUp.Phase.Liveness.Should().Be(SessionLiveness.Alive);
        yours.Phase.Text.Should().Be("watching PR #24");
        yours.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable, "nothing is running, so nothing is claimed");
    }

    [Fact]
    public void A_follow_up_that_has_not_started_says_that_rather_than_nothing()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Queued, runId, "https://github.com/x/y/pull/24"),
            StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null, pullRequestNumber: 24));

        row.State.Should().Be(LifecycleState.Delivered);
        row.Phase.Text.Should().Contain("follow-up queued");
        row.Phase.Detail.Should().Be("not claimed yet",
            "why it has not been claimed is a question only a measurement answers");
    }

    [Fact]
    public void A_follow_up_a_full_node_is_holding_back_names_the_ceiling_holding_it()
    {
        // The closeout monitor reopens a task for a failing check or a review thread: Queued
        // again, its run superseded, its pull request still on it. The dispatcher sees an
        // ordinary queued task and defers it at the ceiling, so the phase line has to say so —
        // the row is Delivered work whose follow-up is not running, and a line that said only
        // "follow-up queued" left a human hunting for a fault that was not there.
        Guid runId = DomainId.New();
        TaskListItem reopened = StatusFixtures.Task(TaskState.Queued, runId, "https://github.com/x/y/pull/24");
        RunDetails superseded = StatusFixtures.Run(
            runId, RunState.Superseded, sessionProcessId: null, pullRequestNumber: 24);

        TaskStatusRow held = StatusFixtures.Compose(
            reopened, superseded, pressure: new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3));

        held.State.Should().Be(LifecycleState.Delivered, "the work is pushed; only the follow-up is queued");
        held.Phase.Detail.Should().Be("waiting for a slot — 3 of 3 running");
        held.WaitingForSlot.Should().BeTrue();

        // A node past its ceiling is holding the queue harder, not less, and says what it is
        // rather than reading as broken arithmetic (Decisions Log #64).
        StatusFixtures.Compose(reopened, superseded, pressure: new DispatchPressure(LiveRuns: 4, MaxConcurrentRuns: 3))
            .Phase.Detail.Should().Be("waiting for a slot — 4 running, over a ceiling of 3");
    }

    [Fact]
    public void A_follow_up_held_by_a_dependency_says_that_rather_than_claiming_a_dispatch()
    {
        // A task reopened onto its pull request, then unassigned, drafted, given a blocker and
        // assigned again, lands Blocked while still carrying the URL — so it composes Delivered
        // with no run. Read as one of the claimed states it printed the dispatch-handoff line,
        // which asserts a handoff the platform never made on a row it is not dispatching at all.
        TaskListItem held = StatusFixtures.Task(TaskState.Blocked, pullRequest: "https://github.com/x/y/pull/24");
        held.UnmetDependencies = [DomainId.New(), DomainId.New()];

        TaskStatusRow row = StatusFixtures.Compose(held);

        row.State.Should().Be(LifecycleState.Delivered);
        row.Phase.Text.Should().Be("follow-up blocked for PR #24");
        row.Phase.Detail.Should().Be("waiting on 2 dependencies to close out");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable, "nothing is running, so nothing is claimed");
        row.Attention.NeedsYou.Should().BeFalse("its blockers are alive, so it queues itself");

        // A blocker observed dead is the difference between waiting and stuck, which is what the
        // reader is on this line for; the recorded death itself is quoted on the attention line.
        held.DependencyFailureReason = "blocker 0190a: the run ended without a merge";
        TaskStatusRow stuck = StatusFixtures.Compose(held);
        stuck.Phase.Detail.Should().Be("a blocker will not close out on its own");
        stuck.Attention.Cause.Should().Be("blocker 0190a: the run ended without a merge");
    }

    [Fact]
    public void A_follow_up_names_the_pull_request_before_its_own_run_has_recorded_the_number()
    {
        // A follow-up run records its number only when it pushes (PullRequestUpdated, at the very
        // end), and a follow-up merely queued has no current run at all. Both read the number off
        // the task's URL, exactly as the row's own PR column does — otherwise the whole of a
        // follow-up says "the pull request" while the column beside it says #24.
        Guid runId = DomainId.New();
        string pullRequest = "https://github.com/x/y/pull/24";

        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, runId, pullRequest),
                StatusFixtures.Run(runId, RunState.Running))
            .Phase.Text.Should().Be("follow-up on PR #24: building");

        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued, null, pullRequest))
            .Phase.Text.Should().Be("follow-up queued for PR #24");

        // A URL whose shape the parser does not recognize yields no number, so the line names the
        // pull request without one rather than printing a guess.
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued, null, "https://example.test/nope"))
            .Phase.Text.Should().Be("follow-up queued for the pull request");
    }

    [Fact]
    public void Gates_claim_no_session_because_they_run_in_the_daemon()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Verifying, sessionProcessId: null));

        row.Phase.Text.Should().Be("gates");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable);
    }

    [Fact]
    public void A_claim_whose_run_document_has_not_appeared_says_so_and_stays_live_work()
    {
        // TaskClaimed commits in its own transaction and the run document only appears once the
        // launcher has checked a worktree out, so every dispatch spends seconds here. A daemon
        // that dies inside that window leaves the task there until a human moves it, which is
        // exactly when an operator goes looking for it.
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, DomainId.New()));

        row.State.Should().Be(LifecycleState.Working);
        row.Group.Should().Be(AttentionBucket.Working);
        row.Phase.Text.Should().Be("dispatch handoff");
        row.Phase.Detail.Should().Contain("has not appeared yet");
    }

    [Fact]
    public void A_finished_run_under_a_still_claimed_task_says_the_lane_is_empty()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null));

        row.Group.Should().Be(AttentionBucket.Working, "the platform still holds the claim");
        row.Phase.Text.Should().Be("run completed");
        row.Phase.Detail.Should().Contain("has not landed yet");
    }

    /// <summary>
    /// The post-PR review watcher's own readings (origin: PR #50 sat Delivered for 23
    /// minutes with a landed Copilot review nobody had read before the merge), each rendered on
    /// the Delivered phase line — never as a new task lifecycle status. AttentionComposer draws
    /// the identical distinction on its own cause line (Decisions Log #89), covered separately.
    /// </summary>
    [Fact]
    public void The_post_PR_review_watcher_s_readings_are_the_delivered_phase()
    {
        Guid runId = DomainId.New();
        RunDetails landed = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        landed.ExternalReviewState = ExternalReviewState.Landed;
        landed.ExternalReviewThreadCount = 2;

        string pullRequest = "https://github.com/x/y/pull/24";

        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), landed)
            .Phase.Text.Should().Be("watching PR #24 — Copilot review landed");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), landed)
            .Phase.Detail.Should().Be("2 comment threads");

        // A landed review recorded while the CI picture was still incomplete has not been
        // re-checked for new unresolved threads this sweep either (CloseoutEngine records the
        // review-state observation ahead of that read), so the detail must not read as the
        // all-clear the case above renders once checks settle (independent pre-PR review,
        // cycle 3).
        RunDetails landedChecksPending = StatusFixtures.Run(
            runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        landedChecksPending.ExternalReviewState = ExternalReviewState.Landed;
        landedChecksPending.ExternalReviewThreadCount = 2;
        landedChecksPending.ExternalReviewChecksPending = true;
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), landedChecksPending)
            .Phase.Text.Should().Be("watching PR #24 — Copilot review landed");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), landedChecksPending)
            .Phase.Detail.Should().Be("2 comment threads, not yet confirmed resolved; its checks may still be reporting");

        RunDetails pending = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        pending.ExternalReviewState = ExternalReviewState.RequestedPending;
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), pending)
            .Phase.Text.Should().Be("watching PR #24 — awaiting Copilot review");

        // A stale review is review activity that happened, just against a commit that is no
        // longer the head — it must render honestly as that, with its own thread count, never
        // collapsed into the "no external review activity observed" text the None arm below
        // renders for a pull request Copilot has genuinely never looked at (independent pre-PR
        // review, cycle 6).
        RunDetails stale = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        stale.ExternalReviewState = ExternalReviewState.Stale;
        stale.ExternalReviewThreadCount = 1;
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), stale)
            .Phase.Text.Should().Be("watching PR #24 — Copilot reviewed an earlier commit");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), stale)
            .Phase.Detail.Should().Be("the review is stale; 1 comment thread");

        // No external review activity while checks may still be reporting is not the same as
        // "only a human's merge is left": the sweep records this observation ahead of its own
        // checks read, so the line stops short of naming the human as the last gate while checks
        // may still be reporting (the same distinction the landed/landedChecksPending pair above
        // draws, independent pre-PR review, cycle 7).
        RunDetails noneChecksPending = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        noneChecksPending.ExternalReviewState = ExternalReviewState.None;
        noneChecksPending.ExternalReviewChecksPending = true;
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), noneChecksPending)
            .Phase.Text.Should().Be("watching PR #24");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), noneChecksPending)
            .Phase.Detail.Should().Be("no external review activity observed; its checks may still be reporting");

        // Once the provider's CI picture is complete and still no external review activity is
        // recorded, nothing is left unresolved on this row but the human's own merge, so the
        // phase says that instead of repeating the checks-pending hedge on a row where checks
        // are not, in fact, still pending (independent pre-PR review, cycle 7).
        RunDetails none = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        none.ExternalReviewState = ExternalReviewState.None;
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), none)
            .Phase.Text.Should().Be("watching PR #24 — awaiting human review");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), none)
            .Phase.Detail.Should().Be("no external review activity observed");

        // A run recorded before this observation existed (or a sweep that has not run yet, or
        // a sweep that read a Copilot review it could not compare against the head commit)
        // carries even less information than "None" (a sweep that looked and found nothing),
        // so it must not claim more than None does either — the never-guess rule.
        RunDetails unobserved = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), unobserved)
            .Phase.Text.Should().Be("watching PR #24");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), unobserved)
            .Phase.Detail.Should().Be("no confirmed review observation recorded; its checks may still be reporting");
    }

    [Fact]
    public void Rows_with_no_live_machinery_carry_no_phase_at_all()
    {
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Draft)).Phase.HasPhase.Should().BeFalse();
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued)).Phase.HasPhase.Should().BeFalse();
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done)).Phase.HasPhase.Should().BeFalse();
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Abandoned)).Phase.HasPhase.Should().BeFalse();
    }

    /// <summary>
    /// The run-side counterpart to h9k task resolve --pr (backlog: a pull request recorded by
    /// h9k task resolve --pr is observed to merge like every other pull request the platform
    /// knows about): once RunDetails.PullRequestNumber is recorded, CloseoutEngine's orphan
    /// sweep actually watches this row for a merge, so this phase line must not say the same
    /// thing it says for a run whose pull request truly is unwatched — that would contradict
    /// the attention line printed directly under it (AttentionComposer.IsOrphanSweepCandidate).
    /// </summary>
    [Fact]
    public void A_failed_run_with_no_recorded_pull_request_is_unwatched_but_a_recorded_one_is_still_watched()
    {
        Guid unwatchedRunId = DomainId.New();
        RunDetails unwatched = StatusFixtures.Run(unwatchedRunId, RunState.Failed, sessionProcessId: null);
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, unwatchedRunId, "https://github.com/x/y/pull/24"), unwatched)
            .Phase.Detail.Should().Be("nothing is watching it any more");

        Guid watchedRunId = DomainId.New();
        RunDetails watched = StatusFixtures.Run(
            watchedRunId, RunState.Failed, sessionProcessId: null, pullRequestNumber: 24);
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, watchedRunId, "https://github.com/x/y/pull/24"), watched)
            .Phase.Detail.Should().Be("still eligible for closeout's merge observation");
    }

    /// <summary>
    /// The "Failed" arm above previously had no "Killed" counterpart, so a Killed run fell to the
    /// generic "watching" default while the attention line printed directly under it already gave
    /// Killed the same rich treatment as Failed (independent pre-PR review, cycle 3, low). Killed
    /// gets the identical two readings Failed does, for the identical reason.
    /// </summary>
    [Fact]
    public void A_killed_run_with_no_recorded_pull_request_is_unwatched_but_a_recorded_one_is_still_watched()
    {
        Guid unwatchedRunId = DomainId.New();
        RunDetails unwatched = StatusFixtures.Run(unwatchedRunId, RunState.Killed, sessionProcessId: null);
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, unwatchedRunId, "https://github.com/x/y/pull/24"), unwatched)
            .Phase.Detail.Should().Be("nothing is watching it any more");

        Guid watchedRunId = DomainId.New();
        RunDetails watched = StatusFixtures.Run(
            watchedRunId, RunState.Killed, sessionProcessId: null, pullRequestNumber: 24);
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, watchedRunId, "https://github.com/x/y/pull/24"), watched)
            .Phase.Detail.Should().Be("still eligible for closeout's merge observation");
    }
}
