using Hall9k.Domain.Features.Run.Events;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Run.Projections;

public sealed class RunDetails
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid NodeId { get; set; }
    public Guid OwnerId { get; set; }
    public int LeaseGeneration { get; set; }
    public Guid SessionId { get; set; }
    public string WorktreePath { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string ExecutorMode { get; set; } = string.Empty;
    public RunState State { get; set; } = RunState.Unknown;
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public string? PullRequestUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    public DateTimeOffset? PullRequestMergedAt { get; set; }
    public List<string> FailingChecks { get; set; } = [];
    public int UnresolvedReviewThreads { get; set; }
    /// <summary>The last errored review observed — the monitor's dedup key: one re-request per errored review.</summary>
    public string? ErroredReviewUrl { get; set; }
    /// <summary>Review re-requests issued for this run; adds to the task's CloseoutAttempts against the shared budget.</summary>
    public int ReviewRerequestCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public List<string> FailedGates { get; set; } = [];
    /// <summary>Pre-PR review loop (log #24): which round of review the run is on, from 1.</summary>
    public int ReviewCycle { get; set; }
    public ReviewVerdict LastReviewVerdict { get; set; } = ReviewVerdict.Unknown;
    public string? FailureReason { get; set; }
    /// <summary>Why closeout was handed to the human — parked is a waiting state, not a failure.</summary>
    public string? ParkedReason { get; set; }
    public DateTimeOffset DispatchedAt { get; set; }
    public bool IsFollowUp { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class RunDetailsProjection : SingleStreamProjection<RunDetails, Guid>
{
    public RunDetails Create(IEvent<RunDispatched> @event) => new()
    {
        Id = @event.Data.Id,
        TaskId = @event.Data.TaskId,
        NodeId = @event.Data.NodeId,
        OwnerId = @event.Data.OwnerId,
        LeaseGeneration = @event.Data.LeaseGeneration,
        SessionId = @event.Data.SessionId,
        WorktreePath = @event.Data.WorktreePath,
        Branch = @event.Data.Branch,
        ExecutorMode = @event.Data.ExecutorMode,
        State = RunState.Dispatched,
        DispatchedAt = @event.Data.DispatchedAt,
        IsFollowUp = @event.Data.IsFollowUp,
    };

    public void Apply(IEvent<RunProcessStarted> @event, RunDetails view)
    {
        view.ProcessId = @event.Data.ProcessId;
        view.ProcessStartedAt = @event.Data.ProcessStartedAt;
        view.State = RunState.Running;
    }

    public void Apply(IEvent<RunResumed> @event, RunDetails view)
    {
        view.ProcessId = @event.Data.ProcessId;
        view.State = RunState.Running;
    }

    public void Apply(IEvent<AgentSessionCompleted> @event, RunDetails view) => view.State = RunState.Verifying;

    public void Apply(IEvent<TokensRecorded> @event, RunDetails view)
    {
        view.InputTokens += @event.Data.InputTokens;
        view.OutputTokens += @event.Data.OutputTokens;
        if (@event.Data.CostUsd is not null)
        {
            view.CostUsd = (view.CostUsd ?? 0m) + @event.Data.CostUsd.Value;
        }
    }

    public void Apply(IEvent<VerificationFailed> @event, RunDetails view) => view.FailedGates = [.. @event.Data.FailedGates];

    public void Apply(IEvent<VerificationPassed> @event, RunDetails view) => view.FailedGates = [];

    public void Apply(IEvent<ReviewDispatched> @event, RunDetails view)
    {
        view.ReviewCycle = @event.Data.Cycle;
        view.State = RunState.UnderReview;
    }

    public void Apply(IEvent<ReviewCompleted> @event, RunDetails view) =>
        view.LastReviewVerdict = @event.Data.Verdict;

    public void Apply(IEvent<ReviewParked> @event, RunDetails view)
    {
        view.ParkedReason = @event.Data.Reason;
        view.State = RunState.ReviewParked;
    }

    public void Apply(IEvent<PullRequestOpened> @event, RunDetails view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.PullRequestNumber = @event.Data.PullRequestNumber;
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestUpdated> @event, RunDetails view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.PullRequestNumber = @event.Data.PullRequestNumber;
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestChecksFailed> @event, RunDetails view)
    {
        view.FailingChecks = [.. @event.Data.FailedChecks];
        view.State = RunState.ChecksFailing;
    }

    public void Apply(IEvent<ReviewFeedbackReceived> @event, RunDetails view)
    {
        view.UnresolvedReviewThreads = @event.Data.UnresolvedThreadCount;
        view.State = RunState.ReviewPending;
    }

    public void Apply(IEvent<ReviewErrored> @event, RunDetails view)
    {
        view.ErroredReviewUrl = @event.Data.ReviewUrl;
        view.State = RunState.ReviewPending;
    }

    public void Apply(IEvent<ReviewRerequested> @event, RunDetails view) => view.ReviewRerequestCount++;

    public void Apply(IEvent<CloseoutParked> @event, RunDetails view)
    {
        view.ParkedReason = @event.Data.Reason;
        view.State = RunState.CloseoutParked;
    }

    public void Apply(IEvent<PullRequestMerged> @event, RunDetails view) =>
        view.PullRequestMergedAt = @event.Data.MergedAt;

    public void Apply(IEvent<PullRequestClosed> @event, RunDetails view)
    {
        view.FailureReason = "Pull request closed without merge.";
        view.State = RunState.Failed;
        view.FinishedAt = @event.Data.ObservedAt;
    }

    public void Apply(IEvent<RunCompleted> @event, RunDetails view)
    {
        view.State = RunState.Completed;
        view.FinishedAt = @event.Data.CompletedAt;
    }

    public void Apply(IEvent<RunFailed> @event, RunDetails view)
    {
        view.State = RunState.Failed;
        view.FailureReason = @event.Data.Reason;
        view.FinishedAt = @event.Data.FailedAt;
    }

    public void Apply(IEvent<RunKilled> @event, RunDetails view)
    {
        view.State = RunState.Killed;
        view.FailureReason = @event.Data.Reason;
        view.FinishedAt = @event.Data.KilledAt;
    }

    public void Apply(IEvent<RunSuperseded> @event, RunDetails view)
    {
        view.State = RunState.Superseded;
        view.FinishedAt = @event.Data.SupersededAt;
    }
}
