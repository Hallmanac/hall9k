using Hall9k.Domain.Features.Run.Events;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Run.Projections;

/// <summary>Lean row for h9k status and the by-TaskId join (no multi-stream projection).</summary>
public sealed class RunListItem
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid NodeId { get; set; }
    public int LeaseGeneration { get; set; }
    public RunState State { get; set; } = RunState.Unknown;
    public string? PullRequestUrl { get; set; }
    public DateTimeOffset DispatchedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class RunListItemProjection : SingleStreamProjection<RunListItem, Guid>
{
    public RunListItem Create(IEvent<RunDispatched> @event) => new()
    {
        Id = @event.Data.Id,
        TaskId = @event.Data.TaskId,
        NodeId = @event.Data.NodeId,
        LeaseGeneration = @event.Data.LeaseGeneration,
        State = RunState.Dispatched,
        DispatchedAt = @event.Data.DispatchedAt,
    };

    public void Apply(IEvent<RunProcessStarted> @event, RunListItem view) => view.State = RunState.Running;

    public void Apply(IEvent<RunResumed> @event, RunListItem view) => view.State = RunState.Running;

    public void Apply(IEvent<AgentSessionCompleted> @event, RunListItem view) => view.State = RunState.Verifying;

    public void Apply(IEvent<ReviewDispatched> @event, RunListItem view) => view.State = RunState.UnderReview;

    public void Apply(IEvent<ReviewParked> @event, RunListItem view) => view.State = RunState.ReviewParked;

    public void Apply(IEvent<PullRequestOpened> @event, RunListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestUpdated> @event, RunListItem view)
    {
        view.PullRequestUrl = @event.Data.PullRequestUrl;
        view.State = RunState.AwaitingReview;
    }

    public void Apply(IEvent<PullRequestChecksFailed> @event, RunListItem view) => view.State = RunState.ChecksFailing;

    public void Apply(IEvent<ReviewFeedbackReceived> @event, RunListItem view) => view.State = RunState.ReviewPending;

    public void Apply(IEvent<ReviewErrored> @event, RunListItem view) => view.State = RunState.ReviewPending;

    public void Apply(IEvent<CloseoutParked> @event, RunListItem view) => view.State = RunState.CloseoutParked;

    public void Apply(IEvent<PullRequestClosed> @event, RunListItem view)
    {
        view.State = RunState.Failed;
        view.FinishedAt = @event.Data.ObservedAt;
    }

    public void Apply(IEvent<RunCompleted> @event, RunListItem view)
    {
        view.State = RunState.Completed;
        view.FinishedAt = @event.Data.CompletedAt;
    }

    public void Apply(IEvent<RunFailed> @event, RunListItem view)
    {
        view.State = RunState.Failed;
        view.FinishedAt = @event.Data.FailedAt;
    }

    public void Apply(IEvent<RunKilled> @event, RunListItem view)
    {
        view.State = RunState.Killed;
        view.FinishedAt = @event.Data.KilledAt;
    }

    public void Apply(IEvent<RunSuperseded> @event, RunListItem view)
    {
        view.State = RunState.Superseded;
        view.FinishedAt = @event.Data.SupersededAt;
    }
}
