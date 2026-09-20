using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Trust;

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
        // A spike's own verdict (task: a spike is a run, not a walk) — a team-visible fact about
        // the task's own outcome, the same tier every other task-stream event here travels at.
        [typeof(SpikeConcluded)] = EventScope.ProjectScoped,
        [typeof(StackedCheckpointRebased)] = EventScope.ProjectScoped,
        [typeof(TaskAbandoned)] = EventScope.ProjectScoped,
        [typeof(TaskAdded)] = EventScope.ProjectScoped,
        [typeof(TaskAssigned)] = EventScope.ProjectScoped,
        [typeof(TaskBranchPushed)] = EventScope.ProjectScoped,
        [typeof(TaskClaimed)] = EventScope.ProjectScoped,
        [typeof(TaskCompleted)] = EventScope.ProjectScoped,
        [typeof(TaskDependencyCompleted)] = EventScope.ProjectScoped,
        [typeof(TaskDependencyFailed)] = EventScope.ProjectScoped,
        [typeof(TaskDependencyRecovered)] = EventScope.ProjectScoped,
        [typeof(TaskFailed)] = EventScope.ProjectScoped,
        [typeof(TaskHandedBack)] = EventScope.ProjectScoped,
        [typeof(TaskHandoffNoted)] = EventScope.ProjectScoped,
        [typeof(TaskHolderReleased)] = EventScope.ProjectScoped,
        [typeof(TaskHolderTakenOver)] = EventScope.ProjectScoped,
        [typeof(TaskTakeRequested)] = EventScope.ProjectScoped,
        [typeof(TaskTakeRefused)] = EventScope.ProjectScoped,
        [typeof(TaskInteractiveClaimUnassigned)] = EventScope.ProjectScoped,
        [typeof(TaskMechanicalResolutionAttempted)] = EventScope.ProjectScoped,
        [typeof(TaskPlacementChanged)] = EventScope.ProjectScoped,
        [typeof(TaskPreApprovedSet)] = EventScope.ProjectScoped,
        [typeof(TaskPublished)] = EventScope.ProjectScoped,
        [typeof(TaskReopened)] = EventScope.ProjectScoped,
        [typeof(TaskRequeued)] = EventScope.ProjectScoped,
        [typeof(TaskResolved)] = EventScope.ProjectScoped,
        [typeof(TaskRetried)] = EventScope.ProjectScoped,
        [typeof(TaskReturnedToDraft)] = EventScope.ProjectScoped,
        [typeof(TaskReviewCapsOverridden)] = EventScope.ProjectScoped,
        [typeof(TaskPrivacySet)] = EventScope.ProjectScoped,
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
        [typeof(AdvisoryReviewThreadsObserved)] = EventScope.ProjectScoped,
        [typeof(AgentSessionCompleted)] = EventScope.NodeScoped,
        [typeof(ChangesRequestedReviewerRerequested)] = EventScope.ProjectScoped,
        [typeof(CloseoutBudgetGranted)] = EventScope.ProjectScoped,
        [typeof(CloseoutParked)] = EventScope.ProjectScoped,
        [typeof(ContextSynthesisCompleted)] = EventScope.ProjectScoped,
        [typeof(ContextSynthesisDispatched)] = EventScope.ProjectScoped,
        [typeof(CopilotReviewUnavailable)] = EventScope.ProjectScoped,
        [typeof(ExternalInteractionLogged)] = EventScope.ProjectScoped,
        [typeof(ExternalReviewObserved)] = EventScope.ProjectScoped,
        // A gate's own process identity, the identical liveness-marker shape RunProcessStarted
        // and GateRetried already stay node-scoped for: no other node has any use for which pid
        // this node's own build or test process is running under.
        [typeof(GateEnded)] = EventScope.NodeScoped,
        [typeof(GateRetried)] = EventScope.NodeScoped,
        [typeof(GateStarted)] = EventScope.NodeScoped,
        [typeof(HumanThreadReplyParked)] = EventScope.ProjectScoped,
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
        [typeof(ReviewThreadReplyPosted)] = EventScope.ProjectScoped,
        [typeof(ReviewThreadReplyRefused)] = EventScope.ProjectScoped,
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
        // The host-coupled-gate permit's own start/clear, the identical liveness-marker shape
        // GateStarted/GateEnded already stay node-scoped for: no other node has any use for
        // which run on THIS node is waiting on, or holding, this node's own permit file
        // (#225).
        [typeof(RunHostCoupledGateWaitEnded)] = EventScope.NodeScoped,
        [typeof(RunHostCoupledGateWaitStarted)] = EventScope.NodeScoped,
        [typeof(RunKilled)] = EventScope.ProjectScoped,
        [typeof(RunLaunchHeld)] = EventScope.NodeScoped,
        [typeof(RunPhaseDelegated)] = EventScope.ProjectScoped,
        [typeof(RunProcessStarted)] = EventScope.NodeScoped,
        [typeof(RunRebasedOntoBase)] = EventScope.ProjectScoped,
        [typeof(RunRecordReconstructed)] = EventScope.NodeScoped,
        [typeof(RunResumed)] = EventScope.NodeScoped,
        [typeof(RunSessionErrorRetried)] = EventScope.NodeScoped,
        // Travels, unlike the local-repair mechanics around it: "the branch your run built is
        // gone and this run started over without it" is a fact about the work itself, and the
        // node most likely to care is the one whose own run left the branch behind
        // (#234).
        [typeof(RunStartedCleanAfterBranchGone)] = EventScope.ProjectScoped,
        [typeof(RunSuperseded)] = EventScope.ProjectScoped,
        [typeof(RunUnattendedExitFlagged)] = EventScope.NodeScoped,
        [typeof(RunUncommittedWorkRecoveryAttempted)] = EventScope.NodeScoped,
        [typeof(RunUncommittedWorkRecoveryCompleted)] = EventScope.NodeScoped,
        [typeof(SettlingGateRepairCapReached)] = EventScope.NodeScoped,
        [typeof(SettlingGateRepairCompleted)] = EventScope.NodeScoped,
        [typeof(SettlingGateRepairDispatched)] = EventScope.NodeScoped,
        [typeof(StackAssessmentCompleted)] = EventScope.NodeScoped,
        [typeof(StackAssessmentDispatched)] = EventScope.NodeScoped,
        [typeof(StackedPullRequestRetargeted)] = EventScope.ProjectScoped,
        [typeof(TokensRecorded)] = EventScope.ProjectScoped,
        [typeof(VerificationFailed)] = EventScope.ProjectScoped,
        [typeof(VerificationPassed)] = EventScope.ProjectScoped,

        // Hall9k.Domain.Features.Project.Events — the project's own identity travels; its
        // settings split at M2a (idea 202383dc): ProjectSettingsChanged keeps every field for
        // replay but is node-scoped from here on (parallel caps, model choices, orchestrator
        // model, and this install's own filesystem paths — never leaked to another node), and the
        // new ProjectTeamSettingsChanged (the claim gate, branch template, backlog policy, review
        // settings, writing conventions) is what actually travels.
        [typeof(ProjectArchived)] = EventScope.ProjectScoped,
        [typeof(ProjectPurgeCancelled)] = EventScope.ProjectScoped,
        [typeof(ProjectPurgeScheduled)] = EventScope.ProjectScoped,
        [typeof(ProjectReactivated)] = EventScope.ProjectScoped,
        [typeof(ProjectRegistered)] = EventScope.ProjectScoped,
        [typeof(ProjectRenamed)] = EventScope.ProjectScoped,
        [typeof(ProjectSettingsChanged)] = EventScope.NodeScoped,
        [typeof(ProjectTeamSettingsChanged)] = EventScope.ProjectScoped,
        // idea 202383dc, M2, Brian's ruling 2026-09-17: this install's own local mirror of the
        // project's ledger-derived key — a fact every install re-derives from the identical ledger
        // itself (h9k project join, h9k project assign-key), never a team decision to replicate.
        [typeof(ProjectKeyAssigned)] = EventScope.NodeScoped,

        // idea b9b09779, piece 6: a project's own prompt addenda are team-visible guidance, the
        // same tier as ProjectTeamSettingsChanged — travels once the distributed-team chain
        // replicates it, even though today the ledger (never this event) is what actually carries
        // an addendum to a fellow member's node.
        [typeof(ProjectPromptAddendumSet)] = EventScope.ProjectScoped,
        [typeof(ProjectPromptAddendumRemoved)] = EventScope.ProjectScoped,

        // GitHub access observed through this install's own connected account (idea 202383dc,
        // A2b): both are what THIS node's own gh call saw, through THIS node's own registered
        // credential, never a canonical team fact another node's differently-connected account
        // would reproduce identically — the same reasoning that keeps every Connection event
        // below node-scoped, applied to the project-stream events that ride on the identical gh
        // round trip.
        [typeof(ProjectGitHubAccessObserved)] = EventScope.NodeScoped,
        [typeof(ProjectGitHubCollaboratorsObserved)] = EventScope.NodeScoped,

        // idea 202383dc, T1: project membership is team-facing (the a2-team-half-walk-2026-09-13
        // ruling's own words), so it travels with this project's other project-scoped streams.
        [typeof(MemberVouched)] = EventScope.ProjectScoped,
        [typeof(MemberRemoved)] = EventScope.ProjectScoped,

        // Hall9k.Domain.Features.Owner — an owner's own cross-node identity.
        [typeof(OwnerRegistered)] = EventScope.OwnerScoped,
        [typeof(OwnerRootClaimed)] = EventScope.OwnerScoped,
        [typeof(OwnerSettingsChanged)] = EventScope.OwnerScoped,
        // idea 202383dc, T1: node vouches are owner-scoped — "other nodes learn them from the
        // files" (the ruling's own words), never from this event.
        [typeof(NodeVouched)] = EventScope.OwnerScoped,
        [typeof(NodeRevoked)] = EventScope.OwnerScoped,

        // Hall9k.Domain.Features.Node — this node's own identity, key, and holds ("holds" is
        // named node-scoped in idea 202383dc's own list).
        [typeof(NodeKeyRegistered)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldCleared)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldProbed)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldRaised)] = EventScope.NodeScoped,
        [typeof(NodeLaunchHoldRunHeld)] = EventScope.NodeScoped,
        [typeof(NodeOwnerClaimed)] = EventScope.NodeScoped,
        [typeof(NodeRegistered)] = EventScope.NodeScoped,
        // idea 202383dc, M2b, task 9408d525: this node's own record of who invited it in — a fact
        // about this install alone, never a team fact, and meaningless read from another node.
        [typeof(NodeInviterRecorded)] = EventScope.NodeScoped,
        // idea 202383dc, M2a: this node's own switch-on point — never a team fact, and reading it
        // from another node would be meaningless (each node's own global sequence is local).
        [typeof(ReplicationSwitchedOn)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Orchestrator — idea 89471598, piece 1: whether an orchestrator
        // window is up for a project is a fact about one machine's own process table, and no
        // other node can check it or act on it. The identical reasoning InteractiveSessionStarted
        // and RunProcessStarted already stay node-scoped for, applied to the window over the
        // project rather than to a run's own session.
        [typeof(OrchestratorLaunched)] = EventScope.NodeScoped,
        [typeof(OrchestratorShutDown)] = EventScope.NodeScoped,
        [typeof(OrchestratorLost)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Message — idea 202383dc, M1a: messages are ephemeral, ruled
        // 2026-09-13 ("read receipts and bookmark announcements are dead ... messages are
        // ephemeral"). Every message and inbox event stays on the node that appended it; only
        // project-scoped facts (the task or idea a message is about) ever travel, and never
        // through a message itself.
        [typeof(MessageQueued)] = EventScope.NodeScoped,
        [typeof(MessageSent)] = EventScope.NodeScoped,
        [typeof(MessageSendFailed)] = EventScope.NodeScoped,
        [typeof(MessageResent)] = EventScope.NodeScoped,
        [typeof(MessageReceived)] = EventScope.NodeScoped,
        [typeof(MessageHandled)] = EventScope.NodeScoped,
        [typeof(InboxCursorAdvanced)] = EventScope.NodeScoped,
        [typeof(InboxSenderIgnored)] = EventScope.NodeScoped,
        [typeof(InboxSenderVouched)] = EventScope.NodeScoped,

        // This install's own permanent legacy-adoption decision (LegacyMessageAdoptionAssigned's
        // own doc) — a purely local migration bookkeeping fact about which of THIS node's own
        // projects claimed the pre-M2 backlog, never a team-visible fact another node's own sweep
        // would reach identically, the identical reasoning every other message event above already
        // carries.
        [typeof(LegacyMessageAdoptionAssigned)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Trust — this node's own message sweep's own sighting of a
        // writer its ledger chain read could not verify (idea 202383dc, T1 criterion 3). The
        // identical reasoning the message events just above already carry: this is a local
        // observation the sweep persists so h9k status can name the writer without a live
        // ledger walk, never a team-visible fact replicated from here — every other node's own
        // sweep reaches the identical conclusion by reading the ledger itself.
        [typeof(UnverifiedLedgerWriteObserved)] = EventScope.NodeScoped,
        [typeof(UnverifiedLedgerWriteResolved)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Invite — idea 202383dc, T2: an invite's own plaintext secret is
        // kept only in the minting node's own store, never the ledger and never replicated; the
        // ledger's owners/<root>/invites/<id>.yaml file (hash, claim, role, expiry, spent) is the
        // team-visible fact, written and updated directly by ILedger, never through this stream.
        [typeof(InviteMinted)] = EventScope.NodeScoped,
        [typeof(InviteSpent)] = EventScope.NodeScoped,

        // The sweep's own record of which projects an invite's vouch write already landed into —
        // this node's sole "already mine" signal (independent pre-PR review, cycle 6, adversarial
        // lens, high), never replicated for the same reason as the other two.
        [typeof(InviteProjectVouched)] = EventScope.NodeScoped,

        // Hall9k.Domain.Features.Connection — a node's own registered credential; never
        // replicated (Guid tokens and gh CLI logins are inherently local to the machine).
        [typeof(ConnectionRegistered)] = EventScope.NodeScoped,
        [typeof(ConnectionReregistered)] = EventScope.NodeScoped,
        [typeof(ConnectionTrackerIdentityObserved)] = EventScope.NodeScoped,
        [typeof(ConnectionGitHubIdentityObserved)] = EventScope.NodeScoped,

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
        [typeof(IdeaPrivacySet)] = EventScope.ProjectScoped,
        [typeof(IdeaPromoted)] = EventScope.ProjectScoped,
        [typeof(IdeaRevised)] = EventScope.ProjectScoped,
        [typeof(IdeaTaskCut)] = EventScope.ProjectScoped,
        // The idea-side half of a spike's own verdict (task: a spike is a run, not a walk) —
        // provenance of a team-visible fact, the same tier IdeaTaskCut already travels at.
        [typeof(IdeaSpikeConcluded)] = EventScope.ProjectScoped,
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
