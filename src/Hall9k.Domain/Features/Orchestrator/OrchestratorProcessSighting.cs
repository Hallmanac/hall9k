namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// What one <see cref="IOrchestratorProcessProbe.Probe"/> call saw: a process with this id exists
/// right now, and this is when it started — or <see langword="null"/> when this user could not
/// read that (an elevated or root-owned process most commonly), never a plausible substitute.
/// <see cref="OrchestratorLiveness"/> is what decides what an unreadable start time means.
/// </summary>
public sealed record OrchestratorProcessSighting(DateTimeOffset? StartedAt);
