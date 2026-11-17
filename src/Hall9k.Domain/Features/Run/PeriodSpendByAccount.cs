using Hall9k.Domain.Features.Run.Events;
using Marten;

namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One Claude Code account's share of a period's run spend: <see cref="ConnectionId"/> names the
/// claude-code connection, or is null for the default login.
/// </summary>
public sealed record PeriodAccountSpend(Guid? ConnectionId, long TotalInputTokens);

/// <summary>
/// A period's recorded spend split by the Claude Code account each run on this node was frozen on
/// (<see cref="RunClaudeCodeAccountRecorded"/>), for <c>h9k config show</c> only. Kept apart from
/// <see cref="PeriodSpend"/>, which every spend gate reads, so the gates never pay for the join.
/// <para>
/// A run's <see cref="TokensRecorded"/> counts toward the account its own stream recorded at dispatch.
/// Everything else in the period total is <see cref="UnattributedInputTokens"/>: a run with no record on
/// this node (a teammate's, whose record is node-scoped, or one dispatched before the freeze existed) and
/// all spend recorded outside a run (plan, publication, challenge, feedback, courier, and purged records).
/// </para>
/// </summary>
public sealed record PeriodSpendByAccount(IReadOnlyList<PeriodAccountSpend> ByAccount, long UnattributedInputTokens)
{
    /// <summary>
    /// Splits <paramref name="periodTotal"/>, the <see cref="PeriodSpend.TotalInputTokens"/> for the same
    /// <paramref name="periodStart"/>, so the account lines and the unattributed line always add up to it.
    /// <paramref name="session"/> must be the one snapshot the total was read on: read committed, a session
    /// recorded between the two reads counts here and not in the total, and the unattributed line goes negative.
    /// </summary>
    public static async Task<PeriodSpendByAccount> ReadAsync(
        IQuerySession session, DateTimeOffset periodStart, long periodTotal, CancellationToken cancellationToken)
    {
        IReadOnlyList<TokensRecorded> runEvents = await session.Events.QueryRawEventDataOnly<TokensRecorded>()
            .Where(e => e.RecordedAt >= periodStart)
            .ToListAsync(cancellationToken);

        Guid[] runIds = [.. runEvents.Select(e => e.Id).Distinct()];
        IReadOnlyList<RunClaudeCodeAccountRecorded> accountRecords = runIds.Length == 0
            ? []
            : await session.Events.QueryRawEventDataOnly<RunClaudeCodeAccountRecorded>()
                .Where(e => e.Id.IsOneOf(runIds))
                .ToListAsync(cancellationToken);

        return Split(runEvents, accountRecords, periodTotal);
    }

    /// <summary>
    /// The pure join behind <see cref="ReadAsync"/>. A run that recorded its account more than once keeps
    /// the latest record, the same one <c>RunDetails</c> projects and every spawn for the run reads.
    /// </summary>
    public static PeriodSpendByAccount Split(
        IEnumerable<TokensRecorded> runEvents,
        IEnumerable<RunClaudeCodeAccountRecorded> accountRecords,
        long periodTotal)
    {
        Dictionary<Guid, Guid?> frozenAccountByRun = accountRecords
            .GroupBy(record => record.Id)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(record => record.RecordedAt).Last().ConnectionId);

        List<PeriodAccountSpend> byAccount = [.. runEvents
            .Where(e => frozenAccountByRun.ContainsKey(e.Id))
            .GroupBy(e => frozenAccountByRun[e.Id])
            .Select(group => new PeriodAccountSpend(
                group.Key,
                group.Sum(e => e.InputTokens + e.CacheReadInputTokens + e.CacheCreationInputTokens)))
            .Where(entry => entry.TotalInputTokens > 0)
            .OrderByDescending(entry => entry.TotalInputTokens)];

        return new PeriodSpendByAccount(byAccount, periodTotal - byAccount.Sum(entry => entry.TotalInputTokens));
    }
}
