using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The resolve walk (Decisions Log #27) through both task read models: failed, then
/// resolved to Done by human attestation — the stream reads added → claimed → failed →
/// resolved in order, the failure reason survives beside the resolution (resolve appends,
/// it never rewrites or hides the failure), and the task shows Done with its PR.
/// </summary>
public sealed class TaskResolvedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private const string FailureReason = "Push rejected: branch was rebased.";
    private const string ResolveReason = "Work merged as PR #7; only the push step failed.";
    private const string PullRequestUrl = "https://github.com/x/y/pull/7";

    [Fact]
    public void Task_details_walks_failed_resolved_done_and_keeps_the_failure_visible()
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

        projection.Apply(new FakeEvent<TaskResolved>(new TaskResolved(
            id, ResolveReason, PullRequestUrl, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Done);
        view.ResolvedReason.Should().Be(ResolveReason, "h9k task show renders the attestation");
        view.FailureReason.Should().Be(FailureReason, "resolve does not erase why the task failed");
        view.PullRequestUrl.Should().Be(PullRequestUrl, "the resolution records where the work landed");
        view.FinishedAt.Should().Be(Now.AddHours(2), "the resolution is when the story actually ended");
    }

    [Fact]
    public void Task_details_resolve_without_a_pull_request_keeps_the_observed_one()
    {
        TaskDetailsProjection projection = new();
        Guid id = DomainId.New();
        Guid runId = DomainId.New();

        TaskDetails view = projection.Create(new FakeEvent<TaskAdded>(new TaskAdded(
            id, DomainId.New(), "Do the thing", ["it is done"], TaskType.Feature,
            null, null, null, Now, DomainId.New())));
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 1, runId, Now)), view);
        projection.Apply(new FakeEvent<TaskCompleted>(new TaskCompleted(
            id, runId, PullRequestUrl, Now.AddHours(1))), view);
        projection.Apply(new FakeEvent<TaskReopened>(new TaskReopened(
            id, runId, "task/abc", "CI checks failing.", Now.AddHours(2), DomainId.New(),
            FollowUpKind.FailingChecks, Automatic: true)), view);
        Guid followUpRunId = DomainId.New();
        projection.Apply(new FakeEvent<TaskClaimed>(new TaskClaimed(
            id, DomainId.New(), DomainId.New(), 2, followUpRunId, Now.AddHours(3))), view);
        projection.Apply(new FakeEvent<TaskFailed>(new TaskFailed(
            id, followUpRunId, FailureReason, Now.AddHours(4))), view);

        projection.Apply(new FakeEvent<TaskResolved>(new TaskResolved(
            id, "The follow-up's work is on the PR already.", null, Now.AddHours(5), DomainId.New())), view);

        view.State.Should().Be(TaskState.Done);
        view.PullRequestUrl.Should().Be(PullRequestUrl, "resolve never erases what was observed");
        view.FollowUpBranch.Should().BeNull("resolution consumes the pending follow-up marker");
    }

    [Fact]
    public void Task_details_clears_the_resolved_reason_on_reopen()
    {
        // Adversarial review, backlog 51 cycle 3: ProjectHomeRenderEngine.IsArchived treats a
        // non-blank ResolvedReason as permanent closure for a Done task (Decisions Log #27's
        // attestation exit means no run of that completion will ever carry RunCompleted). A
        // resolve-then-reopen sequence starts a genuinely new run that CAN still complete
        // normally, so the stale reason has to go, or the follow-up re-archives the moment it
        // completes even though its own pull request is still open.
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
        projection.Apply(new FakeEvent<TaskResolved>(new TaskResolved(
            id, ResolveReason, PullRequestUrl, Now.AddHours(2), DomainId.New())), view);

        view.ResolvedReason.Should().Be(ResolveReason);

        projection.Apply(new FakeEvent<TaskReopened>(new TaskReopened(
            id, failedRunId, "task/abc", "One more look before merge.", Now.AddHours(3), DomainId.New())), view);

        view.State.Should().Be(TaskState.Queued);
        view.ResolvedReason.Should().BeNull(
            "the follow-up run this starts can still reach RunCompleted on its own; the old " +
            "attestation must not keep counting as this run's closure too");
    }

    [Fact]
    public void Task_list_item_shows_done_with_its_pull_request_after_resolve()
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

        projection.Apply(new FakeEvent<TaskResolved>(new TaskResolved(
            id, ResolveReason, PullRequestUrl, Now.AddHours(2), DomainId.New())), view);

        view.State.Should().Be(TaskState.Done, "h9k status shows the resolved task as Done");
        view.PullRequestUrl.Should().Be(PullRequestUrl);
    }
}
