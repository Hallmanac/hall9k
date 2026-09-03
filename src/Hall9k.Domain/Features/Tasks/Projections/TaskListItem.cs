using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Tasks.Projections;

/// <summary>
/// Lean row for h9k status and the daemon's queue query, which stays the cheap filter it
/// always was: State == Queued plus AssignedOwnerId == the node's owner (Decisions Log #34).
/// </summary>
public sealed class TaskListItem
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The epic this task belongs to, or null when ungrouped (Decisions Log #100).</summary>
    public Guid? EpicId { get; set; }
    public string Objective { get; set; } = string.Empty;
    public TaskType Type { get; set; } = TaskType.Unknown;
    public TaskState State { get; set; } = TaskState.Unknown;
    public int LeaseGeneration { get; set; }
    public Guid? ClaimedByNodeId { get; set; }
    /// <summary>The h9k task work claim's own tell (<see cref="TaskAggregate.IsInteractiveClaim"/>'s mirror): the sentinel node id an operator's claim records rather than a real node's.</summary>
    public bool IsInteractiveClaim => ClaimedByNodeId == Guid.Empty;
    public Guid? CurrentRunId { get; set; }
    public string? ExternalReference { get; set; }
    public string? PullRequestUrl { get; set; }
    /// <summary>
    /// What a pending Jira write's most recent failed attempt reported — set only while it is a
    /// rejected credential, since that is the one failure the attention pane surfaces (Brian's
    /// design, 2026-08-28): a terminal, non-auth failure clears the pending write rather than
    /// leaving it here for a row that has nothing left to retry.
    /// </summary>
    public string? PendingJiraWriteFailureReason { get; set; }
    /// <summary>True while the outstanding Jira write is stuck on a rejected credential.</summary>
    public bool PendingJiraWriteIsAuthFailure { get; set; }
    /// <summary>Whose work this is; null until an explicit assignment says (Decisions Log #34).</summary>
    public Guid? AssignedOwnerId { get; set; }
    /// <summary>
    /// When a human said "do this" — the moment that made the task claimable, and so the key
    /// the dispatcher queues on (Decisions Log #64). It is deliberately not <see cref="AddedAt"/>:
    /// a task drafted in January and assigned today is newer work than one drafted and assigned
    /// last week, and once the concurrency ceiling makes the queue's tail wait, that difference
    /// decides who goes first. Null only while nothing is assigned, which is a state the claim
    /// filter cannot see anyway; an unassign clears it, so a reassignment joins the back of the
    /// queue, which is what unassigning and assigning again means.
    /// </summary>
    public DateTimeOffset? AssignedAt { get; set; }
    /// <summary>Declared dependency edges — the cheap re-evaluation query filters on this.</summary>
    public List<Guid> BlockedBy { get; set; } = [];
    /// <summary>Blockers not yet at true closeout; empty on anything but a Blocked task.</summary>
    public List<Guid> UnmetDependencies { get; set; } = [];
    /// <summary>
    /// Blockers observed dead: they will never close out on their own. Oldest first, so the
    /// last entry is the newest observation — the one <see cref="DependencyFailureReason"/> carries.
    /// </summary>
    public List<Guid> DeadDependencies { get; set; } = [];
    /// <summary>Why the newest dead blocker died: what makes h9k status read this task as NeedsHuman.</summary>
    public string? DependencyFailureReason { get; set; }
    /// <summary>
    /// What was recorded about the failure, so a Failed row on the board can say why rather
    /// than showing a bare word (Decisions Log #66). It rides here beside
    /// <see cref="DependencyFailureReason"/> for the same reason that one does: the attention
    /// surface composes from this row, and the launch-failure path can fail a task before the
    /// run stream exists at all, so the run's own records are not always there to read.
    /// Null until something fails, and null again once a retry sends the task back to work.
    /// A document written before this field existed has no key for it and is rebuilt by
    /// <see cref="Hall9k.Domain.Infrastructure.Persistence.TaskLifecycleProjectionBackfill"/>,
    /// which is what keeps an already-Failed task from reading as a failure nobody explained.
    /// </summary>
    public string? FailureReason { get; set; }
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
    public DateTimeOffset AddedAt { get; set; }
    /// <summary>
    /// Why the pending follow-up exists, mirrored from <see cref="TaskAggregate.FollowUpKind"/>
    /// (Decisions Log #81): the attention line reads it to tell a rebase-dispute park, which
    /// refuses --merge-ready, apart from an ordinary review park, which takes it.
    /// </summary>
    public FollowUpKind FollowUpKind { get; set; } = FollowUpKind.Unknown;
}

public sealed class TaskListItemProjection : SingleStreamProjection<TaskListItem, Guid>
{
    public TaskListItem Create(IEvent<TaskAdded> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        EpicId = @event.Data.EpicId,
        Objective = @event.Data.Objective,
        Type = @event.Data.Type,
        // Pre-lifecycle streams replay as they behaved: queued and assigned to the owner who
        // added them, which is the sole owner of a v0 install (Decisions Log #34).
        State = @event.Data.StartsAsDraft ? TaskState.Draft : TaskState.Queued,
        AssignedOwnerId = @event.Data.StartsAsDraft ? null : @event.Data.AddedByOwnerId,
        // A pre-lifecycle stream was assigned by the act of being added, so that is the moment
        // it queued on — the same reading the line above already makes of its owner.
        AssignedAt = @event.Data.StartsAsDraft ? null : @event.Data.AddedAt,
        BlockedBy = [.. @event.Data.BlockedBy ?? []],
        ExternalReference = @event.Data.ExternalReference?.ToString(),
        AddedAt = @event.Data.AddedAt,
    };

    public void Apply(IEvent<TaskPublished> @event, TaskListItem view) => view.State = TaskState.Published;

    public void Apply(IEvent<TaskRevised> @event, TaskListItem view)
    {
        if (@event.Data.Objective.HasValue)
        {
            view.Objective = @event.Data.Objective.Value ?? string.Empty;
        }

        if (@event.Data.BlockedBy.HasValue)
        {
            view.BlockedBy = [.. @event.Data.BlockedBy.Value ?? []];
        }

        if (@event.Data.Type.HasValue)
        {
            view.Type = @event.Data.Type.Value ?? TaskType.Unknown;
        }

        if (@event.Data.EpicId.HasValue)
        {
            view.EpicId = @event.Data.EpicId.Value;
        }
    }

    public void Apply(IEvent<TaskReturnedToDraft> @event, TaskListItem view) => view.State = TaskState.Draft;

    public void Apply(IEvent<TaskAssigned> @event, TaskListItem view)
    {
        view.AssignedOwnerId = @event.Data.AssignedOwnerId;
        view.AssignedAt = @event.Data.AssignedAt;
        view.UnmetDependencies = [.. @event.Data.UnmetDependencies];
        view.DeadDependencies = [];
        view.DeadDependencyReasons = [];
        view.DependencyFailureReason = null;
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<TaskUnassigned> @event, TaskListItem view)
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
    public void Apply(IEvent<TaskDependencyCompleted> @event, TaskListItem view)
    {
        if (view.State != TaskState.Blocked)
        {
            return;
        }

        view.UnmetDependencies = [.. @event.Data.RemainingDependencies];

        // A blocker recorded as dead that finished anyway (someone retried it) stops being a
        // reason to hold the task for a human. What is left showing is the newest death this
        // row still records — the same fallback the aggregate makes, from the same records.
        if (view.DeadDependencies.Remove(@event.Data.DependencyId))
        {
            view.DeadDependencyReasons.Remove(@event.Data.DependencyId);
            view.DependencyFailureReason = SurvivingReason(view);
        }

        if (view.UnmetDependencies.Count == 0)
        {
            view.State = TaskState.Queued;
        }
    }

    // The task stays Blocked; the reason is what makes h9k status read it as NeedsHuman —
    // the same shape the closeout park uses (log #22), for the same reason.
    public void Apply(IEvent<TaskDependencyFailed> @event, TaskListItem view)
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

    // The mirror of the hold (Decisions Log #61): the blocker is back in the pipeline, so the
    // record that made h9k status read this row as NeedsHuman stops counting and the row goes
    // back to the ordinary waiting-on display. Removing nothing means a Completed or an
    // Unassign already cleared it, and that lost race is a no-op rather than a reason wiped
    // off a task that still has a dead blocker.
    public void Apply(IEvent<TaskDependencyRecovered> @event, TaskListItem view)
    {
        if (view.State != TaskState.Blocked || !view.DeadDependencies.Remove(@event.Data.DependencyId))
        {
            return;
        }

        view.DeadDependencyReasons.Remove(@event.Data.DependencyId);
        view.DependencyFailureReason = SurvivingReason(view);
    }

    /// <summary>
    /// What the human is left reading once a blocker stops counting: the newest death this row
    /// still records, and null when none is left. Derived from the records rather than from a
    /// reason carried on the event, because a pass computes its snapshot from the world it read
    /// and cannot see a death appended concurrently.
    /// </summary>
    private static string? SurvivingReason(TaskListItem view) =>
        view.DeadDependencies.Count == 0
            ? null
            : view.DeadDependencyReasons.GetValueOrDefault(view.DeadDependencies[^1]);

    public void Apply(IEvent<TaskClaimed> @event, TaskListItem view)
    {
        view.LeaseGeneration = @event.Data.LeaseGeneration;
        view.ClaimedByNodeId = @event.Data.NodeId;
        view.CurrentRunId = @event.Data.RunId;
        view.State = TaskState.Claimed;
    }

    public void Apply(IEvent<TaskRequeued> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        // A deliberate start-it-mine claim (h9k task start --acknowledge-unmet-dependencies) can
        // give the claim back while UnmetDependencies still names an open blocker — Claim never
        // clears it, only Assign does — and Queued is only ever reachable with every dependency
        // closed out. Landing back on Blocked instead keeps this view honest with the aggregate
        // it mirrors.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<QuestionAsked> @event, TaskListItem view) => view.State = TaskState.NeedsHuman;

    public void Apply(IEvent<AnswerProvided> @event, TaskListItem view) => view.State = TaskState.Claimed;

    public void Apply(IEvent<TaskCompleted> @event, TaskListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.FollowUpKind = FollowUpKind.Unknown;
        view.State = TaskState.Done;
    }

    public void Apply(IEvent<TaskReopened> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.FollowUpKind = @event.Data.Kind ?? FollowUpKind.Unknown;
        // Same invariant the TaskRequeued handler above restores: a deliberately-claimed Blocked
        // task can reach Done/Reopened while still carrying an unmet dependency, since Claim never
        // clears UnmetDependencies — only Assign does.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<TaskFailed> @event, TaskListItem view)
    {
        view.FailureReason = @event.Data.Reason;
        view.State = TaskState.Failed;
    }

    public void Apply(IEvent<TaskRetried> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        // The retry answers the failure, so the board stops reporting it: what happened stays
        // on the stream and in h9k task show, which keeps the retry reason beside it.
        view.FailureReason = null;
        // Retry runs from Failed, and a deliberately-claimed Blocked task whose worktree cut
        // failed can still carry an unmet dependency here — Claim never clears UnmetDependencies,
        // only Assign does, and Queued is only ever reachable with every dependency closed out.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    // The interactive mirror of TaskRetried (TaskDetails.Apply(IEvent<TaskHandedBack>) does the
    // same) — without this, the row this projection feeds the dispatcher's queue query and
    // h9k status from stays Claimed forever, and the task the handback exists to hand off never
    // reaches headless dispatch (independent pre-PR review, cycle 1).
    public void Apply(IEvent<TaskHandedBack> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        // Same invariant above: a handback out of a deliberately-claimed Blocked task must not
        // resurface as Queued while a dependency is still on record unmet.
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<TaskResolved> @event, TaskListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl ?? view.PullRequestUrl;
        view.FollowUpKind = FollowUpKind.Unknown;
        view.State = TaskState.Done;
    }

    public void Apply(IEvent<TaskAbandoned> @event, TaskListItem view)
    {
        view.FollowUpKind = FollowUpKind.Unknown;
        view.State = TaskState.Abandoned;
    }

    // Only the auth-failure flag and its reason matter to this row (AttentionComposer.Compose):
    // everything else about a pending write is h9k task show's business, not the board's.
    public void Apply(IEvent<JiraWriteRequested> @event, TaskListItem view)
    {
        view.PendingJiraWriteFailureReason = null;
        view.PendingJiraWriteIsAuthFailure = false;
    }

    public void Apply(IEvent<JiraWriteSucceeded> @event, TaskListItem view)
    {
        view.PendingJiraWriteFailureReason = null;
        view.PendingJiraWriteIsAuthFailure = false;
    }

    public void Apply(IEvent<JiraWriteFailed> @event, TaskListItem view)
    {
        if (!@event.Data.IsAuthFailure)
        {
            view.PendingJiraWriteFailureReason = null;
            view.PendingJiraWriteIsAuthFailure = false;
            return;
        }

        view.PendingJiraWriteFailureReason = @event.Data.Reason;
        view.PendingJiraWriteIsAuthFailure = true;
    }

    // The row carries the reference wherever it came from. This projection is what the
    // one-item-one-live-task check reads (TaskAddCommand.RefuseSecondAdoptionAsync), so a task
    // linked to a card it caused to exist has to block a later import of that same card exactly
    // as an adopted one does — the two funnel exits meet on this field (PLAN.md §3.1a).
    public void Apply(IEvent<WorkItemLinked> @event, TaskListItem view) =>
        view.ExternalReference = @event.Data.Reference.ToString();
}
