namespace Hall9k.Domain.Features.Node;

/// <summary>
/// One run joined this node's standing launch hold (task: a session that exits at once with no
/// work done is treated as the node failing to launch sessions) — appended once per run for as
/// long as the hold stands, whether this run is the one that raised it
/// (<see cref="NodeLaunchHoldRaised"/>) or a later run the same outage caught. What the episode
/// query counts as "runs held".
/// </summary>
public sealed record NodeLaunchHoldRunHeld(Guid Id, Guid RunId, DateTimeOffset HeldAt);
