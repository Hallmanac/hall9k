namespace Hall9k.Domain.Features.Node;

/// <summary>
/// The hold's own probe relaunched the oldest held run (task: a session that exits at once with
/// no work done is treated as the node failing to launch sessions) — there is no separate probe
/// process; the held work being relaunched is the probe. Appended before the relaunch is
/// dispatched, so a daemon that dies mid-relaunch still remembers the attempt happened and the
/// next backoff computation (<c>LaunchHoldEngine</c>) starts from it rather than from the raise.
/// </summary>
public sealed record NodeLaunchHoldProbed(Guid Id, Guid RunId, DateTimeOffset ProbedAt);
