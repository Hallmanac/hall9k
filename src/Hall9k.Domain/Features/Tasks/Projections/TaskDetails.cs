using Hall9k.Domain.Features.Tasks.Events;
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
    public int LeaseGeneration { get; set; }
    public Guid? ClaimedByNodeId { get; set; }
    public Guid? CurrentRunId { get; set; }
    public List<Guid> RunIds { get; set; } = [];
    public List<TaskQuestion> Conversation { get; set; } = [];
    public string? PullRequestUrl { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public Guid AddedByOwnerId { get; set; }
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
        State = TaskState.Queued,
        AgentContext = @event.Data.AgentContext,
        Constraints = @event.Data.Constraints,
        ExternalReference = @event.Data.ExternalReference?.ToString(),
        AddedAt = @event.Data.AddedAt,
        AddedByOwnerId = @event.Data.AddedByOwnerId,
    };

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
        view.State = TaskState.Done;
        view.FinishedAt = @event.Data.CompletedAt;
    }

    public void Apply(IEvent<TaskFailed> @event, TaskDetails view)
    {
        view.FailureReason = @event.Data.Reason;
        view.State = TaskState.Failed;
        view.FinishedAt = @event.Data.FailedAt;
    }

    public void Apply(IEvent<TaskAbandoned> @event, TaskDetails view)
    {
        view.FailureReason = @event.Data.Reason;
        view.State = TaskState.Abandoned;
        view.FinishedAt = @event.Data.AbandonedAt;
    }
}
