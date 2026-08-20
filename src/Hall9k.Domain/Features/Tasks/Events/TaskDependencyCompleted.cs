namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// One blocker reached true closeout — RunCompleted from the closeout monitor, which only
/// lands when the merge is observed (Decisions Log #34). Nothing weaker counts: a Done task
/// whose pull request is still open is still a blocker. When this clears the last unmet
/// dependency the task moves Blocked -> Queued and the dispatcher may claim it.
/// </summary>
public sealed record TaskDependencyCompleted(
    Guid Id,
    Guid DependencyId,
    IReadOnlyList<Guid> RemainingDependencies,
    DateTimeOffset CompletedAt);
