using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Connectors.Orchestrator;

/// <summary>
/// The exact lines a drain prints (idea 89471598, piece 3's own acceptance criterion: the
/// courier's prompt carries "the feed items as the drain prints them"), composed here in
/// Connectors — reachable from both the Cli's own <c>h9k orchestrator feed</c> and the Daemon's
/// own courier, unlike <c>OrchestratorFeedCommand</c>'s own printing helpers, which the Daemon
/// cannot reference at all. Built from the identical lower-level primitives
/// <c>OrchestratorFeedCommand.PrintItemsAsync</c> composes (<see cref="OrchestratorFeedRenderer.Group"/>,
/// <see cref="OrchestratorFeedRenderer.Render"/>, and <see cref="RelayedText.OneLine"/> — the same
/// function <c>Cli.Infrastructure.ExternalText.OneLine</c> forwards to), so a courier's message
/// and a plain <c>h9k orchestrator feed</c> read of the identical items print identically. The
/// task headings are the one piece they share outright: <see cref="HeadingsAsync"/> is public and
/// the Cli's feed command calls it, so a heading change lands here once.
/// </summary>
public static class OrchestratorFeedPrinter
{
    /// <summary>Sanitized, terminal-safe lines: a heading per task, then one indented line per item, oldest first.</summary>
    public static async Task<IReadOnlyList<string>> ComposeAsync(
        IQuerySession session, OrchestratorFeedRead read, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, string> headings = await HeadingsAsync(session, read.Items, cancellationToken);
        IReadOnlyList<OrchestratorFeedGroup> groups = OrchestratorFeedRenderer.Group(
            read.Items,
            taskId => headings.TryGetValue(taskId, out string? heading)
                ? heading
                // A task id with no projection behind it on this node: replication can carry a
                // task's own events here before its document lands, and a purged project leaves
                // the same shape. Named as unreadable rather than printed bare, so the line still
                // says what it is (AGENTS.md, never guess at unobserved facts).
                : $"{DomainId.Short(taskId)}  (a task this node has no record of yet)");

        return [.. OrchestratorFeedRenderer.Render(groups, zone).Select(RelayedText.OneLine)];
    }

    /// <summary>
    /// How each task in this read is named: its short id and its objective, never the bare id,
    /// plus the unknown-blocker sentence when it waits on a blocker this node has no task for
    /// (<see cref="UnknownBlocker"/>). Public because <c>h9k orchestrator feed</c> names its groups
    /// through this same call, so the two surfaces cannot head a task differently.
    /// One batched load per kind rather than one per group.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> HeadingsAsync(
        IQuerySession session,
        IReadOnlyList<OrchestratorFeedItem> items,
        CancellationToken cancellationToken)
    {
        Guid[] taskIds = [.. items.Select(item => item.TaskId).OfType<Guid>().Distinct()];
        if (taskIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        IReadOnlyList<TaskDetails> tasks = await session.LoadManyAsync<TaskDetails>(cancellationToken, taskIds);

        // The dependency sweep records nothing for a blocker this node has no record of, so no
        // feed item ever names it; the heading is the one place a drain can say so.
        Guid[] blockerIds =
        [
            .. tasks.Where(task => task.State == TaskState.Blocked).SelectMany(task => task.UnmetDependencies).Distinct(),
        ];
        HashSet<Guid> knownBlockers = blockerIds.Length == 0
            ? []
            : [.. await session.Query<TaskListItem>()
                .Where(task => task.Id.IsOneOf(blockerIds))
                .Select(task => task.Id)
                .ToListAsync(cancellationToken)];

        return tasks.ToDictionary(
            task => task.Id,
            task => Heading(
                task,
                task.State == TaskState.Blocked
                    ? UnknownBlocker.Among(task.UnmetDependencies, knownBlockers, task.DeadDependencies)
                    : []));
    }

    /// <summary>
    /// One group's heading: the short id and objective, then the unknown-blocker sentence when the
    /// task is waiting on a blocker this node has no record of. The sentence names the blocker by
    /// short id alone; its objective is not known here and is never guessed.
    /// </summary>
    internal static string Heading(TaskDetails task, IReadOnlyList<Guid> unknownBlockerIds) =>
        $"{DomainId.Short(task.Id)}  "
        + RelayedText.Truncate(RelayedText.OneLine(task.Objective), 90)
        + (UnknownBlocker.Line(unknownBlockerIds) is { } line ? $"  ({line})" : string.Empty);
}
