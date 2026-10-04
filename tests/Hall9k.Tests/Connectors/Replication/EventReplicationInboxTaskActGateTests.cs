using FluentAssertions;
using Hall9k.Connectors.Replication;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
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

    /// <summary>Independent pre-PR review, cycle 8, terminal lap: a plain MemberSafe act's own
    /// unconditional-Allowed shortcut applies whether <c>originNodeId</c> names the sender itself or
    /// a forwarded claim, because this method never runs on a forwarded record until
    /// <see cref="EventReplicationInbox.IsForwardedRecordAdmitted"/> has already refused it unless
    /// the relay is speaking inside a catch-up answer this node itself minted for that exact origin
    /// and stream. A cycle-8 narrowing once made this shortcut native-only, on the theory that
    /// nothing else in this bucket ever checked who the true origin actually was — but that
    /// narrowing then held every forwarded TaskCompleted or run event a LEGITIMATE relay served
    /// inside an admitted answer, until the true origin re-sent it directly, undercutting the
    /// catch-up ask the held-act queue relies on to clear.</summary>
    [Fact]
    public void A_forwarded_plain_member_safe_act_is_allowed_the_same_as_a_native_one()
    {
        Guid taskId = DomainId.New();
        Guid originNodeId = DomainId.New();
        Guid relayNodeId = DomainId.New();
        TaskCompleted completed = new(taskId, DomainId.New(), PullRequestUrl: null, Now);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.MemberSafe, typeof(TaskCompleted), completed, task: null, sender: null,
            originNodeId, relayNodeId);

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

    /// <summary>
    /// The CLI guard (<see cref="TaskOwnerRule"/>) must say what the receive gate says, so a command
    /// that refuses is refusing an act the fleet would drop and one that proceeds is proceeding with
    /// an act the fleet would apply. Every row feeds one set of task facts to both: wherever the gate
    /// does not answer Held, the rule's may-act answer is the gate's Allowed. Where the gate holds
    /// (a creator it has not verified), the rule has no answer either and reports the owner unknown.
    /// </summary>
    [Theory]
    [MemberData(nameof(TaskActAgreementRows))]
    public void The_owner_rule_agrees_with_the_receive_gate_for_a_member_role_sender(
        string? holderRoot, string? assignedRoot, string? creatorRoot, string actingRoot)
    {
        Guid taskId = DomainId.New();
        TaskAggregate task = PublishedUnassignedTask(taskId);
        if (assignedRoot is not null)
        {
            task.Apply(new TaskAssigned(
                taskId, Guid.NewGuid(), UnmetDependencies: [], Now, Guid.NewGuid(), AssignedOwnerRootFingerprint: assignedRoot));
        }

        if (holderRoot is not null)
        {
            task.Apply(new TaskClaimed(
                taskId, DomainId.New(), Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: holderRoot));
        }

        Guid nodeId = DomainId.New();
        EventReplicationInbox.TaskActVerdict gate = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAbandoned), new TaskAbandoned(taskId, null, Now, Guid.NewGuid()),
            task, new EventReplicationInbox.SenderResolution(actingRoot, MembershipRole.Member, new HashSet<Guid> { nodeId }),
            nodeId, nodeId, creatorRoot);

        TaskOwnerCheck check = TaskOwnerRule.Decide(
            actingRoot,
            new TaskOwnerFacts(
                OwnerRootFact.KnownOrAbsent(holderRoot),
                OwnerRootFact.KnownOrAbsent(assignedRoot),
                creatorRoot is null
                    ? OwnerRootFact.Unresolved
                    : OwnerRootFact.Known(creatorRoot)));

        if (gate == EventReplicationInbox.TaskActVerdict.Held)
        {
            check.Outcome.Should().Be(TaskOwnerOutcome.Unknown);
        }
        else
        {
            check.MayAct.Should().Be(gate == EventReplicationInbox.TaskActVerdict.Allowed);
        }
    }

    public static TheoryData<string?, string?, string?, string> TaskActAgreementRows()
    {
        TheoryData<string?, string?, string?, string> rows = [];
        string?[] holders = [null, OwnerRoot, MemberRoot];
        string?[] assignees = [null, OwnerRoot, MemberRoot];
        string?[] creators = [null, OwnerRoot, MemberRoot];
        foreach (string? holder in holders)
        {
            foreach (string? assignee in assignees)
            {
                foreach (string? creator in creators)
                {
                    foreach (string acting in new[] { OwnerRoot, MemberRoot, "some-third-root" })
                    {
                        rows.Add(holder, assignee, creator, acting);
                    }
                }
            }
        }

        return rows;
    }

    private const string TeammateRoot = "teammate-root-fingerprint";

    [Fact]
    public void A_revise_from_an_assignee_who_is_not_the_creator_is_applied_and_the_creators_is_refused()
    {
        Guid taskId = DomainId.New();
        Guid assigneeNodeId = DomainId.New();
        Guid creatorNodeId = DomainId.New();
        TaskAggregate task = DraftHeldBy(taskId, MemberRoot);
        TaskRevised revised = new(
            taskId, Optional<string>.Of("a new objective"), Optional<IReadOnlyList<string>>.None,
            Optional<string>.None, Optional<IReadOnlyList<Guid>>.None, Optional<TaskType>.None,
            Optional<AgentModel>.None, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict fromAssignee = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskRevised), revised, task, MemberSender(assigneeNodeId),
            assigneeNodeId, assigneeNodeId, creatorRootFingerprint: OwnerRoot);
        EventReplicationInbox.TaskActVerdict fromCreator = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskRevised), revised, task,
            new EventReplicationInbox.SenderResolution(OwnerRoot, MembershipRole.Member, new HashSet<Guid> { creatorNodeId }),
            creatorNodeId, creatorNodeId, creatorRootFingerprint: OwnerRoot);

        fromAssignee.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
        fromCreator.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_handoff_from_the_current_assignee_is_applied_whoever_it_names()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = DraftHeldBy(taskId, MemberRoot);

        EventReplicationInbox.TaskActVerdict verdict = EvaluateAssigneeSet(task, TeammateRoot, memberNodeId, creatorRoot: OwnerRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void A_creator_handing_off_an_unassigned_draft_is_applied()
    {
        Guid taskId = DomainId.New();
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = Draft(taskId);

        EventReplicationInbox.TaskActVerdict verdict = EvaluateAssigneeSet(task, TeammateRoot, memberNodeId, creatorRoot: MemberRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void A_member_taking_a_teammates_unassigned_draft_is_refused()
    {
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = Draft(DomainId.New());

        EventReplicationInbox.TaskActVerdict verdict = EvaluateAssigneeSet(task, MemberRoot, memberNodeId, creatorRoot: OwnerRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void A_member_taking_an_unassigned_published_task_for_itself_is_applied()
    {
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = PublishedFreeTask(DomainId.New());

        EventReplicationInbox.TaskActVerdict verdict = EvaluateAssigneeSet(task, MemberRoot, memberNodeId, creatorRoot: OwnerRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void A_non_owner_member_naming_another_root_is_refused()
    {
        Guid memberNodeId = DomainId.New();

        EvaluateAssigneeSet(PublishedFreeTask(DomainId.New()), TeammateRoot, memberNodeId, creatorRoot: OwnerRoot)
            .Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
        EvaluateAssigneeSet(DraftHeldBy(DomainId.New(), TeammateRoot), MemberRoot, memberNodeId, creatorRoot: MemberRoot)
            .Should().Be(
                EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently,
                "the creator who handed the task away no longer holds it, and a held task is not taken");
    }

    [Fact]
    public void A_handoff_whose_creator_is_not_yet_known_holds_rather_than_dropping()
    {
        Guid memberNodeId = DomainId.New();

        EvaluateAssigneeSet(Draft(DomainId.New()), TeammateRoot, memberNodeId, creatorRoot: null)
            .Should().Be(EventReplicationInbox.TaskActVerdict.Held);
    }

    [Fact]
    public void A_relayed_refusal_is_held_for_the_true_origin_not_dropped_for_good()
    {
        Guid memberNodeId = DomainId.New();
        Guid relayNodeId = DomainId.New();
        TaskAggregate task = Draft(DomainId.New());

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeSet),
            new TaskAssigneeSet(task.Id, Guid.NewGuid(), MemberRoot, Now, Guid.NewGuid()), task, MemberSender(memberNodeId),
            originNodeId: memberNodeId, senderNodeId: relayNodeId, creatorRootFingerprint: OwnerRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedWithoutRecording);
    }

    [Fact]
    public void An_assignee_set_on_a_task_held_by_another_roots_ledger_lock_is_refused()
    {
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = HeldByOwnerTask(DomainId.New());

        EvaluateAssigneeSet(task, MemberRoot, memberNodeId, creatorRoot: OwnerRoot)
            .Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void An_assignee_set_from_the_ledger_holder_of_a_task_nobody_is_assigned_is_applied()
    {
        Guid memberNodeId = DomainId.New();
        TaskAggregate task = Draft(DomainId.New());
        task.Apply(new TaskClaimed(
            task.Id, DomainId.New(), Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: MemberRoot));

        EvaluateAssigneeSet(task, MemberRoot, memberNodeId, creatorRoot: OwnerRoot)
            .Should().Be(EventReplicationInbox.TaskActVerdict.Allowed, "the rule lets the holder act, and the CLI sent it on that");
        EvaluateAssigneeSet(task, TeammateRoot, memberNodeId, creatorRoot: OwnerRoot)
            .Should().Be(EventReplicationInbox.TaskActVerdict.Allowed, "a hand-off from the holder is the holder's to make");
    }

    [Fact]
    public void An_assignee_cleared_from_the_ledger_holder_is_applied_and_from_a_third_root_is_not()
    {
        Guid nodeId = DomainId.New();
        TaskAggregate task = DraftHeldBy(DomainId.New(), TeammateRoot);
        task.Apply(new TaskClaimed(
            task.Id, DomainId.New(), Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: MemberRoot));
        TaskAssigneeCleared cleared = new(task.Id, null, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict fromHolder = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeCleared), cleared, task, MemberSender(nodeId),
            nodeId, nodeId, creatorRootFingerprint: OwnerRoot);
        EventReplicationInbox.TaskActVerdict fromCreator = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeCleared), cleared, task,
            new EventReplicationInbox.SenderResolution(OwnerRoot, MembershipRole.Member, new HashSet<Guid> { nodeId }),
            nodeId, nodeId, creatorRootFingerprint: OwnerRoot);

        fromHolder.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
        fromCreator.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
    }

    [Fact]
    public void An_assignee_set_by_an_owner_role_sender_always_applies()
    {
        Guid ownerNodeId = DomainId.New();
        TaskAggregate task = DraftHeldBy(DomainId.New(), MemberRoot);

        EventReplicationInbox.TaskActVerdict verdict = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeSet),
            new TaskAssigneeSet(task.Id, Guid.NewGuid(), TeammateRoot, Now, Guid.NewGuid()), task,
            new EventReplicationInbox.SenderResolution(OwnerRoot, MembershipRole.Owner, new HashSet<Guid> { ownerNodeId }),
            ownerNodeId, ownerNodeId, creatorRootFingerprint: MemberRoot);

        verdict.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
    }

    [Fact]
    public void An_assignee_cleared_is_allowed_only_from_the_current_assignee()
    {
        Guid taskId = DomainId.New();
        Guid nodeId = DomainId.New();
        TaskAggregate task = DraftHeldBy(taskId, MemberRoot);
        TaskAssigneeCleared cleared = new(taskId, null, Now, Guid.NewGuid());

        EventReplicationInbox.TaskActVerdict fromAssignee = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeCleared), cleared, task, MemberSender(nodeId),
            nodeId, nodeId, creatorRootFingerprint: OwnerRoot);
        EventReplicationInbox.TaskActVerdict fromCreator = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeCleared), cleared, task,
            new EventReplicationInbox.SenderResolution(OwnerRoot, MembershipRole.Member, new HashSet<Guid> { nodeId }),
            nodeId, nodeId, creatorRootFingerprint: OwnerRoot);
        EventReplicationInbox.TaskActVerdict fromOwnerRole = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeCleared), cleared, task,
            new EventReplicationInbox.SenderResolution(TeammateRoot, MembershipRole.Owner, new HashSet<Guid> { nodeId }),
            nodeId, nodeId, creatorRootFingerprint: OwnerRoot);

        fromAssignee.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed);
        fromCreator.Should().Be(EventReplicationInbox.TaskActVerdict.DroppedAndRefusedPermanently);
        fromOwnerRole.Should().Be(EventReplicationInbox.TaskActVerdict.Allowed, "an Owner-role override always applies");
    }

    /// <summary>
    /// The agreement test above feeds the rule a queued assignment. A draft somebody has laid hold of
    /// is the case this card adds: the rule and the gate must give one answer for it too, with the
    /// hold recorded by <see cref="TaskAssigneeSet"/> and no go signal anywhere on the stream.
    /// </summary>
    [Theory]
    [MemberData(nameof(TaskActAgreementRows))]
    public void The_owner_rule_agrees_with_the_receive_gate_for_a_draft_a_member_has_laid_hold_of(
        string? holderRoot, string? assigneeRoot, string? creatorRoot, string actingRoot)
    {
        Guid taskId = DomainId.New();
        TaskAggregate task = Draft(taskId);
        if (assigneeRoot is not null)
        {
            task.Apply(new TaskAssigneeSet(taskId, Guid.NewGuid(), assigneeRoot, Now, Guid.NewGuid()));
        }

        if (holderRoot is not null)
        {
            task.Apply(new TaskClaimed(
                taskId, DomainId.New(), Guid.NewGuid(), LeaseGeneration: 1, DomainId.New(), Now, OwnerRootFingerprint: holderRoot));
        }

        Guid nodeId = DomainId.New();
        EventReplicationInbox.TaskActVerdict gate = EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAbandoned), new TaskAbandoned(taskId, null, Now, Guid.NewGuid()),
            task, new EventReplicationInbox.SenderResolution(actingRoot, MembershipRole.Member, new HashSet<Guid> { nodeId }),
            nodeId, nodeId, creatorRoot);

        TaskOwnerCheck check = TaskOwnerRule.Decide(
            actingRoot,
            new TaskOwnerFacts(
                OwnerRootFact.KnownOrAbsent(holderRoot),
                OwnerRootFact.KnownOrAbsent(assigneeRoot),
                creatorRoot is null
                    ? OwnerRootFact.Unresolved
                    : OwnerRootFact.Known(creatorRoot)));

        if (gate == EventReplicationInbox.TaskActVerdict.Held)
        {
            check.Outcome.Should().Be(TaskOwnerOutcome.Unknown);
        }
        else
        {
            check.MayAct.Should().Be(gate == EventReplicationInbox.TaskActVerdict.Allowed);
        }
    }

    private static EventReplicationInbox.TaskActVerdict EvaluateAssigneeSet(
        TaskAggregate task, string namedRoot, Guid memberNodeId, string? creatorRoot) =>
        EventReplicationInbox.EvaluateTaskActVerdict(
            TaskActClassification.Conditional, typeof(TaskAssigneeSet),
            new TaskAssigneeSet(task.Id, Guid.NewGuid(), namedRoot, Now, Guid.NewGuid()), task, MemberSender(memberNodeId),
            memberNodeId, memberNodeId, creatorRoot);

    private static TaskAggregate Draft(Guid taskId)
    {
        TaskAggregate task = new();
        task.Apply(new TaskAdded(
            taskId, DomainId.New(), "Ship the thing", ["it ships"], TaskType.Feature, null, null, null, Now, Guid.NewGuid(),
            StartsAsDraft: true));
        return task;
    }

    private static TaskAggregate DraftHeldBy(Guid taskId, string assigneeRoot)
    {
        TaskAggregate task = Draft(taskId);
        task.Apply(new TaskAssigneeSet(taskId, Guid.NewGuid(), assigneeRoot, Now, Guid.NewGuid()));
        return task;
    }

        private static TaskAggregate PublishedFreeTask(Guid taskId)
    {
        TaskAggregate task = Draft(taskId);
        task.Apply(new TaskPublished(taskId, Now, Guid.NewGuid()));
        return task;
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
