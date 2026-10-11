using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Closeout observed feedback on this task's pull request from someone outside the project's
/// trust-chain roster, and held it for the owner instead of dispatching a lap (task: a
/// changes-requested review or an unresolved review thread from a non-member never dispatches a
/// build lap; it raises a needs-you item). One per <paramref name="FeedbackKey"/>, however many
/// polls observe it again.
/// <para>
/// It carries who, what kind and where, and the stranger's text only where the owner's go will need
/// it: the whole review as observed in <paramref name="Review"/>, or the opener's comments as observed
/// in <paramref name="CommentCopies"/>. Only the owner's go can turn either into a prompt, so the go
/// covers what was held rather than whatever the author has edited it into since, and still works
/// after the head has moved on. Nothing that renders a status row reads them.
/// </para>
/// </summary>
/// <param name="FeedbackKey">The review's url, or the thread's node id.</param>
/// <param name="Reviewer">The login that wrote it; <c>ghost</c> for a deleted account.</param>
/// <param name="Url">The link the owner opens to read it themselves. Blank when the provider reported none.</param>
/// <param name="Review">For a review: the review as observed. Null for a thread.</param>
/// <param name="CommentCopies">
/// For a thread: the opener's own comments as observed, each with its text, which is all the owner's go
/// can cover. Null for a review.
/// </param>
public sealed record NonMemberFeedbackHeld(
    Guid Id,
    string FeedbackKey,
    NonMemberFeedbackKind Kind,
    string Reviewer,
    string Url,
    DateTimeOffset ObservedAt,
    ChangesRequestedReview? Review = null,
    IReadOnlyList<ThreadCommentCopy>? CommentCopies = null);
