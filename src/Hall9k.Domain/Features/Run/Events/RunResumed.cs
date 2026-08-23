namespace Hall9k.Domain.Features.Run.Events;

/// <summary>After an answer: new process, same claude session (exit-and-resume, log #5).</summary>
public sealed record RunResumed(
    Guid Id,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset ResumedAt);
