using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Re-projects task streams whose documents were last written before the lifecycle split
/// (Decisions Log #34) taught the task projections about AssignedOwnerId. The projections are
/// Inline, so a document is only ever rewritten when its stream gets a new event: a task that
/// reached Done last week still carries last week's shape, and nothing in the platform rebuilds
/// it. That matters because the dispatcher's claim filter is <c>State == Queued</c> plus
/// <c>AssignedOwnerId == this node's owner</c> — a document with no assignedOwnerId key at all
/// never matches, so a pre-split task that comes back to Queued (an expired lease, a closeout
/// reopen, h9k task retry) would never be claimed again, and h9k task show would call it
/// unowned. The events already say who owns it (TaskAdded replays as assigned to the owner who
/// added it), so replaying them is the whole migration PLAN.md #34 promised.
///
/// Origin incident (2026-08-20): the lifecycle split shipped the new filter without the
/// rebuild, and every one of the 24 tasks in the dogfooding database was left permanently
/// unclaimable — silently, because an unclaimable task looks exactly like an idle queue.
/// </summary>
public static class TaskLifecycleProjectionBackfill
{
    /// <summary>
    /// What a pre-split document looks like: the key the current projections always write —
    /// as a value or as an explicit null — is simply absent. Written as jsonb_exists rather
    /// than the <c>?</c> operator because <c>?</c> is Marten's parameter placeholder.
    /// </summary>
    private const string PreSplitDocument = "not jsonb_exists(d.data, 'assignedOwnerId')";

    /// <summary>
    /// Rebuilds every task stream still carrying a pre-split document and returns their ids.
    /// Idempotent and self-terminating: a rebuilt document has the key, so the next call finds
    /// nothing. Both task projections are single-stream on the task's own id, so one pass over
    /// the union of the two stale sets repairs both.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> RunAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        Guid[] stale = await StaleStreamsAsync(store, cancellationToken);
        if (stale.Length == 0)
        {
            return [];
        }

        foreach (Guid streamId in stale)
        {
            await store.Advanced.RebuildSingleStreamAsync<TaskListItem>(streamId, cancellationToken);
            await store.Advanced.RebuildSingleStreamAsync<TaskDetails>(streamId, cancellationToken);
        }

        return stale;
    }

    private static async Task<Guid[]> StaleStreamsAsync(
        IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<Guid> rows = await session.Query<TaskListItem>()
            .Where(task => task.MatchesSql(PreSplitDocument))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);
        IReadOnlyList<Guid> details = await session.Query<TaskDetails>()
            .Where(task => task.MatchesSql(PreSplitDocument))
            .Select(task => task.Id)
            .ToListAsync(cancellationToken);

        return [.. rows.Concat(details).Distinct()];
    }
}
