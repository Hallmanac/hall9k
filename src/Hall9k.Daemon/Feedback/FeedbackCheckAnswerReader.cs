using System.Text.RegularExpressions;
using Hall9k.Daemon.Challenge;
using Hall9k.Domain.Features.Tasks.Feedback;

namespace Hall9k.Daemon.Feedback;

/// <summary>
/// Reads the closing line of the feedback check's answer, the way <see cref="Plan.RefinementReport"/> reads a refinement's:
/// from the end, forgiving about decoration and strict about meaning. The contract is one line,
/// <c>FEEDBACK CHECK: affects | unaffected</c>, with only blank lines or a closing code fence after it. An answer is readable
/// only when its last non-blank line is that marker line naming exactly one of the two words. Two marker lines in the closing
/// run, a marker followed by prose, or a word that is neither is no answer, because the daemon acts on this line (it takes the
/// task back to Draft) and a line a person could read two ways is not one to act on.
/// </summary>
public static partial class FeedbackCheckAnswerReader
{
    /// <summary>The marker the closing line starts with.</summary>
    public const string Marker = "FEEDBACK CHECK:";

    [GeneratedRegex(@"^(?:(?:[-*+]|\d+[.)])\s+)?[*_`]*FEEDBACK[ _-]*CHECK[*_`]*\s*:[*_`]*\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();

    /// <summary>The answer the closing line of <paramref name="text"/> gives, or <see cref="FeedbackCheckAnswer.Failed"/> when it gives none.</summary>
    public static FeedbackCheckAnswer Read(string? text)
    {
        if (text is null)
        {
            return FeedbackCheckAnswer.Failed;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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

        return markers.Count == 1 && FeedbackCheckAnswer.TryParseWord(ChallengeReport.StripVerdictDecoration(markers[0]), out FeedbackCheckAnswer answer)
            ? answer
            : FeedbackCheckAnswer.Failed;
    }
}
