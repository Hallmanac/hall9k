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
public sealed record ExternalReviewObserved(
    Guid Id,
    ExternalReviewState State,
    int ThreadCount,
    DateTimeOffset ObservedAt);
