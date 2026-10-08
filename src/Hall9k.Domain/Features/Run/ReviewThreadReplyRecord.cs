namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One reply a follow-up session put through the platform's posting path, as
/// <c>RunDetails</c> keeps it (task: a review-feedback follow-up never answers a human reviewer
/// in the owner's name on its own). The projection form of
/// <see cref="Events.ReviewThreadReplyPosted"/> — see that event for why the disposition is a
/// claim and the human-authored flag is not.
/// </summary>
public sealed record ReviewThreadReplyRecord(
    string ThreadId,
    ReviewThreadDisposition Disposition,
    bool ThreadIsHumanAuthored,
    DateTimeOffset PostedAt);

/// <summary>
/// One reply the posting path refused because it was aimed at a thread a PERSON opened with no
/// resolved park behind it (task: a review-feedback follow-up never answers a human reviewer in
/// the owner's name on its own). The projection form of
/// <see cref="Events.ReviewThreadReplyRefused"/>; nothing reached GitHub.
/// </summary>
public sealed record RefusedThreadReplyRecord(
    string ThreadId,
    ReviewThreadDisposition Disposition,
    string Reason,
    DateTimeOffset RefusedAt);

/// <summary>
/// One top-level comment a session put through <c>h9k pr reply --review</c> to answer a review
/// body, as <c>RunDetails</c> keeps it (task: a dispatched session never speaks to a person at the
/// top level of a pull request on its own). The projection form of
/// <see cref="Events.ReviewBodyReplyPosted"/>.
/// </summary>
public sealed record ReviewBodyReplyRecord(
    string ReviewUrl,
    ReviewThreadDisposition Disposition,
    bool ReviewIsHumanAuthored,
    DateTimeOffset PostedAt);

/// <summary>
/// One decline or route the posting path refused to send onto a PERSON's review body. The
/// projection form of <see cref="Events.ReviewBodyReplyRefused"/>; nothing reached GitHub. Its
/// presence is GitHub's own report of the review (url and author as the command read them), which
/// is what a park draws on to offer the owner a reply choice.
/// </summary>
public sealed record RefusedReviewBodyReplyRecord(
    string ReviewUrl,
    string? Author,
    ReviewThreadDisposition Disposition,
    string Reason,
    DateTimeOffset RefusedAt);
