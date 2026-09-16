namespace Hall9k.Domain.Features.Run;

/// <summary>
/// The one gate (build or test) actually running right now, as the stream last recorded it (task:
/// a run whose verification gate is executing is reported as live work in progress, never as
/// stalled with no session recorded) — named and identified on the same terms
/// <see cref="ActiveSession"/> already carries for an agent session, so a Verifying run's process
/// can be checked against the operating system instead of against the agent stream's own
/// last-write timestamp, which a gate never touches. Null between gates, and once the last
/// configured gate has finished — the honest reading of "nothing is running" <see cref="ActiveSession"/>'s
/// own doc already draws for an agent session.
/// </summary>
/// <param name="GateName">The gate's own configured name (<c>VerifyCommand.Name</c>) — build, test, whatever the project called it.</param>
/// <param name="ProcessId">The gate process's id, on the run's own node.</param>
/// <param name="StartedAt">The process start time, the other half of the identity (the PID-reuse guard, log #2).</param>
public sealed record ActiveGate(string GateName, int ProcessId, DateTimeOffset StartedAt);
