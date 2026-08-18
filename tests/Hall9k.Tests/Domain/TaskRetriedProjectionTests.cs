using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The retry walk (Decisions Log #25) through both task read models: failed, retried back
/// to Queued with the retry reason on record, and the failure reason kept — retry adds to
/// the story, it never erases why the task failed.
/// </summary>
public sealed class TaskRetriedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const string Branch = "task/abc12345-do-the-thing";
    private const string FailureReason = "Push failed: remote rejected the rebased branch";
    private const string RetryReason = "Daemon push bug fixed; the finished work survives in the worktree";

    [Fact]
    public void Task_details_walks_failed_retried_queued_and_keeps_the_failure_reason()
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
            id, RetryReason, Branch, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued);
        view.RetryReason.Should().Be(RetryReason, "h9k task show surfaces why the human retried");
        view.FailureReason.Should().Be(FailureReason, "retry never erases why the task failed");
        view.FollowUpBranch.Should().Be(Branch, "the launcher resumes surviving artifacts");
        view.FollowUpKind.Should().Be(FollowUpKind.Retry);
        view.ClaimedByNodeId.Should().BeNull();
        view.CurrentRunId.Should().BeNull();
        view.FinishedAt.Should().BeNull("a retried task is no longer finished");

        Guid retryRunId = DomainId.New();
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 2, retryRunId, Now.AddHours(3))), view);
        projection.Apply(new FakeEvent<TaskCompleted>(new TaskCompleted(
            id, retryRunId, "https://github.com/x/y/pull/9", Now.AddHours(4))), view);

        view.State.Should().Be(TaskState.Done);
        view.FollowUpBranch.Should().BeNull("completion consumes the resume marker");
        view.FollowUpKind.Should().Be(FollowUpKind.Unknown);
        view.RunIds.Should().Equal(failedRunId, retryRunId);
    }

    [Fact]
    public void Task_list_item_returns_to_queued_on_retry()
    {
        TaskListItemProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();

        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, runId, Now)), view);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(id, runId, FailureReason, Now)), view);
        view.State.Should().Be(TaskState.Failed);

        projection.Apply(new FakeEvent<TaskRetried>(new TaskRetried(
            id, RetryReason, Branch, Now, DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued, "the dispatch loop claims retried tasks like any queued work");
        view.ClaimedByNodeId.Should().BeNull();
        view.CurrentRunId.Should().BeNull();
    }
}
