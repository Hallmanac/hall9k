using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Whether a replicated <see cref="EventScope.ProjectScoped"/> Task, Run or Idea event, applied by a
/// non-owner (Member-role) sender, is safe to apply unconditionally, safe only against the task
/// it targets, or never safe at all from that sender (idea 6be68ee2, trust-ledger finding 5).
/// Every classification here is judged against the SENDER — an owner-role sender's act always
/// applies unconditionally regardless of what this registry says
/// (<c>Hall9k.Connectors.Replication.EventReplicationInbox.EvaluateTaskActVerdict</c>'s own doc);
/// this registry only decides what a MEMBER may still do.
/// </summary>
public enum TaskActClassification
{
    /// <summary>
    /// Never applied from a non-owner sender, whatever the task's own state says — the acts that
    /// would make the receiving owner's node do something on a member's say-so alone
    /// (<see cref="TaskHolderTakenOver"/>'s own unilateral override, <see cref="ReviewParkResolved"/>'s
    /// own human verdict).
    /// </summary>
    OwnerOnly,

    /// <summary>
    /// Applied from any current project member regardless of role, because the act is either
    /// about the sender's own work (adding, completing, or reporting on a task or run this same
    /// node is running) or a plain observation with nothing for a forged copy to gain
    /// (a Jira write, a PR-review sighting, a dependency edge, a publication step, the running
    /// node's own run-lifecycle bookkeeping). <see cref="TaskAssigned"/> is the one exception
    /// carrying its own narrower rule even though it sits in this bucket — see
    /// <c>EventReplicationInbox.EvaluateTaskActVerdict</c>'s own doc.
    /// </summary>
    MemberSafe,

    /// <summary>
    /// Applied from a non-owner sender only when the task this act targets (its own stream for a
    /// Task event, or the task its run belongs to for a Run event) is currently assigned to or
    /// held by that sender's own root — <see cref="TaskClaimed"/> checks assignment alone, and
    /// <see cref="TaskHolderReleased"/> checks the current holder alone; every other member of this
    /// bucket checks both. Evaluated at apply time from the task's own CURRENT aggregate state
    /// (never a projection), and held rather than dropped when that state cannot yet answer the
    /// question at all (<c>EventReplicationInbox.EvaluateTaskActVerdict</c>'s own doc).
    /// </summary>
    Conditional,
}

/// <summary>
/// The classification table itself: every <see cref="EventScope.ProjectScoped"/> event type in
/// <c>Hall9k.Domain.Features.Tasks.Events</c>, <c>Hall9k.Domain.Features.Run.Events</c> and
/// <c>Hall9k.Domain.Features.Idea</c> gets one
/// entry (idea 6be68ee2, trust-ledger finding 5) — <c>TaskActClassificationRegistryTests</c>
/// fails the build the moment a new one ships unclassified, the identical completeness gate
/// <see cref="EventScopeRegistry"/> already runs for scope itself.
/// </summary>
public static class TaskActClassificationRegistry
{
    private static readonly IReadOnlyDictionary<Type, TaskActClassification> Classifications = new Dictionary<Type, TaskActClassification>
    {
        // --- Hall9k.Domain.Features.Tasks.Events ---

        // Answering a question the running node asked is the owner's own act, directed at a
        // specific task this sender must already be the assignee or holder of.
        [typeof(AnswerProvided)] = TaskActClassification.Conditional,

        // A Jira write's own request/outcome bookkeeping — the running node's own mechanics,
        // nothing a forged copy gains from.
        [typeof(JiraMergeNoticeAttempted)] = TaskActClassification.MemberSafe,
        [typeof(JiraMergeNoticeQueued)] = TaskActClassification.MemberSafe,
        [typeof(JiraWriteFailed)] = TaskActClassification.MemberSafe,
        [typeof(JiraWriteRequested)] = TaskActClassification.MemberSafe,
        [typeof(JiraWriteSucceeded)] = TaskActClassification.MemberSafe,
        [typeof(PublicationTokensRecorded)] = TaskActClassification.MemberSafe,

        // The pull-request review pre-flight's own park (idea 6be68ee2, finding 1, phase one)
        // clears the current claim and assignment unconditionally, the identical shape
        // TaskInteractiveClaimUnassigned and TaskRequeued give a member's own give-back — a forged
        // copy from a non-holder sender must not be able to unassign a task it does not hold.
        [typeof(PrReviewPreflightParked)] = TaskActClassification.Conditional,

        // Pull-request review sightings — observations the running node recorded about GitHub's
        // own state, never a decision over who controls the task.
        [typeof(PullRequestReviewAssignmentObserved)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewAssignmentRecalled)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewAuthorResponded)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewFollowThroughObserved)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewFollowThroughOpened)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewGateParked)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewLapOpened)] = TaskActClassification.MemberSafe,
        // Giving back a mention follow-up's claim moves the task's state and run exactly as a
        // requeue does, so a forged copy from a sender who holds nothing must not apply.
        [typeof(PullRequestReviewMentionFollowUpSkipped)] = TaskActClassification.Conditional,
        [typeof(PullRequestReviewMentionObserved)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestReviewVerdictDelivered)] = TaskActClassification.MemberSafe,

        // The running node's own dialog with its owner and its own run-lifecycle bookkeeping.
        [typeof(QuestionAsked)] = TaskActClassification.MemberSafe,
        [typeof(RemoteStackedParentObserved)] = TaskActClassification.MemberSafe,
        [typeof(SpikeConcluded)] = TaskActClassification.MemberSafe,
        [typeof(StackedCheckpointRebased)] = TaskActClassification.MemberSafe,

        // A member may add and complete their own work; adding starts nothing claimable until an
        // owner assigns it, and completing is a report about a task the sender's own node is
        // already running.
        [typeof(TaskAdded)] = TaskActClassification.MemberSafe,

        // Carries its own narrower rule even though the coarse bucket is MemberSafe: see
        // EventReplicationInbox.EvaluateTaskActVerdict's own doc (idea f72138e1, a member may
        // self-assign an owner's published, unassigned task, as h9k task start does; a forged
        // reassignment of an owner's already-assigned or already-held task is refused).
        [typeof(TaskAssigned)] = TaskActClassification.MemberSafe,

        // Judged by the assignee rule, never by TaskAssigned's targetsOwnRoot (EventReplicationInbox's
        // own EvaluateTaskActVerdict): the current assignee may hand a draft to another member, and a
        // member who is not the task's owner may only take an unassigned Published task for itself.
        // Conditional rather than MemberSafe so the plain-MemberSafe shortcut never applies to either.
        [typeof(TaskAssigneeSet)] = TaskActClassification.Conditional,

        // Only from the current assignee; an Owner-role override always applies like any Owner act.
        [typeof(TaskAssigneeCleared)] = TaskActClassification.Conditional,

        // A duplicate-convergence pass abandons its own rival under its own root only
        // (PullRequestReviewDuplicateConvergence's own doc) — the run's own task must already be
        // this sender's, the same conditional check every other lifecycle-ending act here carries.
        [typeof(TaskAbandoned)] = TaskActClassification.Conditional,

        [typeof(TaskBranchPushed)] = TaskActClassification.MemberSafe,

        // Checks assignment alone (never holder, which this event is what SETS) — see
        // EventReplicationInbox.EvaluateTaskActVerdict's own doc.
        [typeof(TaskClaimed)] = TaskActClassification.Conditional,

        [typeof(TaskCompleted)] = TaskActClassification.MemberSafe,

        [typeof(TaskDependencyCompleted)] = TaskActClassification.MemberSafe,
        [typeof(TaskDependencyFailed)] = TaskActClassification.MemberSafe,
        [typeof(TaskDependencyRecovered)] = TaskActClassification.MemberSafe,

        // A node reports its own run's own outcome on its own task.
        [typeof(TaskFailed)] = TaskActClassification.MemberSafe,

        [typeof(TaskHandedBack)] = TaskActClassification.Conditional,
        [typeof(TaskHandoffNoted)] = TaskActClassification.Conditional,

        // Only the current holder may release the ledger holder lock it itself set — checks the
        // holder alone, never assignment (EventReplicationInbox.EvaluateTaskActVerdict's own doc).
        [typeof(TaskHolderReleased)] = TaskActClassification.Conditional,

        // A unilateral override of an absent holder — an owner's own judgment call, never a
        // member's (idea 202383dc, item 4).
        [typeof(TaskHolderTakenOver)] = TaskActClassification.OwnerOnly,

        // Appended on the HOLDER's own node, never the requester's (TaskDecider.RequestTake's own
        // doc: "appended on the HOLDER's own node the moment its own claim-request envelope is
        // received"; TaskDecider.RefuseTake is the holder's own answer the same way) — MemberSafe
        // let a forged copy from any member overwrite PendingTakeRequestedBy* with a fingerprint
        // TaskDecider.GrantTake would then hand the task to, contradicting Decisions Log #231's own
        // "by construction, always the verified requester" (independent pre-PR review, cycle 4,
        // adversarial lens, medium).
        [typeof(TaskTakeRequested)] = TaskActClassification.Conditional,
        [typeof(TaskTakeRefused)] = TaskActClassification.Conditional,

        [typeof(TaskInteractiveClaimUnassigned)] = TaskActClassification.Conditional,

        [typeof(TaskMechanicalResolutionAttempted)] = TaskActClassification.MemberSafe,

        [typeof(TaskPlacementChanged)] = TaskActClassification.Conditional,
        [typeof(TaskPreApprovedSet)] = TaskActClassification.Conditional,

        // Carries PreApproval, the same standing grant TaskPreApprovedSet controls elsewhere.
        [typeof(TaskPublished)] = TaskActClassification.Conditional,

        [typeof(TaskReopened)] = TaskActClassification.Conditional,
        [typeof(TaskRequeued)] = TaskActClassification.Conditional,
        [typeof(TaskResolved)] = TaskActClassification.Conditional,
        [typeof(TaskRetried)] = TaskActClassification.Conditional,
        [typeof(TaskReturnedToDraft)] = TaskActClassification.Conditional,
        [typeof(TaskReviewCapsOverridden)] = TaskActClassification.Conditional,

        // The deprecated predecessor TaskScopeSet supersedes (idea 8c5993c5) — the identical act,
        // classified the same way.
        [typeof(TaskPrivacySet)] = TaskActClassification.Conditional,
        [typeof(TaskScopeSet)] = TaskActClassification.Conditional,

        // The objective, context, model, and constraints the owner's own dispatcher would launch
        // on next — a member revising an owner's task rewrites what that owner's node will run.
        [typeof(TaskRevised)] = TaskActClassification.Conditional,

        [typeof(TaskSessionCapOverridden)] = TaskActClassification.Conditional,
        [typeof(TaskUnassigned)] = TaskActClassification.Conditional,

        [typeof(TrackerAssignmentObserved)] = TaskActClassification.MemberSafe,
        [typeof(TrackerAssignmentWritten)] = TaskActClassification.MemberSafe,
        [typeof(WorkItemLinked)] = TaskActClassification.MemberSafe,
        [typeof(WorkItemPublicationCompleted)] = TaskActClassification.MemberSafe,
        [typeof(WorkItemPublicationDispatched)] = TaskActClassification.MemberSafe,
        [typeof(WorkItemPublicationRequested)] = TaskActClassification.MemberSafe,

        // --- Hall9k.Domain.Features.Idea (card D of idea 8d0b724b) ---
        //
        // The same gate judges an idea act by the idea's own assignee, or with none its creator
        // (EventReplicationInbox.EvaluateIdeaActVerdict). Every idea event is classified, the plain
        // ones MemberSafe, so the one thing they gain is queueing behind an earlier held idea act
        // from the same origin instead of overtaking it; their verdict is unchanged, always applied.

        // Who may end the idea, and who may hand it on, are decided by the assignee rule.
        [typeof(IdeaArchived)] = TaskActClassification.Conditional,
        [typeof(IdeaConcluded)] = TaskActClassification.Conditional,
        [typeof(IdeaAssigneeSet)] = TaskActClassification.Conditional,

        // Only from the current assignee; an Owner-role override always applies like any Owner act.
        [typeof(IdeaAssigneeCleared)] = TaskActClassification.Conditional,

        // Admitted exactly as before this gate knew ideas.
        [typeof(IdeaAssignedToProject)] = TaskActClassification.MemberSafe,
        [typeof(IdeaCaptured)] = TaskActClassification.MemberSafe,
        [typeof(IdeaRevised)] = TaskActClassification.MemberSafe,
        [typeof(IdeaScopeSet)] = TaskActClassification.MemberSafe,
        // Cutting a task from an idea, and the daemon's spike verdict, stay open to any member.
        [typeof(IdeaSpikeConcluded)] = TaskActClassification.MemberSafe,
        [typeof(IdeaTaskCut)] = TaskActClassification.MemberSafe,
        // Historical events no write produces any more, replayed only from a stream that carries one.
        [typeof(IdeaDiscarded)] = TaskActClassification.MemberSafe,
        [typeof(IdeaPrivacySet)] = TaskActClassification.MemberSafe,
        [typeof(IdeaPromoted)] = TaskActClassification.MemberSafe,

        // --- Hall9k.Domain.Features.Run.Events ---

        [typeof(AdvisoryReviewThreadsObserved)] = TaskActClassification.MemberSafe,
        [typeof(ChangesRequestedReviewerRerequested)] = TaskActClassification.MemberSafe,

        // A human's own automatic-budget grant (h9k pr resolve) — the run's own task must already
        // be this sender's to act on.
        [typeof(CloseoutBudgetGranted)] = TaskActClassification.Conditional,

        [typeof(CloseoutParked)] = TaskActClassification.MemberSafe,
        [typeof(ContextSynthesisCompleted)] = TaskActClassification.MemberSafe,
        [typeof(ContextSynthesisDispatched)] = TaskActClassification.MemberSafe,
        [typeof(CopilotReviewUnavailable)] = TaskActClassification.MemberSafe,
        [typeof(ExternalInteractionLogged)] = TaskActClassification.MemberSafe,
        [typeof(ExternalReviewObserved)] = TaskActClassification.MemberSafe,
        [typeof(HumanThreadReplyParked)] = TaskActClassification.MemberSafe,
        [typeof(PrReviewConformanceCompleted)] = TaskActClassification.MemberSafe,
        [typeof(PrReviewConformanceDispatched)] = TaskActClassification.MemberSafe,
        [typeof(PrReviewDelivered)] = TaskActClassification.MemberSafe,
        [typeof(PrReviewPersonaReported)] = TaskActClassification.MemberSafe,
        [typeof(PrReviewPersonaSessionFailed)] = TaskActClassification.MemberSafe,
        [typeof(PrReviewPersonasSelected)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestAutoMergeAttempted)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestChangesRequested)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestChecksFailed)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestClosed)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestConflictObserved)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestMerged)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestOpened)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestRecordedOnFailedRun)] = TaskActClassification.MemberSafe,
        [typeof(PullRequestUpdated)] = TaskActClassification.MemberSafe,

        // h9k review proceed's bare go-ahead at an interactive-mode boundary — the run's own task
        // must already be this sender's.
        [typeof(ReviewBoundaryApproved)] = TaskActClassification.Conditional,

        [typeof(ReviewCompleted)] = TaskActClassification.MemberSafe,
        [typeof(ReviewDisagreementParked)] = TaskActClassification.MemberSafe,

        // h9k review resolve's own directed reply to a pull request's reviewer.
        [typeof(ReviewDisagreementReplyDirected)] = TaskActClassification.Conditional,

        [typeof(ReviewDispatched)] = TaskActClassification.MemberSafe,
        [typeof(ReviewEndedByMerge)] = TaskActClassification.MemberSafe,
        [typeof(ReviewErrored)] = TaskActClassification.MemberSafe,
        [typeof(ReviewFeedbackReceived)] = TaskActClassification.MemberSafe,
        [typeof(ReviewFindingRouted)] = TaskActClassification.MemberSafe,
        [typeof(ReviewFixCompleted)] = TaskActClassification.MemberSafe,
        [typeof(ReviewFixDispatched)] = TaskActClassification.MemberSafe,

        // h9k review fixed — a human took the fix role themselves on this sender's own task.
        [typeof(ReviewHumanFixApplied)] = TaskActClassification.Conditional,

        [typeof(ReviewParked)] = TaskActClassification.MemberSafe,

        // h9k review resolve's own verdict — an owner's own call (idea 6be68ee2, trust-ledger
        // finding 5's own ruling), never a delegate's.
        [typeof(ReviewParkResolved)] = TaskActClassification.OwnerOnly,

        [typeof(ReviewPassCompleted)] = TaskActClassification.MemberSafe,
        [typeof(ReviewRerequested)] = TaskActClassification.MemberSafe,
        [typeof(ReviewRerequestedAfterFixes)] = TaskActClassification.MemberSafe,
        [typeof(ReviewSettled)] = TaskActClassification.MemberSafe,
        [typeof(ReviewThreadReplyPosted)] = TaskActClassification.MemberSafe,
        [typeof(ReviewThreadReplyRefused)] = TaskActClassification.MemberSafe,
        [typeof(ReviewThreadsTriaged)] = TaskActClassification.MemberSafe,
        [typeof(ReviewTrackConcluded)] = TaskActClassification.MemberSafe,

        // The mandatory final-full-pass reawakening a concluded track — the run's own task must
        // already be this sender's.
        [typeof(ReviewTrackReactivated)] = TaskActClassification.Conditional,

        [typeof(ReviewVerdictReprompted)] = TaskActClassification.MemberSafe,
        [typeof(RunBudgetExhausted)] = TaskActClassification.MemberSafe,
        [typeof(RunCompleted)] = TaskActClassification.MemberSafe,
        [typeof(RunDeliveredAutomatically)] = TaskActClassification.MemberSafe,
        [typeof(RunDispatched)] = TaskActClassification.MemberSafe,
        [typeof(RunFailed)] = TaskActClassification.MemberSafe,
        [typeof(RunHandoffRecorded)] = TaskActClassification.MemberSafe,
        [typeof(RunKilled)] = TaskActClassification.MemberSafe,
        [typeof(RunPermissionDenialsRecorded)] = TaskActClassification.MemberSafe,
        [typeof(RunPhaseDelegated)] = TaskActClassification.MemberSafe,
        [typeof(RunRebasedOntoBase)] = TaskActClassification.MemberSafe,

        // Fleet-wide reconciliation, deliberately never node- or owner-scoped on its own terms
        // (RunRecordReconstructed's own doc in EventScopeRegistry) — the identical reasoning
        // applies here: any project member may report the fact.
        [typeof(RunRecordReconstructed)] = TaskActClassification.MemberSafe,

        [typeof(RunStartedCleanAfterBranchGone)] = TaskActClassification.MemberSafe,
        [typeof(RunSuperseded)] = TaskActClassification.MemberSafe,
        [typeof(StackedPullRequestRetargeted)] = TaskActClassification.MemberSafe,
        [typeof(TokensRecorded)] = TaskActClassification.MemberSafe,
        [typeof(VerificationFailed)] = TaskActClassification.MemberSafe,
        [typeof(VerificationPassed)] = TaskActClassification.MemberSafe,
        [typeof(VerificationSkipped)] = TaskActClassification.MemberSafe,
    };

    /// <summary>The classified event types, for a completeness test to enumerate against.</summary>
    public static IReadOnlyCollection<Type> KnownEventTypes => [.. Classifications.Keys];

    /// <summary>Null for any event type this registry does not classify at all — either it is not
    /// a Task/Run act (a different feature's own event, or a NodeScoped Task/Run event this gate
    /// never runs for), or it genuinely shipped unclassified, which
    /// <c>TaskActClassificationRegistryTests</c> fails the build over.</summary>
    public static TaskActClassification? TryClassificationOf(Type eventType) =>
        Classifications.TryGetValue(eventType, out TaskActClassification classification) ? classification : null;
}
