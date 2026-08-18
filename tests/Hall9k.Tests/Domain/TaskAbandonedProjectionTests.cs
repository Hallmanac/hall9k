using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The walk-away exit from Failed (Decisions Log #27) through the details read model:
/// the abandon note lands in its own field beside the observed run failure — abandoning
/// never overwrites why the run failed (never guess at, or erase, observed facts).
/// </summary>
public sealed class TaskAbandonedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private const string FailureReason = "Push rejected: branch was rebased.";
    private const string AbandonReason = "Not worth another run.";

    [Fact]
    public void Task_details_abandon_from_failed_keeps_the_failure_visible_beside_the_walk_away_note()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid failedRunId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, failedRunId, Now)), view);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, failedRunId, FailureReason, Now.AddHours(1))), view);

        projection.Apply(new FakeEvent<TaskAbandoned>(new TaskAbandoned(
            id, AbandonReason, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Abandoned);
        view.AbandonedReason.Should().Be(AbandonReason, "h9k task show renders the walk-away note");
        view.FailureReason.Should().Be(FailureReason, "abandoning does not erase why the run failed");
        view.FinishedAt.Should().Be(Now.AddHours(2), "walking away is when the story actually ended");
    }

    [Fact]
    public void Task_details_abandon_without_a_reason_leaves_the_failure_untouched()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid failedRunId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, failedRunId, Now)), view);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, failedRunId, FailureReason, Now.AddHours(1))), view);

        projection.Apply(new FakeEvent<TaskAbandoned>(new TaskAbandoned(
            id, null, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Abandoned);
        view.AbandonedReason.Should().BeNull("no reason was given, and the unobserved stays unknown");
        view.FailureReason.Should().Be(FailureReason, "abandoning does not erase why the run failed");
    }

    [Fact]
    public void Task_details_abandon_consumes_the_pending_work_markers()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid failedRunId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, failedRunId, Now)), view);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, failedRunId, FailureReason, Now.AddHours(1))), view);
        projection.Apply(new FakeEvent<TaskRetried>(new TaskRetried(
            id, failedRunId, "task/abc-branch", "One more attempt.", Now.AddHours(2), DomainId.New())), view);

        projection.Apply(new FakeEvent<TaskAbandoned>(new TaskAbandoned(
            id, AbandonReason, Now.AddHours(3), DomainId.New())), view);

        view.State.Should().Be(TaskState.Abandoned);
        view.RetryBranch.Should().BeNull("an abandoned task has no retry pending");
        view.RetryReason.Should().Be("One more attempt.", "the retry's why is history, not a pending marker");
        view.FollowUpBranch.Should().BeNull("an abandoned task has no follow-up run pending");
        view.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        view.FollowUpReason.Should().BeNull();
    }
}
