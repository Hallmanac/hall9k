namespace Hall9k.Domain.Features.Node;

/// <summary>
/// This node stopped being able to launch working agent sessions (task: a session that exits
/// at once with no work done is treated as the node failing to launch sessions): a session
/// exited with the zero-work launch-failure shape — one turn, zero tokens, sub-second — which is
/// the node's own trouble, not the task's. Raised once per episode; every further zero-work
/// session while the hold stands joins it (<see cref="NodeLaunchHoldRunHeld"/>) rather than
/// raising it again, which is what keeps this event, and the one warn line it earns, to exactly
/// one per episode.
/// </summary>
/// <param name="Id">The node's own stream id, as <see cref="NodeRegistered.Id"/> already keys it.</param>
/// <param name="CauseText">
/// The triggering session's own result text, reported to the human exactly as observed — never
/// matched against, since the next cause will be a different string (the 2026-09-07 outage's own
/// two causes, an expired credential and a GitHub fetch timeout, already were).
/// </param>
public sealed record NodeLaunchHoldRaised(Guid Id, string CauseText, DateTimeOffset RaisedAt);
