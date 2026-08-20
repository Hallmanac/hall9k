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
    /// <summary>The task's model override, the most specific link in the resolution chain (Decisions Log #33).</summary>
    public AgentModel Model { get; private set; } = AgentModel.Unknown;
    public int LeaseGeneration { get; private set; }
    public Guid? ClaimedByNodeId { get; private set; }
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
    /// bounded-retry counter for PR closeout. A manual reopen resets it: the human asking
    /// for another attempt restores the automatic budget (Decisions Log #22).
    /// </summary>
    public int CloseoutAttempts { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
    public Guid AddedByOwnerId { get; private set; }

    private readonly List<string> _acceptanceCriteria = [];
    public IReadOnlyList<string> AcceptanceCriteria => _acceptanceCriteria;

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
        State = TaskState.Queued;
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
        CloseoutAttempts = @event.Automatic ? CloseoutAttempts + 1 : 0;
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
    }
}
