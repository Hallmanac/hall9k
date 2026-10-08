namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The push step decided a held fix reply will not post, and said why (task: a review-feedback
/// lap's fix reply posts only after the platform's push has moved the pull request's head).
/// Recorded as the step decides, because a run whose push left the head unmoved still reads
/// AwaitingReview afterward, so its state alone cannot tell <c>h9k task show</c> that the reply
/// was withheld rather than waiting.
/// </summary>
/// <param name="ReplyId">The <see cref="ReviewReplyHeld.ReplyId"/> of the reply that will not post.</param>
/// <param name="Reason">The reason in a sentence the owner can read: an unmoved head, a contradicting triage, or a failed post with gh's error.</param>
public sealed record ReviewReplyWithheld(
    Guid Id,
    Guid ReplyId,
    string Reason,
    DateTimeOffset WithheldAt);
