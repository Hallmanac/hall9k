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
    /// True when the most recent failed attempt was an expired or missing twg login — the one
    /// failure that keeps the write pending rather than ending it, and what the attention pane
    /// and the daemon's retry sweep both key on.
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
    /// This task's own override of how many agent sessions its run may hold simultaneously; null
    /// means the node's global <c>SessionCapPerRun</c> default decides (Decisions Log #108).
    /// Unlike <see cref="Model"/>, settable at any time — including mid-run — via
    /// <see cref="TaskSessionCapOverridden"/> rather than <see cref="TaskRevised"/>.
    /// </summary>
    public int? SessionCap { get; private set; }
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

    public void Apply(TaskPublished @event) => State = TaskState.Published;

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
    }

    public void Apply(TaskSessionCapOverridden @event) => SessionCap = @event.SessionCap;

    public void Apply(TaskReturnedToDraft @event) => State = TaskState.Draft;

    public void Apply(TaskAssigned @event)
    {
        AssignedOwnerId = @event.AssignedOwnerId;
        _unmetDependencies.Clear();
        _unmetDependencies.AddRange(@event.UnmetDependencies);
        _deadDependencies.Clear();
        _deadDependencyReasons.Clear();
        DependencyFailureReason = null;
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
    }

    public void Apply(TaskRequeued @event)
    {
        ClaimedByNodeId = null;
        CurrentRunId = null;
        PendingQuestionId = null;
        State = TaskState.Queued;
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
        CurrentRunId = null;
        PendingQuestionId = null;
        State = TaskState.Queued;
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
        State = TaskState.Queued;
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
    }

    public void Apply(TaskRetried @event)
    {
        RetryBranch = @event.Branch;
        ClaimedByNodeId = null;
        CurrentRunId = null;
        PendingQuestionId = null;
        State = TaskState.Queued;
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
    // failure: the shape that lets an expired twg login retry the identical payload rather than
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
            // sweep re-attempts once twg login runs, so nothing about the request is forgotten.
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
        // executing against twg when this event lands — clearing it here would make that write's
        // own outcome unrecordable even though twg genuinely carried it out, stranding a real card
        // with a JiraWriteRequested on the stream and no JiraWriteSucceeded to match it
        // (independent pre-PR review, adversarial lens, cycle 6). TaskDetails — not this aggregate
        // — is what TaskShowCommand and the retry sweep's stale-write query actually read, so
        // clearing the equivalent marker there (TaskDetails.Apply(TaskAbandoned)) is what stops
        // the dead "twg could not authenticate" row and keeps this write's own outcome recordable
        // at the same time.
    }
}
