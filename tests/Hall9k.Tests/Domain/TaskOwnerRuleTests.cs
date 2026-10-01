using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="TaskOwnerRule"/> over resolved facts, no database. The agreement with the receive
/// gate itself is the table in <c>EventReplicationInboxTaskActGateTests</c>; these cover what the
/// gate has no word for: the owner and assignee a refusal names, and the fact that is present but
/// unresolvable.
/// </summary>
public sealed class TaskOwnerRuleTests
{
    private const string Mine = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Theirs = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Third = "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void A_different_root_is_refused_and_the_holder_is_the_owner_named_with_the_assignee_beside_it()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Known(Theirs), OwnerRootFact.Known(Third), OwnerRootFact.Known(Mine));

        TaskOwnerCheck check = TaskOwnerRule.Decide(Mine, facts);

        check.Outcome.Should().Be(TaskOwnerOutcome.NotOwner);
        check.OwnerRootFingerprint.Should().Be(Theirs);
        check.AssigneeRootFingerprint.Should().Be(Third);
    }

    [Fact]
    public void The_creator_does_not_decide_once_the_task_has_an_assignee()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Known(Theirs), OwnerRootFact.Known(Mine));

        TaskOwnerCheck check = TaskOwnerRule.Decide(Mine, facts);

        check.MayAct.Should().BeFalse("the creator is only the root to protect while nobody is assigned or holds the task");
        check.OwnerRootFingerprint.Should().Be(Theirs);
    }

    [Fact]
    public void An_assignment_that_cannot_be_resolved_makes_the_owner_unknown_and_never_falls_through_to_the_creator()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Unresolved, OwnerRootFact.Known(Mine));

        TaskOwnerCheck check = TaskOwnerRule.Decide(Mine, facts);

        check.Outcome.Should().Be(TaskOwnerOutcome.Unknown);
        check.UnknownFact.Should().Be(TaskOwnerRule.AssigneeFact);
    }

    [Fact]
    public void An_unresolvable_assignment_does_not_hide_a_holder_this_node_can_name()
    {
        // A forced takeover leaves the new holder's owner id on the assignment with no fingerprint.
        TaskOwnerFacts facts = new(OwnerRootFact.Known(Theirs), OwnerRootFact.Unresolved, OwnerRootFact.Absent);

        TaskOwnerCheck check = TaskOwnerRule.Decide(Mine, facts);

        check.Outcome.Should().Be(TaskOwnerOutcome.NotOwner);
        check.OwnerRootFingerprint.Should().Be(Theirs);
        check.AssigneeRootFingerprint.Should().BeNull();
    }

    [Fact]
    public void A_creator_that_has_not_been_verified_makes_the_owner_unknown()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Unresolved);

        TaskOwnerCheck check = TaskOwnerRule.Decide(Mine, facts);

        check.Outcome.Should().Be(TaskOwnerOutcome.Unknown);
        check.UnknownFact.Should().Be(TaskOwnerRule.CreatorFact);
    }

    [Fact]
    public void A_task_with_no_fact_at_all_is_unknown_rather_than_anyones()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Absent);

        TaskOwnerRule.Decide(Mine, facts).Outcome.Should().Be(TaskOwnerOutcome.Unknown);
    }
}
