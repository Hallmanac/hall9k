namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The session's terminal result carried the recognizable usage-limit message shape (log
/// #40): the subscription window ran dry mid-flight, not a machine or code fault. The run
/// parks rather than fails — the cause is external and recoverable by clock, so the task
/// stays Claimed and the hourly retry sweep (<c>TokenBudgetRetryEngine</c>) is what clears
/// it, with no human act required.
/// </summary>
public sealed record RunBudgetExhausted(
    Guid Id,
    string ObservedMessage,
    DateTimeOffset ExhaustedAt);
