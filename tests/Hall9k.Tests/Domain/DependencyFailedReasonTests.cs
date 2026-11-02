using FluentAssertions;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The sentence recorded when a task's blocker can never close out. It lands on the dependent's own stream, which
/// replicates through that task's project ledger, so a blocker in another project is named by short id and state alone.
/// </summary>
public sealed class DependencyFailedReasonTests
{
    private const string Objective = "Rotate the payment gateway credentials";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static TaskAggregate DependentIn(Guid projectId)
    {
        TaskAggregate task = new();
        task.Apply(new TaskAdded(
            DomainId.New(), projectId, "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, DomainId.New()));
        return task;
    }

    private static TaskDependency Blocker(Guid projectId, TaskState state, RunState? run = null) =>
        new(DomainId.New(), Objective, state, IsClosedOut: false, run, PullRequestUrl: null, TaskType.Chore, [], ProjectId: projectId);

    private static string ShortId(TaskDependency dependency) => dependency.Id.ToString("N")[^8..];

    [Theory]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public void A_dead_blocker_in_another_project_is_named_by_short_id_and_state_without_its_objective(string state)
    {
        TaskDependency blocker = Blocker(DomainId.New(), state);
        TaskAggregate dependent = DependentIn(DomainId.New());

        string reason = TaskDependencyResolver.DeathReason(dependent, blocker);

        reason.Should().Contain($"Dependency {ShortId(blocker)} ({state})")
            .And.NotContain(Objective)
            .And.NotContain("payment gateway");
    }

    [Fact]
    public void A_done_blocker_whose_run_ended_without_a_merge_in_another_project_is_named_without_its_objective()
    {
        TaskDependency blocker = Blocker(DomainId.New(), TaskState.Done, RunState.Failed);
        TaskAggregate dependent = DependentIn(DomainId.New());

        string reason = TaskDependencyResolver.DeathReason(dependent, blocker);

        reason.Should().Contain($"Dependency {ShortId(blocker)} (Done) reads Done").And.NotContain(Objective);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Abandoned")]
    public void A_dead_blocker_in_the_dependents_own_project_is_described_as_it_always_was(string state)
    {
        Guid project = DomainId.New();
        TaskDependency blocker = Blocker(project, state);
        TaskAggregate dependent = DependentIn(project);

        string reason = TaskDependencyResolver.DeathReason(dependent, blocker);

        reason.Should().Contain($"Dependency {blocker.Describe()}").And.Contain($"\"{Objective}\"");
        reason.Should().Be($"{blocker.DescribeDeath()} (h9k task dequeue {dependent.Id}, then h9k task draft {dependent.Id}).");
    }

    [Fact]
    public void A_blocker_with_no_recorded_project_is_not_treated_as_another_project()
    {
        TaskDependency blocker = Blocker(Guid.Empty, TaskState.Failed);
        TaskAggregate dependent = DependentIn(DomainId.New());

        TaskDependencyResolver.DeathReason(dependent, blocker).Should().Contain($"\"{Objective}\"");
    }

    [Fact]
    public void The_recovery_of_a_blocker_in_another_project_does_not_quote_its_objective_either()
    {
        Guid project = DomainId.New();
        TaskDependency elsewhere = Blocker(DomainId.New(), TaskState.Queued);
        TaskDependency here = Blocker(project, TaskState.Queued);
        TaskAggregate dependent = DependentIn(project);

        TaskDependencyResolver.RecoveryReason(dependent, elsewhere).Should().NotContain(Objective).And.Contain(ShortId(elsewhere));
        TaskDependencyResolver.RecoveryReason(dependent, here).Should().Contain($"\"{Objective}\"");
    }

    [Fact]
    public void A_death_recorded_in_the_older_wording_that_quoted_the_objective_is_the_same_observation()
    {
        TaskDependency blocker = Blocker(DomainId.New(), TaskState.Failed);
        TaskAggregate dependent = DependentIn(DomainId.New());
        string older = $"{blocker.DescribeDeath()} (h9k task dequeue {dependent.Id}, then h9k task draft {dependent.Id}).";

        older.Should().NotBe(TaskDependencyResolver.DeathReason(dependent, blocker), "the wording did change for a blocker in another project");
        TaskDependencyResolver.RecordsDeath(older, dependent, blocker).Should().BeTrue();
        TaskDependencyResolver.RecordsDeath(TaskDependencyResolver.DeathReason(dependent, blocker), dependent, blocker).Should().BeTrue();
    }

    [Fact]
    public void A_death_recorded_for_a_different_death_or_not_at_all_is_not_the_observation_to_record_now()
    {
        TaskDependency blocker = Blocker(DomainId.New(), TaskState.Failed);
        TaskAggregate dependent = DependentIn(DomainId.New());
        TaskDependency abandoned = blocker with { State = TaskState.Abandoned };
        string failedWording = $"{blocker.DescribeDeath()} (h9k task dequeue {dependent.Id}, then h9k task draft {dependent.Id}).";

        TaskDependencyResolver.RecordsDeath(failedWording, dependent, abandoned).Should().BeFalse();
        TaskDependencyResolver.RecordsDeath(null, dependent, blocker).Should().BeFalse();
    }
}
