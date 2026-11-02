namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// One blocker reached true closeout — RunCompleted from the closeout monitor, which only
/// lands when the merge is observed (Decisions Log #34). Nothing weaker counts: a Done task
/// whose pull request is still open is still a blocker. When this clears the last unmet
/// dependency the task moves Blocked -> Queued and the dispatcher may claim it, unless
/// <paramref name="HoldForFeedbackStep"/> says it takes the feedback step, in which case it stays Blocked and held.
/// </summary>
/// <param name="HoldForFeedbackStep">Whether the task is held for its feedback step instead of released when this clears its last blocker; see <see cref="TaskAssigned"/>.</param>
public sealed record TaskDependencyCompleted(
    Guid Id,
    Guid DependencyId,
    IReadOnlyList<Guid> RemainingDependencies,
    DateTimeOffset CompletedAt,
    bool HoldForFeedbackStep = false);
