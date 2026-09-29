using Hall9k.Domain.Features.Run.Projections;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// A pr-review task's own original review run: the first entry in its <c>RunIds</c> history that
/// actually opened a <see cref="RunDetails"/> stream (independent pre-PR review, cycle 5,
/// conformance lens, RunLauncher.cs:161). <c>RunIds[0]</c> alone no longer names it now that a
/// pr-review task's first claim can dispatch nothing but a pre-flight and requeue before any run is
/// ever opened (<c>RunLauncher.EnsurePrReviewPreflightSafeAsync</c>) — that claim still appends its
/// own run id to <c>RunIds</c> at <see cref="Hall9k.Domain.Features.Tasks.Events.TaskClaimed"/>, but
/// no <see cref="RunDetails"/> is ever written for it. Both the mention follow-up's own citation of
/// the review it must answer on top of (<c>AutoPrReviewEngine</c>'s <c>priorReviewRunId</c>) and
/// <c>h9k task show</c>'s "you were asked" linkage (<c>TaskShowCommand.AnsweredMentionAsync</c>) need
/// the run that genuinely carries <c>review-1-findings.md</c>, not merely the first id this task's
/// history happens to list.
/// </summary>
public static class OriginalReviewRunResolver
{
    public static async Task<Guid?> ResolveAsync(
        IQuerySession session, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        foreach (Guid runId in runIds)
        {
            if (await session.LoadAsync<RunDetails>(runId, cancellationToken) is not null)
            {
                return runId;
            }
        }

        return null;
    }
}
