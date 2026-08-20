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
    public string Objective { get; set; } = string.Empty;
    public TaskType Type { get; set; } = TaskType.Unknown;
    public TaskState State { get; set; } = TaskState.Unknown;
    public int LeaseGeneration { get; set; }
    public Guid? ClaimedByNodeId { get; set; }
    public Guid? CurrentRunId { get; set; }
    public string? ExternalReference { get; set; }
    public string? PullRequestUrl { get; set; }
    /// <summary>Whose work this is; null until an explicit assignment says (Decisions Log #34).</summary>
    public Guid? AssignedOwnerId { get; set; }
    /// <summary>Declared dependency edges — the cheap re-evaluation query filters on this.</summary>
    public List<Guid> BlockedBy { get; set; } = [];
    /// <summary>Blockers not yet at true closeout; empty on anything but a Blocked task.</summary>
    public List<Guid> UnmetDependencies { get; set; } = [];
    /// <summary>Blockers observed Failed or Abandoned: they will never close out on their own.</summary>
    public List<Guid> DeadDependencies { get; set; } = [];
    /// <summary>Why the newest dead blocker died: what makes h9k status read this task as NeedsHuman.</summary>
    public string? DependencyFailureReason { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class TaskListItemProjection : SingleStreamProjection<TaskListItem, Guid>
{
    public TaskListItem Create(IEvent<TaskAdded> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        Objective = @event.Data.Objective,
        Type = @event.Data.Type,
        // Pre-lifecycle streams replay as they behaved: queued and assigned to the owner who
        // added them, which is the sole owner of a v0 install (Decisions Log #34).
        State = @event.Data.StartsAsDraft ? TaskState.Draft : TaskState.Queued,
        AssignedOwnerId = @event.Data.StartsAsDraft ? null : @event.Data.AddedByOwnerId,
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
    }

    public void Apply(IEvent<TaskReturnedToDraft> @event, TaskListItem view) => view.State = TaskState.Draft;

    public void Apply(IEvent<TaskAssigned> @event, TaskListItem view)
    {
        view.AssignedOwnerId = @event.Data.AssignedOwnerId;
        view.UnmetDependencies = [.. @event.Data.UnmetDependencies];
        view.DeadDependencies = [];
        view.DependencyFailureReason = null;
        view.State = view.UnmetDependencies.Count == 0 ? TaskState.Queued : TaskState.Blocked;
    }

    public void Apply(IEvent<TaskUnassigned> @event, TaskListItem view)
    {
        view.AssignedOwnerId = null;
        view.UnmetDependencies = [];
        view.DeadDependencies = [];
        view.DependencyFailureReason = null;
        view.State = TaskState.Published;
    }

    // Dependency bookkeeping only means anything while the task is Blocked, and the decider
    // only ever emits these two events from that state. Anything else on the stream is a lost
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
        // reason to hold the task for a human.
        if (view.DeadDependencies.Remove(@event.Data.DependencyId) && view.DeadDependencies.Count == 0)
        {
            view.DependencyFailureReason = null;
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

        if (!view.DeadDependencies.Contains(@event.Data.DependencyId))
        {
            view.DeadDependencies.Add(@event.Data.DependencyId);
        }

        view.DependencyFailureReason = @event.Data.Reason;
    }

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
        view.State = TaskState.Queued;
    }

    public void Apply(IEvent<QuestionAsked> @event, TaskListItem view) => view.State = TaskState.NeedsHuman;

    public void Apply(IEvent<AnswerProvided> @event, TaskListItem view) => view.State = TaskState.Claimed;

    public void Apply(IEvent<TaskCompleted> @event, TaskListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.State = TaskState.Done;
    }

    public void Apply(IEvent<TaskReopened> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.State = TaskState.Queued;
    }

    public void Apply(IEvent<TaskFailed> @event, TaskListItem view) => view.State = TaskState.Failed;

    public void Apply(IEvent<TaskRetried> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.State = TaskState.Queued;
    }

    public void Apply(IEvent<TaskResolved> @event, TaskListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl ?? view.PullRequestUrl;
        view.State = TaskState.Done;
    }

    public void Apply(IEvent<TaskAbandoned> @event, TaskListItem view) => view.State = TaskState.Abandoned;
}
