namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A session posted one top-level comment answering a review BODY through the platform's own
/// posting path (<c>h9k pr reply --review</c>, task: a dispatched session never speaks to a person
/// at the top level of a pull request on its own). The review-body sibling of
/// <see cref="ReviewThreadReplyPosted"/>, recorded for the same reason: the disposition is the
/// session's claim and <see cref="ReviewIsHumanAuthored"/> is not, because the command read the
/// review's author from GitHub at the time it ran.
/// </summary>
/// <param name="ReviewUrl">The review as GitHub reported its address, the one the comment names.</param>
/// <param name="Disposition">The disposition the session claimed when it posted.</param>
/// <param name="ReviewIsHumanAuthored">
/// Whether GitHub's own record of the review's author made it a person's. A review GitHub returned
/// with no readable author is a person's.
/// </param>
public sealed record ReviewBodyReplyPosted(
    Guid Id,
    string ReviewUrl,
    ReviewThreadDisposition Disposition,
    bool ReviewIsHumanAuthored,
    DateTimeOffset PostedAt);
