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
/// during the period and has since left the queue by some other path, so no row is left to report
/// it (independent pre-PR review, cycle 1, conformance lens).
/// <para>
/// Two candidate sources feed this instead: every task the queued section is holding right now
/// (<paramref name="currentlyQueuedTaskIds"/>), plus every task whose stream recorded, inside the
/// period, any event that can close a queued segment (<see cref="TaskPassageQuery"/>'s own
/// <c>BuildQueueSegments</c> switch — <c>TaskClaimed</c>, the dominant path, alongside
/// <c>TaskUnassigned</c>, <c>TaskInteractiveClaimUnassigned</c>, <c>TaskAbandoned</c>,
/// <c>TaskResolved</c>, <c>TaskReturnedToDraft</c>, and <c>TaskCompleted</c> — a task unassigned,
/// abandoned, resolved, or returned to draft mid-period without ever being reclaimed left no row
/// in the queued section and no <c>TaskClaimed</c> for the original candidate scan to find it by
/// (independent pre-PR review, cycle 2, conformance lens). Each candidate's own queued segments are
/// then clipped to the period window by <see cref="TaskPassageQuery.FoldQueuedWithinPeriod"/> — the
/// same segment fold <see cref="TaskPassageQuery.ReadQueuedAsync"/>'s own current-segment reading
/// and <see cref="TaskPassage.Queued"/>'s own lifetime reading both already share, so a task with no
/// queue time actually inside the window (an event landing in the period against a segment that
/// opened and closed entirely before it) contributes nothing rather than being double-counted.
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

        IReadOnlyList<TaskUnassigned> unassignedThisPeriod = await session.Events.QueryRawEventDataOnly<TaskUnassigned>()
            .Where(unassigned => unassigned.UnassignedAt >= periodStart && unassigned.UnassignedAt < now)
            .ToListAsync(cancellationToken);

        IReadOnlyList<TaskInteractiveClaimUnassigned> interactiveUnassignedThisPeriod = await session.Events
            .QueryRawEventDataOnly<TaskInteractiveClaimUnassigned>()
            .Where(unassigned => unassigned.UnassignedAt >= periodStart && unassigned.UnassignedAt < now)
            .ToListAsync(cancellationToken);

        IReadOnlyList<TaskAbandoned> abandonedThisPeriod = await session.Events.QueryRawEventDataOnly<TaskAbandoned>()
            .Where(abandoned => abandoned.AbandonedAt >= periodStart && abandoned.AbandonedAt < now)
            .ToListAsync(cancellationToken);

        IReadOnlyList<TaskResolved> resolvedThisPeriod = await session.Events.QueryRawEventDataOnly<TaskResolved>()
            .Where(resolved => resolved.ResolvedAt >= periodStart && resolved.ResolvedAt < now)
            .ToListAsync(cancellationToken);

        IReadOnlyList<TaskReturnedToDraft> returnedThisPeriod = await session.Events.QueryRawEventDataOnly<TaskReturnedToDraft>()
            .Where(returned => returned.ReturnedAt >= periodStart && returned.ReturnedAt < now)
            .ToListAsync(cancellationToken);

        IReadOnlyList<TaskCompleted> completedThisPeriod = await session.Events.QueryRawEventDataOnly<TaskCompleted>()
            .Where(completed => completed.CompletedAt >= periodStart && completed.CompletedAt < now)
            .ToListAsync(cancellationToken);

        HashSet<Guid> candidates =
        [
            .. currentlyQueuedTaskIds,
            .. claimedThisPeriod.Select(claimed => claimed.Id),
            .. unassignedThisPeriod.Select(unassigned => unassigned.Id),
            .. interactiveUnassignedThisPeriod.Select(unassigned => unassigned.Id),
            .. abandonedThisPeriod.Select(abandoned => abandoned.Id),
            .. resolvedThisPeriod.Select(resolved => resolved.Id),
            .. returnedThisPeriod.Select(returned => returned.Id),
            .. completedThisPeriod.Select(completed => completed.Id),
        ];

        TimeSpan total = TimeSpan.Zero;
        foreach (Guid taskId in candidates)
        {
            IReadOnlyList<IEvent> taskEvents = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
            total += TaskPassageQuery.FoldQueuedWithinPeriod(taskEvents, periodStart, now);
        }

        return total;
    }
}
