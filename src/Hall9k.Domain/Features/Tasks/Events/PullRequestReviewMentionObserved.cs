namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A comment on this pr-review task's own pull request mentioned the install's login (idea
/// 2f079bcd: a mention is auto-pr-review's second trigger, alongside a review request). Recorded
/// on the SAME task a review request would mint or attach to — never a new task type — so
/// <c>h9k task show</c> can name the triggering comment's id, author and time, and so a later
/// sweep tick can tell a comment it has already handled apart from a genuinely new one
/// (<c>Id</c>/<c>CommentId</c> is the dedupe key <c>ObservedReviewMention</c> keeps outside the
/// stream; this event is the durable, human-readable record of what was found once that dedupe
/// let it through).
/// <para>
/// <see cref="CommentCreatedAt"/> is GitHub's own timestamp for the comment — what the no-backfill
/// cutoff compares against when this mention is the one that mints a fresh task — and
/// <see cref="ObservedAt"/> is this install's own poll time, the same split
/// <see cref="PullRequestReviewAssignmentObserved"/> already keeps between a provider fact and a
/// local observation moment.
/// </para>
/// <para>
/// <see cref="CommentDatabaseId"/> is the numeric REST id, set only when the comment is an inline
/// review-comment-thread reply — the one shape the REST reply endpoint's own <c>in_reply_to</c>
/// parameter accepts (<c>CommentId</c> is the GraphQL node id, which 404s there). Null for an
/// issue comment, a review body, or the pull request's own description, exactly as
/// <c>PullRequestMentionComment.DatabaseId</c> is — a reader seeing null already knows to post an
/// ordinary comment instead. Null also on a stream written before this field existed.
/// </para>
/// </summary>
public sealed record PullRequestReviewMentionObserved(
    Guid Id,
    string PullRequestUrl,
    string CommentId,
    string CommentAuthorLogin,
    string CommentBody,
    string CommentUrl,
    DateTimeOffset CommentCreatedAt,
    DateTimeOffset ObservedAt,
    long? CommentDatabaseId = null);
