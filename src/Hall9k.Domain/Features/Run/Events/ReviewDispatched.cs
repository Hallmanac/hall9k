namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// An independent review agent was spawned over the run's diff before its pull request
/// opens (Decisions Log #23): a separate headless session with fresh context — never the
/// session that wrote the code. Cycle counts review rounds from 1. ProcessId + start time
/// are the session's identity for adoption (the PID-reuse guard, log #2). RunState →
/// UnderReview.
/// </summary>
public sealed record ReviewDispatched(
    Guid Id,
    Guid SessionId,
    int Cycle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset DispatchedAt);
