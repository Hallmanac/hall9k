using Hall9k.Domain.Features.Run;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// Closeout observed feedback on this task's pull request from someone outside the project's
/// trust-chain roster, and held it for the owner instead of dispatching a lap (task: a
/// changes-requested review or an unresolved review thread from a non-member never dispatches a
/// build lap; it raises a needs-you item). One per <paramref name="FeedbackKey"/>, however many
/// polls observe it again.
/// <para>
/// It carries who, what kind and where. It never carries a thread's text, since a stranger's words
/// must not travel on a record anything renders; for a review it carries the whole review as
/// observed in <paramref name="Review"/>, which only the owner's go can ever turn into a prompt, so
/// that the go still works after the head has moved on.
/// </para>
/// </summary>
/// <param name="FeedbackKey">The review's url, or the thread's node id.</param>
/// <param name="Reviewer">The login that wrote it; <c>ghost</c> for a deleted account.</param>
/// <param name="Url">The link the owner opens to read it themselves. Blank when the provider reported none.</param>
/// <param name="Review">For a review: the review as observed. Null for a thread.</param>
public sealed record NonMemberFeedbackHeld(
    Guid Id,
    string FeedbackKey,
    NonMemberFeedbackKind Kind,
    string Reviewer,
    string Url,
    DateTimeOffset ObservedAt,
    ChangesRequestedReview? Review = null);
