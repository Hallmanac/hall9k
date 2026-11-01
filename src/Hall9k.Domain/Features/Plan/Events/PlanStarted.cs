namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// Someone ran <c>h9k plan start</c>: the daemon on this node is asked to carry these drafts through refinement and
/// challenge to Published, and to park for a person when it cannot.
/// <para>
/// Every plan event is node-scoped, like the courier's and <c>PrReviewPreflightDispatched</c>: a plan never leaves the
/// node it was started on, so no other node starts a session for it and one that arrived by replication would start
/// nothing. A plan holds ids, phases, rounds, and closed outcomes, and no prose, not even a title.
/// </para>
/// </summary>
/// <param name="Id">The plan's id (the stream key).</param>
/// <param name="ProjectId">The project every draft belongs to.</param>
/// <param name="DraftIds">The drafts, fixed at start, in the order they were named.</param>
/// <param name="StartPhase">Refinement, or Challenge for a draft that starts at challenge.</param>
/// <param name="NoExistingItemAttested">The <c>--no-existing-item</c> attestation the plan's publish passes on.</param>
/// <param name="UntrackedAttested">The <c>--untracked</c> attestation the plan's publish passes on.</param>
/// <param name="StartedByOwnerId">Who started the plan, who gave any attestation, and who is recorded as the publisher.</param>
/// <param name="StartedAt">When the verb was run.</param>
public sealed record PlanStarted(
    Guid Id,
    Guid ProjectId,
    IReadOnlyList<Guid> DraftIds,
    PlanPhase StartPhase,
    bool NoExistingItemAttested,
    bool UntrackedAttested,
    Guid StartedByOwnerId,
    DateTimeOffset StartedAt);
