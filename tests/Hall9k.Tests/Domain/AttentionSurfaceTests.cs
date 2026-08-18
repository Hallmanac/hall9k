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

        StatusCommand.StatusRow row = StatusCommand.Compose(
            task,
            new Dictionary<Guid, RunListItem> { [runId] = run },
            new Dictionary<Guid, RunActivity> { [runId] = silent },
            new Dictionary<Guid, string>(),
            Now);

        row.Bucket.Should().Be("Running", "the task's work state refines to the run's execution state");
        row.Stalled.Should().BeTrue("two hours of stream silence is past the one-hour threshold");
        row.StatusMarkup.Should().Contain("STALLED");
    }

    [Fact]
    public void Done_with_a_pull_request_reads_as_awaiting_review()
    {
        TaskListItem task = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Done, PullRequestUrl = "https://github.com/x/y/pull/7", AddedAt = Now,
        };

        StatusCommand.StatusRow row = StatusCommand.Compose(
            task, new Dictionary<Guid, RunListItem>(), new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(), Now);

        row.Bucket.Should().Be("AwaitingReview");
        row.PullRequest.Should().Contain("#7");
        row.Stalled.Should().BeFalse("finished work is never stalled");
    }

    [Fact]
    public void Retried_task_leaves_the_failed_bucket_and_queues_like_any_other_work()
    {
        TaskListItem task = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.Failed, AddedAt = Now,
        };

        StatusCommand.Compose(
                task, new Dictionary<Guid, RunListItem>(), new Dictionary<Guid, RunActivity>(),
                new Dictionary<Guid, string>(), Now)
            .Bucket.Should().Be("Failed");

        // h9k task retry moves the read model back to Queued (Decisions Log #25).
        task.State = TaskState.Queued;
        StatusCommand.Compose(
                task, new Dictionary<Guid, RunListItem>(), new Dictionary<Guid, RunActivity>(),
                new Dictionary<Guid, string>(), Now)
            .Bucket.Should().Be("Queued");
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
        StatusCommand.StatusRow running = Compose(claimed, new RunListItem { Id = runId, State = RunState.Running });
        running.Bucket.Should().Be("ClosingOut");
        running.Priority.Should().Be(2, "an in-flight follow-up ranks with active work");
    }

    private static StatusCommand.StatusRow Compose(TaskListItem task, RunListItem? run) =>
        StatusCommand.Compose(
            task,
            run is null ? new Dictionary<Guid, RunListItem>() : new Dictionary<Guid, RunListItem> { [run.Id] = run },
            new Dictionary<Guid, RunActivity>(),
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

        StatusCommand.StatusRow active = StatusCommand.Compose(
            task, runs,
            new Dictionary<Guid, RunActivity> { [runId] = new() { Id = runId, LastActivityAt = Now.AddMinutes(-1) } },
            new Dictionary<Guid, string>(),
            Now);
        active.Bucket.Should().Be("UnderReview");
        active.Priority.Should().Be(2, "a run under review is active work, not waiting");
        active.Stalled.Should().BeFalse();

        StatusCommand.StatusRow silent = StatusCommand.Compose(
            task, runs,
            new Dictionary<Guid, RunActivity> { [runId] = new() { Id = runId, LastActivityAt = Now.AddHours(-2) } },
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
    public void Needs_human_outranks_everything_in_priority()
    {
        TaskListItem needsHuman = new()
        {
            Id = DomainId.New(), ProjectId = DomainId.New(), Objective = "x",
            State = TaskState.NeedsHuman, AddedAt = Now,
        };

        StatusCommand.StatusRow row = StatusCommand.Compose(
            needsHuman, new Dictionary<Guid, RunListItem>(), new Dictionary<Guid, RunActivity>(),
            new Dictionary<Guid, string>(), Now);

        row.Priority.Should().Be(0);
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
