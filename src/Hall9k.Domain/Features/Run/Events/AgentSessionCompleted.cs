namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The agent's claude process emitted its final result event and exited. The run is not
/// done — verification gates run next. Detected from the stream file, never the exit code.
/// </summary>
public sealed record AgentSessionCompleted(
    Guid Id,
    DateTimeOffset CompletedAt);
