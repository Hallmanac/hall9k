using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// The queued section's own heading figure in <c>StatusCommand</c> (task: h9k status reports
/// throughput beside spend): how much of this spend period every task actually spent queued, not
/// a currently-queued row's own lifetime wait. That figure is wrong on two counts a currently-queued
/// row's own <see cref="TaskPassageQuery.ReadQueuedAsync"/> total cannot fix on its own: it can
/// include queued time from long before the period started, and it leaves out a task that queued
/// during the period and has since been claimed, so no row is left to report it (independent
/// pre-PR review, cycle 1, conformance lens).
/// <para>
/// Two candidate sources feed this instead: every task the queued section is holding right now
/// (<paramref name="currentlyQueuedTaskIds"/>), plus every task whose own <c>TaskClaimed</c> landed
/// inside the period — the dominant way a task leaves the queue, and the one the origin finding's
/// own scenario names. A task whose queued segment ended some other way mid-period (unassigned,
/// abandoned, resolved without ever being claimed) is not swept in here: those exits are rare next
/// to an ordinary claim, and this heading already says "so far" rather than promising an exact
/// ledger. Each candidate's own queued segments are then clipped to the period window by
/// <see cref="TaskPassageQuery.FoldQueuedWithinPeriod"/> — the same segment fold
/// <see cref="TaskPassageQuery.ReadQueuedAsync"/>'s own current-segment reading and
/// <see cref="TaskPassage.Queued"/>'s own lifetime reading both already share.
/// </para>
/// </summary>
public static class QueuedPeriodTotal
{
    public static async Task<TimeSpan> ReadAsync(
        IQuerySession session, IReadOnlyList<Guid> currentlyQueuedTaskIds, DateTimeOffset periodStart,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskClaimed> claimedThisPeriod = await session.Events.QueryRawEventDataOnly<TaskClaimed>()
            .Where(claimed => claimed.ClaimedAt >= periodStart && claimed.ClaimedAt < now)
            .ToListAsync(cancellationToken);

        HashSet<Guid> candidates = [.. currentlyQueuedTaskIds, .. claimedThisPeriod.Select(claimed => claimed.Id)];

        TimeSpan total = TimeSpan.Zero;
        foreach (Guid taskId in candidates)
        {
            IReadOnlyList<IEvent> taskEvents = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
            total += TaskPassageQuery.FoldQueuedWithinPeriod(taskEvents, periodStart, now);
        }

        return total;
    }
}
