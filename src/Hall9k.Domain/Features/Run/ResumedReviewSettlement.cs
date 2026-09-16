namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The failed run's own settled review, frozen onto <see cref="Events.RunDispatched"/> for the run
/// that resumes directly at pull-request-open (task: a run that failed only at pull-request
/// opening resumes at that step on retry) instead of re-running the build and review pipeline.
/// That resumed run never dispatches a reviewer of its own — nothing on its stream would
/// otherwise say the branch was reviewed at all, or by which composition, or what it left
/// residual — so this is what <see cref="Projections.RunDetails"/> and <see cref="PullRequestBody"/>
/// (in <c>Hall9k.Daemon.Execution</c>) read instead of the resumed run's own (empty) review
/// history. Null for every other dispatch shape, which settles its own review the ordinary way
/// through <see cref="Events.ReviewSettled"/> instead of inheriting one (independent pre-PR
/// review, cycle 3, conformance and adversarial lenses: without this, the resumed run's pull
/// request falsely claimed a reduced or skipped review and dropped every residual finding the
/// original review left for the owner to see).
/// <para>
/// Tokens are the failed run's own whole-run totals, not the resumed run's — the resumed run
/// itself spends none, since it only re-attempts <c>gh pr create</c>, and reporting that as the
/// pull request's token footer would understate the branch's real cost to zero.
/// </para>
/// </summary>
public sealed record ResumedReviewSettlement(
    ReviewSettlement Settlement,
    int ResidualsFixed,
    int ResidualsRouted,
    int ResidualsRoutingFailed,
    int ResidualsRideAlong,
    IReadOnlyList<ReviewRideAlongFinding> RideAlongFindings,
    int ResidualsUnfixed,
    IReadOnlyList<ReviewUnfixedFinding> UnfixedFindings,
    long InputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    long OutputTokens);
