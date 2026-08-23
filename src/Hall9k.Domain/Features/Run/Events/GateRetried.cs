namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A gate failed with a connection-class signature — Postgres or Testcontainers infrastructure,
/// not the agent's work — and is being re-run once in place before the run gives up on it
/// (backlog 53). Recorded so the stream says the flake happened whichever way the retry goes:
/// a passing retry leaves this as the only trace, and a failing one is followed by
/// <see cref="RunFailed"/> with the classification in its reason.
/// </summary>
public sealed record GateRetried(Guid Id, string Gate, string Cause, DateTimeOffset RetriedAt);
