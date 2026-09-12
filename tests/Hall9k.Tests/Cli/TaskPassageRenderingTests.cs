using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Queries;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>h9k task show</c>'s Passage section actually renders from a <see cref="TaskPassage"/>
/// (task: h9k task show tells a task's passage in time). The composition is asserted rather than
/// the console, the same split <see cref="TaskShowCommand.ComposeChangesRequestedReviews"/> already
/// uses — <see cref="TaskShowCommand.ComposePassageLines"/> had no test of its own before this file
/// (independent pre-PR review, cycle 1, adversarial lens), which is exactly the gap that let the
/// mid-flight-review-cycle finding below escape a first look.
/// </summary>
public sealed class TaskPassageRenderingTests
{
    private static readonly TaskPassage Empty = new(
        PassagePhase.NotApplicable, PassagePhase.NotApplicable, TimeSpan.Zero,
        new ReviewCyclePassage(0, TimeSpan.Zero, false, 0, TimeSpan.Zero, false),
        PassagePhase.NotApplicable, PassagePhase.NotApplicable, [], PassagePhase.NotApplicable, [], 0);

    [Fact]
    public void A_passage_with_nothing_applicable_renders_no_lines() =>
        TaskShowCommand.ComposePassageLines(Empty).Should().BeEmpty();

    [Fact]
    public void Queued_still_open_renders_the_so_far_suffix()
    {
        TaskPassage passage = Empty with { Queued = PassagePhase.Open(TimeSpan.FromMinutes(20)) };

        TaskShowCommand.ComposePassageLines(passage)[0].Should().Contain("queued 20m00s so far");
    }

    [Fact]
    public void A_review_cycle_still_running_with_zero_completed_cycles_still_renders_a_line()
    {
        // The exact shape TaskPassageQuery.Compute reports for a task's very first pre-PR review
        // cycle, still in flight: no cycle has completed yet, so Cycles is 0, but StillOpen is
        // true. Gating the row on Cycles > 0 alone silently dropped this line entirely
        // (independent pre-PR review, cycle 1, both lenses).
        TaskPassage passage = Empty with
        {
            Review = new ReviewCyclePassage(0, TimeSpan.FromMinutes(20), true, 0, TimeSpan.Zero, false),
        };

        List<string> lines = TaskShowCommand.ComposePassageLines(passage);

        lines.Should().ContainSingle(line => line.Contains("review 0 cycles") && line.Contains("so far"));
    }

    [Fact]
    public void A_fix_session_still_running_with_zero_completed_fix_sessions_still_renders_a_line()
    {
        TaskPassage passage = Empty with
        {
            Review = new ReviewCyclePassage(1, TimeSpan.FromMinutes(30), false, 0, TimeSpan.FromMinutes(5), true),
        };

        List<string> lines = TaskShowCommand.ComposePassageLines(passage);

        lines.Should().ContainSingle(line => line.Contains("fix session") && line.Contains("so far"));
    }

    [Fact]
    public void An_unknown_phase_renders_the_word_unknown_rather_than_a_zero()
    {
        TaskPassage passage = Empty with { MergeWait = PassagePhase.Unknown() };

        TaskShowCommand.ComposePassageLines(passage).Should()
            .ContainSingle(line => line.Contains("merge wait unknown"));
    }

    [Fact]
    public void A_human_wait_row_renders_its_label_and_elapsed()
    {
        TaskPassage passage = Empty with
        {
            HumanWaits = [new HumanWaitPassage(HumanWaitKind.Question, PassagePhase.Closed(TimeSpan.FromMinutes(40)))],
        };

        TaskShowCommand.ComposePassageLines(passage).Should()
            .ContainSingle(line => line.Contains("waited on your answer") && line.Contains("40m00s"));
    }

    [Fact]
    public void No_laps_ever_recorded_falls_back_to_laps_zero()
    {
        TaskPassage passage = Empty with { Sessions = 1 };

        string summary = TaskShowCommand.ComposePassageLines(passage).Should().ContainSingle().Subject;

        summary.Should().Contain("laps 0");
        summary.Should().Contain("sessions 1");
    }

    [Fact]
    public void Laps_are_broken_down_by_kind_with_the_total_up_front()
    {
        TaskPassage passage = Empty with
        {
            Laps = [new LapKindCount(FollowUpKind.ReviewRequestedChanges, 2), new LapKindCount(FollowUpKind.Rebase, 1)],
        };

        string summary = TaskShowCommand.ComposePassageLines(passage).Should().ContainSingle().Subject;

        summary.Should().Contain("laps 3 (changes requested 2, rebase 1)");
    }
}
