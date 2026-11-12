using System.Text.RegularExpressions;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Domain.Features.Plan;

/// <summary>
/// What reading a refinement report file for a split came to: it is not there, or it is there and names the increments, or it
/// is there and does not read as a split. Read the same way by the daemon when the session ends, by <c>h9k plan show</c>, and by
/// <c>h9k plan split accept</c>, so the three cannot disagree about the file.
/// </summary>
public sealed record RefinementSplitReading(bool Missing, IReadOnlyList<string>? Increments);

/// <summary>
/// Reads the split block a refinement report may end with: a <c>REFINEMENT: split</c> line followed directly by two or more
/// <c>TASK: &lt;objective&gt;</c> lines, one per proposed increment in order, with only blank lines or a closing code fence after
/// it and no blank line anywhere inside it. Every <c>TASK:</c> line is read by the rules a discovery report's are
/// (<see cref="DiscoveryReport"/>), so the same decoration is forgiven and a blank objective is refused.
/// <para>
/// The block is unreadable as a split when it has fewer than two increment lines, a blank objective, a <c>TASK:</c> line directly
/// above its <c>REFINEMENT:</c> line, or a second <c>REFINEMENT:</c> line, whether inside the block or above it in the closing run
/// across blank lines, the way a refinement report with two outcome lines is unreadable. The daemon cuts drafts from this block
/// once a person accepts it, and a block a person could read two ways is not one to act on. It lives in the domain, beside the
/// discovery reader, so the CLI reads the same block the daemon read.
/// </para>
/// </summary>
public static partial class RefinementSplitReport
{
    /// <summary>The marker a refinement report's closing outcome line starts with.</summary>
    public const string OutcomeMarker = "REFINEMENT:";

    /// <summary>The marker each proposed increment's line starts with: the one a discovery report's task lines carry.</summary>
    public const string IncrementMarker = DiscoveryReport.TaskMarker;

    [GeneratedRegex(@"^(?:(?:[-*+]|\d+[.)])\s+)?[*_`]*REFINEMENT[*_`]*\s*:[*_`]*\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutcomeLine();

    /// <summary>Reads a report file from its end. Missing and not a split are told apart, because a refusal says which.</summary>
    public static async Task<RefinementSplitReading> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new RefinementSplitReading(Missing: true, null);
        }

        try
        {
            return new RefinementSplitReading(Missing: false, TryRead(await DiscoveryReport.ReadTailAsync(path, cancellationToken)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RefinementSplitReading(Missing: false, null);
        }
    }

    /// <summary>The increments, in order, that the split block ending <paramref name="report"/> names, or null when it ends in no readable split.</summary>
    public static IReadOnlyList<string>? TryRead(string report)
    {
        string[] lines = report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int index = lines.Length - 1;

        // Blank lines and a code fence that close the file are formatting, skipped until the block's last line.
        while (index >= 0 && (lines[index].Trim().Length == 0 || DiscoveryReport.FenceLine().IsMatch(lines[index].Trim())))
        {
            index--;
        }

        List<string> increments = [];
        for (; index >= 0; index--)
        {
            string? objective = DiscoveryReport.TaskObjectiveOf(lines[index]);
            if (objective is null)
            {
                break;
            }

            increments.Add(objective);
        }

        if (index < 0 || OutcomeLine().Match(lines[index].Trim()) is not { Success: true } outcome
            || !DiscoveryReport.StripDecoration(outcome.Groups[1].Value).Equals(RefinementOutcome.Split.Word, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Above the outcome line: a TASK: line there would be an increment the block put on the wrong side of it, and a second
        // outcome line anywhere in the closing run, blank lines between or not, is a second answer.
        index--;
        if (index >= 0 && DiscoveryReport.TaskObjectiveOf(lines[index]) is not null)
        {
            return null;
        }

        while (index >= 0 && lines[index].Trim().Length == 0)
        {
            index--;
        }

        if (index >= 0 && OutcomeLine().IsMatch(lines[index].Trim()))
        {
            return null;
        }

        increments.Reverse();
        return increments.Count >= 2 && increments.All(increment => increment.Length > 0)
            ? increments
            : null;
    }
}
