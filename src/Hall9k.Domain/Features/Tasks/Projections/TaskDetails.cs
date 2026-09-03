using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Tasks.Projections;

public sealed class TaskQuestion
{
    public Guid QuestionId { get; set; }
    public Guid RunId { get; set; }
    public string Question { get; set; } = string.Empty;
    public DateTimeOffset AskedAt { get; set; }
    public string? Answer { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
}

public sealed class TaskDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Objective { get; set; } = string.Empty;
    public List<string> AcceptanceCriteria { get; set; } = [];
    public TaskType Type { get; set; } = TaskType.Unknown;
    public TaskState State { get; set; } = TaskState.Unknown;
    public string? AgentContext { get; set; }
    public TaskConstraints? Constraints { get; set; }
    public string? ExternalReference { get; set; }
    /// <summary>What the external system said the item's status was when Hall9k last read it; never refreshed.</summary>
    public string? ExternalStatusObserved { get; set; }
    /// <summary>When that reading happened — the stamp that makes the status above history rather than a claim.</summary>
    public DateTimeOffset? ExternalObservedAt { get; set; }
    /// <summary>The system a publication session is outstanding for; null when none is (backlog 18).</summary>
    public string? PendingPublicationProvider { get; set; }
    /// <summary>The board that publication was asked to file under; None when the project bound no key.</summary>
    public JiraProjectKey PendingPublicationProjectKey { get; set; } = JiraProjectKey.None;
    /// <summary>True once the daemon spawned the pending publication's session: what stops a second card.</summary>
    public bool PublicationSessionDispatched { get; set; }
    /// <summary>The session writing the card, which also names the directory its prompt and transcript are in.</summary>
    public Guid? PublicationSessionId { get; set; }
    /// <summary>The node that spawned it: the only machine on which the process identity below means anything.</summary>
    public Guid? PublicationSessionNodeId { get; set; }
    /// <summary>
    /// When it was dispatched — the clock a node other than the one above has to judge it by, since
    /// that node's pid means nothing here and there is no heartbeat behind a publication.
    /// </summary>
    public DateTimeOffset? PublicationSessionDispatchedAt { get; set; }
    /// <summary>Pid and start time together — a process identity, so adoption can tell a live session from a reused pid.</summary>
    public int? PublicationSessionProcessId { get; set; }
    public DateTimeOffset? PublicationSessionStartedAt { get; set; }
    public DateTimeOffset? PublicationRequestedAt { get; set; }
    /// <summary>Who asked. It is what tells a node whether an outstanding publication is its owner's work to do.</summary>
    public Guid? PublicationRequestedByOwnerId { get; set; }
    /// <summary>How the last publication session ended, in words — kept because "no link" alone teaches nobody.</summary>
    public string? PublicationOutcome { get; set; }
    /// <summary>
    /// True when publish was attested --untracked: the tracking-policy gate was cleared by
    /// declaring this task deliberately exempt, rather than by linking an item or confirming
    /// none exists (backlog: a task can be published deliberately untracked under a tracking
    /// backlog policy). False for a task that predates the policy or was published under policy
    /// none — neither of those ever asked for this attestation, so this stays honestly false
    /// for them rather than defaulting to a look-alike state.
    /// </summary>
    public bool UntrackedAttested { get; set; }
    /// <summary>When the untracked attestation was made — the publish itself (see <see cref="Events.TaskPublished.PublishedAt"/>).</summary>
    public DateTimeOffset? UntrackedAttestedAt { get; set; }
    /// <summary>Who made it (see <see cref="Events.TaskPublished.PublishedByOwnerId"/>).</summary>
    public Guid? UntrackedAttestedByOwnerId { get; set; }
    /// <summary>The write hall9k has outstanding against Jira for this task, or null when none is (Brian's design, 2026-08-28).</summary>
    public Guid? PendingJiraWriteId { get; set; }
    /// <summary>Which of create, update, or comment the outstanding write is.</summary>
    public JiraWriteOperation PendingJiraWriteOperation { get; set; } = JiraWriteOperation.Unknown;
    /// <summary>The item the outstanding write targets; null for a create, which has none yet.</summary>
    public string? PendingJiraWriteIssueKey { get; set; }
    /// <summary>The composed payload exactly as requested — what the retry sweep re-attempts with.</summary>
    public string? PendingJiraWritePayloadJson { get; set; }
    public DateTimeOffset? PendingJiraWriteRequestedAt { get; set; }
    public Guid? PendingJiraWriteRequestedByOwnerId { get; set; }
    /// <summary>What the most recent failed attempt reported, kept only while the write is still pending.</summary>
    public string? PendingJiraWriteFailureReason { get; set; }
    /// <summary>True when the most recent failed attempt was a rejected credential — the retry sweep's own filter.</summary>
    public bool PendingJiraWriteIsAuthFailure { get; set; }
    /// <summary>True when closeout's own merge notice is waiting on another Jira write to clear before the retry sweep can attempt it (Brian's design, 2026-08-28).</summary>
    public bool HasQueuedJiraMergeNotice { get; set; }
    /// <summary>Whose work this is; null until an explicit assignment says (Decisions Log #34).</summary>
    public Guid? AssignedOwnerId { get; set; }
    /// <summary>
    /// When a human said "do this": the moment that made the task claimable, and the key the
    /// dispatcher queues on once the concurrency ceiling makes the tail of the queue wait
    /// (Decisions Log #64). Kept here as well as on <see cref="TaskListItem"/> because the
    /// backfill's staleness markers are read against both documents.
    /// </summary>
    public DateTimeOffset? AssignedAt { get; set; }
    /// <summary>The tasks this one waits on, declared at creation or revised in Draft.</summary>
    public List<Guid> BlockedBy { get; set; } = [];
    /// <summary>Blockers not yet at true closeout; empty on anything but a Blocked task.</summary>
    public List<Guid> UnmetDependencies { get; set; } = [];
    /// <summary>
    /// Blockers observed dead: they will never close out on their own. Oldest first, so the
    /// last entry is the newest observation — the one <see cref="DependencyFailureReason"/> carries.
    /// </summary>
    public List<Guid> DeadDependencies { get; set; } = [];
    /// <summary>Why the newest dead blocker died — the reason h9k task show puts in front of the human.</summary>
    public string? DependencyFailureReason { get; set; }
    /// <summary>
    /// What was recorded about each dead blocker, kept per dependency the way the aggregate
    /// keeps it, so this read model answers "which reason survives" from the same records and
    /// replays to the same state. A document written before Decisions Log #61 has no such map
    /// while still listing dead blockers, and would answer that question with silence about a
    /// blocker the stream does record as dead — which is why the field is a staleness marker in
    /// <see cref="Hall9k.Domain.Infrastructure.Persistence.TaskLifecycleProjectionBackfill"/>
    /// and every such document is rebuilt from its events before anything reads it.
    /// </summary>
    public Dictionary<Guid, string> DeadDependencyReasons { get; set; } = [];
    /// <summary>How many times the task has been revised; a draft's edit history at a glance.</summary>
    public int Revisions { get; set; }
    /// <summary>The task's model override; Unknown means the per-role, project, and platform links decide (Decisions Log #33).</summary>
    public AgentModel Model { get; set; } = AgentModel.Unknown;
    /// <summary>This task's own session-cap override; null means the node's global default decides (Decisions Log #111).</summary>
    public int? SessionCap { get; set; }
    /// <summary>This task's own override of the conformance review track's cycle cap; null defers to the project or node (task: review cycle caps become settable).</summary>
    public int? MaxComplianceReviewCycles { get; set; }
    /// <summary>This task's own override of the adversarial review track's cycle cap; null defers to the project or node.</summary>
    public int? MaxAdversarialReviewCycles { get; set; }
    /// <summary>This task's own override of the mandatory final-full-pass round cap; null defers to the project or node.</summary>
    public int? MaxFinalFullPassRounds { get; set; }
    /// <summary>This task's own override of the task-lifetime review-cycle budget; null defers to the project or node.</summary>
    public int? LifetimeReviewCycleBudget { get; set; }
    public int LeaseGeneration { get; set; }
    public Guid? ClaimedByNodeId { get; set; }
    /// <summary>See <see cref="TaskAggregate.IsInteractiveClaim"/>: same discriminator, read off this projection.</summary>
    public bool IsInteractiveClaim => ClaimedByNodeId == Guid.Empty;
    /// <summary>
    /// Whether the current claim's <see cref="Events.TaskClaimed"/> recorded a human's deliberate
    /// override of unmet dependency edges (<c>h9k task start --acknowledge-unmet-dependencies</c>,
    /// task 8a56af78-h9k) — false for every ordinary claim, since only that one entry can ever be
    /// true. Cleared when the claim is given back to run again (requeue, handback, retry, …), so a
    /// later, ordinary claim on the same task never inherits an earlier claim's override. Left set
    /// when the claim instead ends the task's story (complete, resolve, fail, abandon), since it
    /// stays a true fact about the run that just ended rather than something a later event
    /// retracts — <c>h9k task show</c> renders it beside <c>BlockedBy</c> regardless of the task's
    /// current state, so this can still read true on a Done, Failed, or Abandoned task; it is never
    /// a reliable signal of whether a claim is live (`TaskState.Claimed` already answers that).
    /// </summary>
    public bool DependencyOverrideAcknowledged { get; set; }
    public Guid? CurrentRunId { get; set; }
    public List<Guid> RunIds { get; set; } = [];
    public List<TaskQuestion> Conversation { get; set; } = [];
    public string? PullRequestUrl { get; set; }
    public string? FollowUpBranch { get; set; }
    public FollowUpKind FollowUpKind { get; set; } = FollowUpKind.Unknown;
    public string? FollowUpReason { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>
    /// The run <see cref="Events.TaskFailed"/> named. Kept apart from <see cref="FailureReason"/>,
    /// which survives a retry on purpose (see <c>Apply(TaskRetried)</c>), so a reader can still
    /// tell whether the standing reason belongs to the task's current run or an earlier, retried
    /// one — the daemon's project-home render sweep depends on that distinction for its
    /// Abandoned-with-a-dead-launch escape hatch (adversarial review, backlog 51 cycle 4).
    /// </summary>
    public Guid? FailedRunId { get; set; }
    /// <summary>The failed run's branch while a human-requested retry is pending: the launcher resumes it when it survives (Decisions Log #25).</summary>
    public string? RetryBranch { get; set; }
    public string? RetryReason { get; set; }
    /// <summary>
    /// Whether <see cref="RetryReason"/> was last set by <see cref="Events.TaskHandedBack"/>
    /// rather than <see cref="Events.TaskRetried"/> — the two share the field (both resume the
    /// same branch, WorkPromptBuilder wants the same causeless "why this resumes" text either
    /// way), but <c>h9k task show</c>'s fixed "Retried" row label must not attribute a
    /// never-failed handback to a retry that never happened (conformance review, cycle 4). Pure
    /// provenance of the text: unlike <see cref="ResumesFromHandback"/>, a requeue does not touch
    /// this — nothing re-sets <see cref="RetryReason"/> when a requeue happens, so nothing should
    /// re-label it either (conformance review, cycle 7, following cycle 6's finding: clearing
    /// this on <see cref="Events.TaskRequeued"/> fixed the prompt consumer below by breaking this
    /// one, mislabeling a handback's own text as "Retried" the moment its next headless attempt
    /// requeues).
    /// </summary>
    public bool RetryReasonIsHandback { get; set; }
    /// <summary>
    /// Whether the run about to claim this task next is resuming directly from a still-unbroken
    /// human handback — an observed fact <c>WorkPromptBuilder</c> states plainly, rather than the
    /// causeless "a previous attempt worked here" wording it falls back to otherwise (adversarial
    /// review, cycle 1). Answers a different question than <see cref="RetryReasonIsHandback"/>'s
    /// "which event last set the label text": this one is severed by <see cref="Events.TaskRequeued"/>
    /// (adversarial review, cycle 6) because a headless run claimed after a handback can itself
    /// die and requeue, and the abandoned, ungated work that run's own attempt left behind is not
    /// a human's finished work — while the label above must survive that same requeue so
    /// <c>h9k task show</c> still calls the text what it is.
    /// </summary>
    public bool ResumesFromHandback { get; set; }
    /// <summary>The human's attestation that the objective was met despite the run failure (Decisions Log #27); shown by h9k task show.</summary>
    public string? ResolvedReason { get; set; }
    /// <summary>
    /// The run <see cref="Events.TaskResolved"/> attested for. Kept apart from
    /// <see cref="ResolvedReason"/>, which survives a reopen on purpose (see
    /// <c>Apply(TaskReopened)</c>), so a reader can still tell whether the standing reason
    /// belongs to the task's current run or an earlier, superseded one — the same distinction
    /// <see cref="FailedRunId"/> gives <see cref="FailureReason"/>, and for the same consumer:
    /// the daemon's project-home render sweep's Done-archiving check.
    /// </summary>
    public Guid? ResolvedRunId { get; set; }
    /// <summary>The human's walk-away note; kept apart from FailureReason so the run's observed failure stays visible beside it.</summary>
    public string? AbandonedReason { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public Guid AddedByOwnerId { get; set; }
    /// <summary>The idea this draft was promoted from; null when the task was written directly (Decisions Log #35).</summary>
    public Guid? SourceIdeaId { get; set; }
    /// <summary>The epic this task belongs to, or null when ungrouped (Decisions Log #100).</summary>
    public Guid? EpicId { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class TaskDetailsProjection : SingleStreamProjection<TaskDetails, Guid>
{
    public TaskDetails Create(IEvent<TaskAdded> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        Objective = @event.Data.Objective,
        AcceptanceCriteria = [.. @event.Data.AcceptanceCriteria],
        Type = @event.Data.Type,
        // Pre-lifecycle streams replay as they behaved: queued and assigned to the owner who
        // added them, which is the sole owner of a v0 install (Decisions Log #34).
        State = @event.Data.StartsAsDraft ? TaskState.Draft : TaskState.Queued,
        AssignedOwnerId = @event.Data.StartsAsDraft ? null : @event.Data.AddedByOwnerId,
        // A pre-lifecycle stream was assigned by the act of being added, so that is the moment
        // it queued on — the same reading the line above already makes of its owner.
        AssignedAt = @event.Data.StartsAsDraft ? null : @event.Data.AddedAt,
        BlockedBy = [.. @event.Data.BlockedBy ?? []],
        AgentContext = @event.Data.AgentContext,
        Constraints = @event.Data.Constraints,
        ExternalReference = @event.Data.ExternalReference?.ToString(),
        Model = @event.Data.Model ?? AgentModel.Unknown,
        AddedAt = @event.Data.AddedAt,
        AddedByOwnerId = @event.Data.AddedByOwnerId,
        SourceIdeaId = @event.Data.SourceIdeaId,
        EpicId = @event.Data.EpicId,
    };

    public void Apply(IEvent<TaskPublished> @event, TaskDetails view)
    {
        view.State = TaskState.Published;
        // Set unconditionally, not just when true: a task republished WITH tracking after an
        // earlier --untracked publish (unassign -> draft -> revise -> publish, or a fresh
        // --no-existing-item publish) must stop rendering the stale attestation from the first
        // one (adversarial review, backlog: a task can be published deliberately untracked).
        view.UntrackedAttested = @event.Data.UntrackedAttested;
        view.UntrackedAttestedAt = @event.Data.UntrackedAttested ? @event.Data.PublishedAt : null;
        view.UntrackedAttestedByOwnerId = @event.Data.UntrackedAttested ? @event.Data.PublishedByOwnerId : null;
    }

    // Absent means "left alone": a revision that reworded the objective must not also claim
    // the criteria were retyped identically.
    public void Apply(IEvent<TaskRevised> @event, TaskDetails view)
    {
        if (@event.Data.Objective.HasValue)
        {
            view.Objective = @event.Data.Objective.Value ?? string.Empty;
        }

        if (@event.Data.AcceptanceCriteria.HasValue)
        {
            view.AcceptanceCriteria = [.. @event.Data.AcceptanceCriteria.Value ?? []];
        }

        if (@event.Data.AgentContext.HasValue)
        {
            view.AgentContext = @event.Data.AgentContext.Value;
        }

        if (@event.Data.BlockedBy.HasValue)
        {
            view.BlockedBy = [.. @event.Data.BlockedBy.Value ?? []];
        }

        if (@event.Data.Type.HasValue)
        {
            view.Type = @event.Data.Type.Value ?? TaskType.Unknown;
        }

        if (@event.Data.Model.HasValue)
        {
            view.Model = @event.Data.Model.Value ?? AgentModel.Unknown;
        }

        if (@event.Data.EpicId.HasValue)
        {
            view.EpicId = @event.Data.EpicId.Value;
        }

        view.Revisions++;
    }

    public void Apply(IEvent<TaskSessionCapOverridden> @event, TaskDetails view) => view.SessionCap = @event.Data.SessionCap;

    // State-agnostic, unlike Apply(IEvent<TaskRevised>): each cap is independent, so a call
    // naming only one leaves the other three untouched (absent means "leave alone").
    public void Apply(IEvent<TaskReviewCapsOverridden> @event, TaskDetails view)
    {
        if (@event.Data.MaxComplianceReviewCycles.HasValue)
        {
            view.MaxComplianceReviewCycles = @event.Data.MaxComplianceReviewCycles.Value;
        }

        if (@event.Data.MaxAdversarialReviewCycles.HasValue)
        {
            view.MaxAdversarialReviewCycles = @event.Data.MaxAdversarialReviewCycles.Value;
        }

        if (@event.Data.MaxFinalFullPassRounds.HasValue)
        {
            view.MaxFinalFullPassRounds = @event.Data.MaxFinalFullPassRounds.Value;
        }

        if (@event.Data.LifetimeReviewCycleBudget.HasValue)
        {
            view.LifetimeReviewCycleBudget = @event.Data.LifetimeReviewCycleBudget.Value;
        }
    }

    public void Apply(IEvent<TaskReturnedToDraft> @event, TaskDetails view) => view.State = TaskState.Draft;

    public void Apply(IEvent<TaskAssigned> @event, TaskDetails view)
    {
        view.AssignedOwnerId = @event.Data.AssignedOwnerId;
        view.AssignedAt = @event.Data.AssignedAt;
        view.UnmetDependencies = [.. @event.Data.UnmetDependencies];
        view.DeadDependencies = [];
        view.DeadDependencyReasons = [];
        view.DependencyFailureReason = null;
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<TaskUnassigned> @event, TaskDetails view)
    {
        view.AssignedOwnerId = null;
        view.AssignedAt = null;
        view.UnmetDependencies = [];
        view.DeadDependencies = [];
        view.DeadDependencyReasons = [];
        view.DependencyFailureReason = null;
        view.State = TaskState.Published;
    }

    // Dependency bookkeeping only means anything while the task is Blocked, and the decider
    // only ever emits these three events from that state. Anything else on the stream is a lost
    // race — a human unassigned or abandoned the task between a resolver's read and its append
    // — and a lost race replays as a no-op rather than smearing dependency state across a
    // lifecycle that has already moved on.
    public void Apply(IEvent<TaskDependencyCompleted> @event, TaskDetails view)
    {
        if (view.State != TaskState.Blocked)
        {
            return;
        }

        view.UnmetDependencies = [.. @event.Data.RemainingDependencies];
        if (view.DeadDependencies.Remove(@event.Data.DependencyId))
        {
            // The blocker that died was retried and finished after all, so what it said stops
            // counting. Closeout carries no surviving reason on the event, so the display falls
            // back to the newest death this task still records — the same records the aggregate
            // falls back to, so the two never replay to different advice.
            view.DeadDependencyReasons.Remove(@event.Data.DependencyId);
            view.DependencyFailureReason = SurvivingReason(view);
        }

        if (view.UnmetDependencies.Count == 0)
        {
            view.State = TaskState.Queued;
        }
    }

    // The task stays Blocked; the recorded reason is what makes h9k status read it as
    // NeedsHuman — the same shape the closeout park uses (log #22), for the same reason.
    public void Apply(IEvent<TaskDependencyFailed> @event, TaskDetails view)
    {
        if (view.State != TaskState.Blocked)
        {
            return;
        }

        // Oldest first, newest observation last, re-observations included: a blocker whose
        // death changed shape was seen just now, so it takes the newest slot rather than
        // keeping the one it held when it first died.
        view.DeadDependencies.Remove(@event.Data.DependencyId);
        view.DeadDependencies.Add(@event.Data.DependencyId);
        view.DeadDependencyReasons[@event.Data.DependencyId] = @event.Data.Reason;
        view.DependencyFailureReason = @event.Data.Reason;
    }

    // The mirror of the hold (Decisions Log #61): the blocker can reach closeout again, so the
    // recorded death stops counting and h9k task show goes back to the ordinary waiting-on
    // display. Removing nothing means a Completed or an Unassign already cleared it, and that
    // lost race is a no-op rather than a reason wiped off a task that still has a dead blocker.
    public void Apply(IEvent<TaskDependencyRecovered> @event, TaskDetails view)
    {
        if (view.State != TaskState.Blocked || !view.DeadDependencies.Remove(@event.Data.DependencyId))
        {
            return;
        }

        view.DeadDependencyReasons.Remove(@event.Data.DependencyId);
        view.DependencyFailureReason = SurvivingReason(view);
    }

    /// <summary>
    /// What the human is left reading once a blocker stops counting: the newest death this view
    /// still records, and null when none is left. Derived from the records rather than from a
    /// reason carried on the event, because a pass computes its snapshot from the world it read
    /// and cannot see a death appended concurrently.
    /// </summary>
    private static string? SurvivingReason(TaskDetails view) =>
        view.DeadDependencies.Count == 0
            ? null
            : view.DeadDependencyReasons.GetValueOrDefault(view.DeadDependencies[^1]);

    public void Apply(IEvent<TaskClaimed> @event, TaskDetails view)
    {
        view.LeaseGeneration = @event.Data.LeaseGeneration;
        view.ClaimedByNodeId = @event.Data.NodeId;
        view.CurrentRunId = @event.Data.RunId;
        view.RunIds.Add(@event.Data.RunId);
        view.DependencyOverrideAcknowledged = @event.Data.DependencyOverrideAcknowledged;
        view.State = TaskState.Claimed;
    }

    // ResumesFromHandback survives a requeue's own state reset by default, but WorkPromptBuilder
    // reads it as "the run about to claim this task is resuming because of a handback", not "a
    // handback happened somewhere in this task's history" — and a lease expiry or a run failure is
    // this requeue's actual cause, not the handback that put a still-earlier run on this branch
    // (adversarial review, cycle 6: a headless run's lease expiring after a handback otherwise told
    // the next headless run that its own predecessor's abandoned, ungated work was a human's
    // finished work not to be redone). Clearing it here is honest either way: a genuine handback
    // sets it again through TaskHandedBack's own Apply below, immediately before the requeued task
    // is next claimed. RetryReasonIsHandback does NOT clear here (conformance review, cycle 7):
    // it answers a different question — which event last wrote RetryReason's text — and a requeue
    // rewrites neither the text nor which event wrote it, so h9k task show's label must not change
    // either. A genuine retry (TaskRetried, below) is the only other thing that can change it.
    public void Apply(IEvent<TaskRequeued> @event, TaskDetails view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.ResumesFromHandback = false;
        view.DependencyOverrideAcknowledged = false;
        // A deliberate start-it-mine claim (h9k task start --acknowledge-unmet-dependencies) can
        // give the claim back while UnmetDependencies still names an open blocker — Claim never
        // clears it, only Assign does — and Queued is only ever reachable with every dependency
        // closed out. Landing back on Blocked instead keeps this view honest with the aggregate
        // it mirrors and lets the ordinary Blocked-state dependency sweep pick it back up.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<QuestionAsked> @event, TaskDetails view)
    {
        view.Conversation.Add(new TaskQuestion
        {
            QuestionId = @event.Data.QuestionId,
            RunId = @event.Data.RunId,
            Question = @event.Data.Question,
            AskedAt = @event.Data.AskedAt,
        });
        view.State = TaskState.NeedsHuman;
    }

    public void Apply(IEvent<AnswerProvided> @event, TaskDetails view)
    {
        TaskQuestion? question = view.Conversation.FirstOrDefault(q => q.QuestionId == @event.Data.QuestionId);
        if (question is not null)
        {
            question.Answer = @event.Data.Answer;
            question.AnsweredAt = @event.Data.AnsweredAt;
        }

        view.State = TaskState.Claimed;
    }

    public void Apply(IEvent<TaskCompleted> @event, TaskDetails view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.FollowUpBranch = null;
        view.FollowUpKind = FollowUpKind.Unknown;
        view.FollowUpReason = null;
        view.RetryBranch = null;
        view.State = TaskState.Done;
        view.FinishedAt = @event.Data.CompletedAt;
    }

    // ResolvedReason survives here on purpose (adversarial review, backlog 51 cycle 8): it is a
    // human's recorded attestation (Decisions Log #27), and erasing it on reopen would drop the
    // "Resolved: …" row h9k task show renders the moment a follow-up starts, with no way to
    // recover it short of reading the raw stream — the same doctrine Apply(TaskFailed)/
    // Apply(TaskRetried) already hold for FailureReason. What ProjectHomeRenderEngine.IsArchived
    // actually needs is not "was this task ever resolved" but "does the standing resolve
    // attestation belong to the task's CURRENT run" — exactly what ResolvedRunId (compared
    // against CurrentRunId) answers, the same way FailedRunId discriminates FailureReason.
    // Nulling CurrentRunId here is what makes that comparison correctly read "not this run"
    // until the follow-up's own TaskClaimed sets a fresh one.
    public void Apply(IEvent<TaskReopened> @event, TaskDetails view)
    {
        view.FollowUpBranch = @event.Data.Branch;
        view.FollowUpKind = @event.Data.Kind ?? FollowUpKind.Unknown;
        view.FollowUpReason = @event.Data.Reason;
        view.ClaimedByNodeId = null;
        view.DependencyOverrideAcknowledged = false;
        // Same invariant the TaskRequeued handler above restores: a deliberately-claimed Blocked
        // task can reach Done/Reopened while still carrying an unmet dependency, since Claim never
        // clears UnmetDependencies — only Assign does.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
        // Mirrors TaskAggregate.Apply(TaskReopened)'s own reasoning: nulling this unconditionally
        // would drop CloseoutEngine's watch of the pull request a Blocked landing just parked
        // behind an open dependency (adversarial review, cycle 1, on h9k task start).
        view.CurrentRunId = view.State == TaskState.Blocked ? @event.Data.PreviousRunId : null;
        view.FinishedAt = null;
    }

    public void Apply(IEvent<TaskFailed> @event, TaskDetails view)
    {
        view.FailureReason = @event.Data.Reason;
        view.FailedRunId = @event.Data.RunId;
        view.State = TaskState.Failed;
        view.FinishedAt = @event.Data.FailedAt;
    }

    // FailureReason survives on purpose: the retry appends, it never erases why the task failed.
    public void Apply(IEvent<TaskRetried> @event, TaskDetails view)
    {
        view.RetryBranch = @event.Data.Branch;
        view.RetryReason = @event.Data.Reason;
        view.RetryReasonIsHandback = false;
        view.ResumesFromHandback = false;
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.DependencyOverrideAcknowledged = false;
        // Same invariant the TaskRequeued handler above restores: Retry runs from Failed, and a
        // deliberately-claimed Blocked task whose worktree cut failed can still carry an unmet
        // dependency here.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
        view.FinishedAt = null;
    }

    // The interactive mirror of TaskRetried — same RetryBranch resume path, from a live
    // interactive claim rather than a failure.
    public void Apply(IEvent<TaskHandedBack> @event, TaskDetails view)
    {
        view.RetryBranch = @event.Data.Branch;
        view.RetryReason = @event.Data.Reason;
        view.RetryReasonIsHandback = true;
        view.ResumesFromHandback = true;
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.DependencyOverrideAcknowledged = false;
        // Same invariant the TaskRequeued handler above restores: a handback out of a
        // deliberately-claimed Blocked task must not resurface as Queued while a dependency is
        // still on record unmet.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
        view.FinishedAt = null;
    }

    // FailureReason survives here too: resolve records how the story actually ended
    // (Done, with the failure still visible) — it never rewrites or hides the failure.
    public void Apply(IEvent<TaskResolved> @event, TaskDetails view)
    {
        view.ResolvedReason = @event.Data.Reason;
        view.ResolvedRunId = view.CurrentRunId;
        view.PullRequestUrl = @event.Data.PullRequestUrl ?? view.PullRequestUrl;
        view.FollowUpBranch = null;
        view.FollowUpKind = FollowUpKind.Unknown;
        view.FollowUpReason = null;
        view.RetryBranch = null;
        view.State = TaskState.Done;
        view.FinishedAt = @event.Data.ResolvedAt;
    }

    // FailureReason survives here as well: abandoning a Failed task records the walk-away
    // note beside the observed run failure — it never overwrites why the run failed. The
    // pending-work markers are consumed like Complete/Resolve consume them: an ended task
    // has no follow-up or retry pending.
    public void Apply(IEvent<TaskAbandoned> @event, TaskDetails view)
    {
        view.AbandonedReason = @event.Data.Reason;
        view.FollowUpBranch = null;
        view.FollowUpKind = FollowUpKind.Unknown;
        view.FollowUpReason = null;
        view.RetryBranch = null;
        view.State = TaskState.Abandoned;
        view.FinishedAt = @event.Data.AbandonedAt;

        // The publication request goes with them when no session has been dispatched, which is
        // the aggregate's rule and matters most here: this view is what the daemon's sweep
        // queries, so a marker left standing on an abandoned task is a card filed for work
        // nobody intends to do. A dispatched one stays for adoption to finish (TaskAggregate).
        if (!view.PublicationSessionDispatched)
        {
            view.PendingPublicationProvider = null;
            view.PendingPublicationProjectKey = JiraProjectKey.None;
        }

        // A queued merge notice is the same kind of marker (TaskAggregate.Apply(TaskAbandoned)'s
        // own comment has the reasoning): nothing here is still owed once a human has walked away,
        // and this view is exactly what the retry sweep queries to decide whether to drain it.
        view.HasQueuedJiraMergeNotice = false;

        // A write stuck pending on a rejected credential is the same kind of marker again: the
        // retry sweeps already exclude an abandoned task (independent pre-PR review, adversarial
        // lens, cycle 5), so this write will never be retried, expired, or surfaced on the board —
        // leaving it standing here would have TaskShowCommand render a permanently dead
        // "the registered Jira credential was rejected" row as if it were still a live problem.
        // Cleared here, on the read
        // model, rather than on the aggregate (TaskAggregate.Apply(TaskAbandoned)'s own comment has
        // the reasoning): the aggregate's own marker has to survive so an in-flight write's outcome
        // stays recordable by writeId, and this view is what every reader — TaskShowCommand, the
        // retry sweep's stale-write query — actually consults (independent pre-PR review,
        // adversarial lens, cycle 6).
        ClearPendingJiraWrite(view);
    }

    // Publication is a side errand, so none of these touch State: what they move is the pending
    // marker the daemon's publication loop reads and what h9k task show tells the human.
    public void Apply(IEvent<WorkItemPublicationRequested> @event, TaskDetails view)
    {
        view.PendingPublicationProvider = @event.Data.Provider.Value;
        view.PendingPublicationProjectKey = @event.Data.ProjectKey;
        ForgetSession(view);
        view.PublicationRequestedAt = @event.Data.RequestedAt;
        view.PublicationRequestedByOwnerId = @event.Data.RequestedByOwnerId;
        view.PublicationOutcome = null;
    }

    // The session's identity is projected, not just the fact of it: it is what lets a restarted
    // daemon ask whether that session is still running rather than assume either way, and a
    // publication left dispatched with nothing watching it is a task stuck saying "a session is
    // writing the card" forever (origin incident: the pre-PR review of this branch, 2026-08-21).
    // It arrives in two parts because it is observed in two parts — the dispatch is committed
    // before anything is spawned, so that a crash in between cannot leave a live session with
    // nothing on the stream to stop the next sweep dispatching a second one.
    public void Apply(IEvent<WorkItemPublicationDispatched> @event, TaskDetails view)
    {
        view.PublicationSessionDispatched = true;
        view.PublicationSessionId = @event.Data.SessionId;
        view.PublicationSessionNodeId = @event.Data.NodeId;
        view.PublicationSessionDispatchedAt = @event.Data.DispatchedAt;
    }

    public void Apply(IEvent<WorkItemPublicationSessionStarted> @event, TaskDetails view)
    {
        view.PublicationSessionProcessId = @event.Data.ProcessId;
        view.PublicationSessionStartedAt = @event.Data.ProcessStartedAt;
    }

    public void Apply(IEvent<WorkItemPublicationCompleted> @event, TaskDetails view)
    {
        view.PendingPublicationProvider = null;
        view.PendingPublicationProjectKey = JiraProjectKey.None;
        ForgetSession(view);
        view.PublicationOutcome = @event.Data.Outcome;
    }

    // The observed fields travel with the reference deliberately. A status with no stamp reads
    // as the item's current state, which is exactly what this platform does not know: it took
    // one reading, at one moment, and never looked again (PLAN.md #60).
    public void Apply(IEvent<WorkItemLinked> @event, TaskDetails view)
    {
        view.ExternalReference = @event.Data.Reference.ToString();
        view.ExternalStatusObserved = @event.Data.ObservedStatus;
        view.ExternalObservedAt = @event.Data.ObservedAt;
        view.PendingPublicationProvider = null;
        view.PendingPublicationProjectKey = JiraProjectKey.None;
        // A task published --untracked can still be linked later by hand (link-jira, link-issue,
        // or push-to-jira); once it carries a real reference, "untracked by choice" is no longer
        // true of it and must stop rendering alongside the link it just gained (independent
        // pre-PR review, cycle 5, both lenses).
        view.UntrackedAttested = false;
        view.UntrackedAttestedAt = null;
        view.UntrackedAttestedByOwnerId = null;
        ForgetSession(view);
    }

    // Requested, then zero or more auth failures, then finally a success or a terminal failure —
    // the same shape the aggregate applies (TaskAggregate.Apply(JiraWriteFailed)'s own comment
    // has the reasoning): an auth failure leaves the pending marker standing so the identical
    // payload can be retried once the connection is fixed, instead of the write being lost.
    public void Apply(IEvent<JiraWriteRequested> @event, TaskDetails view)
    {
        view.PendingJiraWriteId = @event.Data.WriteId;
        view.PendingJiraWriteOperation = @event.Data.Operation;
        view.PendingJiraWriteIssueKey = @event.Data.IssueKey;
        view.PendingJiraWritePayloadJson = @event.Data.PayloadJson;
        view.PendingJiraWriteRequestedAt = @event.Data.RequestedAt;
        view.PendingJiraWriteRequestedByOwnerId = @event.Data.RequestedByOwnerId;
        view.PendingJiraWriteFailureReason = null;
        view.PendingJiraWriteIsAuthFailure = false;
    }

    public void Apply(IEvent<JiraWriteSucceeded> @event, TaskDetails view)
    {
        if (view.PendingJiraWriteId != @event.Data.WriteId)
        {
            return;
        }

        ClearPendingJiraWrite(view);
    }

    public void Apply(IEvent<JiraWriteFailed> @event, TaskDetails view)
    {
        if (view.PendingJiraWriteId != @event.Data.WriteId)
        {
            return;
        }

        if (@event.Data.IsAuthFailure)
        {
            view.PendingJiraWriteFailureReason = @event.Data.Reason;
            view.PendingJiraWriteIsAuthFailure = true;
        }
        else
        {
            ClearPendingJiraWrite(view);
        }
    }

    private static void ClearPendingJiraWrite(TaskDetails view)
    {
        view.PendingJiraWriteId = null;
        view.PendingJiraWriteOperation = JiraWriteOperation.Unknown;
        view.PendingJiraWriteIssueKey = null;
        view.PendingJiraWritePayloadJson = null;
        view.PendingJiraWriteRequestedAt = null;
        view.PendingJiraWriteRequestedByOwnerId = null;
        view.PendingJiraWriteFailureReason = null;
        view.PendingJiraWriteIsAuthFailure = false;
    }

    // Mirrors TaskAggregate.Apply for the same two events: this view is what the daemon's retry
    // sweep queries to find a merge notice worth draining, so it needs the identical marker.
    public void Apply(IEvent<JiraMergeNoticeQueued> @event, TaskDetails view) => view.HasQueuedJiraMergeNotice = true;

    public void Apply(IEvent<JiraMergeNoticeAttempted> @event, TaskDetails view) => view.HasQueuedJiraMergeNotice = false;

    /// <summary>
    /// The publication session's identity, dropped the moment it stops being the live one. Kept in
    /// one place because every event that ends or restarts a publication has to drop all of it:
    /// a stale pid left beside a fresh request is what would make adoption judge the wrong process.
    /// </summary>
    private static void ForgetSession(TaskDetails view)
    {
        view.PublicationSessionDispatched = false;
        view.PublicationSessionId = null;
        view.PublicationSessionNodeId = null;
        view.PublicationSessionDispatchedAt = null;
        view.PublicationSessionProcessId = null;
        view.PublicationSessionStartedAt = null;
    }
}
