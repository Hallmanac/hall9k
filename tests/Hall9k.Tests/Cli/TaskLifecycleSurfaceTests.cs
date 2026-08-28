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
}
