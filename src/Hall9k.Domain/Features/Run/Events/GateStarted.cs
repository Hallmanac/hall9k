namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// One gate (build or test) actually started running as its own process, inside the daemon's own
/// node (task: a run whose verification gate is executing is reported as live work in progress,
/// never as stalled with no session recorded). The gate's own process identity — the same
/// pid-plus-start-time shape <see cref="RunProcessStarted"/> already records for an agent
/// session — is what lets the display observe it on the same terms, instead of falling back to
/// the agent stream's own last-write timestamp, which a gate never writes to at all. Cleared by
/// <see cref="GateEnded"/> once this gate's own process exits, whichever way it ends.
/// </summary>
public sealed record GateStarted(Guid Id, string GateName, int ProcessId, DateTimeOffset StartedAt, DateTimeOffset ObservedAt);
