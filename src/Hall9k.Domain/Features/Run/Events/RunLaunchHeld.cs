namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// The session's terminal result carried the zero-work launch-failure shape (task: a session
/// that exits at once with no work done is treated as the node failing to launch sessions): an
/// error result with exactly one turn, zero input and zero output tokens, in under the
/// threshold <c>DaemonOptions.LaunchFailureMaxDuration</c> states — the node never actually
/// launched a working session, so this is not this run's own fault and never fails it. The run
/// parks exactly as <see cref="RunBudgetExhausted"/> does, and a node-wide launch hold (recorded
/// on the node's own stream) is what clears it: the probe relaunching the oldest held run is the
/// resumption, not a per-run timer.
/// </summary>
public sealed record RunLaunchHeld(
    Guid Id,
    string ObservedMessage,
    DateTimeOffset HeldAt);
