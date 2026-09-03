using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Marten;

namespace Hall9k.Cli.Infrastructure;

/// <summary>
/// A start-it-mine claim's <c>stream.jsonl</c> is the only record of its own token spend —
/// nothing on this node ever adopts a run dispatched under the <c>Guid.Empty</c> sentinel node id
/// the way <c>RunSupervisor</c> adopts a headless dispatch, so <c>h9k task deliver</c>'s own
/// <c>TaskDeliverCommand.ReadHeadlessResult</c> read is the only place that file is ever read back
/// for a run that reaches delivery. Every other lever that can retire such a run first — handback,
/// release, retry, abandon — was discarding that spend permanently, letting the node's periodic
/// token-spend budget (<c>PeriodSpend</c>) under-count whatever the session actually burned
/// (conformance review, cycle 1, on h9k task start). An attended <c>h9k task work</c> claim writes
/// no <c>stream.jsonl</c> at all, so calling this for that run finds nothing and appends nothing —
/// the same no-op <c>ReadHeadlessResult</c> already returns for a missing file.
/// </summary>
internal static class HeadlessTokenRecovery
{
    public static void AppendIfRecorded(IDocumentSession session, RunDetails run, DateTimeOffset recordedAt)
    {
        TaskDeliverCommand.HeadlessResult result = TaskDeliverCommand.ReadHeadlessResult(run.RunDirectory);
        if (result.Usage is { } usage)
        {
            session.Events.Append(run.Id, new TokensRecorded(
                run.Id, usage.InputTokens, usage.OutputTokens, usage.CostUsd, recordedAt,
                usage.CacheReadInputTokens, usage.CacheCreationInputTokens, run.Model));
        }
    }
}
