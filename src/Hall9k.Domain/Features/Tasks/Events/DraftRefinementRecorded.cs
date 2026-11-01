using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// How a refinement session ended: the one outcome its report named, and the failure from the closed list in
/// <see cref="RefinementFailureReason"/> when nothing came of it. One event for both, the
/// <see cref="DraftChallengeRecorded"/> shape. It holds nothing the session wrote and no machine path: the ledger is
/// public, so the prose stays in the report file on the node that ran the session (<c>h9k plan show</c> derives its
/// path from <paramref name="SessionId"/>).
/// </summary>
/// <param name="Id">The task's id.</param>
/// <param name="SessionId">The session, or null when the refinement ended before any session was spawned.</param>
/// <param name="NodeId">The node that ran the session and holds its report.</param>
/// <param name="Outcome">What the report named, or <see cref="RefinementOutcome.Unknown"/> when it named nothing readable.</param>
/// <param name="Failure">Why nothing came of the session, or <see cref="RefinementFailureReason.Unknown"/> when its outcome was dealt with.</param>
/// <param name="Purpose">Why the session was dispatched.</param>
/// <param name="Model">The model the session ran on.</param>
/// <param name="Effort">The effort the session ran at.</param>
/// <param name="RecordedAt">When the outcome was recorded.</param>
public sealed record DraftRefinementRecorded(
    Guid Id,
    Guid? SessionId,
    Guid NodeId,
    RefinementOutcome Outcome,
    RefinementFailureReason Failure,
    RefinementPurpose Purpose,
    AgentModel Model,
    AgentEffort Effort,
    DateTimeOffset RecordedAt);
