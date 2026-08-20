using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// h9k task list's own promises: --state selects exactly what the Status column shows, and
/// a bounded list always says what it held back and how to see it.
/// </summary>
public sealed class TaskBrowseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_attention_word_selects_the_whole_group_and_an_exact_state_selects_one_bucket()
    {
        Guid runId = DomainId.New();
        TaskStatusRow running = Row(Task(TaskState.Claimed, runId), new RunListItem { Id = runId, State = RunState.Running });
        TaskStatusRow failed = Row(Task(TaskState.Failed));

        TaskStateFilter.Matches(running, "active").Should().BeTrue();
        TaskStateFilter.Matches(running, "running").Should().BeTrue("an exact bucket matches too");
        TaskStateFilter.Matches(running, "needs-you").Should().BeFalse();

        TaskStateFilter.Matches(failed, "needs-you").Should().BeTrue("Failed waits for a human decision");
        TaskStateFilter.Matches(failed, "NEEDS_YOU").Should().BeTrue("separators and case are noise");
        TaskStateFilter.Matches(failed, "Failed").Should().BeTrue();
        TaskStateFilter.Matches(failed, "done").Should().BeFalse();
    }

    [Fact]
    public void The_pull_request_group_is_in_review_and_leaves_its_member_states_selectable()
    {
        // The group covers three buckets, so it must not be spelled like any of them:
        // normalization erases hyphens and case, so awaiting-review would have become
        // AwaitingReview and swallowed the one state an operator asks for when they want
        // the pull requests that are quietly waiting on a reviewer.
        TaskStatusRow awaitingReview = Closeout(RunState.AwaitingReview);
        TaskStatusRow checksFailing = Closeout(RunState.ChecksFailing);
        TaskStatusRow reviewPending = Closeout(RunState.ReviewPending);

        foreach (TaskStatusRow row in new[] { awaitingReview, checksFailing, reviewPending })
        {
            TaskStateFilter.Matches(row, "in-review").Should().BeTrue($"{row.Bucket} is an open pull request");
            TaskStateFilter.Matches(row, "INREVIEW").Should().BeTrue("separators and case are noise");
        }

        TaskStateFilter.Matches(awaitingReview, "AwaitingReview").Should().BeTrue();
        TaskStateFilter.Matches(checksFailing, "AwaitingReview").Should().BeFalse("red CI is not a quiet PR");
        TaskStateFilter.Matches(reviewPending, "awaitingreview").Should().BeFalse("nor is unanswered review feedback");
    }

    [Fact]
    public void A_state_that_names_a_bucket_selects_that_bucket_and_nothing_wider()
    {
        // The whole vocabulary at once: whatever else --state accepts, a word that spells a
        // bucket must select exactly that bucket, or the help's promise ("an exact state
        // selects just that one") is false for the states an attention word happens to spell.
        foreach (string state in TaskStateFilter.Buckets)
        {
            string[] selected = [.. TaskStateFilter.Buckets.Where(bucket => TaskStateFilter.Matches(RowFor(bucket), state))];

            selected.Should().Equal([state], $"--state {state} promises just that one state");
        }
    }

    [Fact]
    public void An_unknown_state_fails_with_the_vocabulary_quoted_rather_than_returning_nothing()
    {
        // Zero rows would read as "you have no such work"; the agent needs to know it
        // mistyped the filter, and what the accepted words are.
        Action filter = () => TaskStateFilter.Validate("in-progress");

        filter.Should().Throw<DomainValidationException>()
            .WithMessage("*not a state h9k tracks*needs-you*Running*");
    }

    [Fact]
    public void Every_composable_bucket_is_a_state_the_filter_accepts()
    {
        Guid runId = DomainId.New();
        string[] buckets =
        [
            Row(Task(TaskState.Queued)).Bucket,
            Row(Task(TaskState.NeedsHuman)).Bucket,
            Row(Task(TaskState.Failed)).Bucket,
            Row(Task(TaskState.Abandoned)).Bucket,
            Row(Task(TaskState.Claimed, runId), new RunListItem { Id = runId, State = RunState.Verifying }).Bucket,
            Row(Task(TaskState.Claimed, runId, "https://github.com/x/y/pull/7")).Bucket,
            Row(Task(TaskState.Done, pullRequest: "https://github.com/x/y/pull/7")).Bucket,
        ];

        foreach (string bucket in buckets)
        {
            Action validate = () => TaskStateFilter.Validate(bucket);
            validate.Should().NotThrow($"'{bucket}' is a state the Status column can show");
        }
    }

    [Fact]
    public void A_bounded_list_says_how_many_it_held_back_and_the_flag_that_shows_them()
    {
        string footer = TaskListCommand.Footer(137, TaskListCommand.DefaultLimit, new TaskListCommand.Settings(), project: null);

        footer.Should().Contain("20 of 137").And.Contain("newest first");
        footer.Should().Contain("117 held back").And.Contain("h9k task list --all");
    }

    [Fact]
    public void The_held_back_hint_repeats_the_filters_so_all_keeps_the_view_you_are_looking_at()
    {
        ProjectDetails project = new() { Id = DomainId.New(), Name = "hall9k" };
        TaskListCommand.Settings settings = new() { Project = "hall", State = "needs-you" };

        string footer = TaskListCommand.Footer(30, 20, settings, project);

        footer.Should().Contain("in hall9k").And.Contain("matching --state needs-you");
        footer.Should().Contain("--all --project hall9k --state needs-you");
    }

    [Fact]
    public void An_unbounded_view_teaches_the_filters_instead_of_claiming_rows_were_held_back()
    {
        string footer = TaskListCommand.Footer(4, 4, new TaskListCommand.Settings(), project: null);

        footer.Should().Contain("4 of 4").And.NotContain("held back");
        footer.Should().Contain("--project <name>").And.Contain("--state <state>");
    }

    /// <summary>A task in the closeout phase: done with a pull request, its run in the given state.</summary>
    private static TaskStatusRow Closeout(RunState state)
    {
        Guid runId = DomainId.New();
        return Row(
            Task(TaskState.Done, runId, "https://github.com/x/y/pull/7"),
            new RunListItem { Id = runId, State = state });
    }

    /// <summary>A composed row that actually sits in the named bucket, whichever surface produces it.</summary>
    private static TaskStatusRow RowFor(string bucket)
    {
        Guid runId = DomainId.New();
        TaskStatusRow row = bucket switch
        {
            "Queued" or "Claimed" or "NeedsHuman" or "Done" or "Failed" or "Abandoned" => Row(Task(bucket)),
            "ClosingOut" => Row(
                Task(TaskState.Claimed, runId, "https://github.com/x/y/pull/7"),
                new RunListItem { Id = runId, State = RunState.Running }),
            _ => Row(Task(TaskState.Claimed, runId), new RunListItem { Id = runId, State = bucket }),
        };

        row.Bucket.Should().Be(bucket, "the fixture has to compose the bucket it claims to");
        return row;
    }

    private static TaskListItem Task(TaskState state, Guid? runId = null, string? pullRequest = null) => new()
    {
        Id = DomainId.New(),
        ProjectId = DomainId.New(),
        Objective = "x",
        State = state,
        CurrentRunId = runId,
        PullRequestUrl = pullRequest,
        AddedAt = Now,
    };

    private static TaskStatusRow Row(TaskListItem task, RunListItem? run = null, DateTimeOffset? silentSince = null) =>
        TaskStatusComposer.Compose(
            task,
            run is null ? new Dictionary<Guid, RunListItem>() : new Dictionary<Guid, RunListItem> { [run.Id] = run },
            run is null || silentSince is null
                ? new Dictionary<Guid, RunActivity>()
                : new Dictionary<Guid, RunActivity> { [run.Id] = new() { Id = run.Id, LastActivityAt = silentSince.Value } },
            new Dictionary<Guid, string>(),
            Now);
}
