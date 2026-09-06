using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Storage;
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
        AppendUsage(session, run, result.Usage, recordedAt);
    }

    /// <summary>
    /// The delegation counterpart of <see cref="AppendIfRecorded"/>: <c>h9k task delegate</c>
    /// spawns each contractor into its own session-scoped stream file
    /// (<see cref="RunPaths.SessionStreamFile"/>), never the run-level one, so this run's own
    /// <see cref="RunDetails.PhaseDelegations"/> is the only index of which files exist to read
    /// back. Called alongside <see cref="AppendIfRecorded"/> at every lever that can retire a run
    /// carrying delegations (deliver, handback, release, retry, abandon) — none of which observed
    /// a delegated contractor's own spend before this, letting the node's periodic token-spend
    /// budget under-count every phase a claim ever delegated (independent pre-PR review, cycle 1,
    /// both lenses, on h9k task delegate).
    /// </summary>
    public static void AppendDelegatedPhaseTokens(IDocumentSession session, RunDetails run, DateTimeOffset recordedAt)
    {
        if (run.PhaseDelegations.Count == 0)
        {
            return;
        }

        string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        foreach (PhaseDelegation delegation in run.PhaseDelegations)
        {
            string sessionStreamFile = RunPaths.SessionStreamFile(resolvedRunDirectory, delegation.SessionFileKey);
            TaskDeliverCommand.HeadlessResult result = TaskDeliverCommand.ReadHeadlessResultFromStreamFile(sessionStreamFile);
            AppendUsage(session, run, result.Usage, recordedAt);
        }
    }

    private static void AppendUsage(
        IDocumentSession session, RunDetails run, TaskDeliverCommand.HeadlessUsage? usage, DateTimeOffset recordedAt)
    {
        if (usage is { } value)
        {
            session.Events.Append(run.Id, new TokensRecorded(
                run.Id, value.InputTokens, value.OutputTokens, value.CostUsd, recordedAt,
                value.CacheReadInputTokens, value.CacheCreationInputTokens, run.Model));
        }
    }
}
