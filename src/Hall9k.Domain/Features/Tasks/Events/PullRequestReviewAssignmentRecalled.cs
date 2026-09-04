namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// The symmetric half of <see cref="PullRequestReviewAssignmentObserved"/>: the reviewer request
/// this task was minted from was withdrawn on GitHub, observed by the same poll. What that means
/// for the task depends on how far it got, which is why this event never changes state on its
/// own — a caller decides that separately, from <see cref="TaskAggregate.State"/> at the moment
/// this is appended, and records the choice on <see cref="Concluded"/> rather than leaving a
/// reader to re-derive it from whatever a following <c>TaskAbandoned</c> may or may not say:
/// true when the recall is what closed the task honestly (recorded before the run ever
/// dispatched — the go signal recalled by the same human authority that gave it), false when the
/// run was already Claimed or parked and this is an observation only. Findings already produced
/// are never discarded for a reviewer reshuffle. <see cref="RecalledByLogin"/> is honestly null
/// when the read could not attribute the withdrawal to a specific actor.
/// </summary>
public sealed record PullRequestReviewAssignmentRecalled(
    Guid Id,
    string PullRequestUrl,
    string? RecalledByLogin,
    DateTimeOffset ObservedAt,
    bool Concluded);
