namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// <c>h9k plan split accept</c>: a person accepted the split a refinement session proposed for one draft, and the increments
/// after the first were cut as new drafts in the same save. The original draft now carries the first increment, and the new
/// drafts join the plan after the drafts it already carried, in increment order. This is the one event that adds drafts to a
/// plan whose drafts are already cut. Every draft it names has split, so none of them splits again in this plan. The stream's
/// version is the fence that makes two accepts at once cut one set of drafts.
/// </summary>
/// <param name="Id">The plan's id.</param>
/// <param name="ProjectId">The project every draft belongs to.</param>
/// <param name="OriginalDraftId">The draft that was parked on the split, which now carries the first increment.</param>
/// <param name="NewDraftIds">The drafts cut for the second increment onward, in order.</param>
/// <param name="NoExistingItemAttested">The <c>--no-existing-item</c> attestation the plan's publish passes on from here.</param>
/// <param name="UntrackedAttested">The <c>--untracked</c> attestation the plan's publish passes on from here.</param>
/// <param name="AcceptedByOwnerId">Who accepted the split and gave any attestation.</param>
/// <param name="AcceptedAt">When.</param>
public sealed record PlanSplitAccepted(
    Guid Id,
    Guid ProjectId,
    Guid OriginalDraftId,
    IReadOnlyList<Guid> NewDraftIds,
    bool NoExistingItemAttested,
    bool UntrackedAttested,
    Guid AcceptedByOwnerId,
    DateTimeOffset AcceptedAt);
