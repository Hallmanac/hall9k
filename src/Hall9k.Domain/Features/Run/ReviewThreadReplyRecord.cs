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
