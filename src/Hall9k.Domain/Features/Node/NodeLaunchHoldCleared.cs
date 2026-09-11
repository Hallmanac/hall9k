namespace Hall9k.Domain.Features.Node;

/// <summary>
/// A relaunch recorded tokens (task: a session that exits at once with no work done is treated
/// as the node failing to launch sessions): the node can launch working sessions again, so the
/// hold clears and the dispatcher resumes claiming. Every run <see cref="NodeLaunchHoldRunHeld"/>
/// named for this episode resumes through the same re-entry paths its own in-place retry would
/// have used (<c>LaunchHoldMonitor.SweepOnceAsync</c>'s own sweep, once it next observes the
/// hold inactive — not just the one run whose relaunch proved the node works again).
/// </summary>
public sealed record NodeLaunchHoldCleared(Guid Id, DateTimeOffset ClearedAt);
