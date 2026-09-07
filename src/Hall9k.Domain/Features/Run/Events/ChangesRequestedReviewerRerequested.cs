namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A changes-requested fix lap pushed, and the platform asked the reviewer who requested those
/// changes to review the new head (task: a changes-requested pull-request review from a human
/// becomes a fix lap). A person who blocked the pull request is owed the answer directly: the fix
/// being pushed is not the same as their being told about it, and GitHub does not re-request on
/// their behalf.
/// <para>
/// Deliberately not <see cref="ReviewRerequestedAfterFixes"/>, which answers a related but
/// different question. That one is the opt-in countersign — a reviewer being asked to confirm
/// findings they raised were addressed, bounded by its own pass cap so a quiet pull request
/// eventually settles without them. This one is unconditional and unbounded by that cap, because
/// nothing settles here without the reviewer: their CHANGES_REQUESTED verdict is what stands until
/// they change it, and the lifetime reopen budget is the only thing that bounds the laps.
/// </para>
/// <para>
/// Recorded only after the provider accepted the request. A refusal is logged against the reviewer
/// and nothing is written here — the run's own
/// <c>RunDetails.RequestedReviewerLogins</c> is what closeout's human-engagement check compares
/// against, and listing a request the provider rejected there would let the reviewer's own
/// eventual re-request read as the platform's and lose a lap the human genuinely granted.
/// </para>
/// </summary>
/// <param name="ReviewUrl">The changes-requested review this answers, so a reader can tell which verdict the new head is aimed at.</param>
public sealed record ChangesRequestedReviewerRerequested(
    Guid Id,
    string Reviewer,
    string ReviewUrl,
    DateTimeOffset RequestedAt);
