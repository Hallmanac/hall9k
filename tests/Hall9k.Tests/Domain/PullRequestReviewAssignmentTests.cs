using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The auto-pr-review feature's own provenance events (idea e5e98a33, PLAN.md §16 decision #34's
/// amendment): observing a GitHub reviewer assignment and its withdrawal never carries a
/// TaskDecider method of its own — these are plain observations, not a decision the aggregate
/// gates — but <see cref="TaskAggregate.Apply(PullRequestReviewAssignmentObserved)"/> and its
/// recall sibling still have to behave, and <see cref="AutoPrReviewAssigneeLogin"/>'s set/clear
/// discipline is exactly what a repeated poll relies on to never re-fire on a withdrawal it
/// already recorded.
/// </summary>
public sealed class PullRequestReviewAssignmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = DomainId.New();

    [Fact]
    public void Observed_records_the_assignee_login()
    {
        TaskAggregate task = QueuedPrReviewTask();

        task.Apply(new PullRequestReviewAssignmentObserved(
            task.Id, "https://github.com/acme/widgets/pull/42", "brian", "ryan", Now));

        task.AutoPrReviewAssigneeLogin.Should().Be("brian");
    }

    [Fact]
    public void Recalled_clears_the_assignee_login_so_a_later_poll_never_re_fires()
    {
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(new PullRequestReviewAssignmentObserved(
            task.Id, "https://github.com/acme/widgets/pull/42", "brian", "ryan", Now));

        task.Apply(new PullRequestReviewAssignmentRecalled(
            task.Id, "https://github.com/acme/widgets/pull/42", "ryan", Now, Concluded: false));

        task.AutoPrReviewAssigneeLogin.Should().BeNull();
    }

    [Fact]
    public void Recalled_never_changes_state_on_its_own_the_caller_decides_that()
    {
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(new PullRequestReviewAssignmentObserved(
            task.Id, "https://github.com/acme/widgets/pull/42", "brian", "ryan", Now));

        task.Apply(new PullRequestReviewAssignmentRecalled(
            task.Id, "https://github.com/acme/widgets/pull/42", "ryan", Now, Concluded: true));

        task.State.Should().Be(TaskState.Queued, "the event only observes; a following TaskAbandoned is what concludes it");
    }

    [Fact]
    public void A_recall_observed_before_dispatch_can_still_abandon_the_task_honestly()
    {
        // Exactly the sequence AutoPrReviewEngine.ConcludeOneAsync builds: recall the assignment,
        // then abandon with the recall as the reason — the go signal recalled by the same
        // authority that gave it (PLAN.md §16 decision #34's amendment).
        TaskAggregate task = QueuedPrReviewTask();
        task.Apply(new PullRequestReviewAssignmentObserved(
            task.Id, "https://github.com/acme/widgets/pull/42", "brian", "ryan", Now));

        PullRequestReviewAssignmentRecalled recalled = new(
            task.Id, "https://github.com/acme/widgets/pull/42", "ryan", Now, Concluded: true);
        task.Apply(recalled);
        TaskAbandoned abandoned = TaskDecider.Abandon(
            task, "The GitHub reviewer assignment that created this task was recalled by ryan before the run ever dispatched.",
            Now, Owner);
        task.Apply(abandoned);

        task.State.Should().Be(TaskState.Abandoned);
    }

    /// <summary>
    /// The same in-memory Add → Apply → Publish → Apply → Assign → Apply chain
    /// AutoPrReviewEngine.CreateOneAsync builds before its single atomic append, proven legal here
    /// independent of any database.
    /// </summary>
    private static TaskAggregate QueuedPrReviewTask()
    {
        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Review pull request acme/widgets#42",
            ["The findings report is walked with the owner (walk-pr-review-findings) and every finding is directed."],
            TaskType.PrReview, agentContext: "Imported from github-pr:acme/widgets#42.", constraints: null,
            externalReference: new ExternalReference(Hall9k.Domain.Shared.ValueObjects.WorkItemProvider.GitHubPullRequest, "acme/widgets#42"),
            addedAt: Now, addedByOwnerId: Owner));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, Owner));
        task.Apply(TaskDecider.Assign(task, Owner, [], Now, Owner));
        return task;
    }
}
