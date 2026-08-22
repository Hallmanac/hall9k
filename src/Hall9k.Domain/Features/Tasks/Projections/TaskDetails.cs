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
    public int LeaseGeneration { get; set; }
    public Guid? ClaimedByNodeId { get; set; }
    public Guid? CurrentRunId { get; set; }
    public List<Guid> RunIds { get; set; } = [];
    public List<TaskQuestion> Conversation { get; set; } = [];
    public string? PullRequestUrl { get; set; }
    public string? FollowUpBranch { get; set; }
    public FollowUpKind FollowUpKind { get; set; } = FollowUpKind.Unknown;
    public string? FollowUpReason { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>The failed run's branch while a human-requested retry is pending: the launcher resumes it when it survives (Decisions Log #25).</summary>
    public string? RetryBranch { get; set; }
    public string? RetryReason { get; set; }
    /// <summary>The human's attestation that the objective was met despite the run failure (Decisions Log #27); shown by h9k task show.</summary>
    public string? ResolvedReason { get; set; }
    /// <summary>The human's walk-away note; kept apart from FailureReason so the run's observed failure stays visible beside it.</summary>
    public string? AbandonedReason { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public Guid AddedByOwnerId { get; set; }
    /// <summary>The idea this draft was promoted from; null when the task was written directly (Decisions Log #35).</summary>
    public Guid? SourceIdeaId { get; set; }
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
    };

    public void Apply(IEvent<TaskPublished> @event, TaskDetails view) => view.State = TaskState.Published;

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

        view.Revisions++;
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
        view.State = TaskState.Claimed;
    }

    public void Apply(IEvent<TaskRequeued> @event, TaskDetails view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.State = TaskState.Queued;
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

    public void Apply(IEvent<TaskReopened> @event, TaskDetails view)
    {
        view.FollowUpBranch = @event.Data.Branch;
        view.FollowUpKind = @event.Data.Kind ?? FollowUpKind.Unknown;
        view.FollowUpReason = @event.Data.Reason;
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.State = TaskState.Queued;
        view.FinishedAt = null;
    }

    public void Apply(IEvent<TaskFailed> @event, TaskDetails view)
    {
        view.FailureReason = @event.Data.Reason;
        view.State = TaskState.Failed;
        view.FinishedAt = @event.Data.FailedAt;
    }

    // FailureReason survives on purpose: the retry appends, it never erases why the task failed.
    public void Apply(IEvent<TaskRetried> @event, TaskDetails view)
    {
        view.RetryBranch = @event.Data.Branch;
        view.RetryReason = @event.Data.Reason;
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.State = TaskState.Queued;
        view.FinishedAt = null;
    }

    // FailureReason survives here too: resolve records how the story actually ended
    // (Done, with the failure still visible) — it never rewrites or hides the failure.
    public void Apply(IEvent<TaskResolved> @event, TaskDetails view)
    {
        view.ResolvedReason = @event.Data.Reason;
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
    }
}
