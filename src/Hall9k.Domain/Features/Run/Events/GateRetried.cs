namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// A gate failed with an infrastructure signature — Postgres or Testcontainers, or MSBuild's own
/// shared child node crashing under concurrent gates on Windows — not the agent's work, and is
/// being re-run once in place before the run gives up on it (backlog 53; the MSBuild shape is
/// Windows field report item 3, ruled 2026-09-01). Recorded so the stream says the flake happened
/// whichever way the retry goes:
/// a passing retry leaves this as the only trace, and a failing one is followed by
/// <see cref="RunFailed"/> with the classification in its reason.
/// </summary>
public sealed record GateRetried(Guid Id, string Gate, string Cause, DateTimeOffset RetriedAt);
