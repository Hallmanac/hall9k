namespace Hall9k.Daemon.Closeout;

/// <summary>
/// One poll's observation of a pull request, as reported by the provider. Timestamps are
/// the provider's own (null when unreported — never guessed). FailingChecks holds the
/// names of checks that completed and failed; HasPendingChecks means the CI picture is
/// still incomplete, so the monitor waits rather than acting on a partial failure list.
/// UnresolvedCopilotThreadCount counts unresolved review threads opened by Copilot —
/// human review conversations belong to humans and are not counted.
/// </summary>
public sealed record PullRequestSnapshot(
    bool IsMerged,
    bool IsClosed,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<string> FailingChecks,
    bool HasPendingChecks,
    int UnresolvedCopilotThreadCount);

/// <summary>
/// The closeout monitor's read seam onto the PR provider (gh in production, a fake in
/// tests). Inspection runs against the project's shared repository path — the run's
/// worktree may already be gone, the origin remote is what matters.
/// </summary>
public interface IPullRequestInspector
{
    Task<PullRequestSnapshot> InspectAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken);
}
