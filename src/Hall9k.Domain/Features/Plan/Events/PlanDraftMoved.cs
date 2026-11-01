using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// One draft moved: a session or a challenge ended and the plan put the draft where that outcome sends it. The event
/// carries the resulting standing rather than leaving the aggregate to work it out, because the deciders own that
/// logic. A phase move reaches no feed.
/// </summary>
/// <param name="Id">The plan's id.</param>
/// <param name="ProjectId">The project, read by the orchestrator feed from the event itself.</param>
/// <param name="DraftId">The draft.</param>
/// <param name="Phase">The phase the draft is now in.</param>
/// <param name="Round">The challenge round the draft is on (zero before its first).</param>
/// <param name="Outcome">The latest outcome that moved it.</param>
/// <param name="Purpose">What the draft's next refinement session is for, or Unknown when it is not in refinement.</param>
/// <param name="Ready">Whether the draft is ready to publish.</param>
/// <param name="SessionId">The session whose report this outcome came from, or null.</param>
/// <param name="MovedAt">When.</param>
public sealed record PlanDraftMoved(
    Guid Id,
    Guid ProjectId,
    Guid DraftId,
    PlanPhase Phase,
    int Round,
    PlanOutcome Outcome,
    RefinementPurpose Purpose,
    bool Ready,
    Guid? SessionId,
    DateTimeOffset MovedAt);
