using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Every event type this platform appends, classified <see cref="EventScope.ProjectScoped"/>,
/// <see cref="EventScope.NodeScoped"/>, or <see cref="EventScope.OwnerScoped"/> (idea 202383dc,
/// ruled 2026-09-12 and 2026-09-13) — the decision log's own classification table, PLAN.md §16.
/// A plain list, not a policy engine: the M2 replication task reads it to decide which events an
/// outbox carries and which never leave the node that appended them. <see cref="ClassificationOf"/>
/// throws for any type this list has not named — <c>EventScopeRegistryTests</c> fails the build
/// the moment a new event type ships without an entry here, so a new type can never be forgotten.
/// <para>
/// The run event types are classified per the 2026-09-13 ruling verbatim: facts about the work
/// travel (dispatched, pull request opened, updated and merged, review dispatched, completed,
/// parked, fixes, verification, tokens recorded, failed, handoff recorded); machine mechanics
/// stay (process started, launch held, gate retried, session error retried, interactive session
/// started and ended, recovery attempts). Every other Run event is classified by the same
/// question the ruling asks of the named ones: a team-visible fact about the pull request, the
/// review, or the run's own outcome travels; this node's own process, session, or local-repair
/// mechanics stays.
/// </para>
/// </summary>
public static class EventScopeRegistry
{
    private static readonly IReadOnlyDictionary<Type, EventScope> Classifications = new Dictionary<Type, EventScope>
    {
        // Hall9k.Domain.Features.Tasks.Events — every task event travels (idea 202383dc: "task
        // and idea streams travel, drafts included").
        [typeof(AnswerProvided)] = EventScope.ProjectScoped,
        [typeof(JiraMergeNoticeAttempted)] = EventScope.ProjectScoped,
        [typeof(JiraMergeNoticeQueued)] = EventScope.ProjectScoped,
        [typeof(JiraWriteFailed)] = EventScope.ProjectScoped,
        [typeof(JiraWriteRequested)] = EventScope.ProjectScoped,
        [typeof(JiraWriteSucceeded)] = EventScope.ProjectScoped,
        [typeof(PublicationTokensRecorded)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewAssignmentObserved)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewAssignmentRecalled)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewAuthorResponded)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewFollowThroughObserved)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewFollowThroughOpened)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewLapOpened)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewMentionObserved)] = EventScope.ProjectScoped,
        [typeof(PullRequestReviewVerdictDelivered)] = EventScope.ProjectScoped,
        [typeof(QuestionAsked)] = EventScope.ProjectScoped,
        [typeof(RemoteStackedParentObserved)] = EventScope.ProjectScoped,
        [typeof(StackedCheckpointRebased)] = EventScope.ProjectScoped,
        [typeof(TaskAbandoned)] = EventScope.ProjectScoped,
        [typeof(TaskAdded)] = EventScope.ProjectScoped,
        [typeof(TaskAssigned)] = EventScope.ProjectScoped,
        [typeof(TaskClaimed)] = EventScope.ProjectScoped,
        [typeof(TaskCompleted)] = EventScope.ProjectScoped,
        [typeof(TaskDependencyCompleted)] = EventScope.ProjectScoped,
        [typeof(TaskDependencyFailed)] = EventScope.ProjectScoped,
        [typeof(TaskDependencyRecovered)] = EventScope.ProjectScoped,
        [typeof(TaskFailed)] = EventScope.ProjectScoped,
        [typeof(TaskHandedBack)] = EventScope.ProjectScoped,
        [typeof(TaskInteractiveClaimUnassigned)] = EventScope.ProjectScoped,
        [typeof(TaskMechanicalResolutionAttempted)] = EventScope.ProjectScoped,
        [typeof(TaskPreApprovedSet)] = EventScope.ProjectScoped,
        [typeof(TaskPublished)] = EventScope.ProjectScoped,
        [typeof(TaskReopened)] = EventScope.ProjectScoped,
        [typeof(TaskRequeued)] = EventScope.ProjectScoped,
        [typeof(TaskResolved)] = EventScope.ProjectScoped,
        [typeof(TaskRetried)] = EventScope.ProjectScoped,
        [typeof(TaskReturnedToDraft)] = EventScope.ProjectScoped,
        [typeof(TaskReviewCapsOverridden)] = EventScope.ProjectScoped,
        [typeof(TaskRevised)] = EventScope.ProjectScoped,
        [typeof(TaskSessionCapOverridden)] = EventScope.ProjectScoped,
        [typeof(TaskUnassigned)] = EventScope.ProjectScoped,
        [typeof(TrackerAssignmentObserved)] = EventScope.ProjectScoped,
        [typeof(TrackerAssignmentWritten)] = EventScope.ProjectScoped,
        [typeof(WorkItemLinked)] = EventScope.ProjectScoped,
        [typeof(WorkItemPublicationCompleted)] = EventScope.ProjectScoped,
        [typeof(WorkItemPublicationDispatched)] = EventScope.ProjectScoped,
        [typeof(WorkItemPublicationRequested)] = EventScope.ProjectScoped,

        // Records this process's own pid and start time (session/mechanics bookkeeping, the
        // same shape as RunProcessStarted below), not a team-visible work fact, so it stays
        // node-scoped rather than following the rest of this stream's own "every task event
        // travels" default (idea 202383dc's "drafts included" ruling is about which tasks
        // travel, not about a liveness marker no other node has any use for).
        [typeof(WorkItemPublicationSessionStarted)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Run.Events — facts about the work (project-scoped) vs machine
        // mechanics (node-scoped), per the 2026-09-13 ruling.
        [typeof(AgentSessionCompleted)] = EventScope.NodeScoped,
        [typeof(ChangesRequestedReviewerRerequested)] = EventScope.ProjectScoped,
        [typeof(CloseoutBudgetGranted)] = EventScope.ProjectScoped,
        [typeof(CloseoutParked)] = EventScope.ProjectScoped,
        [typeof(ContextSynthesisCompleted)] = EventScope.ProjectScoped,
        [typeof(ContextSynthesisDispatched)] = EventScope.ProjectScoped,
        [typeof(ExternalInteractionLogged)] = EventScope.ProjectScoped,
        [typeof(ExternalReviewObserved)] = EventScope.ProjectScoped,
        [typeof(GateRetried)] = EventScope.NodeScoped,
        [typeof(InteractiveSessionEnded)] = EventScope.NodeScoped,
        [typeof(InteractiveSessionStarted)] = EventScope.NodeScoped,
        [typeof(PreFinalPassRebaseRecoveryCompleted)] = EventScope.NodeScoped,
        [typeof(PreFinalPassRebaseRecoveryDispatched)] = EventScope.NodeScoped,
        [typeof(PrReviewConformanceCompleted)] = EventScope.ProjectScoped,
        [typeof(PrReviewConformanceDispatched)] = EventScope.ProjectScoped,
        [typeof(PrReviewDelivered)] = EventScope.ProjectScoped,
        [typeof(PullRequestAutoMergeAttempted)] = EventScope.ProjectScoped,
        [typeof(PullRequestChangesRequested)] = EventScope.ProjectScoped,
        [typeof(PullRequestChecksFailed)] = EventScope.ProjectScoped,
        [typeof(PullRequestClosed)] = EventScope.ProjectScoped,
        [typeof(PullRequestConflictObserved)] = EventScope.ProjectScoped,
        [typeof(PullRequestMechanicalRebaseAttempted)] = EventScope.NodeScoped,
        [typeof(PullRequestMerged)] = EventScope.ProjectScoped,
        [typeof(PullRequestOpened)] = EventScope.ProjectScoped,
        [typeof(PullRequestRecordedOnFailedRun)] = EventScope.ProjectScoped,
        [typeof(PullRequestUpdated)] = EventScope.ProjectScoped,
        [typeof(ReviewBoundaryApproved)] = EventScope.ProjectScoped,
        [typeof(ReviewCompleted)] = EventScope.ProjectScoped,
        [typeof(ReviewDisagreementParked)] = EventScope.ProjectScoped,
        [typeof(ReviewDisagreementReplyDirected)] = EventScope.ProjectScoped,
        [typeof(ReviewDispatched)] = EventScope.ProjectScoped,
        [typeof(ReviewEndedByMerge)] = EventScope.ProjectScoped,
        [typeof(ReviewErrored)] = EventScope.ProjectScoped,
        [typeof(ReviewFeedbackReceived)] = EventScope.ProjectScoped,
        [typeof(ReviewFindingRouted)] = EventScope.ProjectScoped,
        [typeof(ReviewFixCompleted)] = EventScope.ProjectScoped,
        [typeof(ReviewFixDispatched)] = EventScope.ProjectScoped,
        [typeof(ReviewHumanFixApplied)] = EventScope.ProjectScoped,
        [typeof(ReviewParked)] = EventScope.ProjectScoped,
        [typeof(ReviewParkResolved)] = EventScope.ProjectScoped,
        [typeof(ReviewPassCompleted)] = EventScope.ProjectScoped,
        [typeof(ReviewRerequested)] = EventScope.ProjectScoped,
        [typeof(ReviewRerequestedAfterFixes)] = EventScope.ProjectScoped,
        [typeof(ReviewSettled)] = EventScope.ProjectScoped,
        [typeof(ReviewThreadsTriaged)] = EventScope.ProjectScoped,
        [typeof(ReviewTrackConcluded)] = EventScope.ProjectScoped,
        [typeof(ReviewTrackReactivated)] = EventScope.ProjectScoped,
        [typeof(ReviewVerdictReprompted)] = EventScope.ProjectScoped,
        [typeof(RunBudgetExhausted)] = EventScope.ProjectScoped,
        [typeof(RunCompleted)] = EventScope.ProjectScoped,
        [typeof(RunDeliveredAutomatically)] = EventScope.ProjectScoped,
        [typeof(RunDispatched)] = EventScope.ProjectScoped,
        [typeof(RunFailed)] = EventScope.ProjectScoped,
        [typeof(RunHandoffRecorded)] = EventScope.ProjectScoped,
        [typeof(RunKilled)] = EventScope.ProjectScoped,
        [typeof(RunLaunchHeld)] = EventScope.NodeScoped,
        [typeof(RunPhaseDelegated)] = EventScope.ProjectScoped,
        [typeof(RunProcessStarted)] = EventScope.NodeScoped,
        [typeof(RunRebasedOntoBase)] = EventScope.ProjectScoped,
        [typeof(RunRecordReconstructed)] = EventScope.NodeScoped,
        [typeof(RunResumed)] = EventScope.NodeScoped,
        [typeof(RunSessionErrorRetried)] = EventScope.NodeScoped,
        [typeof(RunSuperseded)] = EventScope.ProjectScoped,
        [typeof(RunUnattendedExitFlagged)] = EventScope.NodeScoped,
        [typeof(RunUncommittedWorkRecoveryAttempted)] = EventScope.NodeScoped,
        [typeof(RunUncommittedWorkRecoveryCompleted)] = EventScope.NodeScoped,
        [typeof(SettlingGateRepairCapReached)] = EventScope.NodeScoped,
        [typeof(SettlingGateRepairCompleted)] = EventScope.NodeScoped,
        [typeof(SettlingGateRepairDispatched)] = EventScope.NodeScoped,
        [typeof(StackedPullRequestRetargeted)] = EventScope.ProjectScoped,
        [typeof(TokensRecorded)] = EventScope.ProjectScoped,
        [typeof(VerificationFailed)] = EventScope.ProjectScoped,
        [typeof(VerificationPassed)] = EventScope.ProjectScoped,

        // Hall9k.Domain.Features.Project.Events — the project's own identity travels; its
        // settings stay node-scoped until M2 splits ProjectSettingsChanged into the team part
        // (travels) and the node part (parallel caps, model choices — stays) the 2026-09-13
        // ruling names. One event still carries both today, so it is classified NodeScoped
        // rather than leaking a node's own local settings out ahead of that split.
        [typeof(ProjectArchived)] = EventScope.ProjectScoped,
        [typeof(ProjectPurgeCancelled)] = EventScope.ProjectScoped,
        [typeof(ProjectPurgeScheduled)] = EventScope.ProjectScoped,
        [typeof(ProjectReactivated)] = EventScope.ProjectScoped,
        [typeof(ProjectRegistered)] = EventScope.ProjectScoped,
        [typeof(ProjectRenamed)] = EventScope.ProjectScoped,
        [typeof(ProjectSettingsChanged)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Owner — an owner's own cross-node identity.
        [typeof(OwnerRegistered)] = EventScope.OwnerScoped,
        [typeof(OwnerRootClaimed)] = EventScope.OwnerScoped,
        [typeof(OwnerSettingsChanged)] = EventScope.OwnerScoped,

        // Hall9k.Domain.Features.Node — this node's own identity, key, and holds ("holds" is
        // named node-scoped in idea 202383dc's own list).
        [typeof(NodeKeyRegistered)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldCleared)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldProbed)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldRaised)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldRunHeld)] = EventScope.NodeScoped,
        [typeof(NodeOwnerClaimed)] = EventScope.NodeScoped,
        [typeof(NodeRegistered)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Message — idea 202383dc, M1a: messages are ephemeral, ruled
        // 2026-09-13 ("read receipts and bookmark announcements are dead ... messages are
        // ephemeral"). Every message and inbox event stays on the node that appended it; only
        // project-scoped facts (the task or idea a message is about) ever travel, and never
        // through a message itself.
        [typeof(MessageSent)] = EventScope.NodeScoped,
        [typeof(MessageSendFailed)] = EventScope.NodeScoped,
        [typeof(MessageResent)] = EventScope.NodeScoped,
        [typeof(MessageReceived)] = EventScope.NodeScoped,
        [typeof(MessageHandled)] = EventScope.NodeScoped,
        [typeof(InboxCursorAdvanced)] = EventScope.NodeScoped,
        [typeof(InboxSenderIgnored)] = EventScope.NodeScoped,
        [typeof(InboxSenderVouched)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Connection — a node's own registered credential; never
        // replicated (Guid tokens and gh CLI logins are inherently local to the machine).
        [typeof(ConnectionRegistered)] = EventScope.NodeScoped,
        [typeof(ConnectionReregistered)] = EventScope.NodeScoped,
        [typeof(ConnectionTrackerIdentityObserved)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Epic — a work-organization concept over tasks; team-visible.
        [typeof(EpicAdded)] = EventScope.ProjectScoped,
        [typeof(EpicClosed)] = EventScope.ProjectScoped,
        [typeof(EpicLinkedToJira)] = EventScope.ProjectScoped,

        // Hall9k.Domain.Features.Idea — idea 202383dc: "task and idea streams travel, drafts
        // included", each with a private flag that stops travel until cleared (an M2 concern,
        // not a different scope).
        [typeof(IdeaArchived)] = EventScope.ProjectScoped,
        [typeof(IdeaAssignedToProject)] = EventScope.ProjectScoped,
        [typeof(IdeaCaptured)] = EventScope.ProjectScoped,
        [typeof(IdeaConcluded)] = EventScope.ProjectScoped,
        [typeof(IdeaDiscarded)] = EventScope.ProjectScoped,
        [typeof(IdeaPromoted)] = EventScope.ProjectScoped,
        [typeof(IdeaRevised)] = EventScope.ProjectScoped,
        [typeof(IdeaTaskCut)] = EventScope.ProjectScoped,
    };

    /// <summary>The classified event types, for a completeness test to enumerate against.</summary>
    public static IReadOnlyCollection<Type> KnownEventTypes => [.. Classifications.Keys];

    public static EventScope ClassificationOf(Type eventType) =>
        Classifications.TryGetValue(eventType, out EventScope scope)
            ? scope
            : throw new InvalidOperationException(
                $"Event type {eventType.FullName} is not classified in {nameof(EventScopeRegistry)}. " +
                "Every event type travels project-scoped, stays node-scoped, or stays owner-scoped " +
                "(idea 202383dc) — add it there before it ships.");
}
