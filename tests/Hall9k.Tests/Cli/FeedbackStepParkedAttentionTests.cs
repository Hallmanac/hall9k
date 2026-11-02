using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Feedback;
using Hall9k.Domain.Features.Tasks.Projections;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// A task the feedback step parked waits for a person in either of the two states a park leaves it in: a Draft when the plan
/// that was refining it parked, and Published when the plan published it and queueing it again was refused. Both name the step's
/// own recorded reason in <c>h9k status</c>.
/// </summary>
public sealed class FeedbackStepParkedAttentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static TaskAttention Compose(TaskListItem task, LifecycleState lifecycle) =>
        AttentionComposer.Compose(task, run: null, lifecycle, TaskPhase.None, stalled: false, Now);

    [Fact]
    public void A_parked_draft_names_the_reason_the_plan_gave()
    {
        TaskListItem task = new()
        {
            Id = Guid.NewGuid(),
            State = TaskState.Draft,
            FeedbackStepOutcome = FeedbackStepOutcome.Parked,
            FeedbackStepOutcomeReason = "a challenge said do not publish",
        };

        TaskAttention attention = Compose(task, LifecycleState.Draft);

        attention.Level.Should().Be(AttentionLevel.NeedsYou);
        attention.Cause.Should().Contain("its feedback step parked it").And.Contain("a challenge said do not publish");
        attention.Lever.Should().Be($"h9k task show {TaskListCommand.ShortId(task.Id)}");
    }

    [Fact]
    public void A_parked_published_task_names_the_reason_queueing_it_again_was_refused()
    {
        TaskListItem task = new()
        {
            Id = Guid.NewGuid(),
            State = TaskState.Published,
            FeedbackStepOutcome = FeedbackStepOutcome.Parked,
            FeedbackStepOutcomeReason = "queueing it again was refused: its project is archived",
        };

        TaskAttention attention = Compose(task, LifecycleState.Published);

        attention.Level.Should().Be(AttentionLevel.NeedsYou);
        attention.Cause.Should().Contain("its feedback step parked it").And.Contain("its project is archived");
        attention.Lever.Should().Be($"h9k task queue {TaskListCommand.ShortId(task.Id)}");
    }
}
