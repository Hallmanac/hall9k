namespace Hall9k.Domain.Features.Run.Events;

/// <summary>PID + process start time together are the identity (PID-reuse guard, log #2).</summary>
public sealed record RunProcessStarted(
    Guid Id,
    int ProcessId,
    DateTimeOffset ProcessStartedAt);
