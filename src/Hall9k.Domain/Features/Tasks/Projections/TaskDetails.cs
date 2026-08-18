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
