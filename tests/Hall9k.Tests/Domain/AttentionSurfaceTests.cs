using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Cli;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The attention surface: whether a row wants a human, why, and what to type (Decisions Log
/// #66, absorbing backlog 28). Grouped here with the ordering the pane reads rows in, because
/// the two answer the same question at different resolutions.
/// </summary>
public sealed class AttentionSurfaceTests
{
    private static readonly DateTimeOffset Now = StatusFixtures.Now;

    [Fact]
    public void A_review_parked_run_names_the_recorded_reason_and_the_command_that_clears_it()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked, sessionProcessId: null);
        parked.ParkedReason = "Automatic fix budget spent at cycle 3; findings in review-3-findings.md.";

        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, runId), parked);

        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Cause.Should().Be(parked.ParkedReason, "the reason is quoted, never re-guessed");
        row.Attention.Lever.Should().StartWith("h9k review resolve",
            "a reason without a next action is not done (backlog 28)");
        row.Group.Should().Be(AttentionBucket.NeedsYou);
        row.Priority.Should().Be(0);
    }

    [Fact]
    public void A_review_parked_run_offers_merge_ready_when_the_park_is_not_a_pre_gate_rebase_dispute()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked, sessionProcessId: null);
        parked.ParkedReason = "Automatic fix budget spent at cycle 3; findings in review-3-findings.md.";
        parked.ReviewCycle = 3;

        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);
        task.FollowUpKind = FollowUpKind.Rebase;

        TaskStatusRow row = StatusFixtures.Compose(task, parked);

        row.Attention.Lever.Should().Contain("--merge-ready",
            "a review cycle has already run, so ReviewResolveCommand's refusal does not apply here");
    }

    [Fact]
    public void A_rebase_dispute_park_never_advises_the_merge_ready_form_the_platform_refuses()
    {
        // Mirrors ReviewResolveCommand's own refusal guard: task.FollowUpKind == Rebase &&
        // run.ReviewCycle == 0 is exactly the park where --merge-ready throws a
        // DomainConflictException, because nothing has been rebased yet. The pane must never
        // advise a lever the platform will refuse.
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked, sessionProcessId: null);
        parked.ParkedReason = "A follow-up could not honestly resolve a rebase conflict.";
        parked.ReviewCycle = 0;

        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);
        task.FollowUpKind = FollowUpKind.Rebase;

        TaskStatusRow row = StatusFixtures.Compose(task, parked);

        row.Attention.Lever.Should().NotContain("--merge-ready");
        row.Attention.Lever.Should().Contain("--needs-fixes");
    }

    [Fact]
    public void A_parked_closeout_names_its_reason_and_its_own_lever()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.CloseoutParked, sessionProcessId: null, pullRequestNumber: 24);
        parked.ParkedReason = "Automatic closeout attempts spent (2 of 2).";

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), parked);

        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Cause.Should().Be(parked.ParkedReason);
        row.Attention.Lever.Should().StartWith("h9k pr resolve");
    }

    [Fact]
    public void A_row_waiting_on_a_human_is_never_reported_as_a_run_that_went_quiet()
    {
        // Nothing writes to a run's stream while a human holds the worktree, so a park's last
        // activity stamp freezes at the park and every parked row crosses the stall threshold an
        // hour later. Counting that as a stall renames a wait for a human as a machine failure
        // and files the row out of the Needs-you count into Stalled, which is where a reader
        // looks for the opposite problem.
        Guid parkedRunId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(parkedRunId, RunState.ReviewParked, sessionProcessId: null);
        parked.ParkedReason = "The fix run disputed a review finding (cycle 2).";

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, parkedRunId), parked, silentSince: Now.AddHours(-9));

        row.Stalled.Should().BeFalse("a parked run is quiet by design");
        row.Group.Should().Be(AttentionBucket.NeedsYou);
        row.Attention.Cause.Should().Be(parked.ParkedReason);

        // The same holds for an agent that asked a question: its session exited to wait, so the
        // stream stops, and the ask is what the row is about.
        Guid askedRunId = DomainId.New();
        TaskStatusRow asked = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.NeedsHuman, askedRunId),
            StatusFixtures.Run(askedRunId, RunState.Running, sessionProcessId: null),
            silentSince: Now.AddHours(-9));

        asked.Stalled.Should().BeFalse();
        asked.Group.Should().Be(AttentionBucket.NeedsYou);
        asked.Attention.Cause.Should().Contain("asked a question");
    }

    [Fact]
    public void An_abandoned_task_stops_asking_even_though_its_run_is_still_parked()
    {
        // h9k task abandon appends to the task's stream and deletes its lease; it writes nothing
        // to the run, so the park outlives the task that owned it. Nothing else reaches that run
        // either — the dispatch sweep iterates leases and the closeout monitor watches open pull
        // requests — so a row composed from the run's state alone would sit in Needs-you forever,
        // offering h9k review resolve for work a human deliberately dropped.
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked, sessionProcessId: null);
        parked.ParkedReason = "Automatic fix budget spent at cycle 3.";

        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Abandoned, runId), parked);

        row.State.Should().Be(LifecycleState.Archived);
        row.Attention.NeedsYou.Should().BeFalse("the human already answered this row by walking away");
        row.Attention.Cause.Should().BeEmpty();
        row.Attention.Lever.Should().BeEmpty("a lever that resumes an abandoned task is worse than none");
        row.Group.Should().Be(AttentionBucket.Closed);
    }

    /// <summary>
    /// A budget-parked run is waiting on the clock, not on a human (backlog 40): the
    /// three surfaces all have to say that, distinctly from the two parks beside it that do want
    /// a person. The origin incident was three rows that read as three unrelated Failed rows for
    /// what was really one condition, so the cause names the condition once and counts the rows
    /// it caught, and the level is the one a reader is meant to be able to ignore.
    /// </summary>
    [Fact]
    public void A_budget_parked_run_waits_on_the_clock_rather_than_on_a_human()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.BudgetParked, sessionProcessId: null);
        parked.ParkedReason = "token budget exhausted - resumes when the subscription window resets";

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId), parked, budgetParkedRuns: 3);

        row.Attention.Level.Should().Be(AttentionLevel.WaitingHandled,
            "the daemon retries hourly and clears this itself — a human can read it once and leave it be");
        row.Attention.Cause.Should().Be(
            "token budget exhausted - resumes when the subscription window resets (3 runs waiting)",
            "several parked runs read as one shared condition, not N unrelated failures");
        row.Attention.Lever.Should().BeEmpty("there is nothing for anyone to type");
        row.Phase.Text.Should().Be("waiting on the budget window");
        row.Phase.Detail.Should().Be("the daemon retries hourly; nothing is running");
        row.Phase.Liveness.Should().Be(SessionLiveness.NotApplicable,
            "the session that hit the limit has already exited");
        row.State.Should().Be(LifecycleState.Working, "the run still owns the task and has not pushed");
        row.Group.Should().Be(AttentionBucket.Working,
            "the wait is the run's, so it is counted with the work it belongs to rather than as a group of its own");
        row.Stalled.Should().BeFalse("parked is a wait, not a silence the stall threshold should flag");
    }

    /// <summary>
    /// One row held on the window is one row, and "(1 run waiting)" beside it is noise: the count
    /// exists to say "this is one condition, not N failures", which only means something at N > 1.
    /// </summary>
    [Fact]
    public void A_board_holding_one_budget_parked_run_states_the_condition_without_a_count()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.BudgetParked, sessionProcessId: null);
        parked.ParkedReason = "token budget exhausted - resumes when the subscription window resets";

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId), parked, budgetParkedRuns: 1);

        row.Attention.Cause.Should().Be("token budget exhausted - resumes when the subscription window resets");
    }

    /// <summary>
    /// The count is the project's, not the board's (backlog 40). Every browse surface can be
    /// filtered to one project, so a row that quoted a board-wide total would tell a human that
    /// runs they cannot see on this screen are waiting — a number with nothing to check it
    /// against, which is the opposite of what the shared count was added to do.
    /// </summary>
    [Fact]
    public void The_shared_wait_count_never_borrows_runs_from_a_project_this_row_cannot_see()
    {
        Guid runId = DomainId.New();
        Guid otherProject = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.BudgetParked, sessionProcessId: null);
        parked.ParkedReason = "token budget exhausted - resumes when the subscription window resets";
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);

        TaskStatusRow row = StatusFixtures.Compose(
            task,
            parked,
            budgetParkedByProject: new Dictionary<Guid, int> { [task.ProjectId] = 2, [otherProject] = 5 });

        row.Attention.Cause.Should().Be(
            "token budget exhausted - resumes when the subscription window resets (2 runs waiting)",
            "the five parked runs in the other project are not this row's condition to report");
    }

    [Fact]
    public void A_park_that_recorded_no_reason_says_so_rather_than_showing_a_bare_word()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.ReviewParked, sessionProcessId: null));

        row.Attention.Cause.Should().Contain("without recording a reason",
            "an unrecorded reason is a fact about the record, not licence to invent one");
    }

    [Fact]
    public void A_failed_row_composes_its_cause_from_what_was_recorded()
    {
        Guid runId = DomainId.New();
        TaskListItem failed = StatusFixtures.Task(TaskState.Failed, runId);
        failed.FailureReason = "Verification failed: test";
        RunDetails run = StatusFixtures.Run(runId, RunState.Failed, sessionProcessId: null);
        run.FailedGates = ["test"];

        TaskStatusRow row = StatusFixtures.Compose(failed, run);

        row.State.Should().Be(LifecycleState.Failed);
        row.Attention.Cause.Should().Be("gate failure (test): Verification failed: test");
        row.Attention.Lever.Should().StartWith("h9k task retry");
    }

    [Fact]
    public void A_park_outranks_a_failure_inside_the_needs_you_section()
    {
        Guid parkedRunId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(parkedRunId, RunState.ReviewParked, sessionProcessId: null);
        parked.ParkedReason = "Automatic fix budget spent at cycle 3.";
        TaskStatusRow park = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, parkedRunId), parked);

        Guid failedRunId = DomainId.New();
        TaskListItem failedTask = StatusFixtures.Task(TaskState.Failed, failedRunId);
        failedTask.FailureReason = "Verification failed: test";
        RunDetails failedRun = StatusFixtures.Run(failedRunId, RunState.Failed, sessionProcessId: null);
        TaskStatusRow failure = StatusFixtures.Compose(failedTask, failedRun);

        park.Group.Should().Be(AttentionBucket.NeedsYou);
        failure.Group.Should().Be(AttentionBucket.NeedsYou);
        // The pane sorts inside one section, so a rank composed from the group alone would be
        // the same number here and leave these two ordered by age: a park that has held a
        // worktree all week would print under a task that failed a minute ago.
        park.Priority.Should().BeLessThan(failure.Priority,
            "a park has a worktree stopped behind it; a failure is a decision that can wait its turn");
    }

    [Fact]
    public void The_deferred_queue_is_listed_in_the_order_the_dispatcher_will_serve_it()
    {
        // The section tells a human that each of its rows starts as a run finishes, so its top
        // row has to be the one that starts next: oldest assignment first, ties broken by when
        // the task was added, which is the claim query's ordering exactly (Decisions Log #64).
        // Every other section lists newest first, which here showed the tasks that run last
        // (pre-PR review, 2026-08-22).
        DispatchPressure full = new(LiveRuns: 1, MaxConcurrentRuns: 1);
        const string OldAssignment = "Drafted in January, assigned last week";
        const string NewAssignment = "Drafted last week, assigned this morning";
        TaskStatusRow[] rows =
        [
            StatusFixtures.Compose(
                StatusFixtures.Task(
                    TaskState.Queued, objective: OldAssignment, addedAt: Now, assignedAt: Now.AddHours(1)),
                pressure: full),
            StatusFixtures.Compose(
                StatusFixtures.Task(
                    TaskState.Queued,
                    objective: NewAssignment,
                    addedAt: Now.AddHours(5),
                    assignedAt: Now.AddHours(10)),
                pressure: full),
        ];

        rows.Should().OnlyContain(row => row.Group == AttentionBucket.Queued);
        StatusCommand.SectionRows(rows, AttentionBucket.Queued, inServiceOrder: true)
            .Select(row => row.Objective).Should().Equal([OldAssignment, NewAssignment],
                "the older assignment is claimed first, so it is listed first — even though the "
                + "other task was drafted more recently");

        StatusCommand.SectionRows(rows, AttentionBucket.Queued, inServiceOrder: false)
            .Select(row => row.Objective).Should().Equal([NewAssignment, OldAssignment],
                "the pane's default is newest arrival first, which is the reverse of the order "
                + "the dispatcher serves these in");
    }

    [Fact]
    public void A_kill_and_an_unexplained_failure_read_as_the_different_things_they_are()
    {
        Guid killedRunId = DomainId.New();
        TaskListItem killedTask = StatusFixtures.Task(TaskState.Failed, killedRunId);
        killedTask.FailureReason = "BudgetExceeded";
        RunDetails killed = StatusFixtures.Run(killedRunId, RunState.Killed, sessionProcessId: null);

        StatusFixtures.Compose(killedTask, killed).Attention.Cause
            .Should().Be("the run was killed: BudgetExceeded");

        // Nothing recorded a cause at all — the display says exactly that. Token exhaustion
        // arrives here as whatever text the machinery wrote until backlog 40 records it
        // distinctly, and is shown verbatim rather than sorted into a category nobody observed.
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Failed)).Attention.Cause
            .Should().Be("the failure was recorded without a reason");
    }

    [Fact]
    public void A_dead_blocker_needs_you_and_a_live_one_is_a_hold_you_can_ignore()
    {
        Guid blocker = DomainId.New();
        TaskListItem dead = StatusFixtures.Task(TaskState.Blocked);
        dead.UnmetDependencies = [blocker];
        dead.DependencyFailureReason = "Dependency 3f2a91b2 will never close out; h9k pr resolve 3f2a91b2.";

        TaskStatusRow held = StatusFixtures.Compose(dead);
        held.Attention.NeedsYou.Should().BeTrue();
        held.Attention.Cause.Should().Be(dead.DependencyFailureReason,
            "the recorded reason already names the lever the platform will honour (log #61)");

        // The mirror: the blocker was retried, the recorded death is gone, and the row goes back
        // to a wait the reader is meant to be able to ignore. Origin incident (2026-08-21): two
        // such rows read as red NeedsHuman for hours after their blocker was already rebuilding.
        TaskListItem waiting = StatusFixtures.Task(TaskState.Blocked);
        waiting.UnmetDependencies = [blocker];

        TaskStatusRow recovered = StatusFixtures.Compose(waiting);
        recovered.Attention.Level.Should().Be(AttentionLevel.WaitingHandled);
        recovered.Attention.Marker.Should().NotContain("needs you", "it is consciously ignorable, not an ask");
        recovered.Attention.Cause.Should().Contain("nothing for you to do");
        recovered.Group.Should().Be(AttentionBucket.Blocked);
    }

    [Fact]
    public void An_open_pull_request_with_nothing_recorded_against_it_says_the_merge_is_yours()
    {
        // Origin incident (2026-08-22, PR 24): watching-and-dispatching-follow-ups and
        // watching-with-nothing-left-but-your-merge rendered identically, and "is it my turn?"
        // took a log dive.
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"),
            StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24));

        row.State.Should().Be(LifecycleState.Delivered);
        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Cause.Should().Contain("the merge is yours");

        // The monitor records findings and records nothing at all while a check is still
        // reporting, so this row is what an absence of records looks like — not an observation
        // that the pull request is clean. Saying "observed" here would claim a look nobody took
        // on every pull request the platform opens, since CI is pending the moment one does.
        row.Attention.Cause.Should().NotContain("observed");
        row.Phase.Text.Should().Be("watching PR #24");
        row.Phase.Detail.Should().Be("no external review observation recorded yet; its checks may still be reporting");
    }

    /// <summary>
    /// The post-PR review watcher's own read of Copilot (Decisions Log #88) splits this same
    /// AwaitingReview row three ways, matching the phase line above it exactly (pre-PR review,
    /// cycle 2: the two used to disagree — the phase said "awaiting Copilot review" while the
    /// cause right under it still claimed "read its checks, then the merge is yours").
    /// </summary>
    [Fact]
    public void A_pending_Copilot_review_is_not_the_human_s_turn_and_a_landed_one_is()
    {
        Guid runId = DomainId.New();
        string pullRequest = "https://github.com/x/y/pull/24";

        RunDetails pending = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        pending.ExternalReviewState = ExternalReviewState.RequestedPending;
        TaskStatusRow awaitingCopilot = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), pending);

        awaitingCopilot.Attention.Level.Should().Be(AttentionLevel.WaitingHandled,
            "Copilot has not answered yet, so it is not the human's turn");
        awaitingCopilot.Attention.NeedsYou.Should().BeFalse();

        RunDetails landed = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 24);
        landed.ExternalReviewState = ExternalReviewState.Landed;
        TaskStatusRow reviewLanded = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId, pullRequest), landed);

        reviewLanded.Attention.NeedsYou.Should().BeTrue("Copilot has weighed in; nothing else will read it");
        reviewLanded.Attention.Cause.Should().Contain("Copilot's review landed");
        reviewLanded.Attention.Cause.Should().Contain("the merge is yours");
    }

    /// <summary>
    /// Delivered work nobody is assigned to. h9k pr resolve reopens a done task to Queued and
    /// keeps its pull request; h9k task unassign then takes it to Published, which leaves an open
    /// pull request with no run watching it and no owner whose nodes could claim the follow-up.
    /// Origin incident (pre-PR review, 2026-08-22): every non-Done Delivered row was reported as
    /// waiting-and-handled, so this one said "waiting" while its own phase line said nothing was
    /// watching it and nothing was ever going to move it.
    /// </summary>
    [Fact]
    public void A_delivered_task_nobody_is_assigned_to_asks_for_the_assignment()
    {
        TaskListItem unassigned = StatusFixtures.Task(
            TaskState.Published, pullRequest: "https://github.com/x/y/pull/24");

        TaskStatusRow row = StatusFixtures.Compose(unassigned);

        row.State.Should().Be(LifecycleState.Delivered);
        row.Attention.NeedsYou.Should().BeTrue("nothing clears this on its own");
        row.Attention.Cause.Should().Contain("unassigned");
        row.Attention.Lever.Should().Be($"h9k task assign {TaskListCommand.ShortId(unassigned.Id)}");
        row.Group.Should().Be(AttentionBucket.NeedsYou);
        row.Phase.Detail.Should().Contain("no run record is watching it",
            "the phase and the attention line tell the same story about this row");
    }

    [Fact]
    public void A_pull_request_the_monitor_is_still_working_is_not_an_ask()
    {
        Guid runId = DomainId.New();
        RunDetails failing = StatusFixtures.Run(runId, RunState.ChecksFailing, sessionProcessId: null, pullRequestNumber: 24);
        failing.FailingChecks = ["build (ubuntu)"];

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), failing);

        row.Attention.Level.Should().Be(AttentionLevel.WaitingHandled);
        row.Attention.Cause.Should().Contain("closeout monitor owns the next move");
        row.Phase.Detail.Should().Contain("build (ubuntu)", "the phase names what was observed failing");
    }

    /// <summary>
    /// A conflicting pull request is being handled by the closeout monitor's rebase follow-up
    /// exactly like a failing check or an unresolved thread, so it wants the same marker as
    /// those two — not the silent <see cref="AttentionLevel.None"/> a missing switch arm falls
    /// through to (adversarial pre-PR review, cycle 2).
    /// </summary>
    [Fact]
    public void A_pull_request_conflicting_with_its_base_is_not_an_ask()
    {
        Guid runId = DomainId.New();
        RunDetails conflicting = StatusFixtures.Run(runId, RunState.Conflicting, sessionProcessId: null, pullRequestNumber: 24);

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), conflicting);

        row.Attention.Level.Should().Be(AttentionLevel.WaitingHandled);
        row.Attention.Cause.Should().Contain("closeout monitor owns the next move");
    }

    [Fact]
    public void A_pull_request_closed_without_merging_is_never_reported_as_done()
    {
        Guid runId = DomainId.New();
        RunDetails closed = StatusFixtures.Run(
            runId, RunState.Failed, sessionProcessId: null, pullRequestNumber: 24);
        closed.FailureReason = RunDetails.PullRequestClosedWithoutMerge;
        closed.PullRequestUrl = "https://github.com/x/y/pull/24";

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), closed);

        row.State.Should().Be(LifecycleState.Delivered, "closeout ended, but not the way Done claims");
        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Cause.Should().Contain("without a merge being observed").And.Contain(closed.FailureReason,
            "the closure is quoted from the record that observed it, not inferred from the run state");
        row.Attention.Lever.Should().Be(closed.PullRequestUrl,
            "a follow-up run onto a closed pull request's branch rejoins no watch, so the pull "
            + "request itself is the next act");
    }

    /// <summary>
    /// RunState.Failed is what every run failure records, and only one of the ways to reach it is
    /// a pull request closed without merging. Origin incident (pre-PR review, 2026-08-22): both
    /// the phase and the attention line read that state as a closure, so a task whose gates had
    /// failed and which a human then resolved onto a pull request was described, permanently and
    /// in red, by an event nobody had observed.
    /// </summary>
    [Fact]
    public void A_run_that_failed_on_its_gates_is_never_described_as_a_pull_request_that_closed()
    {
        Guid runId = DomainId.New();
        RunDetails failed = StatusFixtures.Run(runId, RunState.Failed, sessionProcessId: null);
        failed.FailureReason = "Verification failed: test";
        failed.FailedGates = ["test"];

        // Resolved onto a pull request the human says the work landed in, so the row is Delivered.
        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), failed);

        row.State.Should().Be(LifecycleState.Delivered);
        row.Attention.Cause.Should().Contain("Verification failed: test").And.NotContain("closed without merging");
        row.Phase.Text.Should().NotContain("closed without merging");
        row.Phase.Text.Should().Contain("the run ended without a merge");
    }

    /// <summary>
    /// The pull request in the test above is still open, and h9k pr resolve is exactly what puts
    /// it back under the closeout monitor's watch — TaskDecider.Reopen accepts a done task with a
    /// pull request, a recorded run and a branch, which is this row precisely. Origin incident
    /// (pre-PR review, 2026-08-22): the row offered a bare URL instead, on the false premise that
    /// pr resolve does not apply to a task the stream records as Done, which left it permanently
    /// red with nothing that clears it.
    /// </summary>
    [Fact]
    public void An_open_pull_request_nothing_is_watching_is_put_back_under_watch_by_pr_resolve()
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24");
        RunDetails failed = StatusFixtures.Run(runId, RunState.Failed, sessionProcessId: null);
        failed.FailureReason = "Verification failed: test";

        TaskStatusRow row = StatusFixtures.Compose(task, failed);

        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Lever.Should().Be($"h9k pr resolve {TaskListCommand.ShortId(task.Id)}",
            "the follow-up run rejoins the watch set, and its merge is observed — the same remedy "
            + "a dependent blocked on this task is given");
    }

    /// <summary>
    /// The one thing that would make that advice a lie: TaskDecider.Reopen refuses a follow-up
    /// with no branch to resume, so a run document that recorded none gets the pull request rather
    /// than a command the platform will turn down (the never-advise-a-refused-lever rule).
    /// </summary>
    [Fact]
    public void A_run_with_no_branch_recorded_is_never_sent_to_pr_resolve()
    {
        Guid runId = DomainId.New();
        RunDetails failed = StatusFixtures.Run(
            runId, RunState.Failed, sessionProcessId: null, branch: string.Empty);
        failed.FailureReason = "Verification failed: test";

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/24"), failed);

        row.Attention.Lever.Should().Be("https://github.com/x/y/pull/24",
            "there is no branch to resume, so the reopen would be refused");
    }

    /// <summary>
    /// A task closed by hand keeps the run document it was closed on top of, because TaskResolved
    /// does not clear CurrentRunId. Origin incident (pre-PR review, 2026-08-22): the closeout
    /// composition read that leftover run before asking whether anything had been pushed, so
    /// h9k task resolve with no --pr rendered as Delivered — a claim that work was pushed and a
    /// merge was pending, for a task that never pushed anything and never would again.
    /// </summary>
    [Fact]
    public void A_task_closed_by_hand_with_nothing_pushed_is_done_whatever_its_last_run_recorded()
    {
        Guid runId = DomainId.New();
        RunDetails failed = StatusFixtures.Run(runId, RunState.Failed, sessionProcessId: null);
        failed.FailureReason = "Verification failed: test";

        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, runId), failed);

        row.State.Should().Be(LifecycleState.Done, "nothing was pushed, so there is no merge to wait for");
        row.Group.Should().Be(AttentionBucket.Done);
        row.Attention.NeedsYou.Should().BeFalse("a row nothing will ever move again must not sit in red");
        row.Attention.Cause.Should().BeEmpty();
    }

    [Fact]
    public void A_question_asked_names_the_only_command_the_platform_actually_has()
    {
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.NeedsHuman));

        row.Attention.NeedsYou.Should().BeTrue();
        row.Attention.Cause.Should().Contain("no command answers it yet",
            "never advise a lever the platform will refuse");
        row.Attention.Lever.Should().StartWith("h9k task show");
        row.Priority.Should().Be(0, "needs-you outranks everything");
    }

    [Fact]
    public void A_session_the_machine_says_is_gone_stalls_the_row_and_says_which_silence_it_is()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Running),
            liveness: SessionLiveness.Gone);

        row.Stalled.Should().BeTrue();
        row.Group.Should().Be(AttentionBucket.Stalled);
        row.Attention.Cause.Should().Contain("process is gone");
        row.Attention.Lever.Should().StartWith("h9k logs");
    }

    [Fact]
    public void A_live_session_whose_stream_went_quiet_stalls_for_the_other_reason()
    {
        Guid runId = DomainId.New();

        TaskStatusRow row = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.Running),
            silentSince: Now.AddHours(-2));

        row.Stalled.Should().BeTrue();
        row.Attention.Cause.Should().Contain("alive but its stream has been silent");
    }

    [Fact]
    public void Working_and_settled_rows_ask_for_nothing()
    {
        Guid runId = DomainId.New();

        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, runId),
                StatusFixtures.Run(runId, RunState.Running),
                silentSince: Now.AddMinutes(-2))
            .Attention.Level.Should().Be(AttentionLevel.None);
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Draft)).Attention.Level.Should().Be(AttentionLevel.None);
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done)).Attention.Level.Should().Be(AttentionLevel.None);
    }

    [Fact]
    public void Stream_renderer_shows_text_tools_and_outcome_without_duplicating_the_summary()
    {
        string[] lines =
        [
            """{"type":"system","subtype":"init","model":"claude-fable-5"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Working on it."},{"type":"tool_use","name":"Write"}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"All done, summary here."}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"All done, summary here.","usage":{"output_tokens":42}}""",
        ];

        string rendered = string.Join("\n", StreamRenderer.Render(lines));

        rendered.Should().Contain("claude-fable-5");
        rendered.Should().Contain("Working on it.");
        rendered.Should().Contain("⚙ Write");
        rendered.Should().Contain("agent finished (42 output tokens)");
        rendered.Split("All done, summary here.").Should().HaveCount(2, "the result's duplicate of the final message is suppressed");
    }

    [Fact]
    public void Malformed_lines_render_dimmed_rather_than_failing()
    {
        string rendered = string.Join("\n", StreamRenderer.Render(["not json at all"]));

        rendered.Should().Contain("not json at all");
    }
}
