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
    /// The owner's standing pre-approval (task: a task can be published pre-approved): once true,
    /// the daemon merges this task's pull request on its own, deterministically, the moment
    /// GitHub's own gates read satisfied — CI green, the review decision satisfied, no outstanding
    /// requested reviewer, every review thread resolved, and no follow-up live or queued. Every
    /// existing human waypoint (Failed, a review park, a severity-bar failure, a cap trip) still
    /// stops the pipeline exactly as it does for an unflagged task; this flag only removes the
    /// owner as a SYNCHRONOUS gate at the pull request, never any of those. Set at publish
    /// (<see cref="Events.TaskPublished.PreApproved"/>), defaulting false, and flippable
    /// afterward on any live non-terminal task via <see cref="Events.TaskPreApprovedSet"/>.
    /// </summary>
    public bool PreApproved { get; private set; }

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
        PreApproved = @event.PreApproved;
    }

    public void Apply(TaskPreApprovedSet @event) => PreApproved = @event.PreApproved;

    public void Apply(TaskMechanicalResolutionAttempted @event) => MechanicalResolutionAttempts++;

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
        State = _unmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
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

        if (_unmetDependencies.Count == 0)
        {
            State = TaskState.Queued;
        }
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
        RetryBranch = null;
        State = TaskState.Done;
        // A marker set while this same claim was live (h9k task revise --queue-first on a
        // Claimed task) never goes through Apply(TaskClaimed) again, so nothing else clears it —
        // without this it would survive Done and misreport a finished task as still buying a
        // future dispatch turn (independent pre-PR review, cycle 1, adversarial lens).
        QueuePriorityMarked = false;
    }

    public void Apply(TaskReopened @event)
    {
        FollowUpBranch = @event.Branch;
        FollowUpKind = @event.Kind ?? FollowUpKind.Unknown;

        if (@event.Automatic)
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
    }

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
        RetryBranch = null;
        State = TaskState.Done;
        // Same reasoning as Apply(TaskCompleted): a resolved task reaches Done without ever
        // routing back through Apply(TaskClaimed), so a marker set earlier in its life would
        // otherwise survive it.
        QueuePriorityMarked = false;
    }

    public void Apply(TaskRetried @event)
    {
        RetryBranch = @event.Branch;
        ClaimedByNodeId = null;
        CurrentRunId = null;
        PendingQuestionId = null;
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
        RetryBranch = null;
        State = TaskState.Abandoned;
        // Same reasoning as Apply(TaskCompleted): a marker set earlier in this task's life is a
        // dead end here — Abandoned never reopens — so it must not survive to be read back.
        QueuePriorityMarked = false;

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
