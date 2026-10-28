using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Tasks.Queries;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The throughput block's own lines, shared by <c>h9k status</c> (the node's current period)
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
/// <para>
/// A line of phase shares says who ran the period's phases (idea 6e6d0200, Card 6): by hand,
/// delegated to an agent, automatic, or skipped by an override. When more than one owner held claims
/// in the period the block adds the viewer's own line, keyed by <paramref name="viewerOwnerKey"/>
/// (<see cref="PhaseOwnerKey"/>), and never prints another owner's figures: the summary carries every
/// owner's slice so the split is one fold, and this pane reads only the viewer's.
/// </para>
/// </summary>
internal static class ThroughputPane
{
    /// <summary>The node-wide block: one period, no comparison (h9k status).</summary>
    public static IReadOnlyList<string> ComposeLines(
        ThroughputSummary summary, string periodWord, string? viewerOwnerKey = null)
    {
        if (!summary.HasEnoughData)
        {
            return [TooFewLine(summary, periodWord)];
        }

        List<string> lines =
        [
            $"throughput this {periodWord}: {HeadlineClause(summary)}",
            $"  {DetailClause(summary)}",
        ];
        AddPhaseLines(lines, summary, viewerOwnerKey);
        return lines;
    }

    /// <summary>The project-scoped block: this period beside the previous one (h9k project show).</summary>
    public static IReadOnlyList<string> ComposeLines(
        ThroughputSummary current, ThroughputSummary previous, string periodWord, string? viewerOwnerKey = null)
    {
        if (!current.HasEnoughData)
        {
            return [TooFewLine(current, periodWord), PreviousLine(previous, periodWord)];
        }

        List<string> lines =
        [
            $"throughput this {periodWord}: {HeadlineClause(current)}",
            $"  {DetailClause(current)}",
        ];
        AddPhaseLines(lines, current, viewerOwnerKey);
        lines.Add(PreviousLine(previous, periodWord));
        return lines;
    }

    private static string PreviousLine(ThroughputSummary previous, string periodWord) => previous.HasEnoughData
        ? $"  previous {periodWord}: {HeadlineClause(previous)} · {DetailClause(previous)}"
            + (previous.PhaseShares is { } shares ? $" · phases: {SharesText(shares)}" : string.Empty)
        : $"  previous {periodWord}: {TooFewClause(previous)}";

    /// <summary>
    /// The phase shares line, then the viewer's own. Nothing at all when the period folded no phase
    /// to share (<see cref="ThroughputSummary.PhaseShares"/> null), so a summary built without them
    /// prints what it always has.
    /// </summary>
    private static void AddPhaseLines(List<string> lines, ThroughputSummary summary, string? viewerOwnerKey)
    {
        if (summary.PhaseShares is not { } shares)
        {
            return;
        }

        lines.Add($"  phases: {SharesText(shares)}");
        AddViewerLine(lines, summary, viewerOwnerKey);
    }

    /// <summary>
    /// The viewer's own slice, only when more than one owner held claims in the period: with a single
    /// owner the line above is already that owner's. Another owner's slice is never printed, whatever
    /// the summary carries.
    /// </summary>
    private static void AddViewerLine(List<string> lines, ThroughputSummary summary, string? viewerOwnerKey)
    {
        if (viewerOwnerKey is null || summary.OwnerShares is not { Count: > 1 } slices)
        {
            return;
        }

        OwnerPhaseShares? own = slices.FirstOrDefault(slice => slice.OwnerKey == viewerOwnerKey);
        lines.Add(own switch
        {
            null => "  your phases: none of this period's merged tasks ran under a claim of yours",
            { Shares: { } shares } => $"  your phases ({own.MergedCount} merged): {SharesText(shares)}",
            _ => $"  your phases: {own.MergedCount} merged — too few to summarize",
        });
    }

    private static string SharesText(PhaseClassShares shares) =>
        $"by hand {FormatShare(shares.Human)} · delegated {FormatShare(shares.Delegated)} · "
        + $"automatic {FormatShare(shares.Automatic)} · skipped {FormatShare(shares.Skipped)}";

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
