namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One changes-requested review as the run's own history records it (task: a changes-requested
/// pull-request review from a human becomes a fix lap) — what <c>h9k task show</c> renders per
/// review: who asked, when they asked, how many findings they left, and a link to the review
/// itself.
/// <para>
/// Deliberately the summary rather than the findings. The findings' text is what the fix lap
/// reads, and it reaches that lap through the task's own reopen
/// (<see cref="Events.PullRequestChangesRequested"/> keeps the whole of it on the run stream);
/// duplicating every body into a projection would make a read model of somebody else's prose for
/// no reader that wants it.
/// </para>
/// </summary>
/// <param name="SubmittedAt">GitHub's own submission time, null when the provider did not report one.</param>
/// <param name="ObservedAt">When this install's closeout sweep read it — a different fact, kept separately.</param>
public sealed record ChangesRequestedReviewObservation(
    string Reviewer,
    string ReviewUrl,
    DateTimeOffset? SubmittedAt,
    int FindingCount,
    DateTimeOffset ObservedAt);
