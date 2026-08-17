using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The follow-up walk (Decisions Log #20) through both task read models: completed with a
/// PR, reopened onto the PR branch, completed again — the PR URL survives, the follow-up
/// marker is consumed.
/// </summary>
public sealed class TaskReopenedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/7";
    private const string Branch = "task/abc12345-do-the-thing";

    [Fact]
    public void Task_details_walks_done_reopened_queued_and_back_to_done()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid firstRunId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));

        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, firstRunId, Now)), view);
        projection.Apply(new FakeEvent<TaskCompleted>(new TaskCompleted(
            id, firstRunId, PullRequestUrl, Now.AddHours(1))), view);

        projection.Apply(new FakeEvent<TaskReopened>(new TaskReopened(
            id, firstRunId, Branch, "Unresolved review comments", Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued);
        view.FollowUpBranch.Should().Be(Branch);
        view.PullRequestUrl.Should().Be(PullRequestUrl, "the launcher needs the PR URL for the follow-up prompt");
        view.FinishedAt.Should().BeNull("a reopened task is no longer finished");

        Guid followUpRunId = DomainId.New();
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 2, followUpRunId, Now.AddHours(3))), view);
        projection.Apply(new FakeEvent<TaskCompleted>(new TaskCompleted(
            id, followUpRunId, PullRequestUrl, Now.AddHours(4))), view);

        view.State.Should().Be(TaskState.Done);
        view.FollowUpBranch.Should().BeNull("completion consumes the follow-up marker");
        view.RunIds.Should().Equal(firstRunId, followUpRunId);
    }

    [Fact]
    public void Task_list_item_returns_to_queued_on_reopen()
    {
        TaskListItemProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();

        TaskListItem view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, runId, Now)), view);
        projection.Apply(new FakeEvent<TaskCompleted>(new TaskCompleted(id, runId, PullRequestUrl, Now)), view);

        projection.Apply(new FakeEvent<TaskReopened>(new TaskReopened(
            id, runId, Branch, null, Now, DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued, "the dispatch loop claims reopened tasks like any queued work");
        view.PullRequestUrl.Should().Be(PullRequestUrl);
    }
}
