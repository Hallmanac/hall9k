using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Connectors.Replication;

/// <summary>
/// Idea 6be68ee2, trust-ledger finding 5: <see cref="EventReplicationInbox.EvaluateTaskActVerdict"/>
/// is pure, DB-free logic — an owner-role sender's Task/Run act always applies; a non-owner
/// (Member-role) sender's applies only when the task it targets is currently the sender's own root
/// to act on, exactly as <see cref="TaskActClassificationRegistry"/>'s own entries describe.
/// </summary>
public sealed class EventReplicationInboxTaskActGateTests
{
    private const string OwnerRoot = "owner-root-fingerprint";
    private const string MemberRoot = "member-root-fingerprint";
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_members_task_assigned_placing_a_task_on_the_owners_node_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        Guid ownerNodeId = DomainId.New();
        TaskAggregate task = PublishedUnassignedTask(taskId);
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);

        // Self-assigns (a legitimate target), but pins the placement onto the owner's own node —
        // a node this member's own fleet never contains.
        TaskAssigned assigned = new(
            taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(),
            AssignedOwnerRootFingerprint: MemberRoot, PlacedOnNodeId: ownerNodeId);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskAssigned), assigned, task, sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_members_self_assign_of_an_owners_published_unassigned_task_is_applied()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = PublishedUnassignedTask(taskId);
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);

        TaskAssigned assigned = new(
            taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(), AssignedOwnerRootFingerprint: MemberRoot);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskAssigned), assigned, task, sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void A_members_forged_reassignment_of_an_owner_held_task_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = HeldByOwnerTask(taskId);
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);

        TaskAssigned assigned = new(
            taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(), AssignedOwnerRootFingerprint: MemberRoot);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskAssigned), assigned, task, sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    /// <summary>TaskHolderTakenOver's own Apply clears AssignedOwnerFingerprint to null the moment
    /// it sets HolderOwnerRootFingerprint (idea 202383dc's own "reassigned to the taker" doc), so a
    /// forged TaskAssigned naming no placement cannot read the null assignment fingerprint alone as
    /// "free to reassign" — the holder lock has to be checked too, or the task is hijacked away
    /// from its own current holder the moment a takeover has run (independent pre-PR review,
    /// cycle 1, conformance lens, medium).</summary>
    [Fact]
    public void A_members_forged_reassignment_of_a_task_the_owner_took_over_as_holder_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        Guid ownerNodeId = DomainId.New();
        TaskAggregate task = PublishedUnassignedTask(taskId);
        task.Apply(new TaskHolderTakenOver(
            taskId, PreviousHolderNodeId: null, ownerNodeId, Guid.NewGuid(), OwnerRoot, "absent holder", Guid.NewGuid(),
            Now));
        task.AssignedOwnerFingerprint.Should().BeNull();
        task.HolderOwnerRootFingerprint.Should().Be(OwnerRoot);
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);

        TaskAssigned assigned = new(
            taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(), AssignedOwnerRootFingerprint: MemberRoot);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskAssigned), assigned, task, sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_members_task_claimed_for_an_owners_task_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = AssignedToOwnerTask(taskId);
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);

        TaskClaimed claimed = new(taskId, memberNodeId, Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskClaimed), claimed, task, sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_members_task_revised_on_an_owners_task_is_dropped_and_on_their_own_task_applied()
    {
        Guid ownersTaskId = DomainId.New();
        Guid membersTaskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskRevised revisedObjective = new(
            ownersTaskId, Optional<string>.Of("a new objective"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict droppedVerdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskRevised), revisedObjective, AssignedToOwnerTask(ownersTaskId),
            sender, memberNodeId, memberNodeId);
        droppedVerdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);

        TaskRevised revisedOwnTask = revisedObjective with { Id = membersTaskId };
        EventReplicationInbox.TaskActVerdict appliedVerdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskRevised), revisedOwnTask, AssignedToMemberTask(membersTaskId),
            sender, memberNodeId, memberNodeId);
        appliedVerdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void Task_holder_taken_over_from_a_member_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskHolderTakenOver takenOver = new(
            taskId, PreviousHolderNodeId: null, memberNodeId, Guid.NewGuid(), MemberRoot, "absent holder", Guid.NewGuid(),
            Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.OwnerOnly, typeof(TaskHolderTakenOver), takenOver, task: null, sender, memberNodeId,
            memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_members_review_park_resolved_on_the_owners_run_is_dropped()
    {
        Guid runId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        ReviewParkResolved resolved = new(runId, ReviewVerdict.MergeReady, "looks fine", Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.OwnerOnly, typeof(ReviewParkResolved), resolved,
            task: AssignedToOwnerTask(DomainId.New()), sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_member_nodes_own_task_completed_is_applied()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskCompleted completed = new(taskId, DomainId.New(), PullRequestUrl: null, Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskCompleted), completed, task: null, sender, memberNodeId,
            memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void An_owner_roles_senders_act_applies_unconditionally_whatever_the_task_says()
    {
        Guid taskId = DomainId.New();
        Guid ownerNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution owner =
            new(OwnerRoot, MembershipRole.Owner, new HashSet<Guid> { ownerNodeId });
        TaskHolderTakenOver takenOver = new(
            taskId, PreviousHolderNodeId: null, ownerNodeId, Guid.NewGuid(), OwnerRoot, "absent holder", Guid.NewGuid(),
            Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.OwnerOnly, typeof(TaskHolderTakenOver), takenOver, task: null, owner, ownerNodeId,
            ownerNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void A_conditional_act_on_a_task_whose_assignment_is_not_yet_known_here_is_held_rather_than_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskClaimed claimed = new(taskId, memberNodeId, Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskClaimed), claimed, PublishedUnassignedTask(taskId), sender,
            memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Held);
    }

    [Fact]
    public void A_conditional_act_on_a_task_this_node_has_never_seen_at_all_is_also_held()
    {
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        CloseoutBudgetGranted granted = new(DomainId.New(), "fresh grant", Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(CloseoutBudgetGranted), granted, task: null, sender, memberNodeId,
            memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Held);
    }

    [Fact]
    public void An_unresolved_sender_is_refused_for_a_conditional_or_owner_only_act()
    {
        Guid taskId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        TaskClaimed claimed = new(taskId, senderNodeId, Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskClaimed), claimed, AssignedToOwnerTask(taskId), sender: null,
            senderNodeId, senderNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    /// <summary>Plain MemberSafe — every entry in that bucket but <see cref="TaskAssigned"/>'s own
    /// special rule — was never gated at all before this PR and stays that way: it applies even
    /// from a sender this read's own trust chain cannot currently resolve to any project member,
    /// the ordinary shape of <c>TrustChain.Empty</c> in a test that is not itself about trust.
    /// </summary>
    [Fact]
    public void A_plain_member_safe_act_applies_even_from_an_unresolved_sender()
    {
        Guid taskId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        TaskCompleted completed = new(taskId, DomainId.New(), PullRequestUrl: null, Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskCompleted), completed, task: null, sender: null, senderNodeId,
            senderNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    /// <summary>Only the current holder may release the holder lock — checked alone, never
    /// assignment.</summary>
    [Fact]
    public void Task_holder_released_checks_the_current_holder_alone()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskHolderReleased released = new(taskId, Now);

        // Assigned to the member (so a general conditional check would allow it), but held by the
        // owner: only the holder may release it.
        TaskAggregate assignedToMemberButHeldByOwner = AssignedToMemberTask(taskId);
        assignedToMemberButHeldByOwner.Apply(new TaskClaimed(taskId, DomainId.New(), Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: OwnerRoot));

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskHolderReleased), released, assignedToMemberButHeldByOwner,
            sender, memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    /// <summary>An unassigned, unheld task is still its own creator's — a pre-assignment-capable
    /// conditional act (TaskPublished's own bucket) from a sender this read cannot resolve at all
    /// is refused, never blanket-allowed just because nobody has been assigned or has claimed the
    /// task yet (independent pre-PR review, cycle 1, both lenses, high: the earlier version of this
    /// gate let even an unresolved sender revise, scope, pre-approve, publish, or return to draft an
    /// owner's own unassigned task).</summary>
    [Fact]
    public void A_task_published_on_a_genuinely_unassigned_task_is_refused_from_an_unresolved_sender()
    {
        Guid taskId = DomainId.New();
        Guid senderNodeId = DomainId.New();
        TaskPublished published = new(taskId, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskPublished), published, PublishedUnassignedTask(taskId),
            sender: null, senderNodeId, senderNodeId, creatorRootFingerprint: MemberRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    /// <summary>The task's own creator root, once known, is exactly who a pre-assignment-capable
    /// act on their own still-unassigned, still-unheld task is judged against — the member who
    /// created it may publish, scope, pre-approve, revise, return to draft, or abandon it before
    /// anyone is ever assigned.</summary>
    [Fact]
    public void A_creators_own_publish_of_their_own_genuinely_unassigned_task_applies()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskPublished published = new(taskId, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskPublished), published, PublishedUnassignedTask(taskId),
            sender, memberNodeId, memberNodeId, creatorRootFingerprint: MemberRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    /// <summary>A member forging one of these acts onto a genuinely unassigned task they did not
    /// create — the owner's own published draft, say — is refused rather than allowed just because
    /// nobody has claimed it yet: unassigned never means ownerless.</summary>
    [Fact]
    public void A_members_forged_act_on_the_owners_genuinely_unassigned_task_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskPublished published = new(taskId, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskPublished), published, PublishedUnassignedTask(taskId),
            sender, memberNodeId, memberNodeId, creatorRootFingerprint: OwnerRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    /// <summary>When this node never verified who created a genuinely unassigned, unheld task
    /// (<see cref="Hall9k.Domain.Features.Replication.TaskCreatorRootRecord"/>'s own doc on why a
    /// forwarded genesis leaves the creator root unset), a pre-assignment-capable act on it is held
    /// rather than trusted either way — the identical treatment every other not-yet-arrived fact
    /// gets in this gate.</summary>
    [Fact]
    public void A_pre_assignment_capable_act_with_no_known_creator_root_is_held()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskPublished published = new(taskId, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskPublished), published, PublishedUnassignedTask(taskId),
            sender, memberNodeId, memberNodeId, creatorRootFingerprint: null);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Held);
    }

    /// <summary>The owner's own role bypasses the creator-root check entirely, exactly as it
    /// bypasses every other conditional check in this gate.</summary>
    [Fact]
    public void An_owners_publish_of_a_members_genuinely_unassigned_task_applies_regardless_of_creator_root()
    {
        Guid taskId = DomainId.New();
        Guid ownerNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution owner =
            new(OwnerRoot, MembershipRole.Owner, new HashSet<Guid> { ownerNodeId });
        TaskPublished published = new(taskId, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskPublished), published, PublishedUnassignedTask(taskId),
            owner, ownerNodeId, ownerNodeId, creatorRootFingerprint: MemberRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    /// <summary>The same act forged onto a task already known to be someone else's is still
    /// refused — the pre-assignment leniency only ever covers a genuinely unassigned task, never a
    /// known mismatch.</summary>
    [Fact]
    public void A_task_published_forged_onto_an_already_assigned_task_is_dropped()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        EventReplicationInbox.SenderResolution sender = MemberSender(memberNodeId);
        TaskPublished published = new(taskId, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskPublished), published, AssignedToOwnerTask(taskId), sender,
            memberNodeId, memberNodeId);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    private static EventReplicationInbox.SenderResolution MemberSender(Guid memberNodeId) =>
        new(MemberRoot, MembershipRole.Member, new HashSet<Guid> { memberNodeId });

    private static TaskAggregate PublishedUnassignedTask(Guid taskId)
    {
        TaskAggregate task = new();
        task.Apply(new TaskAdded(taskId, DomainId.New(), "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid()));
        task.Apply(new TaskPublished(taskId, Now, Guid.NewGuid()));
        return task;
    }

    private static TaskAggregate AssignedToOwnerTask(Guid taskId)
    {
        TaskAggregate task = PublishedUnassignedTask(taskId);
        task.Apply(new TaskAssigned(taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(), AssignedOwnerRootFingerprint: OwnerRoot));
        return task;
    }

    private static TaskAggregate AssignedToMemberTask(Guid taskId)
    {
        TaskAggregate task = PublishedUnassignedTask(taskId);
        task.Apply(new TaskAssigned(taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(), AssignedOwnerRootFingerprint: MemberRoot));
        return task;
    }

    private static TaskAggregate HeldByOwnerTask(Guid taskId)
    {
        TaskAggregate task = AssignedToOwnerTask(taskId);
        task.Apply(new TaskClaimed(taskId, DomainId.New(), Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: OwnerRoot));
        return task;
    }
}
