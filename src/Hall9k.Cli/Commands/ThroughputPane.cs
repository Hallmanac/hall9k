using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks.Queries;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The throughput block's own two lines, shared by <c>h9k status</c> (the node's current period)
/// and <c>h9k project show</c> (a project's current period beside its previous one) — task: h9k
/// status reports throughput beside spend, so speed and efficiency read in the same glance as
/// cost. Composed rather than printed directly, the same split <c>TaskShowCommand.ComposePassageLines</c>
/// already draws, so what a reader sees is assertable without a terminal.
/// <para>
/// Every figure comes off <see cref="ThroughputSummary"/>, which folds the identical
/// <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskPassage"/> a merged task's own <c>h9k task
/// show</c> passage section renders — a per-task number and this block's own number can never
/// disagree because neither one re-derives the fold the other already owns.
/// </para>
/// </summary>
internal static class ThroughputPane
{
    /// <summary>The node-wide block: one period, no comparison (h9k status).</summary>
    public static IReadOnlyList<string> ComposeLines(ThroughputSummary summary, string periodWord)
    {
        if (!summary.HasEnoughData)
        {
            return [TooFewLine(summary, periodWord)];
        }

        return
        [
            $"throughput this {periodWord}: {HeadlineClause(summary)}",
            $"  {DetailClause(summary)}",
        ];
    }

    /// <summary>The project-scoped block: this period beside the previous one (h9k project show).</summary>
    public static IReadOnlyList<string> ComposeLines(
        ThroughputSummary current, ThroughputSummary previous, string periodWord)
    {
        if (!current.HasEnoughData)
        {
            return [TooFewLine(current, periodWord), PreviousLine(previous, periodWord)];
        }

        return
        [
            $"throughput this {periodWord}: {HeadlineClause(current)}",
            $"  {DetailClause(current)}",
            PreviousLine(previous, periodWord),
        ];
    }

    private static string PreviousLine(ThroughputSummary previous, string periodWord) => previous.HasEnoughData
        ? $"  previous {periodWord}: {HeadlineClause(previous)} · {DetailClause(previous)}"
        : $"  previous {periodWord}: {TooFewClause(previous)}";

    private static string TooFewLine(ThroughputSummary summary, string periodWord) =>
        $"throughput this {periodWord}: {TooFewClause(summary)}";

    private static string TooFewClause(ThroughputSummary summary) => summary.MergedCount == 0
        ? "nothing merged yet"
        : $"{summary.MergedCount} merged — too few to summarize";

    private static string HeadlineClause(ThroughputSummary summary) =>
        $"{summary.MergedCount} merged · claim to merge median {FormatHours(summary.MedianClaimToMerge)}, "
        + $"p90 {FormatHours(summary.P90ClaimToMerge)}";

    private static string DetailClause(ThroughputSummary summary) =>
        $"first-pass {FormatShare(summary.FirstPassMergeShare)} · "
        + $"laps/merged task {summary.LapsPerMergedTask:0.0} · "
        + $"queued {FormatShare(summary.QueuedShare)} of task time · "
        + $"waiting on a human {FormatShare(summary.HumanWaitShare)} of task time";

    private static string FormatHours(TimeSpan? value) => value is { } elapsed ? DurationFormat.Hours(elapsed) : "unknown";

    private static string FormatShare(double? share) => share is { } value ? $"{value * 100:0}%" : "unknown";
}
