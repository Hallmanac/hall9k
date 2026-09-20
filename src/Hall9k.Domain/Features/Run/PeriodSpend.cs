using Hall9k.Domain.Features.Courier;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Domain.Features.Run;

/// <summary>One model's share of a period's recorded spend, most-spent first.</summary>
public sealed record PeriodSpendByModel(AgentModel Model, long TotalInputTokens);

/// <summary>
/// The platform's whole recorded token spend since a period start, summed live from
/// <see cref="TokensRecorded"/> — a run session's spend —
/// <see cref="PublicationTokensRecorded"/> — a card-publication errand's, which rides the task's
/// own stream because it has no run to carry a <see cref="TokensRecorded"/> of its own — and
/// <see cref="CourierTokensRecorded"/> — a feed courier's, which rides neither a run's nor a
/// task's stream because it has no task at all (idea 89471598, piece 3) — plus every
/// <see cref="PurgedSpendRecord"/> a project purge has ever carried forward on their behalf
/// (task: an archived project can be purged), rather than held in a stored counter (backlog:
/// spend-governor step three): a daemon restart can neither lose nor double-count it, because
/// there is nothing to lose, every read replays the same events (and re-reads the same surviving
/// records). Platform-wide rather than scoped to one node, the same way the total either event
/// already prices is: the spend-budget setting is a per-node pacing throttle, but what it paces
/// against is every token the install has ever spent, on any node — except a courier's own spend,
/// which never leaves the node it ran on (<see cref="CourierTokensRecorded"/>'s own event scope
/// is node-scoped, the same as the presence it depends on), so this total only ever carries a
/// courier's cost on the machine that spawned it.
/// </summary>
public sealed record PeriodSpend(long TotalInputTokens, IReadOnlyList<PeriodSpendByModel> ByModel)
{
    public static async Task<PeriodSpend> ReadAsync(
        IQuerySession session, DateTimeOffset periodStart, CancellationToken cancellationToken)
    {
        IReadOnlyList<TokensRecorded> runEvents = await session.Events.QueryRawEventDataOnly<TokensRecorded>()
            .Where(e => e.RecordedAt >= periodStart)
            .ToListAsync(cancellationToken);

        IReadOnlyList<PublicationTokensRecorded> publicationEvents = await session.Events
            .QueryRawEventDataOnly<PublicationTokensRecorded>()
            .Where(e => e.RecordedAt >= periodStart)
            .ToListAsync(cancellationToken);

        IReadOnlyList<CourierTokensRecorded> courierEvents = await session.Events
            .QueryRawEventDataOnly<CourierTokensRecorded>()
            .Where(e => e.RecordedAt >= periodStart)
            .ToListAsync(cancellationToken);

        IReadOnlyList<PurgedSpendRecord> purgedRecords = await session.Query<PurgedSpendRecord>()
            .Where(r => r.RecordedAt >= periodStart)
            .ToListAsync(cancellationToken);

        List<(AgentModel Model, long InputTokens)> spend = [
            .. runEvents.Select(e => (
                e.Model ?? AgentModel.Unknown, e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens)),
            .. publicationEvents.Select(e => (
                e.Model ?? AgentModel.Unknown, e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens)),
            .. courierEvents.Select(e => (
                e.Model ?? AgentModel.Unknown, e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens)),
            .. purgedRecords.Select(r => (r.Model, r.TotalInputTokens)),
        ];

        List<PeriodSpendByModel> byModel = [.. spend
            .GroupBy(entry => entry.Model)
            .Select(group => new PeriodSpendByModel(group.Key, group.Sum(entry => entry.InputTokens)))
            .OrderByDescending(entry => entry.TotalInputTokens)];

        return new PeriodSpend(byModel.Sum(entry => entry.TotalInputTokens), byModel);
    }
}
