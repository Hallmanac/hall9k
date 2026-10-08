namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The platform's posting path refused to put a decline or a route on a PERSON's review body (task:
/// a dispatched session never speaks to a person at the top level of a pull request on its own).
/// Nothing reached GitHub. The review-body sibling of <see cref="ReviewThreadReplyRefused"/>, with
/// one more job: this record is GitHub's own report of the review, taken by the command at the time
/// it ran, so it is what lets the park offer the owner a reply choice for a review at all. A review
/// url a session composed has no such record and takes the plain park.
/// </summary>
/// <param name="ReviewUrl">The review as GitHub reported its address.</param>
/// <param name="Author">The review's author login as GitHub reported it; null when it returned none.</param>
/// <param name="Disposition">The disposition the session claimed: decline or route.</param>
/// <param name="Reason">The refusal in the words the session was given.</param>
public sealed record ReviewBodyReplyRefused(
    Guid Id,
    string ReviewUrl,
    string? Author,
    ReviewThreadDisposition Disposition,
    string Reason,
    DateTimeOffset RefusedAt);
