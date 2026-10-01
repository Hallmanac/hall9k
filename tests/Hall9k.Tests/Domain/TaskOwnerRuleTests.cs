using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="TaskOwnerRule"/> over resolved facts, no database. The agreement with the receive
/// gate itself is the table in <c>EventReplicationInboxTaskActGateTests</c>; these cover what the
/// gate has no word for: an assignment recorded by owner id alone, the fact that is present but
/// unresolvable, and an owner's second fleet node.
/// </summary>
public sealed class TaskOwnerRuleTests
{
    private const string Mine = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Theirs = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Third = "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public void An_owners_second_fleet_node_acts_on_a_held_task_because_both_owner_ids_resolve_to_one_root()
    {
        // Two owner ids, one per node, both resolve to the same root; the holder fact is that root.
        TaskOwnerFacts facts = new(OwnerRootFact.Known(Mine), OwnerRootFact.Absent, OwnerRootFact.Absent);

        TaskOwnerRule.Decide(Mine, facts).MayAct.Should().BeTrue();
    }

    [Fact]
    public void An_owners_second_fleet_node_acts_on_a_task_assigned_with_a_fingerprint()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Known(Mine), OwnerRootFact.Absent);

        TaskOwnerRule.Decide(Mine, facts).MayAct.Should().BeTrue();
    }

    [Fact]
    public void An_owners_second_fleet_node_acts_on_an_unassigned_task_with_a_verified_creator_root()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Known(Mine));

        TaskOwnerRule.Decide(Mine, facts).MayAct.Should().BeTrue();
    }

    [Fact]
    public void Either_the_holder_or_the_assignee_may_act_when_they_differ()
    {
        TaskOwnerFacts facts = new(OwnerRootFact.Known(Theirs), OwnerRootFact.Known(Mine), OwnerRootFact.Absent);

        TaskOwnerRule.Decide(Mine, facts).MayAct.Should().BeTrue();
        TaskOwnerRule.Decide(Theirs, facts).MayAct.Should().BeTrue();
        TaskOwnerRule.Decide(Third, facts).MayAct.Should().BeFalse();
    }

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
