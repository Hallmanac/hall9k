namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// Closeout observed a human reviewer's CHANGES_REQUESTED review on the pull request's current
/// head (task: a changes-requested pull-request review from a human becomes a fix lap). Recorded
/// in the same sweep that reopens the task for the fix lap, and it is the reopen — not this
/// event — that dispatches anything: this is the observation, kept so <c>h9k task show</c> can
/// render each such review with its reviewer, its time, and how many findings it carried.
/// <para>
/// Distinct from <see cref="ReviewFeedbackReceived"/> on purpose. That one counts unresolved
/// review THREADS, whoever opened them, and drives the automated
/// <c>FollowUpKind.ReviewFeedback</c> lap that replies and resolves on its own. This one is a
/// person's formal verdict, and the lap it drives never sends a disagreement back to them
/// without the implementer's say-so (Brian's ruling, 2026-09-06 12:15).
/// </para>
/// </summary>
/// <param name="Reviews">
/// Every changes-requested review this sweep observed on the head, with their bodies and inline
/// comments as findings — the whole of what the fix lap is answering.
/// </param>
/// <param name="ObservedAt">This install's own observation time, distinct from each review's own <c>SubmittedAt</c>.</param>
public sealed record PullRequestChangesRequested(
    Guid Id,
    IReadOnlyList<ChangesRequestedReview> Reviews,
    DateTimeOffset ObservedAt);
