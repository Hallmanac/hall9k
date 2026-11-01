using System.Text.RegularExpressions;
using Hall9k.Daemon.Challenge;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Plan;

/// <summary>
/// Reads the closing line of a refinement session's report (<c>h9k plan start</c>), the way
/// <see cref="ChallengeReport"/> reads a challenge's: from the end of the file, forgivingly about decoration and strictly
/// about meaning. The contract is one line, <c>REFINEMENT: revised | unchanged | question</c>, at the end of the file
/// with only blank lines or a closing code fence after it, and the prose above it free.
/// <para>
/// A report is readable only when its last non-blank line (a trailing code fence aside) is that marker line naming exactly
/// one outcome. Two marker lines in the closing run, a marker followed by prose, or a marker whose text is anything but
/// one of the three words is unreadable, because the daemon acts on this line (it applies a revision) and a line a
/// person could read two ways is not one to act on. A quoted example of the marker earlier in the prose is outside the
/// closing run and so counts for nothing. The same decoration the challenge contract forgives is forgiven here: a list
/// marker, emphasis, backticks, and a trailing period.
/// </para>
/// </summary>
public static partial class RefinementReport
{
    /// <summary>The marker the closing line starts with.</summary>
    public const string OutcomeMarker = "REFINEMENT:";

    [GeneratedRegex(@"^(?:(?:[-*+]|\d+[.)])\s+)?[*_`]*REFINEMENT[*_`]*\s*:[*_`]*\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();

    /// <summary>The outcome a report names, or the closed reason it names none. The file is read from its end.</summary>
    public static async Task<(RefinementOutcome? Outcome, RefinementFailureReason? Failure)> ReadAsync(
        string reportPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
        {
            return (null, RefinementFailureReason.NoReport);
        }

        string text = await ChallengeReport.ReadTailAsync(reportPath, cancellationToken);
        return TryRead(text) is { } outcome
            ? (outcome, null)
            : (null, RefinementFailureReason.UnreadableReport);
    }

    /// <summary>The one outcome the closing line of <paramref name="report"/> names, or null when it is not a readable one.</summary>
    public static RefinementOutcome? TryRead(string report)
    {
        string[] lines = report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> markers = [];
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (markers.Count == 0 && ChallengeReport.FenceLine().IsMatch(line))
            {
                continue;
            }

            Match match = MarkerLine().Match(line);
            if (!match.Success)
            {
                break;
            }

            markers.Add(match.Groups[1].Value);
        }

        return markers.Count == 1 && RefinementOutcome.TryParseWord(ChallengeReport.StripVerdictDecoration(markers[0]), out RefinementOutcome outcome)
            ? outcome
            : null;
    }
}
