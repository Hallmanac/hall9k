using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Orchestrator;
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
/// and a plain <c>h9k orchestrator feed</c> read of the identical items print identically without
/// the two implementations sharing a single call.
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
    /// How each task in this read is named: its short id and its objective, never the bare id.
    /// One batched load rather than one per group.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> HeadingsAsync(
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
        return tasks.ToDictionary(
            task => task.Id,
            task => $"{DomainId.Short(task.Id)}  "
                + RelayedText.Truncate(RelayedText.OneLine(task.Objective), 90));
    }
}
