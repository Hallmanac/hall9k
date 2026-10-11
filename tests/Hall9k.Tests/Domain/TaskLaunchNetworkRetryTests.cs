using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The decision half of the launch network backoff: how consecutive failures are counted, how long
/// each one waits, and that <see cref="TaskDecider.Claim"/> honours the same predicate the
/// dispatcher's queue read does. Pure aggregates, no database.
/// </summary>
public sealed class TaskLaunchNetworkRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid NodeId = Guid.NewGuid();

    private static TaskAggregate ClaimedTask()
    {
        (TaskAggregate task, _) = TaskSeed.Start(
            TaskDecider.Add(
                DomainId.New(), DomainId.New(), "Check out a pull request", ["done"], TaskType.Chore,
                null, null, null, Now, OwnerId),
            OwnerId, Now);
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now));
        return task;
    }

    private static void FailTheLaunchOverTheNetwork(TaskAggregate task, DateTimeOffset at)
    {
        int attempt = TaskDecider.NextLaunchNetworkAttempt(task);
        task.Apply(TaskDecider.RequeueAfterLaunchNetworkError(
            task, attempt, bound: 3, TaskDecider.LaunchNetworkBackoff(attempt, TimeSpan.FromMinutes(2)),
            "Could not resolve host: github.com", at));
    }

    [Fact]
    public void The_wait_starts_at_the_initial_backoff_and_doubles_with_each_consecutive_failure()
    {
        TimeSpan initial = TimeSpan.FromMinutes(2);

        TaskDecider.LaunchNetworkBackoff(1, initial).Should().Be(TimeSpan.FromMinutes(2));
        TaskDecider.LaunchNetworkBackoff(2, initial).Should().Be(TimeSpan.FromMinutes(4));
        TaskDecider.LaunchNetworkBackoff(3, initial).Should().Be(TimeSpan.FromMinutes(8));
    }

    [Fact]
    public void A_network_requeue_records_the_attempt_the_bound_the_earliest_claim_and_gits_error()
    {
        TaskAggregate task = ClaimedTask();

        FailTheLaunchOverTheNetwork(task, Now);

        task.State.Should().Be(TaskState.Queued);
        task.LaunchNetworkRetryAttempt.Should().Be(1);
        task.LaunchNetworkRetryNotBefore.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void Claim_refuses_a_task_until_its_backoff_has_passed_and_the_claim_ends_the_wait()
    {
        TaskAggregate task = ClaimedTask();
        FailTheLaunchOverTheNetwork(task, Now);

        Action early = () => TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(1));
        early.Should().Throw<DomainConflictException>().WithMessage("*waiting out a launch network backoff*");

        TaskClaimed claimed = TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(2));
        task.Apply(claimed);

        task.LaunchNetworkRetryNotBefore.Should().BeNull();
        task.LaunchNetworkRetryAttempt.Should().Be(1, "the launch this claim starts may fail the same way and counts on from it");
    }

    [Fact]
    public void The_predicate_the_queue_read_and_claim_share_admits_a_missing_or_elapsed_backoff_only()
    {
        TaskDecider.IsPastLaunchNetworkBackoff(null, Now).Should().BeTrue();
        TaskDecider.IsPastLaunchNetworkBackoff(Now.AddSeconds(1), Now).Should().BeFalse();
        TaskDecider.IsPastLaunchNetworkBackoff(Now, Now).Should().BeTrue();
    }

    [Fact]
    public void An_unbroken_run_of_network_failures_counts_up()
    {
        TaskAggregate task = ClaimedTask();
        FailTheLaunchOverTheNetwork(task, Now);
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(2)));

        TaskDecider.NextLaunchNetworkAttempt(task).Should().Be(2);
    }

    [Fact]
    public void A_requeue_for_another_reason_between_two_failures_restarts_the_count()
    {
        TaskAggregate task = ClaimedTask();
        FailTheLaunchOverTheNetwork(task, Now);
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(2)));
        task.Apply(TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now.AddMinutes(3)));
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(4)));

        TaskDecider.NextLaunchNetworkAttempt(task).Should().Be(1);
    }

    [Fact]
    public void A_reopen_between_two_failures_restarts_the_count()
    {
        TaskAggregate task = ClaimedTask();
        FailTheLaunchOverTheNetwork(task, Now);
        Guid runId = DomainId.New();
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, runId, Now.AddMinutes(2)));
        task.Apply(TaskDecider.Complete(task, runId, "https://github.com/x/y/pull/7", Now.AddMinutes(3)));
        task.Apply(TaskDecider.Reopen(
            task, runId, "task/abc", "Unresolved review comments", FollowUpKind.ReviewFeedback,
            automatic: true, Now.AddMinutes(4), OwnerId));
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(5)));

        TaskDecider.NextLaunchNetworkAttempt(task).Should().Be(1);
    }

    [Fact]
    public void A_retry_between_two_failures_restarts_the_count()
    {
        TaskAggregate task = ClaimedTask();
        FailTheLaunchOverTheNetwork(task, Now);
        Guid runId = DomainId.New();
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, runId, Now.AddMinutes(2)));
        task.Apply(TaskDecider.Fail(task, runId, "Launch failed: something else", Now.AddMinutes(3)));
        task.Apply(TaskDecider.Retry(task, runId, null, "try again", Now.AddMinutes(4), OwnerId));
        task.Apply(TaskDecider.Claim(task, NodeId, OwnerId, DomainId.New(), Now.AddMinutes(5)));

        TaskDecider.NextLaunchNetworkAttempt(task).Should().Be(1);
    }
}
