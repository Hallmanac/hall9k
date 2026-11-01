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

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? CancelReason { get; set; }

    public List<PlanDraftState> Drafts { get; set; } = [];

    public bool IsLive => State.IsLive;

    public bool IsParked => Drafts.Any(draft => draft.IsParked);

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
        Drafts = [.. @event.Data.DraftIds.Select(draftId => PlanDraftState.Start(draftId, @event.Data.StartPhase))],
    };

    public void Apply(IEvent<PlanRefinementDispatched> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanChallengeRequested> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanDraftMoved> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanParked> @event, PlanDetails view) => view.Draft(@event.Data.DraftId)?.Apply(@event.Data);

    public void Apply(IEvent<PlanResumed> @event, PlanDetails view)
    {
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

    public void Apply(IEvent<PlanCancelled> @event, PlanDetails view)
    {
        view.State = PlanState.Cancelled;
        view.EndedAt = @event.Data.CancelledAt;
        view.CancelReason = @event.Data.Reason;
    }
}
