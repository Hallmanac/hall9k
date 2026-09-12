using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks;

public sealed class TaskAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Objective { get; private set; } = string.Empty;
    public TaskType Type { get; private set; } = TaskType.Unknown;
    public TaskState State { get; private set; } = TaskState.Unknown;
    public string? AgentContext { get; private set; }
    public TaskConstraints? Constraints { get; private set; }
    public ExternalReference? ExternalReference { get; private set; }

    /// <summary>
    /// The system a publication session is outstanding for, or null when none is (backlog 18).
    /// Set by the request and cleared when the session ends, so the daemon's publication loop
    /// can tell work still to do from work already done without a second document.
    /// </summary>
    public WorkItemProvider? PendingPublicationProvider { get; private set; }

    /// <summary>The board the outstanding publication was asked to file under; None when nothing was bound.</summary>
    public JiraProjectKey PendingPublicationProjectKey { get; private set; } = JiraProjectKey.None;

    /// <summary>
    /// True once the daemon has actually spawned the pending publication's session. It is what
    /// stops the next sweep dispatching a second agent to create a second card for the same
    /// task — the one failure of this feature that costs a human cleanup in Jira rather than a
    /// retry here.
    /// </summary>
    public bool PublicationSessionDispatched { get; private set; }

    /// <summary>The write hall9k has outstanding against Jira for this task, or null when none is (Brian's design, 2026-08-28).</summary>
    public Guid? PendingJiraWriteId { get; private set; }

    /// <summary>Which of create, update, or comment the outstanding write is.</summary>
    public JiraWriteOperation PendingJiraWriteOperation { get; private set; } = JiraWriteOperation.Unknown;

    /// <summary>The item the outstanding write targets; null for a create, which has none yet.</summary>
    public string? PendingJiraWriteIssueKey { get; private set; }

    /// <summary>The composed payload exactly as requested — what the retry sweep re-attempts with, unread and unmirrored.</summary>
    public string? PendingJiraWritePayloadJson { get; private set; }

    public Guid PendingJiraWriteRequestedByOwnerId { get; private set; }

    /// <summary>What the most recent failed attempt reported, kept only while the write is still pending.</summary>
    public string? PendingJiraWriteFailureReason { get; private set; }

    /// <summary>
    /// True when the most recent failed attempt was a rejected credential — the one failure that
    /// keeps the write pending rather than ending it, and what the attention pane and the
    /// daemon's retry sweep both key on.
    /// </summary>
    public bool PendingJiraWriteIsAuthFailure { get; private set; }

    /// <summary>
    /// True when closeout's own merge notice could not be submitted because another Jira write
    /// was already outstanding on this task, and is waiting for that write to clear so the
    /// daemon's retry sweep can attempt it (Brian's design, 2026-08-28).
    /// </summary>
    public bool HasQueuedJiraMergeNotice { get; private set; }

    /// <summary>The task's model override, the most specific link in the resolution chain (Decisions Log #33).</summary>
    public AgentModel Model { get; private set; } = AgentModel.Unknown;

    /// <summary>
    /// The owner's standing pre-approval (task: a task can be published pre-approved), three-valued
    /// since the mode that waits for human review landed. Anything other than
    /// <see cref="PreApprovalMode.Off"/> means the daemon merges this task's pull request on its
    /// own, deterministically, the moment GitHub's own gates read satisfied — CI green, the review
    /// decision satisfied, no outstanding requested reviewer, every review thread resolved, and no
    /// follow-up live or queued — with <see cref="PreApprovalMode.AfterHumanReview"/> holding one
    /// gate longer: until a human reviewer has actually been requested and every requested reviewer
    /// has approved the current head. Every existing human waypoint (Failed, a review park, a
    /// severity-bar failure, a cap trip) still stops the pipeline exactly as it does for an
    /// unflagged task; pre-approval only removes the owner as a SYNCHRONOUS gate at the pull
    /// request, never any of those. Granted at creation
    /// (<see cref="Events.TaskAdded.PreApproval"/>) or at publish
    /// (<see cref="Events.TaskPublished.PreApproval"/>), defaulting off at both, and flippable
    /// afterward on any live non-terminal task via <see cref="Events.TaskPreApprovedSet"/>.
    /// </summary>
    public PreApprovalMode PreApproval { get; private set; } = PreApprovalMode.Off;

    /// <summary>
    /// Which install published the work this task mirrors, or null when it is local work — see
    /// <see cref="Tasks.TaskOrigin"/>. Set once at creation and never again: a mirror's origin is a
    /// fact about where the copy came from, not a link that is maintained afterwards (Decisions Log
    /// #60 — the record is read once at adoption and never re-checked).
    /// </summary>
    public TaskOrigin? Origin { get; private set; }

    /// <summary>
    /// This pre-approved task's own mechanical-resolution budget spend — see
    /// <see cref="Events.TaskMechanicalResolutionAttempted"/>'s own doc for the shape and why it
    /// is a single pool. Meaningless on an unflagged task, which never spends it.
    /// </summary>
    public int MechanicalResolutionAttempts { get; private set; }

    /// <summary>
    /// This task's own override of how many agent sessions its run may hold simultaneously; null
    /// means the node's global <c>SessionCapPerRun</c> default decides (Decisions Log #111).
    /// Unlike <see cref="Model"/>, settable at any time — including mid-run — via
    /// <see cref="TaskSessionCapOverridden"/> rather than <see cref="TaskRevised"/>.
    /// </summary>
    public int? SessionCap { get; private set; }

    /// <summary>This task's own override of the conformance review track's cycle cap; null defers to the project or node (task: review cycle caps become settable).</summary>
    public int? MaxComplianceReviewCycles { get; private set; }
    /// <summary>This task's own override of the adversarial review track's cycle cap; null defers to the project or node.</summary>
    public int? MaxAdversarialReviewCycles { get; private set; }
    /// <summary>This task's own override of the mandatory final-full-pass round cap; null defers to the project or node.</summary>
    public int? MaxFinalFullPassRounds { get; private set; }
    /// <summary>This task's own override of the task-lifetime review-cycle budget; null defers to the project or node.</summary>
    public int? LifetimeReviewCycleBudget { get; private set; }
    /// <summary>
    /// This task's own override of which pre-PR review stages a run gets (task: the review
    /// pipeline's stage composition becomes configuration recorded per run); null defers to the
    /// project or node. Draft-only, set at <c>h9k task add</c> or revised at <c>h9k task revise</c>
    /// — unlike the review-cycle caps above, deliberately not settable mid-run: see
    /// <c>Hall9k.Domain.Features.Run.ReviewStageComposition</c>'s own doc for why.
    /// </summary>
    public ReviewStageComposition? ReviewStageComposition { get; private set; }
    /// <summary>
    /// This task's own override of whether true closeout closes its linked GitHub issue; null
    /// defers to the project's own close-linked-issue setting, live (task: a task's linked GitHub
    /// issue is closed at true closeout under a configurable rule). Set at
    /// <see cref="Events.TaskPublished"/> or revised at <see cref="Events.TaskRevised"/>.
    /// </summary>
    public CloseLinkedIssueRule? CloseLinkedIssue { get; private set; }
    public int LeaseGeneration { get; private set; }
    public Guid? ClaimedByNodeId { get; private set; }

    /// <summary>
    /// Whether the current claim is an operator working the task interactively (h9k task work)
    /// rather than a node's headless dispatch. An interactive claim carries no <c>TaskLease</c>
    /// document — no liveness lease, no heartbeat reclaim (AGENTS.md) — and is represented on the
    /// SAME <see cref="TaskState.Claimed"/> state and <see cref="TaskClaimed"/> event a node's
    /// claim uses, discriminated only by <see cref="ClaimedByNodeId"/> carrying the sentinel
    /// <see cref="Guid.Empty"/> ("a human, not a machine") rather than a real registered node's
    /// id — chosen deliberately so the whole existing claim/complete/fail/requeue state machine,
    /// and the generation fence built on <see cref="LeaseGeneration"/>, apply unchanged, and so
    /// <see cref="Guid.Empty"/> never collides with a node id a real node registers with
    /// (<c>NodeBootstrap</c> always mints one via <c>DomainId.New()</c>, never the empty guid).
    /// </summary>
    public bool IsInteractiveClaim => ClaimedByNodeId == Guid.Empty;

    /// <summary>
    /// A recorded, task-level fact (task: interactive mode becomes a recorded property of the
    /// task, design ruling R2 — "interactive mode is a property of the task, not of the claim")
    /// distinct from <see cref="IsInteractiveClaim"/>: that reads true only while the CURRENT
    /// claim carries the sentinel node id, and reverts on any give-back including
    /// <see cref="Apply(TaskRequeued)"/>/<see cref="Apply(TaskRetried)"/>/<see cref="Apply(TaskReopened)"/>;
    /// this stays true across <see cref="Apply(TaskRetried)"/>, <see cref="Apply(TaskReopened)"/>,
    /// and a <see cref="Apply(TaskRequeued)"/> given <c>--keep-interactive</c>, surviving exactly
    /// as long as the human who turned it on has not explicitly turned it off. Set true by
    /// <see cref="Apply(TaskClaimed)"/> whenever <see cref="TaskClaimed.InteractiveMode"/> says so
    /// (h9k task work's claim always does; h9k task start's does only when the human asked for it)
    /// and never unset by any other claim — a plain node claim or an ordinary reclaim carries the
    /// flag false and leaves this alone rather than clearing it. The clearing acts are
    /// <see cref="Apply(TaskHandedBack)"/> (h9k task handback) and a default
    /// <see cref="Apply(TaskRequeued)"/> (h9k task release, design ruling R6 amended 2026-09-05):
    /// both are the human's own explicit act of returning the task to the machine, so headless
    /// dispatch stops gating phase boundaries for a human who walked away —
    /// <c>--keep-interactive</c> on release is the one stated exception. Delivering
    /// (h9k task deliver) never touches this field at all — that command appends no
    /// <see cref="TaskAggregate"/> event of its own — which is exactly what lets a delivered run's
    /// review/fix/re-review/pull-request boundaries keep parking for the human under this flag.
    /// </summary>
    public bool InteractiveModeEnabled { get; private set; }

    public Guid? CurrentRunId { get; private set; }
    public Guid? PendingQuestionId { get; private set; }
    public string? PullRequestUrl { get; private set; }
    /// <summary>Set while a follow-up run is pending: the next claim resumes this branch instead of cutting a new one.</summary>
    public string? FollowUpBranch { get; private set; }
    /// <summary>Why the pending follow-up run exists; the launcher picks the agent prompt from it.</summary>
    public FollowUpKind FollowUpKind { get; private set; } = FollowUpKind.Unknown;
    /// <summary>
    /// The pull request head closeout observed when it reopened this task for a ReviewFeedback or
    /// FailingChecks follow-up (task: a lap reviews only what it changed) — the launcher carries
    /// this onto the follow-up run's own RunDispatched as the seed for its opening Discovery
    /// cycle's diff instruction. Null for a Rebase follow-up, a manual reopen, or a task never
    /// reopened at all; see <see cref="TaskReopened.PullRequestHeadSha"/>'s own doc.
    /// </summary>
    public string? FollowUpPullRequestHeadSha { get; private set; }
    /// <summary>
    /// The commit a pending <see cref="FollowUpKind.StackReplay"/> follow-up must replay this
    /// branch <em>from</em> — see <see cref="TaskReopened.StackReplayUpstreamCommit"/>'s own doc.
    /// Null for every other follow-up kind and for a task never reopened at all.
    /// </summary>
    public string? StackReplayUpstreamCommit { get; private set; }
    /// <summary>
    /// The commit a pending <see cref="FollowUpKind.StackReplay"/> follow-up must replay this
    /// branch <em>onto</em> — see <see cref="TaskReopened.StackReplayOntoCommit"/>'s own doc.
    /// Null for every other follow-up kind and for a task never reopened at all.
    /// </summary>
    public string? StackReplayOntoCommit { get; private set; }
    /// <summary>
    /// The human CHANGES_REQUESTED reviews a pending
    /// <see cref="FollowUpKind.ReviewRequestedChanges"/> follow-up is answering — see
    /// <see cref="TaskReopened.ChangesRequestedReviews"/>'s own doc. Empty for every other
    /// follow-up kind and for a task never reopened at all.
    /// </summary>
    public IReadOnlyList<ChangesRequestedReview> ChangesRequestedReviews { get; private set; } = [];
    /// <summary>
    /// Set while a human-requested retry of a failed task is pending (Decisions Log #25):
    /// the failed run's branch, resumed by the next claim when it still exists — the
    /// launcher starts clean from the base branch when it is gone (or when this is null).
    /// </summary>
    public string? RetryBranch { get; private set; }
    /// <summary>
    /// Automatic (monitor-driven) reopens since the last human-initiated one — the
    /// lifetime-ceiling counter for PR closeout (Decisions Log #22, backlog 45). A manual
    /// reopen resets it: the human asking for another attempt restores the automatic budget.
    /// </summary>
    public int CloseoutAttempts { get; private set; }

    /// <summary>
    /// Automatic stacked rebases spent since the last human-initiated reopen — the rebase
    /// budget for a stacked child (task: a stacked pull-request edge exists as an explicit opt-in
    /// dependency), bounded by <c>DaemonOptions.MaxStackReplayRuns</c> and reset by a manual reopen
    /// exactly as <see cref="CloseoutAttempts"/> is.
    /// <para>
    /// Two things spend it, and one budget covers both because both answer the same cause — the
    /// parent's branch moving, which this task neither caused nor can prevent. A replay follow-up
    /// run closeout dispatches once the child's pull request is open
    /// (<see cref="Events.TaskReopened"/> with <see cref="FollowUpKind.StackReplay"/>), and a
    /// checkpoint rebase the review loop performs inside a run that has not opened one yet
    /// (<see cref="Events.StackedCheckpointRebased"/>, task: a stacked child absorbs its parent's
    /// post-delivery churn safely). The name still says "replays" because that is what the
    /// dispatched half is and what <c>DaemonOptions.MaxStackReplayRuns</c> is named for; a park
    /// message past the cap says "rebase(s)", which is the honest word for the mixture.
    /// </para>
    /// <para>
    /// Counted separately, and deliberately NOT added to <see cref="CloseoutAttempts"/>: a replay
    /// is not a lap on an obstruction of this task's own — it is the parent's branch moving, which
    /// this task neither caused nor can prevent. Folding it into the lifetime ceiling would let a
    /// parent that force-pushes half a dozen times spend the child's whole review budget before a
    /// single one of the child's own review laps ever ran.
    /// </para>
    /// </summary>
    public int StackReplaysDispatched { get; private set; }

    /// <summary>
    /// This task's most recent automatic reopen's obstruction identity — the failing check
    /// name, or the exact set of unresolved review-thread ids, at the moment of dispatch
    /// (Decisions Log #80, backlog 45). Compared against the NEXT automatic decision's own
    /// obstruction: the same key means the lap made no progress and counts against
    /// <see cref="ConsecutiveObstructionLaps"/>; a different key means something cleared, so
    /// the count restarts at the new obstruction's first lap. Null before any automatic
    /// reopen, and after a manual one wipes the slate.
    /// </summary>
    public string? LastAutomaticObstructionKey { get; private set; }

    /// <summary>
    /// Consecutive automatic laps spent on <see cref="LastAutomaticObstructionKey"/> without
    /// clearing it — the progress-based cap (DaemonOptions.MaxCloseoutLapsPerObstruction),
    /// deliberately separate from the lifetime ceiling <see cref="CloseoutAttempts"/> already
    /// tracks (Decisions Log #80, backlog 45).
    /// </summary>
    public int ConsecutiveObstructionLaps { get; private set; }

    /// <summary>
    /// Every lap's obstruction summary, oldest first — the lap history a lifetime-ceiling
    /// park names, so the human sees what the machine already tried before spending their
    /// own attention (Decisions Log #80, backlog 45). Cleared on a manual reopen along with
    /// the counter it explains.
    /// </summary>
    private readonly List<string> _automaticLapHistory = [];
    public IReadOnlyList<string> AutomaticLapHistory => _automaticLapHistory;

    /// <summary>
    /// Human-started unresolved review-thread ids observed at the most recent automatic
    /// dispatch decision — what the next decision diffs against to recognize a newly opened
    /// human thread, one of the two mechanical human-engagement signals that grants a lap
    /// regardless of the progress cap (Decisions Log #80, backlog 45).
    /// </summary>
    private readonly List<string> _knownHumanReviewThreadIds = [];
    public IReadOnlyList<string> KnownHumanReviewThreadIds => _knownHumanReviewThreadIds;

    /// <summary>Reviewers with a pending review request, observed at the most recent automatic dispatch decision.</summary>
    private readonly List<string> _knownPendingReviewRequestLogins = [];
    public IReadOnlyList<string> KnownPendingReviewRequestLogins => _knownPendingReviewRequestLogins;
    public DateTimeOffset AddedAt { get; private set; }
    public Guid AddedByOwnerId { get; private set; }

    /// <summary>The idea this task was promoted from; null when it was written directly (Decisions Log #35).</summary>
    public Guid? SourceIdeaId { get; private set; }

    /// <summary>
    /// The epic this task belongs to, or null when ungrouped (Decisions Log #100).
    /// Independent of <see cref="SourceIdeaId"/>: membership and provenance are separate
    /// records, and a task belongs to at most one epic at a time.
    /// </summary>
    public Guid? EpicId { get; private set; }

    /// <summary>
    /// Whose work this is. Set by the explicit human act of assignment and read by the claim
    /// guard: a node claims only its own owner's tasks (Decisions Log #34). Null until
    /// assigned, and null again after unassign.
    /// </summary>
    public Guid? AssignedOwnerId { get; private set; }

    private readonly List<string> _acceptanceCriteria = [];
    public IReadOnlyList<string> AcceptanceCriteria => _acceptanceCriteria;

    /// <summary>The tasks this one waits on; declared at creation or revised in Draft.</summary>
    private readonly List<Guid> _blockedBy = [];
    public IReadOnlyList<Guid> BlockedBy => _blockedBy;

    /// <summary>
    /// The one blocker this task is <em>stacked on</em>, or null when it is stacked on nothing —
    /// which is every task's default (task: a stacked pull-request edge exists as an explicit
    /// opt-in dependency). Always a member of <see cref="BlockedBy"/>, an invariant
    /// <see cref="Handlers.TaskDecider.Add"/> and <see cref="Handlers.TaskDecider.Revise"/> enforce
    /// rather than repair, so a stacked edge is always <em>also</em> a dependency edge and the
    /// cycle detection at publish sees it without knowing it is stacked.
    /// <para>
    /// Null is never a stand-in for "probably stacked": the tool never infers a stacked edge from
    /// an ordinary blocked-by (Brian's cohesion ruling, 2026-08-28 — a stacked edge is reserved for
    /// tasks cohesive in feature set, and the declaration is always a human's).
    /// </para>
    /// </summary>
    public Guid? StackedOnTaskId { get; private set; }

    /// <summary>
    /// Whether <paramref name="dependencyId"/> is the blocker this task is stacked on, rather than
    /// one it is merely blocked by. The single question every stacked-aware rule asks.
    /// </summary>
    public bool IsStackedOn(Guid dependencyId) =>
        StackedOnTaskId is { } parentId && parentId == dependencyId;

    /// <summary>
    /// The pull request this task is stacked on when its parent lives on GitHub rather than in this
    /// install's records, or null when it is not stacked that way — which is every task's default
    /// (task: a stacked child can stand on a pull request another install owns).
    /// <see cref="StackedOnTaskId"/>'s alternative, never its companion: exactly one of the two can
    /// be set, an invariant <see cref="Handlers.TaskDecider.VetStackedEdge"/> enforces rather than
    /// repairs.
    /// <para>
    /// It carries no <see cref="BlockedBy"/> edge, and cannot: there is no local task to name, so
    /// there is nothing for the publish-time cycle walk or the unmet-dependency bookkeeping to see.
    /// What holds the child instead is <see cref="RemoteStackedParentState"/> — the last thing the
    /// closeout watcher's sweep actually observed about that pull request.
    /// </para>
    /// </summary>
    public int? StackedOnPullRequestNumber { get; private set; }

    /// <summary>Whether this task's parent is a pull request on GitHub rather than a local task.</summary>
    public bool IsStackedOnRemotePullRequest => StackedOnPullRequestNumber is > 0;

    /// <summary>
    /// The last state the sweep observed that pull request in, or <see cref="RemoteParentState.Unknown"/>
    /// when it has never been looked at (or every look so far failed). Never a guess: an unobserved
    /// parent reads as unknown, and the child waits.
    /// </summary>
    public RemoteParentState RemoteStackedParentState { get; private set; } = RemoteParentState.Unknown;

    /// <summary>The branch that pull request opens FROM, as last observed — what this child's own branch is cut from.</summary>
    public string RemoteStackedParentHeadBranch { get; private set; } = string.Empty;

    /// <summary>That head branch's commit, as last observed; blank when none was reported.</summary>
    public string RemoteStackedParentHeadCommit { get; private set; } = string.Empty;

    /// <summary>The branch that pull request opens INTO, as last observed — read, never assumed to be the project's own.</summary>
    public string RemoteStackedParentBaseBranch { get; private set; } = string.Empty;

    /// <summary>That pull request's own URL, as last observed; blank when none was reported.</summary>
    public string RemoteStackedParentUrl { get; private set; } = string.Empty;

    /// <summary>
    /// The issue or tracker item that pull request says it closes, or null when it names none —
    /// which is an ordinary shape, not a gap. <c>h9k task show</c> names a local task carrying the
    /// same reference when one exists, and says nothing when none does.
    /// </summary>
    public ExternalReference? RemoteStackedParentWorkItem { get; private set; }

    /// <summary>
    /// When the reading above was taken, or null when the parent has never been observed at all.
    /// The honest half of "last observed state" — and honest about its own limit: a sweep that
    /// re-reads an unchanged pull request appends nothing
    /// (<see cref="Events.RemoteStackedParentObserved"/>'s own doc), so this is when the reading
    /// last CHANGED, not when the sweep last looked. A parent stable all afternoon carries this
    /// morning's timestamp with nothing wrong.
    /// </summary>
    public DateTimeOffset? RemoteStackedParentObservedAt { get; private set; }

    /// <summary>What that look saw, in a sentence — the log's account and the task surface's.</summary>
    public string? RemoteStackedParentDetail { get; private set; }

    /// <summary>
    /// Why a human is needed rather than more patience, or null when nothing here needs one. Set
    /// only where the parent pull request can no longer become a base this child builds on — it
    /// closed unmerged — which is slice two's dead-parent rule one pull request over. A pull
    /// request that simply is not open yet is ordinary waiting and sets nothing.
    /// </summary>
    public string? RemoteStackedParentHoldReason { get; private set; }

    /// <summary>
    /// Whether the declared remote parent still holds this task back — the remote half of what
    /// <see cref="UnmetDependencies"/> answers for local blockers. False for every task that
    /// declared no remote parent, which is the unchanged rule.
    /// <para>
    /// Asked at exactly three doors — <see cref="Apply(TaskAssigned)"/>,
    /// <see cref="Apply(TaskDependencyCompleted)"/> and
    /// <see cref="Apply(Events.RemoteStackedParentObserved)"/>, the last of which asks it in both
    /// directions because it is the one door where the answer itself changes — and deliberately NOT
    /// at the four give-the-claim-back doors that share their shape (<see cref="Apply(TaskRequeued)"/>,
    /// <see cref="Apply(TaskReopened)"/>, <see cref="Apply(TaskHandedBack)"/>,
    /// <see cref="Apply(TaskRetried)"/>). Those four restore a snapshot frozen at assignment and
    /// never re-evaluate the parent, so asking a LIVE question there would be a stricter rule than
    /// the local edge's, not parity with it — and it would land a follow-up reopened for an
    /// unrelated cause (failing checks) in Blocked whenever the parent's pull request happened to
    /// read absent. A remote parent that dies after the child has delivered is answered by
    /// closeout's own ParentDead park, which is the designed path.
    /// </para>
    /// </summary>
    public bool AwaitsRemoteStackedParent =>
        IsStackedOnRemotePullRequest && !RemoteStackedParentState.ReleasesChild;

    /// <summary>
    /// The subset of <see cref="BlockedBy"/> that had not reached true closeout when this task
    /// was assigned, minus each one since observed complete. Empty on a Queued task by
    /// construction: emptying it is what moves Blocked -> Queued.
    /// </summary>
    private readonly List<Guid> _unmetDependencies = [];
    public IReadOnlyList<Guid> UnmetDependencies => _unmetDependencies;

    /// <summary>
    /// Blockers observed dead: they will never close out on their own. Oldest first, so the
    /// last entry is the newest observation — which is the one <see cref="DependencyFailureReason"/>
    /// carries.
    /// </summary>
    private readonly List<Guid> _deadDependencies = [];
    public IReadOnlyList<Guid> DeadDependencies => _deadDependencies;

    /// <summary>
    /// What was recorded about each dead blocker, kept per dependency rather than only as the
    /// newest one. The resolver reads it to answer two questions it cannot otherwise answer
    /// honestly: what reason survives when one of several dead blockers recovers, and whether
    /// a blocker that is still dead died a <em>different</em> death since it was last recorded
    /// (Decisions Log #61).
    /// </summary>
    private readonly Dictionary<Guid, string> _deadDependencyReasons = [];

    /// <summary>Why the newest dead dependency died, as observed — the reason the human reads.</summary>
    public string? DependencyFailureReason { get; private set; }

    /// <summary>
    /// What this task currently records about that blocker's death, or null when it records
    /// none. Null is "nothing recorded", never a stand-in for a death nobody observed.
    /// </summary>
    public string? RecordedDependencyFailure(Guid dependencyId) =>
        _deadDependencyReasons.GetValueOrDefault(dependencyId);

    private readonly List<Guid> _runIds = [];
    public IReadOnlyList<Guid> RunIds => _runIds;

    /// <summary>
    /// Whether a human has marked this task to take the next free dispatch slot regardless of
    /// assignment age (task 45136b29, idea fcaded0b's R7 ruling) — a recorded task-level fact,
    /// not a handback-only flag: <c>h9k task revise --queue-first</c> sets it directly, and
    /// <c>h9k task handback --first</c> sets it as part of handing a claim back. Cleared the
    /// moment the run it earned actually dispatches (<see cref="Apply(TaskClaimed)"/>), so it
    /// never outlives the turn it bought — and cleared the same way when the turn it was waiting
    /// for never comes: a task that reaches Done (<see cref="Apply(TaskCompleted)"/>,
    /// <see cref="Apply(TaskResolved)"/>) or Abandoned (<see cref="Apply(TaskAbandoned)"/>)
    /// without ever routing back through another claim would otherwise carry a marker set
    /// earlier in its life straight into a state nothing will ever dispatch again.
    /// </summary>
    public bool QueuePriorityMarked { get; private set; }

    /// <summary>
    /// The GitHub login this task's own auto-created reviewer assignment currently believes is
    /// requested (idea e5e98a33), or null when no assignment is currently on record — either
    /// because this task was never auto-created, or because the assignment was recalled. This is
    /// the poll's own comparison point (the <see cref="TaskReopened.KnownPendingReviewRequestLogins"/>
    /// pattern applied here): set by <see cref="Apply(Events.PullRequestReviewAssignmentObserved)"/>,
    /// cleared by <see cref="Apply(Events.PullRequestReviewAssignmentRecalled)"/>, so a withdrawn
    /// assignment is never re-observed as a fresh recall on every later poll.
    /// </summary>
    public string? AutoPrReviewAssigneeLogin { get; private set; }

    /// <summary>
    /// The most recent GitHub comment that mentioned the install's login on this task's own pull
    /// request (idea 2f079bcd: a mention is auto-pr-review's second trigger), or null when none
    /// has ever been observed. Set by <see cref="Apply(Events.PullRequestReviewMentionObserved)"/>
    /// and never cleared afterward — a mention is provenance about a comment that was written,
    /// not a live condition a later event retracts the way a review-request assignment can be
    /// recalled. <c>h9k task show</c> reads these four fields to name the comment's id, author,
    /// body and time; the dedupe that stops the SAME comment id firing twice lives outside the
    /// stream, on <c>ObservedReviewMention</c>, since a fresh sweep has to answer "have I already
    /// acted on this" before it ever reads this task's stream at all.
    /// </summary>
    public string? LatestMentionCommentId { get; private set; }

    /// <summary>See <see cref="LatestMentionCommentId"/>.</summary>
    public string? LatestMentionAuthorLogin { get; private set; }

    /// <summary>See <see cref="LatestMentionCommentId"/>.</summary>
    public string? LatestMentionBody { get; private set; }

    /// <summary>See <see cref="LatestMentionCommentId"/>.</summary>
    public string? LatestMentionUrl { get; private set; }

    /// <summary>See <see cref="LatestMentionCommentId"/> — GitHub's own timestamp for the comment, not this install's poll time.</summary>
    public DateTimeOffset? LatestMentionCreatedAt { get; private set; }

    /// <summary>See <see cref="LatestMentionCommentId"/> — the numeric REST id, set only for an inline review-comment-thread reply, the one shape the REST reply endpoint's own <c>in_reply_to</c> accepts; null for every other comment shape, or a mention observed before this field existed.</summary>
    public long? LatestMentionCommentDatabaseId { get; private set; }

    /// <summary>
    /// The run a human reviewer's own review lap is riding on (<c>h9k pr review</c>, Decisions
    /// Log #149), or null when no lap has ever been opened on this task. Never cleared by
    /// <see cref="Apply(Events.PullRequestReviewVerdictDelivered)"/>: the verdict ends the lap,
    /// and the pair of facts a reader wants afterwards is "a lap ran, on this run, and it ended
    /// with this verdict" — clearing the run would leave the verdict attributed to nothing.
    /// <para>
    /// <see cref="ReviewLapOpen"/> is the half that does close, and it is what the daemon's own
    /// startup adoption reads: a lap-dispatched run has no agent process of its own to check for
    /// liveness, so adoption must leave it alone rather than fail it as "dispatched but never
    /// started".
    /// </para>
    /// </summary>
    public Guid? ReviewLapRunId { get; private set; }

    /// <summary>Whether a review lap is open right now — true from <see cref="Apply(Events.PullRequestReviewLapOpened)"/> until the reviewer's verdict lands.</summary>
    public bool ReviewLapOpen { get; private set; }

    /// <summary>
    /// The read-only checkout THIS entry into the lap opened with, or null when it passed
    /// <c>--no-worktree</c> and read no code locally at all. Provenance of what the reviewer
    /// asked for, and deliberately never what anything releases:
    /// <c>RunDetails.WorktreePath</c> is the single place a checkout is named for every consumer
    /// that acts on one (<c>PrReviewEngine.FinalizeAsync</c>,
    /// <c>RunLauncher.CleanUpPreviousPrReviewWorktreesAsync</c>). The two can therefore disagree
    /// in exactly one direction, and harmlessly: a re-entry passing <c>--no-worktree</c> over a
    /// run that does have a checkout nulls this while the run keeps naming it, so the checkout is
    /// still released. The other direction cannot happen — <c>h9k pr review</c> refuses to cut a
    /// checkout for a run that records none, precisely so nothing is ever left named only here.
    /// </summary>
    public string? ReviewLapWorktreePath { get; private set; }

    /// <summary>
    /// The verdict the reviewer submitted to GitHub at the end of their lap, or
    /// <see cref="ReviewerVerdict.Unknown"/> when no lap has ever delivered one — including on
    /// a pr-review task closed the older way, with <c>h9k review resolve --merge-ready</c> and
    /// nothing posted to the pull request at all.
    /// </summary>
    public ReviewerVerdict ReviewerVerdict { get; private set; } = ReviewerVerdict.Unknown;

    /// <summary>
    /// Whether this pr-review task's posted review is being followed through — true from
    /// <see cref="Apply(Events.PullRequestReviewFollowThroughOpened)"/> until the task reaches
    /// Done or a human walks away from it (task: a pr-review task stays open while the pull
    /// request's review threads are unresolved). It is what the closeout watcher's own
    /// follow-through sweep selects on, alongside the state: a NeedsHuman pr-review task with
    /// this flag clear is an ordinary findings park waiting to be walked, and polling GitHub for
    /// it would be a read nobody asked for.
    /// </summary>
    public bool PrReviewFollowThroughOpen { get; private set; }

    /// <summary>
    /// The pull request the follow-through is watching, as the review was posted against it —
    /// kept separately from <see cref="PullRequestUrl"/>, which stays null on a pr-review task
    /// until its own <see cref="Apply(TaskCompleted)"/> records it, precisely so a waiting task
    /// can be read for the number without borrowing a field that means "the pull request this
    /// task's own work opened" everywhere else.
    /// </summary>
    public string? PrReviewFollowThroughPullRequestUrl { get; private set; }

    /// <summary>
    /// The run whose posted review this follow-through belongs to — the run that had just completed
    /// when the wait began. Read rather than <see cref="CurrentRunId"/> by everything that has to
    /// name a run while the task waits, so a follow-through's own completion is attributed to the
    /// run that produced the review even if something later nulls the current one.
    /// </summary>
    public Guid? PrReviewFollowThroughRunId { get; private set; }

    /// <summary>
    /// Whether the follow-through poll has looked at the pull request at least once since the wait
    /// began. It is what tells "no threads outstanding" from "not looked at yet" — two different
    /// facts, and only one of them is Done's business — so every surface that would otherwise
    /// print a count of zero says "not looked at yet" instead.
    /// <para>
    /// It is deliberately NOT a gate on notifying. The first look counts replies like any other,
    /// because a reviewer's own comments are not replies to them and so there is no self-wake to
    /// suppress — where suppressing that whole first look lost every answer that beat it to the
    /// pull request (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    public bool PrReviewFollowThroughObserved { get; private set; }

    /// <summary>
    /// The GitHub login whose review threads this follow-through is about, as the last poll read
    /// it back from <c>gh</c>, or null before the first poll has looked. Never a configured name
    /// (the same discipline <c>AutoPrReviewEngine</c> keeps): it is re-read every sweep, and this
    /// is only the record of what the last one saw.
    /// </summary>
    public string? PrReviewReviewerLogin { get; private set; }

    /// <summary>
    /// The reviewer's own threads on the watched pull request as the last poll counted them —
    /// the comparison point that tells the next poll "somebody answered" from "nothing has
    /// happened since". Empty before the first poll, and empty afterwards for a pull request where
    /// the reviewer opened no threads at all (an approval with only a note, a review posted by
    /// hand as a plain comment).
    /// </summary>
    private readonly List<PrReviewThreadWatermark> _prReviewThreads = [];
    public IReadOnlyList<PrReviewThreadWatermark> PrReviewThreads => _prReviewThreads;

    /// <summary>How many of <see cref="PrReviewThreads"/> the last poll found still unresolved — what holds the follow-through open.</summary>
    public int PrReviewOpenThreadCount => _prReviewThreads.Count(thread => !thread.IsResolved);

    /// <summary>
    /// The reviewer's threads as the FIRST poll of this follow-through counted them, and the one
    /// thing here that does not move (self-review, round one) — which is what makes it the record
    /// <c>h9k pr review --since-my-review</c> reads a thread's own RESOLUTION against: resolved
    /// since your review is news even on a thread that gained no comment, and
    /// <see cref="PrReviewThreads"/> cannot say it, because it advances with every poll.
    /// <para>
    /// The COMMENTS a scoped lap shows are not diffed against it — those are the thread's own
    /// comments after the reviewer's last word in it, read live
    /// (<c>ReviewThread.FirstReplyIndexFor</c>). This anchor could not answer that half honestly:
    /// it is taken a poll interval after the review was posted, so a reply that beat the first
    /// poll there is already inside it, and skipping past it hid exactly that reply (independent
    /// pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// <para>
    /// The first poll rather than the review itself, because that is the earliest thread state
    /// this platform actually observed: the verdict is posted through GitHub and the response
    /// carries no thread ids, so nothing at delivery time knows what the review left behind. A
    /// scoped lap opened before any poll has run finds this empty, which reads as "no resolution
    /// change observed" — the honest answer rather than a guess.
    /// </para>
    /// </summary>
    private readonly List<PrReviewThreadWatermark> _prReviewReviewedThreads = [];
    public IReadOnlyList<PrReviewThreadWatermark> PrReviewReviewedThreads => _prReviewReviewedThreads;

    /// <summary>
    /// The head the review itself was posted against, as <see cref="Events.PullRequestReviewFollowThroughOpened"/>
    /// recorded it, or null when no verdict recorded one (a review closed the older way, with
    /// nothing posted from here). The fixed half of the same pair as
    /// <see cref="PrReviewReviewedThreads"/>: <see cref="PrReviewObservedHeadSha"/> advances with
    /// every poll, so a scoped lap diffing the code half against THAT would ask git for
    /// <c>head..head</c> and report no commits at all right after a push was announced.
    /// </summary>
    public string? PrReviewReviewedHeadSha { get; private set; }

    /// <summary>Whether the last poll found a review request outstanding for <see cref="PrReviewReviewerLogin"/> — the second reason a follow-through stays open.</summary>
    public bool PrReviewReReviewRequested { get; private set; }

    /// <summary>The watched pull request's head as the last poll read it, or null before the first poll — what a moved head is compared against.</summary>
    public string? PrReviewObservedHeadSha { get; private set; }

    /// <summary>
    /// How many commits the watched pull request carried at the last poll, or null when the
    /// provider reported no count — an honest absence that costs the next poll its "new commits"
    /// number and nothing else.
    /// </summary>
    public int? PrReviewObservedCommitCount { get; private set; }

    /// <summary>
    /// What the most recent <see cref="Apply(Events.PullRequestReviewAuthorResponded)"/> said the
    /// author had done, or null when the author has not answered since the review was posted.
    /// The one line every surface shows for a follow-through that needs the reviewer back.
    /// </summary>
    public string? PrReviewAuthorActivitySummary { get; private set; }

    /// <summary>
    /// Blocker ids a human has already acknowledged as open and chosen to claim across anyway
    /// (<see cref="Handlers.TaskDecider.ClaimDeliberately"/>'s or
    /// <see cref="Handlers.TaskDecider.ClaimInteractively"/>'s own Blocked-entry branch,
    /// <c>h9k task start</c>/<c>h9k task work --acknowledge-unmet-dependencies</c>) — what lets a
    /// later deliberate claim on the SAME still-open blockers proceed without asking again (idea
    /// fcaded0b's R7 ruling: "an acknowledgment already given at claim time carries forward
    /// without re-asking"; <c>h9k task handback --now</c> is one consumer, a reclaim through
    /// <c>h9k task work</c> is another). Reset by a fresh assignment
    /// (<see cref="Apply(TaskAssigned)"/>, <see cref="Apply(TaskUnassigned)"/>): a new assignment
    /// cycle's blockers are a new set of facts to warn about, even when some ids happen to repeat.
    /// </summary>
    private readonly List<Guid> _acknowledgedUnmetDependencyIds = [];
    public IReadOnlyList<Guid> AcknowledgedUnmetDependencyIds => _acknowledgedUnmetDependencyIds;

    /// <summary>
    /// Whether every dependency still blocking this task was already acknowledged by an earlier
    /// claim — what a later claim attempt (a reclaim of a Blocked task after a handback or a
    /// retry) checks before asking a human to pass <c>--acknowledge-unmet-dependencies</c> again.
    /// </summary>
    public bool UnmetDependenciesAlreadyAcknowledged =>
        _unmetDependencies.Count > 0 && _unmetDependencies.All(_acknowledgedUnmetDependencyIds.Contains);

    public void Apply(TaskAdded @event)
    {
        Id = @event.Id;
        ProjectId = @event.ProjectId;
        Objective = @event.Objective;
        _acceptanceCriteria.Clear();
        _acceptanceCriteria.AddRange(@event.AcceptanceCriteria);
        Type = @event.Type;
        AgentContext = @event.AgentContext;
        Constraints = @event.Constraints;
        ExternalReference = @event.ExternalReference;
        Model = @event.Model ?? AgentModel.Unknown;
        AddedAt = @event.AddedAt;
        AddedByOwnerId = @event.AddedByOwnerId;
        SourceIdeaId = @event.SourceIdeaId;
        EpicId = @event.EpicId;
        ReviewStageComposition = @event.ReviewStageComposition;
        _blockedBy.Clear();
        _blockedBy.AddRange(@event.BlockedBy ?? []);
        StackedOnTaskId = @event.StackedOnTaskId;
        PreApproval = @event.EffectivePreApproval;
        Origin = @event.Origin;
        StackedOnPullRequestNumber = @event.StackedOnPullRequestNumber;

        if (@event.StartsAsDraft)
        {
            State = TaskState.Draft;
            return;
        }

        // A stream written before the lifecycle split (Decisions Log #34) replays as it
        // behaved: queued on arrival, assigned to the owner who added it. That owner is the
        // sole owner of a v0 install, so this reads an observed fact rather than inventing
        // provenance for a historical task.
        AssignedOwnerId = @event.AddedByOwnerId;
        State = TaskState.Queued;
    }

    public void Apply(TaskPublished @event)
    {
        State = TaskState.Published;
        PreApproval = @event.EffectivePreApproval;
        if (@event.CloseLinkedIssue.HasValue)
        {
            CloseLinkedIssue = @event.CloseLinkedIssue.Value;
        }
    }

    public void Apply(TaskPreApprovedSet @event) => PreApproval = @event.EffectivePreApproval;

    public void Apply(TaskMechanicalResolutionAttempted @event) => MechanicalResolutionAttempts++;

    // The in-run half of the rebase budget Apply(TaskReopened)'s StackReplay arm spends the
    // dispatched half of — see StackedCheckpointRebased's own doc for why one budget covers both.
    public void Apply(StackedCheckpointRebased @event) => StackReplaysDispatched++;

    // Absent means "left alone" — a revision that reworded the objective must not also claim
    // the criteria were retyped identically (Optional carries that distinction).
    public void Apply(TaskRevised @event)
    {
        if (@event.Objective.HasValue)
        {
            Objective = @event.Objective.Value ?? string.Empty;
        }

        if (@event.AcceptanceCriteria.HasValue)
        {
            _acceptanceCriteria.Clear();
            _acceptanceCriteria.AddRange(@event.AcceptanceCriteria.Value ?? []);
        }

        if (@event.AgentContext.HasValue)
        {
            AgentContext = @event.AgentContext.Value;
        }

        if (@event.BlockedBy.HasValue)
        {
            _blockedBy.Clear();
            _blockedBy.AddRange(@event.BlockedBy.Value ?? []);
        }

        // Applied after BlockedBy on purpose: a revision that rewrites both must land the new
        // dependency set before the edge that has to be a member of it, so the invariant the
        // decider enforced (StackedOnTaskId is one of BlockedBy) is what this aggregate ends up
        // holding rather than the edge being checked against the previous set.
        if (@event.StackedOnTaskId.HasValue)
        {
            StackedOnTaskId = @event.StackedOnTaskId.Value;
        }

        // The remote form of the same declaration, applied beside its twin and for the same reason
        // it sits after BlockedBy. Repointing the edge discards what was observed about the parent
        // that is no longer declared: the recorded state, head branch and hold all describe a
        // DIFFERENT pull request, and carrying them onto the new one would let a child dispatch on
        // its predecessor's Open (adversarial reading of the revise path).
        if (@event.StackedOnPullRequestNumber.HasValue)
        {
            StackedOnPullRequestNumber = @event.StackedOnPullRequestNumber.Value;
            ForgetRemoteStackedParentObservation();
        }

        if (@event.Type.HasValue)
        {
            Type = @event.Type.Value ?? TaskType.Unknown;
        }

        if (@event.Model.HasValue)
        {
            Model = @event.Model.Value ?? AgentModel.Unknown;
        }

        if (@event.EpicId.HasValue)
        {
            EpicId = @event.EpicId.Value;
        }

        if (@event.QueuePriority.HasValue)
        {
            QueuePriorityMarked = @event.QueuePriority.Value;
        }

        if (@event.ReviewStageComposition.HasValue)
        {
            ReviewStageComposition = @event.ReviewStageComposition.Value;
        }

        if (@event.CloseLinkedIssue.HasValue)
        {
            CloseLinkedIssue = @event.CloseLinkedIssue.Value;
        }

        // The third clearing act alongside Apply(TaskHandedBack) and a default
        // Apply(TaskRequeued) — the one that needs no active interactive claim at all (task:
        // interactive mode becomes a recorded property of the task, the gap independent pre-PR
        // review, cycle 1, found in h9k task start).
        if (@event.ClearInteractiveMode)
        {
            InteractiveModeEnabled = false;
        }
    }

    public void Apply(TaskSessionCapOverridden @event) => SessionCap = @event.SessionCap;

    // State-agnostic, unlike Apply(TaskRevised): each cap is independent, so a call naming only
    // one leaves the other three untouched (absent means "leave alone").
    public void Apply(TaskReviewCapsOverridden @event)
    {
        if (@event.MaxComplianceReviewCycles.HasValue)
        {
            MaxComplianceReviewCycles = @event.MaxComplianceReviewCycles.Value;
        }

        if (@event.MaxAdversarialReviewCycles.HasValue)
        {
            MaxAdversarialReviewCycles = @event.MaxAdversarialReviewCycles.Value;
        }

        if (@event.MaxFinalFullPassRounds.HasValue)
        {
            MaxFinalFullPassRounds = @event.MaxFinalFullPassRounds.Value;
        }

        if (@event.LifetimeReviewCycleBudget.HasValue)
        {
            LifetimeReviewCycleBudget = @event.LifetimeReviewCycleBudget.Value;
        }
    }

    public void Apply(TaskReturnedToDraft @event) => State = TaskState.Draft;

    public void Apply(TaskAssigned @event)
    {
        AssignedOwnerId = @event.AssignedOwnerId;
        _unmetDependencies.Clear();
        _unmetDependencies.AddRange(@event.UnmetDependencies);
        _deadDependencies.Clear();
        _deadDependencyReasons.Clear();
        DependencyFailureReason = null;
        // A fresh assignment recomputes the blocker set from scratch, so any acknowledgment
        // recorded against the previous set no longer means anything — carrying it forward here
        // would let a stale acknowledgment silently cover a blocker nobody actually warned about
        // (task 45136b29, R7).
        _acknowledgedUnmetDependencyIds.Clear();
        // A declared remote parent that has not been observed open (or merged) lands the
        // assignment Blocked exactly as an unmet local blocker does, so an assignment can never
        // hand a stacked child to the dispatcher ahead of the pull request it stands on. What is
        // deliberately NOT reset alongside the dependency bookkeeping above is the observation
        // itself: the unmet set is a fact about this assignment and is recomputed per assignment,
        // while what GitHub last said about that pull request is a fact about the world, and
        // discarding it here would re-block a child whose parent is demonstrably open until the
        // next sweep looked again.
        State = _unmetDependencies.Count == 0 && !AwaitsRemoteStackedParent
            ? TaskState.Queued
            : TaskState.Blocked;
    }

    // Unassigning returns the task to the state it was assigned from, dependency bookkeeping
    // and all: the unmet set is only meaningful for an assigned task, and the next assignment
    // recomputes it against the dependencies as they stand then.
    public void Apply(TaskUnassigned @event)
    {
        AssignedOwnerId = null;
        _unmetDependencies.Clear();
        _deadDependencies.Clear();
        _deadDependencyReasons.Clear();
        DependencyFailureReason = null;
        _acknowledgedUnmetDependencyIds.Clear();
        State = TaskState.Published;
    }

    // Dependency bookkeeping only means anything while the task is Blocked, and the decider
    // only ever emits these three events from that state. Anything else on the stream is a lost
    // race — a human unassigned or abandoned the task between a resolver's read and its append
    // — and a lost race replays as a no-op rather than smearing dependency state across a
    // lifecycle that has already moved on.
    public void Apply(TaskDependencyCompleted @event)
    {
        if (State != TaskState.Blocked)
        {
            return;
        }

        _unmetDependencies.Remove(@event.DependencyId);
        if (_deadDependencies.Remove(@event.DependencyId))
        {
            // The blocker that died was retried and finished after all, so what it said stops
            // counting. Closeout is the ordinary unblocking path and carries no surviving
            // reason on the event the way a recovery does, so the display falls back to the
            // newest death this task still records: a reason it observed and kept, never a
            // stand-in for one nobody saw. Null when no blocker is left dead.
            _deadDependencyReasons.Remove(@event.DependencyId);
            DependencyFailureReason = _deadDependencies.Count == 0
                ? null
                : _deadDependencyReasons.GetValueOrDefault(_deadDependencies[^1]);
        }

        // A remote stacked parent holds the task exactly as an unmet local blocker does, so the
        // last local blocker clearing is not on its own enough to release one (task: a stacked
        // child can stand on a pull request another install owns). The two halves are asked
        // together here and in Apply(TaskAssigned) and Apply(RemoteStackedParentObserved), which
        // are the only three places the Blocked -> Queued door exists.
        if (_unmetDependencies.Count == 0 && !AwaitsRemoteStackedParent)
        {
            State = TaskState.Queued;
        }
    }

    /// <summary>
    /// One look at the pull request this task is stacked on (task: a stacked child can stand on a
    /// pull request another install owns). Recorded whatever the task's state — the surface says
    /// "last observed" and means it, so a delivered child's record stays current too — while the
    /// Blocked &lt;-> Queued door below is the one thing gated on state.
    /// <para>
    /// An observation naming a pull request this task no longer declares is dropped rather than
    /// recorded: a revision can repoint the edge between a sweep's read and its append, and letting
    /// the late write land would attribute another pull request's state to the declared parent.
    /// </para>
    /// </summary>
    public void Apply(RemoteStackedParentObserved @event)
    {
        if (StackedOnPullRequestNumber != @event.PullRequestNumber)
        {
            return;
        }

        RemoteStackedParentState = @event.State;
        RemoteStackedParentHeadBranch = @event.HeadBranch;
        RemoteStackedParentHeadCommit = @event.HeadCommit;
        RemoteStackedParentBaseBranch = @event.BaseBranch;
        RemoteStackedParentUrl = @event.Url;
        RemoteStackedParentWorkItem = @event.LinkedWorkItem;
        RemoteStackedParentObservedAt = @event.ObservedAt;
        RemoteStackedParentDetail = @event.Detail;

        // Derived from the state through the one rule both projections also ask, rather than
        // carried on the event: which observations need a human is a standing rule, not something
        // the sweep saw (RemoteStackedParentHold's own doc).
        RemoteStackedParentHoldReason = RemoteStackedParentHold.ReasonFor(
            @event.State, @event.PullRequestNumber);

        // The one door that swings both ways, because it is the one door where the answer can
        // change under a task that is not moving. A release is the Blocked -> Queued half every
        // other door also asks; the Queued -> Blocked half takes that release back when the same
        // question turns the other way, and it is not symmetry for its own sake. A Queued task has
        // no live run, so the release has not been spent: for a child that never dispatched a fresh
        // cut is still ahead of it, and StackedBaseResolver.ResolveRemote answers every state but
        // Open with the project's own base — a run carrying none of the parent's work, which is the
        // hazard this whole feature exists to prevent. Blocked is where the platform holds work
        // whose premise is in question, and with the hold reason recorded it is also what makes
        // h9k status read a dead parent as NeedsHuman, the same shape a dead local blocker uses
        // (log #22). Found undispatched-but-queued by the independent pre-PR review, cycle 1
        // (adversarial lens): the parent closed unmerged while the child sat in the queue waiting
        // for dispatch capacity, and nothing took the release back.
        //
        // A child BETWEEN runs (reopened, requeued, handed back, retried) sits Queued too, and it
        // is re-blocked here as well, deliberately: its branch does hold the parent's work already,
        // so nothing would be dropped by dispatching, but a follow-up on a parent that closed
        // without merging is a lap spent on a pull request that has nowhere to merge — which is the
        // same call closeout's own ParentDead park makes for its delivered sibling. This is not the
        // live question the four give-back doors are documented for refusing: those ask on every
        // give-back regardless of whether anything moved, while this fires only when an observation
        // actually changed the answer.
        if (State == TaskState.Blocked && _unmetDependencies.Count == 0 && !AwaitsRemoteStackedParent)
        {
            State = TaskState.Queued;
        }
        else if (State == TaskState.Queued && AwaitsRemoteStackedParent)
        {
            State = TaskState.Blocked;
        }
    }

    /// <summary>
    /// Drops everything recorded about a remote parent, because it describes a pull request this
    /// task no longer declares. Never a way to say "unobserved" about a parent still declared: that
    /// would be a guess dressed as a gap.
    /// </summary>
    private void ForgetRemoteStackedParentObservation()
    {
        RemoteStackedParentState = RemoteParentState.Unknown;
        RemoteStackedParentHeadBranch = string.Empty;
        RemoteStackedParentHeadCommit = string.Empty;
        RemoteStackedParentBaseBranch = string.Empty;
        RemoteStackedParentUrl = string.Empty;
        RemoteStackedParentWorkItem = null;
        RemoteStackedParentObservedAt = null;
        RemoteStackedParentDetail = null;
        RemoteStackedParentHoldReason = null;
    }

    // A dead blocker leaves the task Blocked on purpose: h9k status reads it as NeedsHuman
    // (the closeout park does the same, log #22), so the human sees it without the platform
    // either dispatching work whose premise died or stranding it in silence.
    public void Apply(TaskDependencyFailed @event)
    {
        if (State != TaskState.Blocked)
        {
            return;
        }

        // Oldest first, newest observation last, re-observations included: a blocker whose
        // death changed shape was seen just now, so it takes the newest slot rather than
        // keeping the one it held when it first died. Everything that reads this list
        // backwards for "the newest blocker still dead" depends on that staying true.
        _deadDependencies.Remove(@event.DependencyId);
        _deadDependencies.Add(@event.DependencyId);
        _deadDependencyReasons[@event.DependencyId] = @event.Reason;
        DependencyFailureReason = @event.Reason;
    }

    // The mirror of the hold: a blocker recorded dead was seen alive again, so the record that
    // held this task stops counting and the display returns to what the remaining blockers
    // actually say. Removing nothing means the recovery lost a race with a Completed or an
    // Unassign that already cleared the record, and a lost race is a no-op rather than a
    // reason wiped off a task that still has a dead blocker.
    public void Apply(TaskDependencyRecovered @event)
    {
        if (State != TaskState.Blocked || !_deadDependencies.Remove(@event.DependencyId))
        {
            return;
        }

        // Derived here rather than read off the event, exactly as the closeout path derives it.
        // A pass computes what survives from the world it read, and a death appended between
        // that read and this commit is invisible to it — trusting the snapshot would silence a
        // hold for a blocker that is still dead, and silence it for good, because every later
        // sweep compares against these records and finds the death already recorded.
        _deadDependencyReasons.Remove(@event.DependencyId);
        DependencyFailureReason = _deadDependencies.Count == 0
            ? null
            : _deadDependencyReasons.GetValueOrDefault(_deadDependencies[^1]);
    }

    public void Apply(TaskClaimed @event)
    {
        LeaseGeneration = @event.LeaseGeneration;
        ClaimedByNodeId = @event.NodeId;
        CurrentRunId = @event.RunId;
        _runIds.Add(@event.RunId);
        State = TaskState.Claimed;
        // The marker earned its turn the moment a run actually dispatches for it — cleared
        // regardless of which decider produced this claim (a node's ordinary Claim, an
        // operator's ClaimInteractively, or a deliberate ClaimDeliberately), so it never
        // outlives the dispatch it bought (task 45136b29, R7).
        QueuePriorityMarked = false;

        // Whether this claim's own acknowledgment was freshly given or carried forward from an
        // earlier one, the still-open blockers it covered are now on record acknowledged, so a
        // later reclaim of the same still-open set (after a handback or a retry) does not ask
        // again (design ruling R7).
        if (@event.DependencyOverrideAcknowledged)
        {
            _acknowledgedUnmetDependencyIds.Clear();
            _acknowledgedUnmetDependencyIds.AddRange(_unmetDependencies);
        }

        if (@event.InteractiveMode)
        {
            InteractiveModeEnabled = true;
        }
    }

    public void Apply(TaskRequeued @event)
    {
        ClaimedByNodeId = null;
        CurrentRunId = null;
        PendingQuestionId = null;
        EndAnyOpenReviewLap();
        // A deliberate start-it-mine claim (h9k task start --acknowledge-unmet-dependencies) can
        // give the claim back while its dependency snapshot still names an open blocker — Claim
        // never clears _unmetDependencies, only Assign does — and Queued is only reachable with
        // every dependency closed out (TaskDecider.Claim's own doc). Landing back on Blocked
        // instead preserves that invariant and lets TaskDependencyResolver's ordinary Blocked
        // sweep pick the task back up once the blocker actually clears.
        State = _unmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
        // The second exit door alongside Apply(TaskHandedBack) (design ruling R6, amended
        // 2026-09-05): a default h9k task release is the human's own explicit act of returning
        // the task to the machine, so it clears the flag exactly as handback does. Every other
        // requeue caller — a node's lease expiring, or a release given --keep-interactive — leaves
        // the flag alone by construction (TaskRequeued.ClearInteractiveMode's own doc).
        if (@event.ClearInteractiveMode)
        {
            InteractiveModeEnabled = false;
        }
    }

    public void Apply(QuestionAsked @event)
    {
        PendingQuestionId = @event.QuestionId;
        State = TaskState.NeedsHuman;
    }

    public void Apply(AnswerProvided @event)
    {
        PendingQuestionId = null;
        State = TaskState.Claimed;
    }

    public void Apply(TaskCompleted @event)
    {
        if (@event.PullRequestUrl is not null && @event.PullRequestUrl != PullRequestUrl)
        {
            ResetAutomaticCloseoutState();
        }

        PullRequestUrl = @event.PullRequestUrl;
        FollowUpBranch = null;
        FollowUpKind = FollowUpKind.Unknown;
        FollowUpPullRequestHeadSha = null;
        StackReplayUpstreamCommit = null;
        StackReplayOntoCommit = null;
        ChangesRequestedReviews = [];
        RetryBranch = null;
        State = TaskState.Done;
        // A marker set while this same claim was live (h9k task revise --queue-first on a
        // Claimed task) never goes through Apply(TaskClaimed) again, so nothing else clears it —
        // without this it would survive Done and misreport a finished task as still buying a
        // future dispatch turn (independent pre-PR review, cycle 1, adversarial lens).
        QueuePriorityMarked = false;
        // Ordinarily already closed by the verdict that got the task here — but a pr-review task
        // a reviewer opened a lap on and then closed the older way (h9k review resolve
        // --merge-ready, no verdict posted) reaches Done with the flag still true, and h9k task
        // show would report an open lap on a finished task.
        EndAnyOpenReviewLap();
        // Done is where every follow-through ends: every thread the reviewer opened resolved, or
        // the pull request merged or closed. Leaving the flag standing would keep the closeout
        // watcher spending a gh read per interval on a task nothing will act on again.
        EndAnyPrReviewFollowThrough();
    }

    public void Apply(TaskReopened @event)
    {
        FollowUpBranch = @event.Branch;
        FollowUpKind = @event.Kind ?? FollowUpKind.Unknown;
        FollowUpPullRequestHeadSha = @event.PullRequestHeadSha;
        StackReplayUpstreamCommit = @event.StackReplayUpstreamCommit;
        StackReplayOntoCommit = @event.StackReplayOntoCommit;
        ChangesRequestedReviews = @event.ChangesRequestedReviews ?? [];

        if (@event.Automatic && @event.Kind == FollowUpKind.StackReplay)
        {
            // A replay spends its own budget and nothing else's — see StackReplaysDispatched's own
            // doc for why the lifetime ceiling is deliberately left alone here. The obstruction
            // bookkeeping is left alone too: this lap cleared no obstruction of this task's own, so
            // touching LastAutomaticObstructionKey would tell the NEXT decision that whatever it
            // was working on had changed, restarting a progress count that should have kept
            // climbing.
            StackReplaysDispatched++;
        }
        else if (@event.Automatic)
        {
            CloseoutAttempts++;
            ConsecutiveObstructionLaps = @event.ObstructionKey is not null
                    && @event.ObstructionKey == LastAutomaticObstructionKey
                ? ConsecutiveObstructionLaps + 1
                : 1;
            LastAutomaticObstructionKey = @event.ObstructionKey;
            if (@event.ObstructionSummary is not null)
            {
                _automaticLapHistory.Add(@event.ObstructionSummary);
            }
        }
        else
        {
            ResetAutomaticCloseoutState();
        }

        _knownHumanReviewThreadIds.Clear();
        _knownHumanReviewThreadIds.AddRange(@event.KnownHumanReviewThreadIds ?? []);
        _knownPendingReviewRequestLogins.Clear();
        _knownPendingReviewRequestLogins.AddRange(@event.KnownPendingReviewRequestLogins ?? []);

        ClaimedByNodeId = null;
        PendingQuestionId = null;
        // Same invariant Apply(TaskRequeued) restores: a deliberately-claimed Blocked task
        // (h9k task start --acknowledge-unmet-dependencies) that reached Done/Reopened while
        // still carrying an unmet dependency — Claim never clears _unmetDependencies, only
        // Assign does — must not resurface as Queued while that dependency is still on record
        // unmet, or a closeout-dispatched follow-up runs headless behind the still-open blocker.
        State = _unmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
        // Landing on Blocked leaves no new run to take over this one's watch — nulling
        // CurrentRunId unconditionally, the way every other give-the-claim-back event does,
        // would silently drop CloseoutEngine's own merge/close detection for the pull request
        // this reopen just parked behind an open dependency, and would leave a later
        // h9k pr resolve (once the blocker clears) with no recorded run to follow up on at all
        // (adversarial review, cycle 1, on h9k task start). Landing on Queued still nulls it:
        // the next claim (TaskClaimed) overwrites it with the follow-up's own fresh run id
        // moments later, same as every sibling event.
        CurrentRunId = State == TaskState.Blocked ? @event.PreviousRunId : null;
        EndAnyOpenReviewLap();
        // TaskDecider.Reopen refuses a pr-review task outright, so this is defence rather than a
        // reachable path today — but a reopen lands the task Queued or Blocked, and a
        // follow-through flag surviving into either would advertise a watch nothing is doing.
        EndAnyPrReviewFollowThrough();
    }

    /// <summary>
    /// Closes <see cref="ReviewLapOpen"/> wherever a task gives its claim back, because a lap
    /// cannot outlive the claim it rides on: <c>h9k pr approve</c> / <c>h9k pr request-changes</c>
    /// refuse a task with no <see cref="CurrentRunId"/> ("no run to record the verdict against"),
    /// so once one of these events lands the verdict that would otherwise close the lap is
    /// unreachable and the flag can only ever be wrong from then on.
    /// <para>
    /// The flag was previously cleared by <see cref="Apply(Events.PullRequestReviewVerdictDelivered)"/>
    /// alone, which left exactly one honest way to leave a lap — <c>h9k task release</c>, accepted
    /// because a lap's claim carries the interactive <see cref="Guid.Empty"/> sentinel — with the
    /// flag stuck true forever. The daemon then read it on a LATER, automated run of the same task
    /// and skipped that run's adoption as "a reviewer owns it": a dead agent never failed, a live
    /// one never re-monitored, and the task Claimed indefinitely with no lease to expire
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// <para>
    /// <see cref="ReviewLapRunId"/> and <see cref="ReviewLapWorktreePath"/> are deliberately left
    /// alone, the same choice the verdict makes: they are provenance of a lap that happened, and
    /// the pair a reader wants afterwards is "a lap ran, on this run" whether it ended in a
    /// verdict or in the reviewer walking away.
    /// </para>
    /// </summary>
    private void EndAnyOpenReviewLap() => ReviewLapOpen = false;

    /// <summary>
    /// Zeroes every automatic-closeout counter — the progress cap
    /// (<see cref="ConsecutiveObstructionLaps"/>, <see cref="LastAutomaticObstructionKey"/>,
    /// <see cref="AutomaticLapHistory"/>) and the lifetime ceiling (<see cref="CloseoutAttempts"/>)
    /// — along with the human-engagement watermarks, so closeout starts unencumbered whenever a
    /// human grants a fresh attempt (a manual <c>TaskReopened</c>) or the task lands on a pull
    /// request its spend was never scoped to (a <c>h9k task retry</c> that opens a second pull
    /// request; independent pre-PR review, 2026-08-23). Without the latter case, a task retried
    /// onto PR#2 would start pre-debited and pre-capped by PR#1's spend, and a lifetime-ceiling
    /// park would misattribute PR#1's lap history to a pull request that no longer exists — the
    /// unobserved-fact attribution AGENTS.md's never-guess rule forbids.
    /// </summary>
    private void ResetAutomaticCloseoutState()
    {
        CloseoutAttempts = 0;
        StackReplaysDispatched = 0;
        ConsecutiveObstructionLaps = 0;
        LastAutomaticObstructionKey = null;
        _automaticLapHistory.Clear();
        _knownHumanReviewThreadIds.Clear();
        _knownPendingReviewRequestLogins.Clear();
        // A human grant (a manual h9k pr resolve) or a fresh pull request (a retry that opens a
        // second one) restarts the pre-approved auto-merge budget alongside the ordinary closeout
        // one, the same "matching the existing pr-resolve retry-budget pattern" the feature's own
        // acceptance criteria calls for.
        MechanicalResolutionAttempts = 0;
    }

    // The mirror of TaskRetried, from Claimed (interactive) rather than Failed: the branch an
    // operator cut interactively is what the next claim — headless dispatch, through the
    // ordinary Queued path — resumes via RetryBranch, exactly like a human-requested retry's
    // surviving branch (Decisions Log #25).
    public void Apply(TaskHandedBack @event)
    {
        RetryBranch = @event.Branch;
        ClaimedByNodeId = null;
        CurrentRunId = null;
        PendingQuestionId = null;
        EndAnyOpenReviewLap();
        // The one explicit human act that clears interactive mode (design ruling R9): the task
        // goes back to the machine, headless from here, and every later boundary this run's
        // engines own goes back to advancing on its own.
        InteractiveModeEnabled = false;
        // Same invariant Apply(TaskRequeued) restores: a handback out of a deliberately-claimed
        // Blocked task must not resurface as Queued while a dependency is still on record unmet.
        State = _unmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(TaskFailed @event) => State = TaskState.Failed;

    // The failure stays on the stream; resolve only moves the state and records where the
    // work landed. A resolved task is Done like any other — reopenable when it has a PR.
    public void Apply(TaskResolved @event)
    {
        if (@event.PullRequestUrl is not null && @event.PullRequestUrl != PullRequestUrl)
        {
            ResetAutomaticCloseoutState();
        }

        PullRequestUrl = @event.PullRequestUrl ?? PullRequestUrl;
        FollowUpBranch = null;
        FollowUpKind = FollowUpKind.Unknown;
        FollowUpPullRequestHeadSha = null;
        StackReplayUpstreamCommit = null;
        StackReplayOntoCommit = null;
        ChangesRequestedReviews = [];
        RetryBranch = null;
        State = TaskState.Done;
        // Same reasoning as Apply(TaskCompleted): a resolved task reaches Done without ever
        // routing back through Apply(TaskClaimed), so a marker set earlier in its life would
        // otherwise survive it.
        QueuePriorityMarked = false;
        // Same case as Apply(TaskCompleted): a lap left open on a task closed the older way.
        EndAnyOpenReviewLap();
        // Same case again, reached the one way Resolve can reach it: a waiting review's own later
        // run failed — a scoped lap that died — leaving the follow-through flag standing on a
        // FAILED task, which is the only state TaskDecider.Resolve accepts (Decisions Log #27).
        // Resolve is therefore never the reviewer's lever for ending a live follow-through, and
        // nothing on this feature advertises it as one (independent pre-PR review, cycle 1, both
        // lenses); it is the attestation exit from that failure, and Done ends the watch on the
        // way through.
        EndAnyPrReviewFollowThrough();
    }

    public void Apply(TaskRetried @event)
    {
        RetryBranch = @event.Branch;
        ClaimedByNodeId = null;
        CurrentRunId = null;
        PendingQuestionId = null;
        EndAnyOpenReviewLap();
        // Same invariant Apply(TaskRequeued) restores: Retry runs from Failed, and a deliberately-
        // claimed Blocked task whose worktree cut failed can still carry an unmet dependency here.
        State = _unmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    // Publication is a side errand rather than a lifecycle move: the task's state is untouched
    // by all three of these, because asking for a card, spawning the session, and the session
    // ending say nothing about whether the work is drafted, queued, or done. What they move is
    // the pending marker the daemon's loop reads.
    public void Apply(WorkItemPublicationRequested @event)
    {
        PendingPublicationProvider = @event.Provider;
        PendingPublicationProjectKey = @event.ProjectKey;
        PublicationSessionDispatched = false;
    }

    // Dispatched is the whole of what this aggregate needs: it is the guard that stops a second
    // session, and it is true from the moment the dispatch is committed — before the process
    // exists. Which process it turned out to be (WorkItemPublicationSessionStarted) is a question
    // only the daemon's adoption asks, so it is projected onto TaskDetails and deliberately not
    // applied here.
    public void Apply(WorkItemPublicationDispatched @event) => PublicationSessionDispatched = true;

    public void Apply(WorkItemPublicationCompleted @event)
    {
        PendingPublicationProvider = null;
        PendingPublicationProjectKey = JiraProjectKey.None;
        PublicationSessionDispatched = false;
    }

    // The link is the errand's real ending, whether or not the session that produced it has
    // exited yet: once the task carries a reference there is nothing left to publish, and a
    // pending marker left standing would let the next sweep dispatch a second card for a task
    // that already has one.
    public void Apply(WorkItemLinked @event)
    {
        ExternalReference = @event.Reference;
        PendingPublicationProvider = null;
        PendingPublicationProjectKey = JiraProjectKey.None;
        PublicationSessionDispatched = false;
    }

    // Requested, then zero or more auth failures, then finally a success or a terminal (non-auth)
    // failure: the shape that lets a rejected credential retry the identical payload rather than
    // losing it (Brian's design, 2026-08-28).
    public void Apply(JiraWriteRequested @event)
    {
        PendingJiraWriteId = @event.WriteId;
        PendingJiraWriteOperation = @event.Operation;
        PendingJiraWriteIssueKey = @event.IssueKey;
        PendingJiraWritePayloadJson = @event.PayloadJson;
        PendingJiraWriteRequestedByOwnerId = @event.RequestedByOwnerId;
        PendingJiraWriteFailureReason = null;
        PendingJiraWriteIsAuthFailure = false;
    }

    public void Apply(JiraWriteSucceeded @event)
    {
        if (PendingJiraWriteId != @event.WriteId)
        {
            return;
        }

        ClearPendingJiraWrite();
    }

    public void Apply(JiraWriteFailed @event)
    {
        if (PendingJiraWriteId != @event.WriteId)
        {
            return;
        }

        if (@event.IsAuthFailure)
        {
            // Kept pending on purpose: the payload that just failed is exactly what the retry
            // sweep re-attempts once the connection is fixed, so nothing about the request is forgotten.
            PendingJiraWriteFailureReason = @event.Reason;
            PendingJiraWriteIsAuthFailure = true;
        }
        else
        {
            ClearPendingJiraWrite();
        }
    }

    public void Apply(JiraMergeNoticeQueued @event) => HasQueuedJiraMergeNotice = true;

    public void Apply(JiraMergeNoticeAttempted @event) => HasQueuedJiraMergeNotice = false;

    public void Apply(PullRequestReviewAssignmentObserved @event) => AutoPrReviewAssigneeLogin = @event.AssigneeLogin;

    // State is never touched here (see the event's own doc comment): the caller that appends
    // this decides Concluded from the state it read before appending, and a following
    // TaskAbandoned (never this Apply) is what actually moves State when it concluded the task.
    public void Apply(PullRequestReviewAssignmentRecalled @event) => AutoPrReviewAssigneeLogin = null;

    // State is never touched here: a mention attaches to whatever state the task is already in
    // (Queued, Claimed, AwaitingAuthor, NeedsHuman) — the caller that appends this is the one
    // that decides, separately, whether the mention also earns a mint, a claim, or a dispatched
    // follow-up, all of which carry their own events.
    public void Apply(PullRequestReviewMentionObserved @event)
    {
        LatestMentionCommentId = @event.CommentId;
        LatestMentionAuthorLogin = @event.CommentAuthorLogin;
        LatestMentionBody = @event.CommentBody;
        LatestMentionUrl = @event.CommentUrl;
        LatestMentionCreatedAt = @event.CommentCreatedAt;
        LatestMentionCommentDatabaseId = @event.CommentDatabaseId;
    }

    public void Apply(PullRequestReviewLapOpened @event)
    {
        ReviewLapOpen = true;
        ReviewLapRunId = @event.RunId;
        // Blank means --no-worktree skipped the checkout, which is an absence and is stored as
        // one rather than as an empty path a caller might hand to `git worktree remove`.
        ReviewLapWorktreePath = @event.WorktreePath.IsNotBlank() ? @event.WorktreePath : null;
    }

    // State is never touched here: the verdict is a GitHub review that already happened, and
    // what moves this task to Done is the run's own PrReviewDelivered reaching PrReviewEngine's
    // finalize — exactly as h9k review resolve --merge-ready already did before this feature.
    public void Apply(PullRequestReviewVerdictDelivered @event)
    {
        ReviewLapOpen = false;
        ReviewerVerdict = @event.Verdict;
    }

    // The one place a pr-review task's own finalize now lands instead of Done (task: a pr-review
    // task stays open while the pull request's review threads are unresolved). CurrentRunId is
    // deliberately kept: the run has ended, but it is what the scoped lap
    // (h9k pr review --since-my-review) reads the original findings report from, and clearing it
    // would leave a waiting task unable to name the review it is waiting on.
    public void Apply(PullRequestReviewFollowThroughOpened @event)
    {
        PrReviewFollowThroughOpen = true;
        PrReviewFollowThroughPullRequestUrl = @event.PullRequestUrl;
        PrReviewFollowThroughRunId = @event.RunId;
        PrReviewFollowThroughObserved = false;
        PrReviewReviewerLogin = null;
        PrReviewReReviewRequested = false;
        PrReviewAuthorActivitySummary = null;
        PrReviewObservedCommitCount = null;
        _prReviewThreads.Clear();
        _prReviewReviewedThreads.Clear();
        PrReviewObservedHeadSha = @event.HeadSha;
        PrReviewReviewedHeadSha = @event.HeadSha;
        State = TaskState.AwaitingAuthor;
        // Same dead end Apply(TaskCompleted) reasons about: a marker bought for a dispatch slot
        // that a waiting task will not take must not survive into the state it waits in.
        QueuePriorityMarked = false;
        EndAnyOpenReviewLap();
    }

    // State is never touched here (see the event's own doc): this is the watermark a LATER poll
    // compares against, and what moves the task is the PullRequestReviewAuthorResponded appended
    // beside it, or an ordinary TaskCompleted.
    public void Apply(PullRequestReviewFollowThroughObserved @event)
    {
        // The FIRST observation of this follow-through fixes the review's own anchor and no later
        // one touches it again (see PrReviewReviewedThreads): that is what the scoped lap diffs
        // against, and advancing it with the poll would erase exactly the replies the needs-you
        // line was about.
        if (!PrReviewFollowThroughObserved)
        {
            _prReviewReviewedThreads.AddRange(@event.Threads);
        }

        PrReviewFollowThroughObserved = true;
        PrReviewReviewerLogin = @event.ReviewerLogin;
        _prReviewThreads.Clear();
        _prReviewThreads.AddRange(@event.Threads);
        PrReviewReReviewRequested = @event.ReReviewRequested;
        PrReviewObservedHeadSha = @event.HeadSha;
        PrReviewObservedCommitCount = @event.CommitCount;
    }

    // Ordered after the observation it rides with, which is why nothing about the watermark is
    // touched here: that event already re-baselined it, so the same replies cannot fire a second
    // notification on the next poll.
    public void Apply(PullRequestReviewAuthorResponded @event)
    {
        PrReviewAuthorActivitySummary = @event.Summary;
        State = TaskState.NeedsHuman;
    }

    /// <summary>
    /// Stops the follow-through, because whatever reaches here has ended it: Done
    /// (<see cref="Apply(TaskCompleted)"/>, <see cref="Apply(TaskResolved)"/>) or a human walking
    /// away (<see cref="Apply(TaskAbandoned)"/>). What it clears is everything that would go on
    /// speaking as if the watch were live — the open flag, an outstanding re-review request, the
    /// author-activity line, and the two thread watermarks a scoped lap diffs against. Left
    /// standing, the flag alone would keep the closeout watcher's follow-through sweep spending a
    /// <c>gh</c> read per interval, forever, on a task nothing will act on again — and the
    /// summary would keep a stale "the author replied" line on a row whose story is over.
    /// <para>
    /// The rest is deliberately left alone, the same choice <see cref="EndAnyOpenReviewLap"/>
    /// makes for the lap's own run and worktree: <see cref="PrReviewFollowThroughPullRequestUrl"/>,
    /// <see cref="PrReviewFollowThroughRunId"/>, <see cref="PrReviewFollowThroughObserved"/>,
    /// <see cref="PrReviewReviewerLogin"/>, <see cref="PrReviewReviewedHeadSha"/>,
    /// <see cref="PrReviewObservedHeadSha"/> and <see cref="PrReviewObservedCommitCount"/> are
    /// provenance — which pull request was followed through, on whose behalf, whether it was ever
    /// looked at, and what the last look actually saw — and a reader of a closed-out task wants
    /// those facts more than they want the fields blank. None of them can restart anything,
    /// because every reader of them gates on the follow-through being open first.
    /// </para>
    /// </summary>
    private void EndAnyPrReviewFollowThrough()
    {
        PrReviewFollowThroughOpen = false;
        PrReviewReReviewRequested = false;
        PrReviewAuthorActivitySummary = null;
        _prReviewThreads.Clear();
        _prReviewReviewedThreads.Clear();
    }

    /// <summary>
    /// Whether this task's own <c>--from-pr</c> adoption should be answered by naming THIS task
    /// rather than minting a second one (task: a pr-review task stays open while the pull
    /// request's review threads are unresolved). Both a waiting review and a completed one
    /// qualify: the waiting one is genuinely still live on that pull request, and the completed
    /// one is the record of a review whose findings and verdict a second adoption would abandon.
    /// </summary>
    public bool HoldsPullRequestForRepeatAdoption =>
        Type == TaskType.PrReview
        && (State == TaskState.AwaitingAuthor
            || State == TaskState.Done
            || (State == TaskState.NeedsHuman && PrReviewFollowThroughOpen));

    private void ClearPendingJiraWrite()
    {
        PendingJiraWriteId = null;
        PendingJiraWriteOperation = JiraWriteOperation.Unknown;
        PendingJiraWriteIssueKey = null;
        PendingJiraWritePayloadJson = null;
        PendingJiraWriteFailureReason = null;
        PendingJiraWriteIsAuthFailure = false;
    }

    // Abandoning consumes the pending-work markers like Complete and Resolve do — a dead
    // task must not advertise a resumable branch — and clears the pending question so a
    // late answer cannot flip an Abandoned task back to Claimed (Answer guards on
    // PendingQuestionId, not on state).
    public void Apply(TaskAbandoned @event)
    {
        PendingQuestionId = null;
        FollowUpBranch = null;
        FollowUpKind = FollowUpKind.Unknown;
        FollowUpPullRequestHeadSha = null;
        StackReplayUpstreamCommit = null;
        StackReplayOntoCommit = null;
        ChangesRequestedReviews = [];
        RetryBranch = null;
        State = TaskState.Abandoned;
        // Same reasoning as Apply(TaskCompleted): a marker set earlier in this task's life is a
        // dead end here — Abandoned never reopens — so it must not survive to be read back.
        QueuePriorityMarked = false;
        // Same case, and the same dead end: an abandoned task has no lap open on it.
        EndAnyOpenReviewLap();
        // And the same dead end for the follow-through: abandoning a waiting pr-review task is
        // exactly how a reviewer says they are done watching that pull request, so the watch
        // must stop rather than outlive the task that owned it.
        EndAnyPrReviewFollowThrough();

        // A publication nobody has started yet is one of those markers, for the reason
        // TaskDecider.RequestWorkItemPublication refuses to make one: filing a card for abandoned
        // work puts it on somebody's board when nobody here intends to do it. That rule only held
        // at the moment of asking — the daemon's sweep reads the marker and never the state, so
        // abandoning between the request and the sweep still produced a card, and one nothing
        // could then record either, because linking an abandoned task is refused too. Origin
        // incident (2026-08-22): the pre-PR review of this branch traced it from push-to-jira with
        // the daemon stopped, then abandon, then the daemon starting.
        //
        // A dispatched publication keeps its markers. A session is already out there writing a
        // card, and those markers are how adoption finds it, waits for it and ends it honestly;
        // clearing them here would leave it detached with nothing watching.
        if (!PublicationSessionDispatched)
        {
            PendingPublicationProvider = null;
            PendingPublicationProjectKey = JiraProjectKey.None;
        }

        // A queued merge notice is the same kind of marker: nothing here is still owed once a
        // human has walked away from the task, and leaving it set would have the retry sweep
        // deliver a "the pull request merged" comment for work nobody intends to do the moment
        // whatever was blocking it happens to clear (independent pre-PR review, cycle 5).
        HasQueuedJiraMergeNotice = false;

        // Deliberately left standing here, unlike every other marker above: PendingJiraWriteId is
        // the key JiraWriteCoordinator re-reads by writeId to record an already-in-flight write's
        // outcome (RecordJiraWriteSuccess/RecordJiraWriteFailure both throw when it does not
        // match), and a create or update dispatched moments before this abandon can still be
        // executing against Jira when this event lands — clearing it here would make that write's
        // own outcome unrecordable even though Jira genuinely carried it out, stranding a real card
        // with a JiraWriteRequested on the stream and no JiraWriteSucceeded to match it
        // (independent pre-PR review, adversarial lens, cycle 6). TaskDetails — not this aggregate
        // — is what TaskShowCommand and the retry sweep's stale-write query actually read, so
        // clearing the equivalent marker there (TaskDetails.Apply(TaskAbandoned)) is what stops
        // the dead "the registered Jira credential was rejected" row and keeps this write's own
        // outcome recordable
        // at the same time.
    }
}
