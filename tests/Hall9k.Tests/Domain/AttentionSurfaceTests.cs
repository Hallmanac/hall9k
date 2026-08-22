using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

public sealed class AttentionSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claimed_task_shows_its_runs_execution_state_and_stalls_after_an_hour_of_silence()
    {
        Guid runId = DomainId.New();
        TaskListItem task = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = runId, AddedAt = Now,
        };
        RunListItem run = new() { Id = runId, State = RunState.Running };
        RunActivity silent = new() { Id = runId, LastActivityAt = Now.AddHours(-2) };

        TaskStatusRow row = TaskStatusComposer.Compose(
            task,
            new Dictionary<Guid, RunListItem> { [runId] = run },
            new Dictionary<Guid, RunActivity> { [runId] = silent },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now);

        row.Bucket.Should().Be("Running", "the task's work state refines to the run's execution state");
        row.Stalled.Should().BeTrue("two hours of stream silence is past the one-hour threshold");
        row.StatusMarkup.Should().Contain("STALLED");
    }

    [Fact]
    public void A_claim_whose_run_has_not_appeared_yet_is_live_work_rather_than_a_closed_row()
    {
        // TaskClaimed commits in its own transaction and the run document only appears once the
        // launcher has inspected the pull request and checked a worktree out, so every dispatch
        // spends seconds as a claim with nothing to refine it. A daemon that dies inside that
        // window leaves the task there for good: the lease sweep that would requeue it only runs
        // while the daemon runs. Counting the row as Closed would drop it out of h9k status
        // entirely, in exactly the situation an operator is looking for it.
        TaskListItem claimed = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = DomainId.New(), AddedAt = Now,
        };

        TaskStatusRow row = Compose(claimed, run: null);

        row.Bucket.Should().Be("Claimed");
        row.Attention.Should().Be(AttentionBucket.Active, "the platform holds the claim, so the work is live");
        row.Priority.Should().Be(2, "the dispatch handoff ranks with the work it is becoming");
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Killed")]
    [InlineData("Superseded")]
    public void A_finished_run_under_a_still_claimed_task_stays_live_until_the_task_moves(string runState)
    {
        // The closing half of the same handoff: the run has ended but the task's own transition
        // has not committed yet. The claim still says the platform owns the next move.
        Guid runId = DomainId.New();
        TaskListItem claimed = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = runId, AddedAt = Now,
        };

        TaskStatusRow row = Compose(claimed, new RunListItem { Id = runId, State = runState });

        row.Attention.Should().Be(AttentionBucket.Active, $"a {runState} run on a claimed task is mid-handoff");
    }

    [Fact]
    public void Done_with_a_pull_request_reads_as_awaiting_review()
    {
        TaskListItem task = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Done, PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };

        TaskStatusRow row = TaskStatusComposer.Compose(
            task, new Dictionary<Guid, RunListItem>(), new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), Now);

        row.Bucket.Should().Be("AwaitingReview");
        row.PullRequestMarkup.Should().Contain("#7");
        row.Stalled.Should().BeFalse("finished work is never stalled");
    }

    [Fact]
    public void Done_run_states_refine_the_closeout_display()
    {
        Guid runId = DomainId.New();
        TaskListItem task = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Done, CurrentRunId = runId,
            PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };

        Compose(task, new RunListItem { Id = runId, State = RunState.AwaitingReview })
            .Bucket.Should().Be("AwaitingReview");
        Compose(task, new RunListItem { Id = runId, State = RunState.ChecksFailing })
            .Bucket.Should().Be("ChecksFailing");
        Compose(task, new RunListItem { Id = runId, State = RunState.ReviewPending })
            .Bucket.Should().Be("ReviewPending");
        Compose(task, new RunListItem { Id = runId, State = RunState.CloseoutParked })
            .Bucket.Should().Be("NeedsHuman", "a parked closeout belongs on the attention surface");
        Compose(task, new RunListItem { Id = runId, State = RunState.Completed })
            .Bucket.Should().Be("Done", "the observed merge ends the closeout — AwaitingReview disappears");
    }

    [Fact]
    public void Reopened_task_with_a_pull_request_reads_as_closing_out_through_the_follow_up_run()
    {
        Guid runId = DomainId.New();
        TaskListItem queued = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Queued, PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };
        TaskListItem claimed = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = runId,
            PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };

        Compose(queued, run: null).Bucket.Should().Be("ClosingOut", "a reopened task's PR is being driven to completion");
        TaskStatusRow running = Compose(claimed, new RunListItem { Id = runId, State = RunState.Running });
        running.Bucket.Should().Be("ClosingOut");
        running.Priority.Should().Be(2, "an in-flight follow-up ranks with active work");
    }

    private static TaskStatusRow Compose(TaskListItem task, RunListItem? run) =>
        TaskStatusComposer.Compose(
            task,
            run is null ? new Dictionary<Guid, RunListItem>() : new Dictionary<Guid, RunListItem> { [run.Id] = run },
            new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now);

    [Fact]
    public void Under_review_reads_as_live_work_between_verifying_and_awaiting_review()
    {
        Guid runId = DomainId.New();
        TaskListItem task = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = runId, AddedAt = Now,
        };
        RunListItem run = new() { Id = runId, State = RunState.UnderReview };
        Dictionary<Guid, RunListItem> runs = new() { [runId] = run };

        TaskStatusRow active = TaskStatusComposer.Compose(
            task, runs,
            new Dictionary<Guid, RunActivity> { [runId] = new() { Id = runId, LastActivityAt = Now.AddMinutes(-1) } },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now);
        active.Bucket.Should().Be("UnderReview");
        active.Priority.Should().Be(2, "a run under review is active work, not waiting");
        active.Stalled.Should().BeFalse();

        TaskStatusRow silent = TaskStatusComposer.Compose(
            task, runs,
            new Dictionary<Guid, RunActivity> { [runId] = new() { Id = runId, LastActivityAt = Now.AddHours(-2) } },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now);
        silent.Stalled.Should().BeTrue("a silent review session stalls like any other live session");
    }

    [Fact]
    public void Review_parked_run_surfaces_as_needs_human_even_mid_closeout()
    {
        Guid runId = DomainId.New();
        TaskListItem claimed = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = runId, AddedAt = Now,
        };
        TaskListItem followUp = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Claimed, CurrentRunId = runId,
            PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };
        RunListItem parked = new() { Id = runId, State = RunState.ReviewParked };

        Compose(claimed, parked).Bucket.Should().Be("NeedsHuman", "a review park hands the diff to the human");
        Compose(claimed, parked).Priority.Should().Be(0);
        Compose(followUp, parked).Bucket.Should().Be(
            "NeedsHuman", "the park outranks the ClosingOut composition — the follow-up cannot push");
    }

    [Fact]
    public void Failed_task_ranks_just_under_needs_human_because_it_waits_for_a_decision()
    {
        TaskListItem failed = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Failed, AddedAt = Now,
        };

        TaskStatusRow row = Compose(failed, run: null);

        row.Bucket.Should().Be("Failed");
        row.Priority.Should().Be(1,
            "Failed is a needs-human waypoint (log #27): retry, resolve, or abandon is a human's call");
    }

    [Fact]
    public void Needs_human_outranks_everything_in_priority()
    {
        TaskListItem needsHuman = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.NeedsHuman, AddedAt = Now,
        };

        TaskStatusRow row = TaskStatusComposer.Compose(
            needsHuman, new Dictionary<Guid, RunListItem>(), new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), Now);

        row.Priority.Should().Be(0);
    }

    [Fact]
    public void A_queued_task_says_it_is_waiting_for_a_slot_when_the_node_is_at_its_ceiling()
    {
        // Queued-waiting-for-a-slot is not a state (Decisions Log #64): the task is Queued and
        // the line is composed from the count the daemon's last sweep published, so a quiet
        // board reads as throttled rather than stalled.
        TaskListItem queued = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Queued, AddedAt = Now,
        };

        TaskStatusRow full = Compose(queued, new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3));

        full.Bucket.Should().Be("Queued", "the ceiling defers a claim; it does not restate the task");
        full.Attention.Should().Be(AttentionBucket.Queued);
        full.WaitingForSlot.Should().BeTrue();
        full.Activity.Should().Be("waiting for a slot — 3 of 3 running");
    }

    [Fact]
    public void A_queued_task_says_nothing_about_slots_when_there_is_room_or_no_measurement()
    {
        TaskListItem queued = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Queued, AddedAt = Now,
        };

        TaskStatusRow room = Compose(queued, new DispatchPressure(LiveRuns: 1, MaxConcurrentRuns: 3));
        room.WaitingForSlot.Should().BeFalse("a task about to be claimed is not being held back");
        room.Activity.Should().BeEmpty();

        TaskStatusRow unmeasured = Compose(queued, pressure: null);
        unmeasured.WaitingForSlot.Should().BeFalse(
            "no current measurement means nothing is known about capacity, which is not the same as a full node");
    }

    [Fact]
    public void A_full_node_says_nothing_about_slots_on_work_it_is_not_holding_back()
    {
        // The pressure explains a queue that will not move. A task whose pull request is open
        // and whose closeout is being watched is waiting on GitHub, not on this machine, and
        // borrowing the ceiling's line for it would name the wrong cause.
        TaskListItem awaitingReview = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Done, PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };

        TaskStatusRow row = Compose(awaitingReview, new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3));

        row.Bucket.Should().Be("AwaitingReview");
        row.WaitingForSlot.Should().BeFalse();
    }

    [Fact]
    public void A_closeout_follow_up_held_back_by_the_ceiling_reads_as_queued_rather_than_running()
    {
        // The closeout monitor reopens a task for a failing check or a review thread: Queued
        // again, its run superseded, its pull request still on it. The dispatcher sees an
        // ordinary queued task and defers it at the ceiling, so the board has to say so —
        // composed off the bucket instead of the state, this row read as running work while the
        // daemon's log called it deferred (pre-PR review, 2026-08-22).
        TaskListItem reopened = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Queued, PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };

        TaskStatusRow held = Compose(reopened, new DispatchPressure(LiveRuns: 3, MaxConcurrentRuns: 3));

        held.WaitingForSlot.Should().BeTrue();
        held.Bucket.Should().Be("Queued", "no follow-up is in flight yet — the run it is waiting to start is");
        held.Attention.Should().Be(AttentionBucket.Queued, "it belongs in the section that explains the wait");
        held.Activity.Should().Be("waiting for a slot — 3 of 3 running");

        // With room on the node the follow-up is dispatching, and the row is closeout work again.
        TaskStatusRow dispatching = Compose(reopened, new DispatchPressure(LiveRuns: 1, MaxConcurrentRuns: 3));
        dispatching.Bucket.Should().Be("ClosingOut");
        dispatching.WaitingForSlot.Should().BeFalse();
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
            Compose(Queued(OldAssignment, addedAt: Now, assignedAt: Now.AddHours(1)), full),
            Compose(Queued(NewAssignment, addedAt: Now.AddHours(5), assignedAt: Now.AddHours(10)), full),
        ];

        StatusCommand.SectionRows(rows, AttentionBucket.Queued, inServiceOrder: true)
            .Select(row => row.Objective).Should().Equal([OldAssignment, NewAssignment],
                "the older assignment is claimed first, so it is listed first — even though the "
                + "other task was drafted more recently");

        StatusCommand.SectionRows(rows, AttentionBucket.Queued, inServiceOrder: false)
            .Select(row => row.Objective).Should().Equal([NewAssignment, OldAssignment],
                "the pane's default is newest arrival first, which is the reverse of the order "
                + "the dispatcher serves these in");
    }

    private static TaskListItem Queued(string objective, DateTimeOffset addedAt, DateTimeOffset assignedAt) => new()
    {
        Id = DomainId.New(), ProjectId = DomainId.New(), Objective = objective,
        State = TaskState.Queued, AddedAt = addedAt, AssignedAt = assignedAt,
    };

    private static TaskStatusRow Compose(TaskListItem task, DispatchPressure? pressure) =>
        TaskStatusComposer.Compose(
            task,
            new Dictionary<Guid, RunListItem>(),
            new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            Now,
            pressure);

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
