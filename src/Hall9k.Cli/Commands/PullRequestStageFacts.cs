using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;

namespace Hall9k.Cli.Commands;

/// <summary>
/// What the record says about a pull request a task is being iterated on, read off the run closeout
/// watches it through. Pure, so the rules <c>h9k task drive</c> and its exits depend on are pinned
/// without a store.
/// </summary>
internal static class PullRequestStageFacts
{
    /// <summary>
    /// Whether the pull request this run carries can still be iterated on: not merged (the run
    /// completed) and not closed without a merge. A run that failed or was killed without either is
    /// treated as open, because the pull request itself does not stop existing when nothing watches it.
    /// </summary>
    public static bool IsOpen(RunDetails pullRequestRun) =>
        pullRequestRun.State != RunState.Completed
        && pullRequestRun.FailureReason != RunDetails.PullRequestClosedWithoutMerge;

    /// <summary>
    /// One line on what closeout last observed on the pull request, for the follow-up anchor: the run's
    /// state and the findings it last recorded. Null when the run recorded nothing worth saying.
    /// </summary>
    public static string? DescribeLastObservation(RunDetails pullRequestRun)
    {
        List<string> parts = [$"the run watching it is {pullRequestRun.State.Value}"];
        if (pullRequestRun.UnresolvedReviewThreads > 0)
        {
            parts.Add(pullRequestRun.UnresolvedReviewThreads == 1
                ? "1 unresolved review thread"
                : $"{pullRequestRun.UnresolvedReviewThreads} unresolved review threads");
        }

        if (pullRequestRun.FailingChecks.Count > 0)
        {
            parts.Add($"failing checks: {string.Join(", ", pullRequestRun.FailingChecks)}");
        }
        else if (pullRequestRun.ExternalReviewChecksPending)
        {
            parts.Add("checks still pending");
        }

        if (pullRequestRun.CloseoutReport is { } report && report.Conflicting)
        {
            parts.Add("the branch conflicts with its base");
        }

        if (pullRequestRun.ExternalReviewDecision.IsNotBlank())
        {
            parts.Add($"review decision {pullRequestRun.ExternalReviewDecision}");
        }

        return string.Join("; ", parts) + ".";
    }
}
