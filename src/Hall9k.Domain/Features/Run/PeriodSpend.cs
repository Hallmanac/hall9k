using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;

namespace Hall9k.Domain.Features.Run;

/// <summary>One model's share of a period's recorded spend, most-spent first.</summary>
public sealed record PeriodSpendByModel(AgentModel Model, long TotalInputTokens);

/// <summary>
/// The platform's whole recorded token spend since a period start, summed live from
/// <see cref="TokensRecorded"/> rather than held in a stored counter (backlog: spend-governor
/// step three) — a daemon restart can neither lose nor double-count it, because there is nothing
/// to lose: every read replays the same events. Platform-wide rather than scoped to one node,
/// the same way the total <see cref="TokensRecorded"/> already prices is: the spend-budget
/// setting is a per-node pacing throttle, but what it paces against is every token the install
/// has ever spent, on any node.
/// </summary>
public sealed record PeriodSpend(long TotalInputTokens, IReadOnlyList<PeriodSpendByModel> ByModel)
{
    public static async Task<PeriodSpend> ReadAsync(
        IQuerySession session, DateTimeOffset periodStart, CancellationToken cancellationToken)
    {
        IReadOnlyList<TokensRecorded> events = await session.Events.QueryRawEventDataOnly<TokensRecorded>()
            .Where(e => e.RecordedAt >= periodStart)
            .ToListAsync(cancellationToken);

        List<PeriodSpendByModel> byModel = [.. events
            .GroupBy(e => e.Model ?? AgentModel.Unknown)
            .Select(group => new PeriodSpendByModel(
                group.Key,
                group.Sum(e => e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens)))
            .OrderByDescending(entry => entry.TotalInputTokens)];

        return new PeriodSpend(byModel.Sum(entry => entry.TotalInputTokens), byModel);
    }
}
