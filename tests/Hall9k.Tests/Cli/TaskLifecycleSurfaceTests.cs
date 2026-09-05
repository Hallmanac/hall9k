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
/// The first of the board's three surfaces (Decisions Log #66): the lifecycle word, and only the
/// lifecycle word. Draft, Published, Working, Delivered, Done, Failed, Archived — with the
/// persisted states behind them untouched, which is what makes this pass display-first.
/// </summary>
public sealed class TaskLifecycleSurfaceTests
{
    private static readonly DateTimeOffset Now = StatusFixtures.Now;

    [Fact]
    public void The_status_column_shows_seven_words_and_never_a_run_state()
    {
        string[] lifecycle = [.. LifecycleState.All.Select(state => state.Word)];
        lifecycle.Should().Equal("Draft", "Published", "Working", "Delivered", "Done", "Failed", "Archived");

        // Every run state under every task state the composer can be handed. The run vocabulary
        // is the phase line's material now, and the column it used to leak into prints exactly
        // one of the seven words above. (Failed names both a task's ending and a run's, so the
        // check is what the column renders, not whether the two word lists overlap.)
        string[] taskStates =
        [
            TaskState.Draft, TaskState.Published, TaskState.Queued, TaskState.Blocked,
            TaskState.Claimed, TaskState.NeedsHuman, TaskState.Done, TaskState.Failed, TaskState.Abandoned,
        ];
        string[] leaked = ["Claimed", "Queued", "Blocked", "NeedsHuman", "ClosingOut", .. TaskStateFilter.RunStates];

        foreach (string taskState in taskStates)
        {
            foreach (string runState in TaskStateFilter.RunStates)
            {
                Guid runId = DomainId.New();
                foreach (string? pullRequest in new[] { null, "https://github.com/x/y/pull/7" })
                {
                    TaskStatusRow row = StatusFixtures.Compose(
                        StatusFixtures.Task(taskState, runId, pullRequest),
                        StatusFixtures.Run(runId, runState, sessionProcessId: null));

                    lifecycle.Should().Contain(row.State.Word);
                    leaked.Where(word => word != row.State.Word)
                        .Should().NotContain(word => row.StateMarkup.Contains(word),
                            $"the Status column showed {row.StateMarkup} for {taskState}/{runState}");
                }
            }
        }
    }

    [Theory]
    [InlineData("Published")]
    [InlineData("Queued")]
    [InlineData("Blocked")]
    public void The_three_pre_dispatch_states_all_read_as_published(string persisted)
    {
        // Display-first (Brian, 2026-08-22): the streams still record Queued and Blocked, and
        // they retire when the ranking model lands. Until then they are facts on the line below,
        // not words in the Status column.
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(persisted));

        row.State.Should().Be(LifecycleState.Published);
        row.StateMarkup.Should().Contain("Published");
        row.Facts.Should().NotBeEmpty("the distinction moved onto the derived-facts line, it did not vanish");
    }

    [Fact]
    public void The_derived_facts_line_says_which_kind_of_published_this_is()
    {
        Guid blocker = DomainId.New();
        TaskListItem blocked = StatusFixtures.Task(TaskState.Blocked);
        blocked.UnmetDependencies = [blocker];

        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Published)).Facts
            .Should().ContainSingle().Which.Should().Contain("not assigned");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued)).Facts
            .Should().ContainSingle().Which.Should().Contain("the dispatcher has not claimed it yet");

        IReadOnlyList<string> waiting = StatusFixtures.Compose(blocked).Facts;
        waiting.Should().HaveCount(2, "the count and the blockers are separate facts, so ranking facts can join them");
        waiting[0].Should().Contain("1 dependency");
        waiting[1].Should().Contain(TaskListCommand.ShortId(blocker));
    }

    [Fact]
    public void A_published_row_held_by_a_dead_blocker_stops_claiming_it_is_waiting()
    {
        // TaskDependencyFailed appends to the dead list and leaves UnmetDependencies alone, so
        // the count arm read a recorded death as an ordinary wait. A Published row has no phase
        // line, which makes this line the only one the browse surfaces print for it — so the row
        // rendered a red needs-you column beside a claim the stream contradicts, with the
        // recorded reason nowhere on it. Origin incident (2026-08-22, pre-PR review cycle 4).
        Guid blocker = DomainId.New();
        TaskListItem held = StatusFixtures.Task(TaskState.Blocked);
        held.UnmetDependencies = [blocker];
        held.DependencyFailureReason =
            $"Dependency {TaskListCommand.ShortId(blocker)} (Failed) ended there and will never close out on its own.";

        TaskStatusRow row = StatusFixtures.Compose(held);

        row.Facts[0].Should().Be("a blocker will not close out on its own",
            "the same words its phase-line twin uses for the same records");
        row.Facts.Should().NotContain(fact => fact.Contains("to close out", StringComparison.Ordinal));
        row.Facts[1].Should().Contain(TaskListCommand.ShortId(blocker));
        row.Attention.NeedsYou.Should().BeTrue();
        row.SummaryMarkup.Should().ContainSingle()
            .Which.Should().Contain("will not close out on its own",
                "the browse surfaces print this line and no other for a Published row");
    }

    [Fact]
    public void A_queued_row_names_a_dispatch_slot_only_where_a_sweep_measured_one()
    {
        // The never-guess rule (AGENTS.md) against the commonest reading of a still queue: with
        // no daemon sweeping, nothing is dispatching because nothing is running, and there is no
        // slot contention at all. The line said there was, unconditionally, before the fact was
        // routed through the measurement the dispatcher publishes (Decisions Log #64, #66).
        TaskListItem queued = StatusFixtures.Task(TaskState.Queued);

        StatusFixtures.Compose(queued).Facts
            .Should().ContainSingle("no measurement means nothing is known about capacity")
            .Which.Should().NotContain("slot");
        StatusFixtures.Compose(queued, pressure: new DispatchPressure(LiveRuns: 1, MaxConcurrentRuns: 3)).Facts
            .Should().ContainSingle("a node with room is not holding anything back")
            .Which.Should().NotContain("slot");

        IReadOnlyList<string> full = StatusFixtures
            .Compose(queued, pressure: new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3)).Facts;
        full.Should().HaveCount(2);
        full[1].Should().Be("waiting for a slot — 3 of 3 running");
    }

    [Fact]
    public void A_queued_row_held_by_a_full_node_is_the_row_the_queue_section_lists()
    {
        // The pane earns a queue section only where the ceiling is the reason nothing moves
        // (Decisions Log #64), and it decides that off the flag rather than by reading a
        // sentence back out of a display string.
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued)).WaitingForSlot.Should().BeFalse();
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Queued),
                pressure: new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3))
            .WaitingForSlot.Should().BeTrue();

        // The pressure explains a queue that will not move. A pull request being watched is
        // waiting on GitHub, not on this machine, and borrowing the ceiling's line for it would
        // name the wrong cause.
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Done, pullRequest: "https://github.com/x/y/pull/7"),
                pressure: new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3))
            .WaitingForSlot.Should().BeFalse();
    }

    [Fact]
    public void Nothing_but_an_observed_merge_renders_as_done()
    {
        // The origin confusion (task 17): Done rendered while the pull request was open and
        // follow-up runs were still going, contradicting the platform's own strictest rule.
        Guid runId = DomainId.New();
        TaskListItem done = StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/7");

        StatusFixtures.Compose(done, StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null))
            .State.Should().Be(LifecycleState.Delivered);
        StatusFixtures.Compose(done, StatusFixtures.Run(runId, RunState.ReviewPending, sessionProcessId: null))
            .State.Should().Be(LifecycleState.Delivered);
        StatusFixtures.Compose(done, StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null))
            .State.Should().Be(LifecycleState.Done, "the observed merge is true closeout");

        RunDetails merged = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null);
        merged.PullRequestMergedAt = Now;
        StatusFixtures.Compose(done, merged).State.Should().Be(LifecycleState.Done);

        // Nothing was ever pushed, so there is no merge to wait for and the story really is over.
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done)).State.Should().Be(LifecycleState.Done);
    }

    /// <summary>
    /// A pr-review task's Done never watched a merge (Decisions Log #99): its own park resolves
    /// with <c>h9k review resolve --merge-ready</c> directly, with no pull request of its own to
    /// open. The ledger's closeout-reason prose has to say that rather than the merge-observed
    /// wording every merge-watching task type earns, or it asserts an observation nobody made —
    /// exactly the Windows field report item 12 defect (2026-09-03): pr-review task 7f1812db
    /// reported "the merge was observed" while the pull request it reviewed still sat open.
    /// </summary>
    [Theory]
    [InlineData("Feature")]
    [InlineData("Bugfix")]
    [InlineData("Refactor")]
    [InlineData("Chore")]
    [InlineData("Research")]
    public void A_merge_watching_task_types_done_reason_names_the_observed_merge(string taskType)
    {
        TaskShowCommand.DoneReason(taskType, "https://github.com/x/y/pull/1")
            .Should().Be("the merge was observed");
    }

    /// <summary>
    /// A merge-watching task closed by hand with no pull request ever opened never had a merge
    /// to watch (self-review, task bc1ea50d): the same never-guess-at-unobserved-facts rule this
    /// branch fixes for a pr-review task applies here too, since asserting "the merge was
    /// observed" for a task that never pushed is the identical unobserved-fact claim under a
    /// different task type.
    /// </summary>
    [Theory]
    [InlineData("Feature")]
    [InlineData("Bugfix")]
    [InlineData("Refactor")]
    [InlineData("Chore")]
    [InlineData("Research")]
    public void A_merge_watching_task_type_closed_with_no_pull_request_names_nothing_to_watch(string taskType)
    {
        TaskShowCommand.DoneReason(taskType, "")
            .Should().Be("the task was closed with no pull request to watch");
    }

    [Fact]
    public void A_pr_review_tasks_done_reason_names_the_delivered_review_never_a_merge()
    {
        // A pr-review task's own PullRequestUrl names the pull request it reviewed, not one of
        // its own (PrReviewEngine.FinalizeAsync) — a non-blank URL here must not flip the answer
        // to the merge-observed wording the way it would for a merge-watching task type.
        string reason = TaskShowCommand.DoneReason(
            TaskType.PrReview, "https://github.com/AgelessRx/arx-platform/pull/1976");

        reason.Should().Be("the review was delivered");
        reason.Should().NotContain("merge", "no merge was ever watched for a pr-review task's own closeout");
    }

    [Fact]
    public void A_done_pr_review_row_never_renders_the_merge_was_observed()
    {
        // Windows field report item 12 (2026-09-03): PrReviewEngine.FinalizeAsync records the
        // reviewed pull request's own URL on TaskCompleted and completes the run alongside it —
        // reaching lifecycle Done exactly as an ordinary merged task does — even though that
        // pull request (AgelessRx/arx-platform#1976) sat open the whole time. Nothing here ever
        // watched it merge, so the reason must not claim otherwise.
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(
            TaskState.Done, runId, "https://github.com/AgelessRx/arx-platform/pull/1976", type: TaskType.PrReview);
        RunDetails run = StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null);

        TaskStatusRow row = StatusFixtures.Compose(task, run);

        row.State.Should().Be(LifecycleState.Done, "PrReviewEngine.FinalizeAsync completes the run the same way a merge would");

        string gloss = TaskShowCommand.StateGloss(row);

        gloss.Should().Contain("the review was delivered")
            .And.NotContain("the merge was observed");
    }

    [Fact]
    public void A_done_feature_row_that_pushed_and_merged_still_asserts_the_observed_merge()
    {
        Guid runId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(
            TaskState.Done, runId, "https://github.com/x/y/pull/1", type: TaskType.Feature);
        RunDetails run = StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null);

        TaskStatusRow row = StatusFixtures.Compose(task, run);

        TaskShowCommand.StateGloss(row).Should().Contain("the merge was observed");
    }

    /// <summary>
    /// A Feature task closed by hand with no pull request ever opened (<c>h9k task resolve</c>
    /// with no <c>--pr</c>) never had a merge to watch: <see cref="TaskStatusComposer.Closed"/>
    /// still reads it as true closeout for display purposes (there was nothing to observe), but
    /// the reason has to say so honestly rather than claiming a merge nobody watched.
    /// </summary>
    [Fact]
    public void A_done_feature_row_with_no_pull_request_names_nothing_to_watch()
    {
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, type: TaskType.Feature));

        row.State.Should().Be(LifecycleState.Done, "nothing was ever pushed, so there is no merge to wait for");

        TaskShowCommand.StateGloss(row).Should()
            .Contain("the task was closed with no pull request to watch")
            .And.NotContain("the merge was observed");
    }

    [Fact]
    public void A_claimed_task_is_working_until_it_pushes_and_delivered_afterwards()
    {
        Guid runId = DomainId.New();

        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Claimed, runId), StatusFixtures.Run(runId, RunState.Running))
            .State.Should().Be(LifecycleState.Working);
        StatusFixtures.Compose(
                StatusFixtures.Task(TaskState.Claimed, runId, "https://github.com/x/y/pull/7"),
                StatusFixtures.Run(runId, RunState.Running))
            .State.Should().Be(LifecycleState.Delivered, "a follow-up run works on already-delivered work");
    }

    /// <summary>
    /// TaskDecider.Revise's own Draft-only gate lets the queue-first marker through on a
    /// currently-Claimed task (Decisions Log #127), which reads as LifecycleState.Working, not
    /// Published — the one lifecycle word PublishedFacts.Compose used to refuse to say anything
    /// about at all, so the marker was recorded on the stream but invisible everywhere on the
    /// board until the task's next turn in the queue (independent pre-PR review, cycle 1,
    /// conformance lens).
    /// </summary>
    [Fact]
    public void A_marked_task_says_so_even_while_working()
    {
        Guid runId = DomainId.New();
        TaskListItem working = StatusFixtures.Task(TaskState.Claimed, runId);
        working.QueuePriorityMarked = true;

        TaskStatusRow row = StatusFixtures.Compose(working, StatusFixtures.Run(runId, RunState.Running));

        row.State.Should().Be(LifecycleState.Working);
        row.Facts.Should().ContainSingle()
            .Which.Should().Contain("marked queue-first");
    }

    /// <summary>
    /// Pre-approval is settable on any live non-terminal task, not only a Published one (task: a
    /// task can be published pre-approved), so the board must not go quiet about it just because
    /// the task has moved on to Working — the identical reasoning the queue-first marker above
    /// already gets.
    /// </summary>
    [Fact]
    public void A_pre_approved_task_says_so_even_while_working()
    {
        Guid runId = DomainId.New();
        TaskListItem working = StatusFixtures.Task(TaskState.Claimed, runId, preApproved: true);

        TaskStatusRow row = StatusFixtures.Compose(working, StatusFixtures.Run(runId, RunState.Running));

        row.State.Should().Be(LifecycleState.Working);
        row.Facts.Should().ContainSingle()
            .Which.Should().Contain("pre-approved");
    }

    /// <summary>
    /// LifecycleState.Done renders only at TRUE closeout (the merge observed), so a pre-approved
    /// task's own fact must not survive there: it would claim a future merge for a pull request
    /// that has already merged (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public void A_truly_closed_out_done_task_no_longer_states_its_pre_approval()
    {
        Guid runId = DomainId.New();
        const string PullRequest = "https://github.com/acme/widgets/pull/9";
        TaskListItem task = StatusFixtures.Task(TaskState.Done, runId, PullRequest, preApproved: true);

        TaskStatusRow row = StatusFixtures.Compose(task, StatusFixtures.Run(runId, RunState.Completed, sessionProcessId: null));

        row.State.Should().Be(LifecycleState.Done, "closeout observed the merge");
        row.Facts.Should().BeEmpty(
            "the pull request already merged — there is nothing left for pre-approval to govern");
    }

    [Fact]
    public void An_abandoned_task_reads_as_archived_whichever_word_the_stream_records()
    {
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Abandoned)).State.Should().Be(LifecycleState.Archived);
        // Backlog 33 renames the persisted word in its own pass; when it does, nothing here changes.
        StatusFixtures.Compose(StatusFixtures.Task("Archived")).State.Should().Be(LifecycleState.Archived);
    }

    /// <summary>
    /// The blocker lists on h9k task show and h9k task assign name a dependency in the same
    /// vocabulary as its own row. Origin incident (pre-PR review, 2026-08-22): they interpolated
    /// the persisted state, so a blocker whose pull request was open printed "(Done)" beside the
    /// mark saying it was still holding the dependent back — the premature Done this pass exists
    /// to remove, reproduced on the one screen that explains the true-closeout rule.
    /// </summary>
    [Fact]
    public void A_blocker_reads_the_same_word_on_the_dependency_list_as_on_its_own_row()
    {
        Guid runId = DomainId.New();
        const string PullRequest = "https://github.com/x/y/pull/10";

        TaskDependency pushed = Dependency(TaskState.Done, PullRequest, RunState.AwaitingReview, closedOut: false);
        TaskStatusRow ownRow = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, PullRequest),
            StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null));

        TaskStatusComposer.State(pushed).Should().Be(LifecycleState.Delivered)
            .And.Be(ownRow.State, "the same task cannot be two words one command apart");
        pushed.Blocks.Should().BeTrue("the word and the mark beside it agree now");

        TaskStatusComposer.State(Dependency(TaskState.Done, PullRequest, RunState.Completed, closedOut: true))
            .Should().Be(LifecycleState.Done, "the observed merge is true closeout, on either screen");
        TaskStatusComposer.State(Dependency(TaskState.Queued, pullRequest: null, runState: null, closedOut: false))
            .Should().Be(LifecycleState.Published);
    }

    /// <summary>
    /// A blocker resolved by hand with no pull request. The dependency rule still refuses it
    /// (there is no merge observation to be had), and the display still calls it Done for the
    /// reason every other surface does: nothing was ever pushed, so nothing is pending. The mark
    /// beside the word and the recorded death reason are what carry the disagreement — the word
    /// itself must not contradict the blocker's own row.
    /// </summary>
    [Fact]
    public void A_blocker_that_never_pushed_reads_done_even_though_it_never_closes_out()
    {
        TaskDependency resolved = Dependency(TaskState.Done, pullRequest: null, RunState.Failed, closedOut: false);

        TaskStatusComposer.State(resolved).Should().Be(LifecycleState.Done)
            .And.Be(StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done)).State);
        resolved.IsDead.Should().BeTrue("the mark, not the word, is what says it will never close out");
        TaskStatusComposer.DependencyMark(resolved).Should().Contain("never closes out");
    }

    /// <summary>
    /// The mark lives beside the word because the word depends on it: three answers rather than
    /// two, since a blocker that will never close out needs a human and one that simply has not
    /// yet does not (Decisions Log #34). Origin incident (pre-PR review cycle 4, 2026-08-22):
    /// h9k task assign printed the word alone, so a hand-resolved blocker was listed as "(Done)"
    /// directly under the sentence saying it had not closed out.
    /// </summary>
    [Fact]
    public void A_blocker_is_marked_wherever_its_word_is_printed()
    {
        TaskStatusComposer.DependencyMark(
                Dependency(TaskState.Done, "https://github.com/x/y/pull/10", RunState.Completed, closedOut: true))
            .Should().Contain("closed out");
        TaskStatusComposer.DependencyMark(
                Dependency(TaskState.Done, pullRequest: null, RunState.Failed, closedOut: false))
            .Should().Contain("never closes out");
        TaskStatusComposer.DependencyMark(
                Dependency(TaskState.Done, "https://github.com/x/y/pull/10", RunState.AwaitingReview, closedOut: false))
            .Should().Contain("waiting", "a pull request still under watch is the one case that clears itself");
    }

    private static TaskDependency Dependency(
        TaskState state, string? pullRequest, RunState? runState, bool closedOut) =>
        new(DomainId.New(), "blocker", state, closedOut, runState, pullRequest, TaskType.Chore, []);

    [Fact]
    public void A_persisted_state_this_build_does_not_know_says_so_instead_of_picking_one()
    {
        TaskStatusRow row = StatusFixtures.Compose(StatusFixtures.Task("NeedsRefinement"));

        row.State.Should().Be(LifecycleState.Unknown);
        row.Group.Should().Be(AttentionBucket.Closed);
    }

    [Fact]
    public void Every_row_carries_its_assignee_and_an_unassigned_one_says_so()
    {
        Guid ownerId = DomainId.New();
        TaskListItem assigned = StatusFixtures.Task(TaskState.Queued);
        assigned.AssignedOwnerId = ownerId;

        StatusFixtures.Compose(assigned, owners: new Dictionary<Guid, string> { [ownerId] = "Brian" })
            .Assignee.Should().Be("Brian");
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Draft)).AssigneeMarkup
            .Should().Be("[dim]—[/]", "an empty cell would read as a gap");
    }

    [Fact]
    public void The_rollup_counts_the_lifecycle_states_and_still_sums_to_the_task_count()
    {
        TaskStatusRow[] rows =
        [
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Draft)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Draft)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Published)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Blocked)),
            StatusFixtures.Compose(StatusFixtures.Task(TaskState.Queued)),
        ];

        TaskRollup rollup = TaskRollup.From(rows);

        rollup.Draft.Should().Be(2);
        rollup.Ready.Should().Be(1);
        rollup.Blocked.Should().Be(1);
        rollup.Queued.Should().Be(1);
        rollup.Total.Should().Be(rows.Length, "the buckets stay single-assignment");
        rollup.Summary().Should().Contain("2 draft").And.Contain("1 ready to assign").And.Contain("1 blocked");
    }

    /// <summary>
    /// A Jira write stuck on a rejected credential carries no lifecycle state of its own, so it must
    /// not shadow a park or a failure that is the row's actual reason for wanting a human
    /// (independent pre-PR review, cycle 1): the arm used to be checked first, so a task that was
    /// separately Failed, or whose run was separately parked, read as the credential-refresh row
    /// and never named the lever that would actually move it.
    /// </summary>
    [Fact]
    public void A_stuck_jira_write_does_not_hide_a_failed_row_behind_a_rejected_credential()
    {
        TaskListItem task = StatusFixtures.Task(TaskState.Failed);
        task.FailureReason = "the run crashed";
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task);

        row.Attention.Cause.Should().Be("the run crashed");
        row.Attention.Lever.Should().Contain("h9k task retry");
    }

    /// <summary>The companion case: a review park outranks the stuck write exactly as a failure does.</summary>
    [Fact]
    public void A_stuck_jira_write_does_not_hide_a_review_park_behind_a_rejected_credential()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked);
        parked.ParkedReason = "a finding could not be settled";
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task, parked);

        row.Attention.Cause.Should().Be("a finding could not be settled");
        row.Attention.Lever.Should().Contain("h9k review resolve");
    }

    /// <summary>
    /// Independent pre-PR review, cycle 5, adversarial finding: a cap-0 takeover park or the
    /// lifetime-budget park accepts <c>--needs-fixes</c> rather than refusing it outright, but
    /// granting one there will not clear the park. The fixture below is the per-track cap-0 case,
    /// where nothing ever runs before the identical re-park; a final-full-pass or lifetime-budget
    /// cap-0 park dispatches one more fix session but re-parks right behind it just the same,
    /// since the cap or budget itself never resets (cycle 2, adversarial lens: the original
    /// wording claimed "nothing ever runs in between" held for every cap-0 case, which is false
    /// for those latter two). The row's lever must agree with the park's own reason instead of
    /// offering the command as though it settled anything.
    /// </summary>
    [Fact]
    public void A_park_where_needs_fixes_buys_no_progress_offers_only_merge_ready()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked);
        parked.ParkedReason = "The conformance review's cap is 0, from a task override — a cap that low " +
            "parks every cycle immediately, so granting a fresh round with --needs-fixes will not clear " +
            "this park.";
        parked.ParkedNeedsFixesOffersNoProgress = true;
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);

        TaskStatusRow row = StatusFixtures.Compose(task, parked);

        row.Attention.Lever.Should().Contain("--merge-ready")
            .And.NotContain("--needs-fixes \"…\"", "the ordinary needs-fixes offer contradicts this park's own reason");
    }

    /// <summary>
    /// The companion case: an ordinary review park still offers both verdicts exactly as before —
    /// the new no-progress lever only applies when the run actually recorded it.
    /// </summary>
    [Fact]
    public void An_ordinary_review_park_still_offers_needs_fixes()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.ReviewParked);
        parked.ParkedReason = "a finding could not be settled";
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);

        TaskStatusRow row = StatusFixtures.Compose(task, parked);

        row.Attention.Lever.Should().Contain("--needs-fixes");
    }

    /// <summary>
    /// With nothing else amiss, the stuck write is still the reason the row wants a human — the
    /// fallback the reordering has to preserve.
    /// </summary>
    [Fact]
    public void A_stuck_jira_write_still_surfaces_when_nothing_else_is_amiss()
    {
        TaskListItem task = StatusFixtures.Task(TaskState.Done);
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task);

        row.Attention.Cause.Should().Be("Jira rejected the registered credentials");
        row.Attention.Lever.Should().Be("h9k connection add jira --site https://your-org.atlassian.net --email you@example.com");
    }

    /// <summary>
    /// The companion direction for the interactive-claim nudge (Decisions Log #103): a stale
    /// claim is never the reason a Jira write failed to authenticate, so it must not shadow the
    /// credential-refresh row either (adversarial pre-PR review, cycle 1) — the nudge arm is
    /// checked after this one for exactly that reason.
    /// </summary>
    [Fact]
    public void A_stuck_jira_write_does_not_hide_behind_a_stale_interactive_claim_nudge()
    {
        Guid runId = DomainId.New();
        RunDetails interactive = StatusFixtures.Run(runId, RunState.Running, sessionProcessId: null);
        interactive.LastInteractiveActivityAt = StatusFixtures.Now.AddDays(-4);
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId, claimedByNodeId: Guid.Empty);
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task, interactive);

        row.Attention.Cause.Should().Be("Jira rejected the registered credentials");
        row.Attention.Lever.Should().Be("h9k connection add jira --site https://your-org.atlassian.net --email you@example.com");
    }

    /// <summary>
    /// A budget park clears itself on a clock and is explicitly not an ask, so it must not
    /// outrank a stuck write — the opposite ordering that independent pre-PR review cycle 2 found:
    /// the pending-write arm had been moved past BudgetParked, so a task parked on a spent budget
    /// window read as an ignorable wait for as long as that window held, even while the same
    /// stuck write kept getting retried underneath it every five minutes.
    /// </summary>
    [Fact]
    public void A_stuck_jira_write_outranks_a_budget_park()
    {
        Guid runId = DomainId.New();
        RunDetails parked = StatusFixtures.Run(runId, RunState.BudgetParked, sessionProcessId: null);
        TaskListItem task = StatusFixtures.Task(TaskState.Claimed, runId);
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task, parked);

        row.Attention.Level.Should().Be(AttentionLevel.NeedsYou);
        row.Attention.Cause.Should().Be("Jira rejected the registered credentials");
        row.Attention.Lever.Should().Be("h9k connection add jira --site https://your-org.atlassian.net --email you@example.com");
    }

    /// <summary>
    /// The companion direction: a dead blocker or a stalled run is the row's actual reason for
    /// wanting a human, so a stuck write must not hide either behind the credential-refresh row —
    /// the same review's other complaint about the same reordering, since the pending-write arm had
    /// also been moved ahead of both.
    /// </summary>
    [Fact]
    public void A_stuck_jira_write_does_not_hide_a_dead_blocker_behind_a_rejected_credential()
    {
        TaskListItem task = StatusFixtures.Task(TaskState.Blocked);
        task.DependencyFailureReason = "the blocker was abandoned";
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task);

        row.Attention.Cause.Should().Be("the blocker was abandoned");
    }

    /// <summary>
    /// A pull request open and awaiting review is the one case where a NeedsYou row is still
    /// grouped under Delivered rather than the Needs-you section (the merge is genuinely the
    /// reader's own to make), but that exception must not swallow a stuck Jira write riding along
    /// on the same Delivered, AwaitingReview row: the write wants the connection refreshed, not a
    /// merge decision, and it has to stay in the section AGENTS.md's own relay table promises it —
    /// otherwise `h9k status` and `h9k task list --state needs-you` both go quiet on it while the
    /// daemon keeps retrying the same doomed write underneath (independent pre-PR review,
    /// adversarial lens, cycle 11).
    /// </summary>
    [Fact]
    public void A_stuck_jira_write_stays_needs_you_even_on_a_delivered_awaiting_review_row()
    {
        Guid runId = DomainId.New();
        RunDetails awaitingReview = StatusFixtures.Run(runId, RunState.AwaitingReview, sessionProcessId: null);
        TaskListItem task = StatusFixtures.Task(TaskState.Done, runId, pullRequest: "https://github.com/x/y/pull/10");
        task.PendingJiraWriteIsAuthFailure = true;
        task.PendingJiraWriteFailureReason = "Jira rejected the registered credentials";

        TaskStatusRow row = StatusFixtures.Compose(task, awaitingReview);

        row.State.Should().Be(LifecycleState.Delivered);
        row.Group.Should().Be(AttentionBucket.NeedsYou);
        row.Attention.Cause.Should().Be("Jira rejected the registered credentials");
        row.Attention.Lever.Should().Be("h9k connection add jira --site https://your-org.atlassian.net --email you@example.com");
    }
}
