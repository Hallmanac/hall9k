namespace Hall9k.Domain.Features.Plan.Events;

/// <summary>
/// The daemon requested the challenge of one draft, in the same save as the task stream's
/// <c>DraftChallengeRequested</c>. The round is the draft's current one; a request repeated after a failed challenge
/// names the same round.
/// </summary>
public sealed record PlanChallengeRequested(
    Guid Id,
    Guid ProjectId,
    Guid DraftId,
    int Round,
    DateTimeOffset RequestedAt);
