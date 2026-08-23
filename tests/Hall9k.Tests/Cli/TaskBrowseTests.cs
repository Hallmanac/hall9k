using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// h9k task list's own promises: --state selects what a reader means by the word they typed, and
/// a bounded list always says what it held back and how to see it.
/// </summary>
public sealed class TaskBrowseTests
{
    /// <summary>
    /// The --help tree is a first-class interface (AGENTS.md), so every group --state accepts
    /// has to be discoverable from it. Origin incident (2026-08-20): the lifecycle split added
    /// blocked, ready and draft to the filter and left the help text listing the pre-split
    /// seven, so an agent looking for its drafts read --help and concluded there was no way.
    /// </summary>
    [Fact]
    public void Every_attention_group_state_accepts_is_named_in_the_state_help_text()
    {
        string help = HelpFor(nameof(TaskListCommand.Settings.State));
        string[] groups = [.. TaskStateFilter.AttentionSpelling.Split(", ")];

        groups.Should().HaveCount(Enum.GetValues<AttentionBucket>().Length,
            "one spelling per group — the vocabulary and the help text are the same string");

        foreach (string group in groups)
        {
            TaskStateFilter.Validate(group);
            help.Should().Contain(group);
        }
    }

    [Fact]
    public void Every_lifecycle_state_the_status_column_shows_is_named_in_the_help_text_too()
    {
        string help = HelpFor(nameof(TaskListCommand.Settings.State));

        foreach (string state in TaskStateFilter.LifecycleStates)
        {
            TaskStateFilter.Validate(state);
            help.Should().Contain(state, "a word the Status column prints must be a word --state accepts");
        }
    }

    private static string HelpFor(string property) =>
        typeof(TaskListCommand.Settings)
            .GetProperty(property)!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

    [Fact]
    public void A_lifecycle_word_selects_the_column_and_an_attention_word_selects_the_whole_group()
    {
        Guid runId = DomainId.New();
        TaskStatusRow working = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId), StatusFixtures.Run(runId, RunState.Running));
        TaskStatusRow failed = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Failed));

        TaskStateFilter.Matches(working, "working").Should().BeTrue("the Status column says Working");
        TaskStateFilter.Matches(working, "attention-working").Should().BeTrue();
        TaskStateFilter.Matches(working, "active").Should().BeTrue("the pre-rename group spelling still lands");
        TaskStateFilter.Matches(working, "running").Should().BeTrue("the run state is selectable on the phase line's material");
        TaskStateFilter.Matches(working, "needs-you").Should().BeFalse();

        TaskStateFilter.Matches(failed, "needs-you").Should().BeTrue("Failed waits for a human decision");
        TaskStateFilter.Matches(failed, "NEEDS_YOU").Should().BeTrue("separators and case are noise");
        TaskStateFilter.Matches(failed, "Failed").Should().BeTrue();
        TaskStateFilter.Matches(failed, "done").Should().BeFalse();
    }

    /// <summary>
    /// A budget park is a run state, so it is selectable in the run family and nowhere else
    /// (backlog 40). "Show me everything waiting on the budget window" is the same question as
    /// "show me everything under review", answered by the same vocabulary — and the board gains
    /// no group of its own for it, because its groups are the lifecycle's.
    /// </summary>
    [Fact]
    public void A_budget_parked_run_is_selectable_on_the_run_vocabulary_and_counted_with_its_lifecycle()
    {
        Guid runId = DomainId.New();
        TaskStatusRow parked = StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Claimed, runId),
            StatusFixtures.Run(runId, RunState.BudgetParked, sessionProcessId: null));

        TaskStateFilter.Validate("budget-parked");
        TaskStateFilter.Matches(parked, "budget-parked").Should().BeTrue();
        TaskStateFilter.Matches(parked, "BudgetParked").Should().BeTrue("separators and case are noise");
        TaskStateFilter.Matches(parked, "working").Should().BeTrue("the Status column still says Working");
        TaskStateFilter.Matches(parked, "needs-you").Should().BeFalse(
            "the daemon retries this hourly — it is a wait a human can ignore, not an ask");
    }

    /// <summary>
    /// The ruling (Brian, 2026-08-22): where a word names both a lifecycle state and an attention
    /// group, <b>the column wins</b> — the word a reader types selects the rows whose Status
    /// column shows that word, because that is the word they just read off the screen. A parked
    /// closeout is the row that made the old group-first precedence a lie: the Status column calls
    /// it Delivered, the board counts it under needs-you because it has stopped, and
    /// <c>--state delivered</c> used to leave it out of the very list a reader went looking for
    /// it in.
    /// </summary>
    [Fact]
    public void A_delivered_row_that_needs_you_is_still_returned_by_the_delivered_column_word()
    {
        TaskStatusRow parked = Delivered(RunState.CloseoutParked);

        parked.State.Should().Be(LifecycleState.Delivered);
        parked.Group.Should().Be(AttentionBucket.NeedsYou, "a parked closeout has stopped and wants a human");

        TaskStateFilter.Matches(parked, "delivered").Should().BeTrue("the Status column says Delivered");
        TaskStateFilter.Matches(parked, "Delivered").Should().BeTrue();
        TaskStateFilter.Matches(parked, "needs-you").Should().BeTrue("the group is where the board counts it");
        TaskStateFilter.Matches(parked, "attention-delivered")
            .Should().BeFalse("the group spelling selects the group, and this row is not counted in it");
    }

    /// <summary>
    /// The structural half of the same ruling: no word reaches two vocabularies, so nothing --state
    /// accepts can quietly answer with a set the reader did not ask for. Where a word was taken,
    /// the losing vocabulary gets a spelling of its own — run-failed, and the four attention-
    /// group spellings — rather than an entry in --help that cannot be selected.
    /// </summary>
    [Fact]
    public void No_word_state_accepts_belongs_to_two_vocabularies()
    {
        string[] groups = [.. TaskStateFilter.AttentionSpelling.Split(", ")];
        string[] all = [.. TaskStateFilter.LifecycleStates, .. groups, .. TaskStateFilter.RunStates];

        string[] normalized = [.. all.Select(word => new string([.. word.Where(char.IsLetterOrDigit)]).ToLowerInvariant())];

        normalized.Should().OnlyHaveUniqueItems(
            "hyphens and case are noise, so two vocabularies sharing a word share a filter");
    }

    [Fact]
    public void The_delivered_group_covers_every_pushed_row_and_leaves_its_run_states_selectable()
    {
        // The group must not be spelled like a run state it contains: normalization erases
        // hyphens and case, so awaiting-review would have become AwaitingReview and swallowed
        // the one state an operator asks for when they want the quiet pull requests
        // (origin incident, 2026-08-20).
        TaskStatusRow awaitingReview = Delivered(RunState.AwaitingReview);
        TaskStatusRow checksFailing = Delivered(RunState.ChecksFailing);
        TaskStatusRow reviewPending = Delivered(RunState.ReviewPending);

        foreach (TaskStatusRow row in new[] { awaitingReview, checksFailing, reviewPending })
        {
            TaskStateFilter.Matches(row, "attention-delivered")
                .Should().BeTrue($"{row.RunState.Value} is an open pull request");
            TaskStateFilter.Matches(row, "in-review").Should().BeTrue("the pre-rename spelling still lands");
        }

        TaskStateFilter.Matches(awaitingReview, "AwaitingReview").Should().BeTrue();
        TaskStateFilter.Matches(checksFailing, "AwaitingReview").Should().BeFalse("red CI is not a quiet PR");
        TaskStateFilter.Matches(reviewPending, "awaitingreview").Should().BeFalse("nor is unanswered review feedback");
    }

    [Fact]
    public void A_run_state_selects_on_the_phase_lines_material_and_nothing_wider()
    {
        // Every advertised run state reaches its own rows and no others — including the one whose
        // own word is taken. Failed names a lifecycle state too and the lifecycle vocabulary is
        // matched first, so the run state is advertised as run-failed rather than under a word
        // that would quietly return the tasks that failed instead of the pull requests closed
        // without merging.
        TaskStateFilter.RunStates.Should().Contain("run-failed").And.NotContain(RunState.Failed.Value);

        foreach (string state in TaskStateFilter.RunStates)
        {
            string[] selected = [.. TaskStateFilter.RunStates.Where(other =>
                TaskStateFilter.Matches(Delivered(RunStateFor(other)), state))];

            selected.Should().Equal([state], $"--state {state} promises just that one run state");
        }

        TaskStateFilter.Matches(Delivered(RunState.Failed), "Failed")
            .Should().BeFalse("the bare word is the lifecycle state, and this row's task is Delivered");
    }

    /// <summary>The run state a <c>--state</c> run-vocabulary spelling selects, for building the row it should match.</summary>
    private static RunState RunStateFor(string spelling) =>
        spelling == "run-failed"
            ? RunState.Failed
            : spelling;

    [Fact]
    public void Abandoned_still_selects_the_rows_the_column_now_calls_archived()
    {
        TaskStatusRow archived = StatusFixtures.Compose(StatusFixtures.Task(TaskState.Abandoned));

        TaskStateFilter.Validate("abandoned");
        TaskStateFilter.Matches(archived, "abandoned").Should().BeTrue();
        TaskStateFilter.Matches(archived, "Archived").Should().BeTrue();
    }

    [Fact]
    public void An_unknown_state_fails_with_the_vocabulary_quoted_rather_than_returning_nothing()
    {
        // Zero rows would read as "you have no such work"; the agent needs to know it
        // mistyped the filter, and what the accepted words are.
        Action filter = () => TaskStateFilter.Validate("in-progress");

        filter.Should().Throw<DomainValidationException>()
            .WithMessage("*not a state h9k tracks*Delivered*needs-you*Running*");
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

    /// <summary>A pushed task whose run is in the given state: the Delivered family, one member at a time.</summary>
    private static TaskStatusRow Delivered(RunState state)
    {
        Guid runId = DomainId.New();
        return StatusFixtures.Compose(
            StatusFixtures.Task(TaskState.Done, runId, "https://github.com/x/y/pull/7"),
            StatusFixtures.Run(runId, state, sessionProcessId: null, pullRequestNumber: 7));
    }
}
