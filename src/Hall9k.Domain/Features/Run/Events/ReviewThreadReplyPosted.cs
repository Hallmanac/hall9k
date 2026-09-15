namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A follow-up session posted one in-thread reply through the platform's own posting path
/// (<c>h9k pr reply</c>, task: a review-feedback follow-up never answers a human reviewer in the
/// owner's name on its own). Recorded for every reply the path accepts, a bot's thread as much as
/// a person's, because the record's job is to make the session's own claim about each thread
/// checkable afterwards rather than to mark the interesting ones.
/// <para>
/// <see cref="Disposition"/> is the session's claim and <see cref="ThreadIsHumanAuthored"/> is
/// not: the first comes off the command line the session typed, the second is matched against the
/// human-authored thread ids closeout itself observed at dispatch
/// (<c>TaskReopened.HumanReviewThreads</c>). That asymmetry is the whole reason this event exists.
/// The posting path refuses a human-authored thread on a decline or a route, so a session
/// determined to answer one anyway has to claim <see cref="ReviewThreadDisposition.Fix"/> — and
/// <c>RunSupervisor</c> compares every claim recorded here against the same thread's
/// <c>THREAD DISPOSITION:</c> block in the session's closing summary, so the contradiction lands
/// in the run log instead of passing unseen.
/// </para>
/// </summary>
public sealed record ReviewThreadReplyPosted(
    Guid Id,
    string ThreadId,
    ReviewThreadDisposition Disposition,
    bool ThreadIsHumanAuthored,
    DateTimeOffset PostedAt);
