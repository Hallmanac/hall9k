namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// One task's worth of <see cref="OrchestratorFeedItem"/>s, oldest first — what
/// <c>h9k orchestrator feed</c> prints under a single heading.
/// </summary>
/// <param name="Heading">
/// How the group is named: the task's own short id and objective, or the honest catch-all for
/// items belonging to no task. Composed by the caller, which is the only layer that can read a
/// task's objective — the feed never prints a bare id.
/// </param>
public sealed record OrchestratorFeedGroup(
    Guid? TaskId,
    string Heading,
    IReadOnlyList<OrchestratorFeedItem> Items);
