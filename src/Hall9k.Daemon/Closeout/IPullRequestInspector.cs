namespace Hall9k.Daemon.Closeout;

/// <summary>
/// An error-placeholder review: the reviewer's LATEST review says it could not review
/// the pull request. Reviewer is the provider login the review was authored under;
/// Url identifies the exact errored review (the monitor's dedup key, and what a park
/// reason names for the human).
/// </summary>
public sealed record ErroredReview(string Reviewer, string Url);

/// <summary>
/// One poll's observation of a pull request, as reported by the provider. Timestamps are
/// the provider's own (null when unreported — never guessed). FailingChecks holds the
/// names of checks that completed and failed; HasPendingChecks means the CI picture is
/// still incomplete, so the monitor waits rather than acting on a partial failure list.
/// UnresolvedCopilotThreadCount counts unresolved review threads opened by Copilot —
/// human review conversations belong to humans and are not counted.
/// ErroredCopilotReview is set when Copilot's latest review is an error placeholder:
/// an errored review produces zero threads, so without this signal it would read as a
/// clean pass (origin incident: PR #6, 2026-08-17, GitHub partial outage).
/// </summary>
public sealed record PullRequestSnapshot(
    bool IsMerged,
    bool IsClosed,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<string> FailingChecks,
    bool HasPendingChecks,
    int UnresolvedCopilotThreadCount,
    ErroredReview? ErroredCopilotReview);

/// <summary>
/// The closeout monitor's seam onto the PR provider (gh in production, a fake in
/// tests): the read side per poll, plus the one write closeout needs — re-requesting
/// an errored Copilot review. Both run against the project's shared repository path —
/// the run's worktree may already be gone, the origin remote is what matters.
/// </summary>
public interface IPullRequestInspector
{
    Task<PullRequestSnapshot> InspectAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Re-request a review from the reviewer whose review errored, through the
    /// provider's API — never the website, which may be down when this matters (the
    /// origin incident's exact circumstance).
    /// </summary>
    Task RerequestReviewAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, string reviewer,
        CancellationToken cancellationToken);
}
