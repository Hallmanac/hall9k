using Hall9k.Domain.Features.Plan.Events;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// A plan: one or more drafts carried by the daemon through refinement and challenge to Published, on a stream of its
/// own on the node it was started on. Not a run, and it holds no task: a plan creates no run, lease, branch, or
/// worktree. State changes only through <c>Apply</c>; the decisions live in <see cref="Handlers.PlanDecider"/>.
/// </summary>
public sealed class PlanAggregate
{
    private readonly List<PlanDraftState> _drafts = [];

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    public PlanState State { get; private set; } = PlanState.Unknown;

    public PlanPhase StartPhase { get; private set; } = PlanPhase.Unknown;

    public bool NoExistingItemAttested { get; private set; }

    public bool UntrackedAttested { get; private set; }

    public Guid StartedByOwnerId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The reason a person gave when they cancelled the plan, or null.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>The drafts, in the order they were named at start.</summary>
    public IReadOnlyList<PlanDraftState> Drafts => _drafts;

    public bool IsLive => State.IsLive;

    /// <summary>Whether any draft is parked for a person.</summary>
    public bool IsParked => _drafts.Any(draft => draft.IsParked);

    public PlanDraftState? Draft(Guid draftId) => _drafts.FirstOrDefault(draft => draft.DraftId == draftId);

    /// <summary>Folds any plan event, for a caller that holds an event as an object (the daemon's in-memory look-ahead).</summary>
    public void ApplyEvent(object @event)
    {
        switch (@event)
        {
            case PlanStarted started:
                Apply(started);
                break;
            case PlanRefinementDispatched dispatched:
                Apply(dispatched);
                break;
            case PlanChallengeRequested requested:
                Apply(requested);
                break;
            case PlanDraftMoved moved:
                Apply(moved);
                break;
            case PlanParked parked:
                Apply(parked);
                break;
            case PlanResumed resumed:
                Apply(resumed);
                break;
            case PlanDraftPublished published:
                Apply(published);
                break;
            case PlanDraftAbandoned abandoned:
                Apply(abandoned);
                break;
            case PlanPublished ended:
                Apply(ended);
                break;
            case PlanCancelled cancelled:
                Apply(cancelled);
                break;
            default:
                break;
        }
    }

    public void Apply(PlanStarted @event)
    {
        Id = @event.Id;
        ProjectId = @event.ProjectId;
        State = PlanState.Live;
        StartPhase = @event.StartPhase;
        NoExistingItemAttested = @event.NoExistingItemAttested;
        UntrackedAttested = @event.UntrackedAttested;
        StartedByOwnerId = @event.StartedByOwnerId;
        StartedAt = @event.StartedAt;
        _drafts.AddRange(@event.DraftIds.Select(draftId => PlanDraftState.Start(draftId, @event.StartPhase)));
    }

    public void Apply(PlanRefinementDispatched @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanChallengeRequested @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanDraftMoved @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanParked @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanResumed @event)
    {
        foreach (PlanDraftState draft in _drafts)
        {
            draft.ApplyResume();
        }
    }

    public void Apply(PlanDraftPublished @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanDraftAbandoned @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanPublished @event)
    {
        State = PlanState.Published;
        EndedAt = @event.PublishedAt;
    }

    public void Apply(PlanCancelled @event)
    {
        State = PlanState.Cancelled;
        EndedAt = @event.CancelledAt;
        CancelReason = @event.Reason;
    }
}
