using Hall9k.Domain.Features.Run.Events;

namespace Hall9k.Domain.Features.Run;

public sealed class RunAggregate
{
    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid NodeId { get; private set; }
    public Guid OwnerId { get; private set; }
    public int LeaseGeneration { get; private set; }
    public Guid SessionId { get; private set; }
    public string WorktreePath { get; private set; } = string.Empty;
    public string Branch { get; private set; } = string.Empty;
    public ExecutorMode ExecutorMode { get; private set; } = ExecutorMode.Unknown;
    public RunState State { get; private set; } = RunState.Unknown;
    public int? ProcessId { get; private set; }
    public DateTimeOffset? ProcessStartedAt { get; private set; }
    public string? PullRequestUrl { get; private set; }
    public int? PullRequestNumber { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public decimal? CostUsd { get; private set; }
    public DateTimeOffset DispatchedAt { get; private set; }

    private readonly List<string> _failedGates = [];
    public IReadOnlyList<string> FailedGates => _failedGates;

    public void Apply(RunDispatched @event)
    {
        Id = @event.Id;
        TaskId = @event.TaskId;
        NodeId = @event.NodeId;
        OwnerId = @event.OwnerId;
        LeaseGeneration = @event.LeaseGeneration;
        SessionId = @event.SessionId;
        WorktreePath = @event.WorktreePath;
        Branch = @event.Branch;
        ExecutorMode = @event.ExecutorMode;
        DispatchedAt = @event.DispatchedAt;
        State = RunState.Dispatched;
    }

    public void Apply(RunProcessStarted @event)
    {
        ProcessId = @event.ProcessId;
        ProcessStartedAt = @event.ProcessStartedAt;
        State = RunState.Running;
    }

    public void Apply(RunResumed @event)
    {
        ProcessId = @event.ProcessId;
        State = RunState.Running;
    }

    public void Apply(AgentSessionCompleted @event) => State = RunState.Verifying;

    public void Apply(TokensRecorded @event)
    {
        InputTokens += @event.InputTokens;
        OutputTokens += @event.OutputTokens;
        if (@event.CostUsd is not null)
        {
            CostUsd = (CostUsd ?? 0m) + @event.CostUsd.Value;
        }
    }

    public void Apply(VerificationFailed @event)
    {
        _failedGates.Clear();
        _failedGates.AddRange(@event.FailedGates);
    }

    public void Apply(VerificationPassed @event)
    {
        _failedGates.Clear();
    }

    public void Apply(PullRequestOpened @event)
    {
        PullRequestUrl = @event.PullRequestUrl;
        PullRequestNumber = @event.PullRequestNumber;
        State = RunState.AwaitingReview;
    }

    public void Apply(RunCompleted @event) => State = RunState.Completed;

    public void Apply(RunFailed @event) => State = RunState.Failed;

    public void Apply(RunKilled @event) => State = RunState.Killed;

    public void Apply(RunSuperseded @event) => State = RunState.Superseded;
}
