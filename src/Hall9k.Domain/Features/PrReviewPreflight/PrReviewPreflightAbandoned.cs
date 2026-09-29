namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// A pull-request review pre-flight (idea 6be68ee2, finding 1, phase one) never reached a real
/// verdict — a wall-clock timeout, a budget exhaustion, a launch failure, or a daemon restart that
/// found its process gone — appended to its own stream alongside the task's own requeue so this
/// permanently-incomplete row is never again mistaken for one still genuinely in flight.
/// <para>
/// Without this marker, a completed-never row and a genuinely-running one were indistinguishable
/// by <see cref="CompletedAt"/> alone: <c>RunLauncher.EnsurePrReviewPreflightSafeAsync</c> read the
/// abandoned row as "already dispatched for this exact head" and returned without ever starting a
/// fresh pre-flight, and <c>RunSupervisor.ResumeStrandedPreflightsAsync</c> re-abandoned the same
/// row every sweep — a <c>TaskRequeued</c>/<c>TaskClaimed</c> loop with no pre-flight ever actually
/// running again (independent pre-PR review, cycle 1, both lenses).
/// </para>
/// </summary>
public sealed record PrReviewPreflightAbandoned(Guid Id, DateTimeOffset AbandonedAt);
