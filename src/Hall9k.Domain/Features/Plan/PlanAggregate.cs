using Hall9k.Domain.Features.Plan.Events;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// A plan: one or more drafts carried by the daemon through refinement and challenge to Published, on a stream of its
/// own on the node it was started on. A plan started from an idea begins with none: it runs a discovery session, parks at a
/// gate for a person, and gets its drafts only when they pursue (<see cref="PlanDraftsCut"/>). Not a run, and it holds no
/// task: a plan creates no run, lease, branch, or worktree. State changes only through <c>Apply</c>; the decisions live in <see cref="Handlers.PlanDecider"/>.
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

    /// <summary>The idea discovery runs on, and the session and park it is in; null for a plan started from drafts.</summary>
    public PlanDiscoveryState? Discovery { get; private set; }

    /// <summary>The idea the plan was started from, or null.</summary>
    public Guid? IdeaId => Discovery?.IdeaId;

    /// <summary>The epic <c>h9k plan pursue --epic-title</c> created for the cut drafts, or null.</summary>
    public Guid? EpicId { get; private set; }

    /// <summary>The reason a person gave when they stopped the plan at the gate, or null.</summary>
    public string? StopReason { get; private set; }

    /// <summary>The reason a person gave when they cancelled the plan, or null.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>The drafts, in the order they were named at start.</summary>
    public IReadOnlyList<PlanDraftState> Drafts => _drafts;

    public bool IsLive => State.IsLive;

    /// <summary>Whether a plan started from an idea is still in discovery: it has no drafts yet, and pursuing is what cuts them.</summary>
    public bool InDiscovery => IsLive && Discovery is not null && _drafts.Count == 0;

    /// <summary>Whether discovery's report waits at the gate for a person.</summary>
    public bool AtGate => InDiscovery && Discovery is { AtGate: true };

    /// <summary>Whether the plan is parked for a person: in discovery, or on any draft.</summary>
    public bool IsParked => Discovery?.IsParked == true || _drafts.Any(draft => draft.IsParked);

    public PlanDraftState? Draft(Guid draftId) => _drafts.FirstOrDefault(draft => draft.DraftId == draftId);

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
        Discovery = @event.IdeaId is { } ideaId
            ? new PlanDiscoveryState { IdeaId = ideaId }
            : null;
        _drafts.AddRange(@event.DraftIds.Select(draftId => PlanDraftState.Start(draftId, @event.StartPhase)));
    }

    public void Apply(PlanDiscoveryDispatched @event) => Discovery?.Apply(@event);

    public void Apply(PlanDiscoveryParked @event) => Discovery?.Apply(@event);

    public void Apply(PlanDraftsCut @event)
    {
        Discovery?.ApplyCut();
        EpicId = @event.EpicId;
        NoExistingItemAttested = @event.NoExistingItemAttested;
        UntrackedAttested = @event.UntrackedAttested;
        StartedByOwnerId = @event.CutByOwnerId;
        _drafts.AddRange(@event.DraftIds.Select(draftId => PlanDraftState.Start(draftId, PlanPhase.Refinement)));
    }

    public void Apply(PlanRefinementDispatched @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanChallengeRequested @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanDraftMoved @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanParked @event) => Draft(@event.DraftId)?.Apply(@event);

    public void Apply(PlanResumed @event)
    {
        Discovery?.ApplyResume();
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

    public void Apply(PlanStopped @event)
    {
        State = PlanState.Stopped;
        EndedAt = @event.StoppedAt;
        StopReason = @event.Reason;
    }

    public void Apply(PlanCancelled @event)
    {
        State = PlanState.Cancelled;
        EndedAt = @event.CancelledAt;
        CancelReason = @event.Reason;
    }
}
