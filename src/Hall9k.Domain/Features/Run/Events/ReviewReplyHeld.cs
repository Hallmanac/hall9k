namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A session handed the platform's posting path a fix reply, and the path held it instead of
/// posting (<c>h9k pr reply</c>, task: a review-feedback lap's fix reply posts only after the
/// platform's push has moved the pull request's head). A reply that says Fixed is a claim about
/// the pull request's head, and the session never pushes: the platform does, after its gates. So
/// the words are vetted and recorded here, and the daemon posts them in the push step, once the
/// push has actually moved the head, with a line naming that push.
/// <para>
/// Exactly one of <see cref="ThreadId"/> and <see cref="ReviewUrl"/> is set: the reply answers
/// either a review thread or a review body. <see cref="ReplyId"/> is what ties this record to
/// the <see cref="ReviewThreadReplyPosted"/> / <see cref="ReviewBodyReplyPosted"/> that later
/// posts it, or to the <see cref="ReviewReplyWithheld"/> that says it never will, so a reply
/// is posted at most once however many times the push step runs for the run.
/// </para>
/// </summary>
/// <param name="ReplyId">This held reply's own id (UUIDv7); the run's stream id is <c>Id</c>.</param>
/// <param name="ThreadId">The review thread the reply answers; null for a review-body reply.</param>
/// <param name="ReviewUrl">The review whose body the reply answers; null for a thread reply.</param>
/// <param name="Disposition">The disposition the session claimed; only a fix is ever held.</param>
/// <param name="TargetIsHumanAuthored">Whether the thread (matched against closeout's own read) or the review (read from GitHub) is a person's.</param>
/// <param name="Body">The reply as vetted against the writing conventions, before the push line is appended.</param>
public sealed record ReviewReplyHeld(
    Guid Id,
    Guid ReplyId,
    string? ThreadId,
    string? ReviewUrl,
    ReviewThreadDisposition Disposition,
    bool TargetIsHumanAuthored,
    string Body,
    DateTimeOffset HeldAt);
