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
/// to queued, claimed again, completed — the failure reason survives the whole way (retry
/// appends, it never erases), and completion consumes the retry marker.
/// </summary>
public sealed class TaskRetriedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const string Branch = "task/abc12345-do-the-thing";
    private const string FailureReason = "Push rejected: branch was rebased.";
    private const string RetryReason = "Daemon push bug fixed; the completed work is intact.";

    [Fact]
    public void Task_details_walks_failed_retried_queued_and_keeps_the_failure_visible()
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
            id, failedRunId, Branch, RetryReason, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued);
        view.RetryBranch.Should().Be(Branch, "the launcher resumes the failed run's branch when it survives");
        view.RetryReason.Should().Be(RetryReason, "h9k task show renders it");
        view.RetryReasonIsHandback.Should().BeFalse("a real failure earned this reason, not a handback");
        view.FailureReason.Should().Be(FailureReason, "retry does not erase why the task failed");
        view.ClaimedByNodeId.Should().BeNull();
        view.CurrentRunId.Should().BeNull();
        view.FinishedAt.Should().BeNull("a retried task is no longer finished");

        Guid retryRunId = DomainId.New();
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 2, retryRunId, Now.AddHours(3))), view);
        projection.Apply(new FakeEvent<TaskCompleted>(new TaskCompleted(
            id, retryRunId, "https://github.com/x/y/pull/9", Now.AddHours(4))), view);

        view.State.Should().Be(TaskState.Done);
        view.RetryBranch.Should().BeNull("completion consumes the retry marker");
        view.RunIds.Should().Equal(failedRunId, retryRunId);
    }

    [Fact]
    public void Task_list_item_returns_to_queued_on_retry()
    {
        TaskListItemProjection projection = new();
        Guid id = DomainId.New();
        Guid failedRunId = DomainId.New();

        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, failedRunId, Now)), view);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, failedRunId, FailureReason, Now.AddHours(1))), view);
        view.State.Should().Be(TaskState.Failed);

        projection.Apply(new FakeEvent<TaskRetried>(new TaskRetried(
            id, failedRunId, Branch, RetryReason, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued, "the daemon's queue query picks the task up again");
        view.ClaimedByNodeId.Should().BeNull();
        view.CurrentRunId.Should().BeNull();
    }

    [Fact]
    public void A_handback_reads_as_a_handback_never_as_a_retry()
    {
        // TaskHandedBack shares TaskRetried's RetryReason field (both resume the same branch,
        // and WorkPromptBuilder wants the identical causeless "why this resumes" text either
        // way), but h9k task show's fixed "Retried" row label must not attribute a never-failed
        // handback to a retry that never happened (conformance review, cycle 4).
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();
        const string handbackReason = "Stepping away; the migration script is drafted but untested.";

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskHandedBack>(new TaskHandedBack(
            id, runId, Branch, handbackReason, Now.AddHours(1), DomainId.New())), view);

        view.RetryReason.Should().Be(handbackReason);
        view.RetryReasonIsHandback.Should().BeTrue("this task never failed; nothing was retried");

        projection.Apply(new FakeEvent<TaskRetried>(new TaskRetried(
            id, runId, Branch, RetryReason, Now.AddHours(2), DomainId.New())), view);

        view.RetryReasonIsHandback.Should().BeFalse("a later real retry replaces the handback marker");
    }
}
