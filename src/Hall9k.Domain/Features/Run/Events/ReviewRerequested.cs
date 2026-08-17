namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor re-requested a review from the reviewer whose review errored,
/// through the provider's API — never the website, which may be down when this matters
/// (the origin incident's exact circumstance). Each re-request draws on the same
/// automatic closeout budget as follow-up dispatches; the run stays ReviewPending until
/// the reviewer answers, and the recorded ErroredReviewUrl keeps the monitor from
/// re-requesting the same errored review on every sweep.
/// </summary>
public sealed record ReviewRerequested(
    Guid Id,
    string Reviewer,
    string ErroredReviewUrl,
    DateTimeOffset RequestedAt);
