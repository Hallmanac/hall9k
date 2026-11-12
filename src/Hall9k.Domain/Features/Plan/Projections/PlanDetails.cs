using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Plan.Events;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Plan.Projections;

/// <summary>
/// The read view of one plan, which <c>h9k plan show</c>, <c>h9k plan list</c>, <c>h9k status</c>, and the daemon's
/// start-time checks read. Node-local like the stream it folds: a plan's events never replicate, so a node holds the
/// plans started on it and no others. The per-draft transitions are <see cref="PlanDraftState"/>'s own.
/// </summary>
public sealed class PlanDetails
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public PlanState State { get; set; } = PlanState.Unknown;

    public PlanPhase StartPhase { get; set; } = PlanPhase.Unknown;

    public bool NoExistingItemAttested { get; set; }

    public bool UntrackedAttested { get; set; }

    public Guid StartedByOwnerId { get; set; }

    /// <summary>The task whose feedback step this plan carries, or null for an ordinary plan.</summary>
    public Guid? FeedbackStepTaskId { get; set; }

    public bool IsFeedbackStep => FeedbackStepTaskId is not null;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>The idea discovery runs on, and the session and park it is in; null for a plan started from drafts.</summary>
    public PlanDiscoveryState? Discovery { get; set; }

    /// <summary>The epic <c>h9k plan pursue --epic-title</c> created for the cut drafts, or null.</summary>
    public Guid? EpicId { get; set; }

    /// <summary>The reason a person gave when they stopped the plan at the gate, or null.</summary>
    public string? StopReason { get; set; }

    /// <summary>The idea the plan was started from, or null.</summary>
    public Guid? IdeaId => Discovery?.IdeaId;

    /// <summary>Whether a plan started from an idea is still in discovery, with no drafts yet.</summary>
    public bool InDiscovery => IsLive && Discovery is not null && Drafts.Count == 0;

    /// <summary>
    /// Where the plan as a whole stands: discovery until its drafts are cut, then the earliest phase of any draft still open,
    /// and published once every draft is closed.
    /// </summary>
    public PlanPhase Phase => Discovery is not null && Drafts.Count == 0
        ? PlanPhase.Discovery
        : Drafts.Where(draft => !draft.IsClosed).OrderBy(draft => draft.Phase == PlanPhase.Refinement ? 0 : 1).Select(draft => draft.Phase).FirstOrDefault()
            ?? (Drafts.Count > 0 ? PlanPhase.Published : StartPhase);

    /// <summary>Whether discovery's report waits at the gate for a person.</summary>
    public bool AtGate => InDiscovery && Discovery is { AtGate: true };

    public List<PlanDraftState> Drafts { get; set; } = [];

    public bool IsLive => State.IsLive;

    public bool IsParked => Discovery?.IsParked == true || Drafts.Any(draft => draft.IsParked);

    public bool Includes(Guid draftId) => Drafts.Any(draft => draft.DraftId == draftId);

    public PlanDraftState? Draft(Guid draftId) => Drafts.FirstOrDefault(draft => draft.DraftId == draftId);
}

public sealed partial class PlanDetailsProjection : SingleStreamProjection<PlanDetails, Guid>
{
    public PlanDetails Create(IEvent<PlanStarted> @event) => new()
    {
        Id = @event.Data.Id,
        ProjectId = @event.Data.ProjectId,
        State = PlanState.Live,
        StartPhase = @event.Data.StartPhase,
        NoExistingItemAttested = @event.Data.NoExistingItemAttested,
        UntrackedAttested = @event.Data.UntrackedAttested,
        StartedByOwnerId = @event.Data.StartedByOwnerId,
        StartedAt = @event.Data.StartedAt,
        Discovery = @event.Data.IdeaId is { } ideaId
            ? new PlanDiscoveryState { IdeaId = ideaId }
            : null,
        FeedbackStepTaskId = @event.Data.FeedbackStepTaskId,
        Drafts = [.. @event.Data.DraftIds.Select(draftId => PlanDraftState.Start(
            draftId, @event.Data.StartPhase, @event.Data.FeedbackStepTaskId is null ? null : RefinementPurpose.FeedbackRefine))],
    };

    public void Apply(IEvent<PlanDiscoveryDispatched> @event, PlanDetails view) => view.Discovery?.Apply(@event.Data);

    public void Apply(IEvent<PlanDiscoveryParked> @event, PlanDetails view) => view.Discovery?.Apply(@event.Data);

    public void Apply(IEvent<PlanDraftsCut> @event, PlanDetails view)
    {
        view.Discovery?.ApplyCut();
        view.EpicId = @event.Data.EpicId;
        view.NoExistingItemAttested = @event.Data.NoExistingItemAttested;
        view.UntrackedAttested = @event.Data.UntrackedAttested;
        view.StartedByOwnerId = @event.Data.CutByOwnerId;
        view.Drafts.AddRange(@event.Data.DraftIds.Select(draftId => PlanDraftState.Start(draftId, PlanPhase.Refinement)));
    }

    public void Apply(IEvent<PlanSplitAccepted> @event, PlanDetails view)
    {
        view.NoExistingItemAttested = @event.Data.NoExistingItemAttested;
        view.UntrackedAttested = @event.Data.UntrackedAttested;
        view.Draft(@event.Data.OriginalDraftId)?.ApplySplitOriginal();
        view.Drafts.AddRange(@event.Data.NewDraftIds.Select(PlanDraftState.StartSplit));
    }

    public void Apply(IEvent<PlanRefinementDispatched> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanChallengeRequested> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanDraftMoved> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanParked> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanResumed> @event, PlanDetails view)
    {
        view.Discovery?.ApplyResume();
        foreach (PlanDraftState draft in view.Drafts)
        {
            draft.ApplyResume();
        }
    }

    public void Apply(IEvent<PlanDraftPublished> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanDraftAbandoned> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanPublished> @event, PlanDetails view)
    {
        view.State = PlanState.Published;
        view.EndedAt = @event.Data.PublishedAt;
    }

    public void Apply(IEvent<PlanStopped> @event, PlanDetails view)
    {
        view.State = PlanState.Stopped;
        view.EndedAt = @event.Data.StoppedAt;
        view.StopReason = @event.Data.Reason;
    }

    public void Apply(IEvent<PlanCancelled> @event, PlanDetails view)
    {
        view.State = PlanState.Cancelled;
        view.EndedAt = @event.Data.CancelledAt;
        view.CancelReason = @event.Data.Reason;
    }
}
