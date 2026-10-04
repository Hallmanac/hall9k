using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;
using Verdict = Hall9k.Connectors.Replication.EventReplicationInbox.TaskActVerdict;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Card D of idea 8d0b724b: <see cref="EventReplicationInbox.EvaluateIdeaActVerdict"/> is pure, DB-free
/// logic. An Owner-role sender's act always applies; a Member-role sender may name an assignee,
/// conclude or archive an idea only when its root is the idea's assignee, or with none its creator,
/// the same answer <see cref="TaskOwnerRule"/> gives the CLI. The plain idea events keep applying
/// exactly as they did before this gate knew ideas.
/// </summary>
public sealed class EventReplicationInboxIdeaActGateTests
{
    private const string CreatorRoot = "creator-root-fingerprint";
    private const string AssigneeRoot = "assignee-root-fingerprint";
    private const string TeammateRoot = "teammate-root-fingerprint";
    private const string OwnerRoot = "project-owner-root-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_hand_off_from_the_assignee_is_applied_whoever_it_names()
    {
        IdeaAggregate idea = IdeaHeldBy(AssigneeRoot);

        Verdict verdict = Judge(typeof(IdeaAssigneeSet), idea, Sender(AssigneeRoot), creatorRoot: CreatorRoot);

        verdict.Should().Be(Verdict.Allowed);
    }

    [Fact]
    public void A_creator_assigning_an_idea_nobody_holds_is_applied()
    {
        Verdict verdict = Judge(typeof(IdeaAssigneeSet), Captured(), Sender(CreatorRoot), creatorRoot: CreatorRoot);

        verdict.Should().Be(Verdict.Allowed);
    }

    [Fact]
    public void A_member_taking_a_teammates_unassigned_idea_for_themselves_is_refused()
    {
        Verdict verdict = Judge(typeof(IdeaAssigneeSet), Captured(), Sender(TeammateRoot), creatorRoot: CreatorRoot);

        verdict.Should().Be(
            Verdict.DroppedAndRefusedPermanently,
            "unlike a Published task there is no self-take exception for an idea (decision b8aa9007)");
    }

    [Fact]
    public void A_creator_who_handed_the_idea_away_cannot_assign_it_again()
    {
        IdeaAggregate idea = IdeaHeldBy(AssigneeRoot);

        Judge(typeof(IdeaAssigneeSet), idea, Sender(CreatorRoot), creatorRoot: CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
    }

    [Theory]
    [InlineData(typeof(IdeaConcluded))]
    [InlineData(typeof(IdeaArchived))]
    public void A_conclude_or_archive_by_an_assignee_who_is_not_the_creator_is_applied(Type eventType)
    {
        Judge(eventType, IdeaHeldBy(AssigneeRoot), Sender(AssigneeRoot), creatorRoot: CreatorRoot)
            .Should().Be(Verdict.Allowed);
    }

    [Theory]
    [InlineData(typeof(IdeaConcluded))]
    [InlineData(typeof(IdeaArchived))]
    public void A_conclude_or_archive_by_the_creator_after_handing_the_idea_away_is_refused(Type eventType)
    {
        Judge(eventType, IdeaHeldBy(AssigneeRoot), Sender(CreatorRoot), creatorRoot: CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
    }

    [Theory]
    [InlineData(typeof(IdeaConcluded))]
    [InlineData(typeof(IdeaArchived))]
    public void A_conclude_or_archive_by_a_non_owner_member_is_refused(Type eventType)
    {
        Judge(eventType, Captured(), Sender(TeammateRoot), creatorRoot: CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
        Judge(eventType, IdeaHeldBy(AssigneeRoot), Sender(TeammateRoot), creatorRoot: CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
    }

    [Theory]
    [InlineData(typeof(IdeaConcluded))]
    [InlineData(typeof(IdeaArchived))]
    public void A_creator_concluding_or_archiving_an_idea_nobody_holds_is_applied(Type eventType)
    {
        Judge(eventType, Captured(), Sender(CreatorRoot), creatorRoot: CreatorRoot).Should().Be(Verdict.Allowed);
    }

    [Theory]
    [InlineData(typeof(IdeaConcluded))]
    [InlineData(typeof(IdeaArchived))]
    [InlineData(typeof(IdeaAssigneeSet))]
    public void An_act_on_an_idea_whose_creator_root_is_unknown_is_held_not_applied_or_dropped(Type eventType)
    {
        Judge(eventType, Captured(), Sender(CreatorRoot), creatorRoot: null).Should().Be(Verdict.Held);
        Judge(eventType, Captured(), Sender(TeammateRoot), creatorRoot: null).Should().Be(
            Verdict.Held, "nothing proves the sender is not the creator either, so nothing is dropped on a claim");
    }

    [Fact]
    public void An_unknown_creator_does_not_hold_an_idea_somebody_holds_because_the_assignee_decides_it()
    {
        Judge(typeof(IdeaConcluded), IdeaHeldBy(AssigneeRoot), Sender(AssigneeRoot), creatorRoot: null)
            .Should().Be(Verdict.Allowed);
        Judge(typeof(IdeaConcluded), IdeaHeldBy(AssigneeRoot), Sender(CreatorRoot), creatorRoot: null)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_spike_verdict_from_a_teammates_node_is_applied_and_so_is_every_plain_idea_event()
    {
        Guid teammateNode = DomainId.New();
        IdeaAggregate idea = Captured();
        Type[] plainTypes =
        [
            typeof(IdeaSpikeConcluded), typeof(IdeaTaskCut), typeof(IdeaRevised), typeof(IdeaScopeSet),
            typeof(IdeaAssignedToProject), typeof(IdeaCaptured), typeof(IdeaPrivacySet), typeof(IdeaDiscarded),
            typeof(IdeaPromoted),
        ];

        foreach (Type eventType in plainTypes)
        {
            TaskActClassificationRegistry.TryClassificationOf(eventType).Should().Be(TaskActClassification.MemberSafe);
            EventReplicationInbox.EvaluateIdeaActVerdict(
                    TaskActClassification.MemberSafe, eventType, idea, Sender(TeammateRoot, teammateNode),
                    teammateNode, teammateNode, CreatorRoot)
                .Should().Be(Verdict.Allowed, eventType.Name);
        }

        EventReplicationInbox.EvaluateIdeaActVerdict(
                TaskActClassification.MemberSafe, typeof(IdeaSpikeConcluded), idea, sender: null,
                teammateNode, teammateNode)
            .Should().Be(Verdict.Allowed, "a plain observation needs no sender resolution at all");
    }

    [Fact]
    public void The_four_acts_the_gate_judges_are_conditional_and_nothing_else_about_an_idea_is()
    {
        Type[] conditional = [typeof(IdeaAssigneeSet), typeof(IdeaAssigneeCleared), typeof(IdeaConcluded), typeof(IdeaArchived)];
        foreach (Type eventType in conditional)
        {
            TaskActClassificationRegistry.TryClassificationOf(eventType).Should().Be(TaskActClassification.Conditional);
        }
    }

    [Fact]
    public void An_owner_roles_act_on_an_idea_applies_whatever_the_idea_says()
    {
        Guid ownerNode = DomainId.New();
        EventReplicationInbox.SenderResolution owner = new(OwnerRoot, MembershipRole.Owner, new HashSet<Guid> { ownerNode });

        foreach (Type eventType in new[] { typeof(IdeaAssigneeSet), typeof(IdeaAssigneeCleared), typeof(IdeaConcluded), typeof(IdeaArchived) })
        {
            EventReplicationInbox.EvaluateIdeaActVerdict(
                    TaskActClassification.Conditional, eventType, IdeaHeldBy(AssigneeRoot), owner, ownerNode, ownerNode, CreatorRoot)
                .Should().Be(Verdict.Allowed, eventType.Name);
        }
    }

    [Fact]
    public void An_unresolved_sender_is_refused_for_a_conditional_idea_act()
    {
        Guid node = DomainId.New();

        EventReplicationInbox.EvaluateIdeaActVerdict(
                TaskActClassification.Conditional, typeof(IdeaConcluded), Captured(), sender: null, node, node, CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_conditional_idea_act_on_an_idea_this_node_has_never_seen_is_held()
    {
        Judge(typeof(IdeaConcluded), idea: null, Sender(CreatorRoot), creatorRoot: CreatorRoot).Should().Be(Verdict.Held);
    }

    [Fact]
    public void An_assignee_recorded_without_a_root_refuses_a_member_because_nobody_can_tell_whose_it_is()
    {
        IdeaAggregate idea = Captured();
        idea.Apply(new IdeaAssigneeSet(idea.Id, DomainId.New(), AssigneeOwnerRootFingerprint: null, Now, Guid.NewGuid()));

        Judge(typeof(IdeaConcluded), idea, Sender(CreatorRoot), creatorRoot: CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void An_assignee_cleared_is_allowed_only_from_the_current_assignee_and_holds_when_nobody_is_recorded()
    {
        IdeaAggregate held = IdeaHeldBy(AssigneeRoot);

        Judge(typeof(IdeaAssigneeCleared), held, Sender(AssigneeRoot), CreatorRoot).Should().Be(Verdict.Allowed);
        Judge(typeof(IdeaAssigneeCleared), held, Sender(CreatorRoot), CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently, "a creator who handed the idea away no longer holds it");
        Judge(typeof(IdeaAssigneeCleared), held, Sender(TeammateRoot), CreatorRoot)
            .Should().Be(Verdict.DroppedAndRefusedPermanently);
        Judge(typeof(IdeaAssigneeCleared), Captured(), Sender(CreatorRoot), CreatorRoot)
            .Should().Be(Verdict.Held, "the assignment it releases may simply not have arrived here yet");
    }

    [Fact]
    public void A_relayed_refusal_is_held_for_the_true_origin_not_dropped_for_good()
    {
        Guid originNode = DomainId.New();
        Guid relayNode = DomainId.New();
        EventReplicationInbox.SenderResolution relay = new(TeammateRoot, MembershipRole.Member, new HashSet<Guid> { relayNode });

        EventReplicationInbox.EvaluateIdeaActVerdict(
                TaskActClassification.Conditional, typeof(IdeaConcluded), Captured(), relay, originNode, relayNode, CreatorRoot)
            .Should().Be(Verdict.DroppedWithoutRecording);
    }

    /// <summary>
    /// The CLI guard (<see cref="TaskOwnerRule"/> over <see cref="TaskOwnerFacts"/> with no holder) must say
    /// what the receive gate says, so a command that refuses is refusing an act the fleet would drop and
    /// one that proceeds is proceeding with an act the fleet would apply. Wherever the gate holds (a creator
    /// it has not verified) the rule has no answer either and reports the owner unknown.
    /// </summary>
    [Theory]
    [MemberData(nameof(AgreementRows))]
    public void The_owner_rule_agrees_with_the_receive_gate_for_a_member_role_sender(
        string? assigneeRoot, string? creatorRoot, string actingRoot)
    {
        IdeaAggregate idea = assigneeRoot is null ? Captured() : IdeaHeldBy(assigneeRoot);
        Verdict gate = Judge(typeof(IdeaConcluded), idea, Sender(actingRoot), creatorRoot);

        TaskOwnerCheck check = TaskOwnerRule.Decide(
            actingRoot,
            new TaskOwnerFacts(
                OwnerRootFact.Absent,
                OwnerRootFact.KnownOrAbsent(assigneeRoot),
                creatorRoot is null ? OwnerRootFact.Unresolved : OwnerRootFact.Known(creatorRoot)));

        if (gate == Verdict.Held)
        {
            check.Outcome.Should().Be(TaskOwnerOutcome.Unknown);
        }
        else
        {
            check.MayAct.Should().Be(gate == Verdict.Allowed);
        }
    }

    public static TheoryData<string?, string?, string> AgreementRows()
    {
        TheoryData<string?, string?, string> rows = [];
        foreach (string? assignee in new string?[] { null, AssigneeRoot, CreatorRoot })
        {
            foreach (string? creator in new string?[] { null, CreatorRoot, TeammateRoot })
            {
                foreach (string acting in new[] { AssigneeRoot, CreatorRoot, TeammateRoot, "some-third-root" })
                {
                    rows.Add(assignee, creator, acting);
                }
            }
        }

        return rows;
    }

    private static Verdict Judge(Type eventType, IdeaAggregate? idea, EventReplicationInbox.SenderResolution sender, string? creatorRoot)
    {
        Guid nodeId = sender.FleetNodeIds.Single();
        return EventReplicationInbox.EvaluateIdeaActVerdict(
            TaskActClassification.Conditional, eventType, idea, sender, nodeId, nodeId, creatorRoot);
    }

    private static EventReplicationInbox.SenderResolution Sender(string root, Guid? nodeId = null) =>
        new(root, MembershipRole.Member, new HashSet<Guid> { nodeId ?? DomainId.New() });

    private static IdeaAggregate Captured()
    {
        IdeaAggregate idea = new();
        idea.Apply(IdeaDecider.Capture(DomainId.New(), Guid.NewGuid(), "A rough thought", projectId: null, Now, ProjectHome.None));
        return idea;
    }

    private static IdeaAggregate IdeaHeldBy(string assigneeRoot)
    {
        IdeaAggregate idea = Captured();
        idea.Apply(new IdeaAssigneeSet(idea.Id, Guid.NewGuid(), assigneeRoot, Now, Guid.NewGuid()));
        return idea;
    }
}
