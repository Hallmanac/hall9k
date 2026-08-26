namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The closeout monitor's post-PR review-state watcher observed Copilot's review landed,
/// requested but not yet submitted, or absent altogether (origin incident: PR #50 sat
/// Delivered for 23 minutes with a landed Copilot review nobody had read before the merge).
/// Read only by the Delivered phase line — it never moves <see cref="RunState"/> and never
/// becomes a new task lifecycle status.
/// </summary>
/// <param name="ThreadCount">
/// Every review thread Copilot's review opened, resolved or not — what the phase names
/// alongside a landed review. Distinct from <see cref="ReviewFeedbackReceived"/>'s unresolved
/// count, which drives the run into ReviewPending regardless of who opened the thread.
/// </param>
/// <param name="ChecksPending">
/// The provider's own CI picture was still incomplete at the moment this was observed
/// (<c>PullRequestSnapshot.HasPendingChecks</c>), recorded in the same sweep and ahead of the
/// checks-and-threads read that would otherwise decide the run's next state (pre-PR review,
/// cycle 3). While this is true a landed review has not been read against a settled CI result
/// and its threads have not been re-checked for new unresolved ones this sweep either, so the
/// Delivered surfaces must not name the human as the last gate — the identical caveat a quiet
/// pull request already carries. False means this sweep got past both the checks and the
/// unresolved-thread reads without moving the run off <c>AwaitingReview</c>, which only happens
/// when checks passed and every thread, Copilot's included, was resolved.
/// </param>
public sealed record ExternalReviewObserved(
    Guid Id,
    ExternalReviewState State,
    int ThreadCount,
    bool ChecksPending,
    DateTimeOffset ObservedAt);
