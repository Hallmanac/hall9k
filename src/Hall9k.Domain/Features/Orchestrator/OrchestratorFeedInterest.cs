using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Trust;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// The orchestrator feed's interest filter (idea 89471598, piece 2): one table from event type to
/// the <see cref="OrchestratorFeedLevel"/> band that event belongs to, and nothing else but the
/// exceptions listed below. No model reads it, no heuristic widens it, and an event type lands in
/// the same band on every node unless one of those exceptions applies, which is the whole point,
/// because piece 3's courier and a window's own start-up drain must agree exactly about what an
/// item is.
/// <para>
/// <b>An unnamed type is not in the feed at all.</b> Unlike
/// <see cref="Infrastructure.Persistence.EventScopeRegistry"/>, which a test forces to name every
/// event type this platform appends, this is a curated interest list: the feed is what an
/// orchestrator would want to know, not a mirror of the log. A new event type therefore ships
/// silent here by default, and adding it is a deliberate act. <c>OrchestratorFeedInterestTests</c>
/// still checks every type named below against that registry, so a rename or a retirement
/// cannot leave a dangling entry behind.
/// </para>
/// <para>
/// <b>Four entries also read something beyond the type</b> (<see cref="Admits(object)"/>), and
/// all four only ever decide whether the event is an item at all, never which band it lands in.
/// <see cref="MessageReceived"/> is admitted only for a message
/// from a person or another node's window, never for the JSON payloads the daemon's own reactors
/// exchange (<see cref="MessageKind.MechanicalKindValues"/>) — the identical rule <c>h9k messages</c>
/// and <c>h9k status</c>'s own unread count already apply. <see cref="PullRequestAutoMergeAttempted"/>
/// is admitted only for an attempt that failed: a pre-approved merge the daemon completed on its
/// own asks nothing of anybody, and "a merge that stays failed" is the actionable half.
/// <see cref="RunRecordReconstructed"/> is admitted only when THIS node minted it: a peer's own
/// daemon rebuilt nothing, so a replicated copy of a teammate's reconstruction is never this
/// node's own thing to act on — and with it now travelling project-scoped, a backlog of them
/// landing at once (task: a run stream whose first event is a reconstruction) must not page this
/// node's own window for work it never touched. <see cref="PullRequestReviewAuthorResponded"/>
/// takes the same origin gate for the same reason: the watch that recorded it is this node's own,
/// so a teammate's node holding a replicated copy of the owner's review task has nobody on it to
/// tell that the owner's review moved.
/// </para>
/// <para>
/// <b>One entry's band depends on more than its type.</b> <see cref="TaskAbandoned"/> and
/// <see cref="TaskResolved"/> are Transitions on this table, but a replicated one recorded under a
/// different owner root than this node's, on a task this node's owner root may act on, is
/// Actionable and urgent at every level: another owner ended the reader's own task, and a window
/// reading at Actionable would otherwise never hear of it. That reads the event's origin root and
/// the task's owner, so it is decided in <see cref="OrchestratorFeedSelection"/> through
/// <see cref="OrchestratorFeedOwnership"/>, not by <see cref="BandOf"/>. The same end recorded under
/// this owner's own root (one of the owner's other nodes) stays the Transitions line it is here.
/// </para>
/// </summary>
public static class OrchestratorFeedInterest
{
    private static readonly IReadOnlyDictionary<Type, OrchestratorFeedLevel> Bands = new Dictionary<Type, OrchestratorFeedLevel>
    {
        // ─── Actionable: somebody is owed something. ────────────────────────────────────────────

        // Parks and disputes. Every one of these is a recorded stop with a human's name on the
        // next move — the same set AttentionComposer already renders as NeedsYou.
        [typeof(ReviewParked)] = OrchestratorFeedLevel.Actionable,
        [typeof(CloseoutParked)] = OrchestratorFeedLevel.Actionable,
        [typeof(ReviewDisagreementParked)] = OrchestratorFeedLevel.Actionable,
        [typeof(HumanThreadReplyParked)] = OrchestratorFeedLevel.Actionable,
        // The enforcement half of the park above (AGENTS.md's git rules, §16 #62/#152/#159): a
        // session reached for h9k pr reply on a person's own thread and was refused. An operator
        // who cannot see the refusal cannot tell it from a session that behaved.
        [typeof(ReviewThreadReplyRefused)] = OrchestratorFeedLevel.Actionable,
        // A dispute the loop declined to settle itself: the finding left this pull request onto a
        // draft nobody has published (Decisions Log #63), or the routing itself failed.
        [typeof(ReviewFindingRouted)] = OrchestratorFeedLevel.Actionable,
        // The ask-and-exit park (Decisions Log #5): an agent asked and stopped.
        [typeof(QuestionAsked)] = OrchestratorFeedLevel.Actionable,

        // idea 6be68ee2, companion 1bb803e1: an owner-role member write a non-root node's own
        // invite match asked for, parked on this root for its own human to approve
        // (h9k project member approve) — the acceptance criterion's own "needs-you item on the
        // root (h9k status and the feed)" names both surfaces, and only h9k status had one
        // (independent pre-PR review, cycle 1, conformance lens, medium).
        [typeof(OwnerActHeld)] = OrchestratorFeedLevel.Actionable,

        // Gate and run failures.
        [typeof(VerificationFailed)] = OrchestratorFeedLevel.Actionable,
        [typeof(SettlingGateRepairCapReached)] = OrchestratorFeedLevel.Actionable,
        [typeof(PullRequestChecksFailed)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunFailed)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunKilled)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunBudgetExhausted)] = OrchestratorFeedLevel.Actionable,
        [typeof(ReviewErrored)] = OrchestratorFeedLevel.Actionable,

        // A merge that stays failed — admitted only on a failed attempt (see Admits).
        [typeof(PullRequestAutoMergeAttempted)] = OrchestratorFeedLevel.Actionable,

        // Daemon trouble: the machinery, not the work, went wrong. Each of these is the daemon
        // recording that it had to repair, retry, hold, or rebuild something rather than simply
        // run it — the class of thing an orchestrator wants to hear about even when the run
        // survives, because a node doing this repeatedly is a node in trouble.
        [typeof(RunSessionErrorRetried)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunUncommittedWorkRecoveryAttempted)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunUnattendedExitFlagged)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunRecordReconstructed)] = OrchestratorFeedLevel.Actionable,
        [typeof(RunLaunchHeld)] = OrchestratorFeedLevel.Actionable,

        // Somebody answered a pull request the owner reviewed (replies, new commits, or a
        // re-review asked for). The task is NeedsHuman and only the reviewer moves it, so the
        // board alone leaves a window open on the project unaware — admitted only on the node that
        // recorded it (see Admits).
        [typeof(PullRequestReviewAuthorResponded)] = OrchestratorFeedLevel.Actionable,

        // A message from a person or another node's window — admitted at every level, which is
        // what the narrowest band means here, and payload-gated to exclude the daemon's own
        // machine traffic (see Admits).
        [typeof(MessageReceived)] = OrchestratorFeedLevel.Actionable,

        // A root-key rotation landed on, or was voided from, a project's own ledger (idea 6be68ee2,
        // PR B of the succession chain): an owner's own identity root moved, or moved back — the
        // one trust fact loud enough to page an orchestrator window over, unlike an ordinary vouch
        // or revoke, which stays a quiet fleet-management act.
        [typeof(RootRotationObserved)] = OrchestratorFeedLevel.Actionable,
        [typeof(RootRotationRevoked)] = OrchestratorFeedLevel.Actionable,

        // ─── Transitions: the work's own movement. ──────────────────────────────────────────────

        // The task lifecycle words a human reads in h9k status's Status column
        // (Cli.Commands.LifecycleState), each mapped to the event that actually moves it:
        // Published, Working, Delivered (the pull request pushed), Done (the merge observed, or a
        // human's own resolve), Failed, and Archived — which the card calls abandoned.
        [typeof(TaskPublished)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskClaimed)] = OrchestratorFeedLevel.Transitions,
        [typeof(PullRequestOpened)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskCompleted)] = OrchestratorFeedLevel.Transitions,
        [typeof(PullRequestMerged)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskResolved)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskFailed)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskAbandoned)] = OrchestratorFeedLevel.Transitions,

        // Ideas logged or updated. IdeaDiscarded and IdeaPromoted are retired (backlog 31) and
        // named anyway: a project's own history still carries them, and an entry here is what
        // keeps a drain over that history from going quiet about an idea's ending.
        [typeof(IdeaCaptured)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaRevised)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaAssignedToProject)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaTaskCut)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaConcluded)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaArchived)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaSpikeConcluded)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaDiscarded)] = OrchestratorFeedLevel.Transitions,
        [typeof(IdeaPromoted)] = OrchestratorFeedLevel.Transitions,

        // Claims or takeovers involving another node (idea 202383dc, items 4 and 5). TaskClaimed
        // is already above: it is both this project's "Working" transition and, when a foreign
        // node is the claimant, the claim itself — one event, one entry, and the description is
        // what names the node.
        [typeof(TaskHolderTakenOver)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskTakeRequested)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskTakeRefused)] = OrchestratorFeedLevel.Transitions,
        [typeof(TaskHolderReleased)] = OrchestratorFeedLevel.Transitions,

        // ─── Everything: the machinery's own movement. ─────────────────────────────────────────

        // A run's phase changes: the events that move RunAggregate.State, minus the ones a
        // narrower band already claims, plus the gate bracket and the per-cycle review milestones
        // — which is how a window follows the review loop at all, since the loop's own progress
        // is a cycle count rather than a state.
        [typeof(RunDispatched)] = OrchestratorFeedLevel.Everything,
        [typeof(RunProcessStarted)] = OrchestratorFeedLevel.Everything,
        [typeof(RunResumed)] = OrchestratorFeedLevel.Everything,
        [typeof(AgentSessionCompleted)] = OrchestratorFeedLevel.Everything,
        [typeof(GateStarted)] = OrchestratorFeedLevel.Everything,
        [typeof(GateEnded)] = OrchestratorFeedLevel.Everything,
        [typeof(VerificationPassed)] = OrchestratorFeedLevel.Everything,
        // The skip sibling of the pass above (task: a delivered diff that touches no buildable or
        // testable source skips the build and test gates, independent pre-PR review, cycle 1,
        // conformance lens, low): without an entry here, the one run where the gates were
        // deliberately skipped is the one the feed says nothing about after "the agent session
        // finished; the gates are next".
        [typeof(VerificationSkipped)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewDispatched)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewPassCompleted)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewCompleted)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewFixDispatched)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewFixCompleted)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewParkResolved)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewBoundaryApproved)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewSettled)] = OrchestratorFeedLevel.Everything,
        [typeof(ReviewFeedbackReceived)] = OrchestratorFeedLevel.Everything,
        [typeof(PullRequestUpdated)] = OrchestratorFeedLevel.Everything,
        [typeof(PullRequestConflictObserved)] = OrchestratorFeedLevel.Everything,
        [typeof(PullRequestClosed)] = OrchestratorFeedLevel.Everything,
        [typeof(RunPhaseDelegated)] = OrchestratorFeedLevel.Everything,
        [typeof(RunSuperseded)] = OrchestratorFeedLevel.Everything,
        [typeof(RunCompleted)] = OrchestratorFeedLevel.Everything,
    };

    /// <summary>Every event type the feed knows about, for a test to check against the scope
    /// registry.</summary>
    public static IReadOnlyCollection<Type> InterestingEventTypes => [.. Bands.Keys];

    /// <summary>Which band this event type sits in, or null when the feed has no interest in it
    /// at all.</summary>
    public static OrchestratorFeedLevel? BandOf(Type eventType) =>
        Bands.TryGetValue(eventType, out OrchestratorFeedLevel? band) ? band : null;

    /// <summary>
    /// Whether a project reading at <paramref name="level"/> is handed this event. The type's own
    /// band decides, and <see cref="Admits(object)"/> is the payload gate the exceptions named on
    /// this class need — applied here too, so no caller can reach one without the other. Never
    /// knows whether this event was replicated, so the origin gates of
    /// <see cref="RunRecordReconstructed"/> and <see cref="PullRequestReviewAuthorResponded"/>
    /// always read local here — the overload <see cref="OrchestratorFeedSelection"/>
    /// actually calls threads that through explicitly.
    /// </summary>
    public static bool Admits(object eventData, OrchestratorFeedLevel level) =>
        Admits(eventData.GetType(), eventData, level, isReplicated: false);

    /// <summary>
    /// The same answer for an event whose recorded type is known apart from its payload, which is
    /// what a read over the raw log has and what <see cref="OrchestratorFeedSelection"/> goes
    /// through. The recorded type is the one this table is keyed on, and routing the selection
    /// here rather than letting it test the band and the payload separately is what keeps
    /// admission a single decision with one expression of it rather than two to keep in step.
    /// </summary>
    public static bool Admits(Type eventType, object eventData, OrchestratorFeedLevel level) =>
        Admits(eventType, eventData, level, isReplicated: false);

    /// <summary>The overload that actually knows whether this event arrived by replication —
    /// <see cref="OrchestratorFeedSelection.SelectAsync"/>'s own caller, which reads it off the
    /// candidate rather than guessing.</summary>
    public static bool Admits(Type eventType, object eventData, OrchestratorFeedLevel level, bool isReplicated) =>
        BandOf(eventType) is { } band && level.Admits(band) && Admits(eventData, isReplicated);

    /// <summary>
    /// The payload gate for the entries whose admission is not decided by type alone, read as
    /// though this event were this node's own (never replicated) — every caller that cannot know
    /// otherwise goes through here. Every other event returns true: this never narrows a band, it
    /// only answers "is this particular record a feed item at all".
    /// </summary>
    public static bool Admits(object eventData) => Admits(eventData, isReplicated: false);

    /// <summary>The same gate, told whether this event arrived by replication — the one payload
    /// question that is not about the event's own data at all.</summary>
    public static bool Admits(object eventData, bool isReplicated) => eventData switch
    {
        // A message from a person, or from another node's own window — never the JSON the
        // daemon's claim reactors post to each other. The kinds that are never recorded as a
        // received message in the first place (events, events-request, events-unavailable) cannot
        // reach here at all, so the mechanical three are the whole exclusion.
        MessageReceived received => !MessageKind.MechanicalKindValues.Contains(received.Kind),
        // A merge the daemon completed itself asks nothing of anybody; one GitHub refused is the
        // actionable half.
        PullRequestAutoMergeAttempted attempted => !attempted.Succeeded,
        // A peer's own daemon rebuilt this run, not this node's — never this node's own thing to
        // act on, and never worth paging this node's window over (task: a run stream whose first
        // event is a reconstruction — 14 replays landing on restart must not page the window).
        RunRecordReconstructed => !isReplicated,
        // The watch that recorded this is this node's own; a replicated copy on a teammate's node
        // is news about a review that is not theirs.
        PullRequestReviewAuthorResponded => !isReplicated,
        _ => true,
    };
}
