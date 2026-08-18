using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Tasks.Projections;

/// <summary>Lean row for h9k status and the daemon's queue query (State == Queued).</summary>
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
        State = TaskState.Queued,
        ExternalReference = @event.Data.ExternalReference?.ToString(),
        AddedAt = @event.Data.AddedAt,
    };

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

    public void Apply(IEvent<TaskRetried> @event, TaskListItem view)
    {
        view.ClaimedByNodeId = null;
        view.CurrentRunId = null;
        view.State = TaskState.Queued;
    }

    public void Apply(IEvent<TaskFailed> @event, TaskListItem view) => view.State = TaskState.Failed;

    public void Apply(IEvent<TaskAbandoned> @event, TaskListItem view) => view.State = TaskState.Abandoned;
}
