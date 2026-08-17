using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Tolerant reader of the review loop's marker lines (Decisions Log #23). The reviewer
/// ends with "VERDICT: merge-ready | needs-fixes", the fix session with
/// "RESOLUTION: fixed | disputed". The last marker in the summary wins (agents sometimes
/// quote the instructions before answering); anything unparseable maps to the Unknown
/// sentinel — the engine decides what honesty requires, never this parser.
/// </summary>
public static class ReviewResultParser
{
    public static ReviewVerdict ParseVerdict(string? summary) =>
        LastMarkerValue(summary, "VERDICT:") switch
        {
            { } value when value.Contains("merge-ready", StringComparison.OrdinalIgnoreCase) => ReviewVerdict.MergeReady,
            { } value when value.Contains("needs-fixes", StringComparison.OrdinalIgnoreCase) => ReviewVerdict.NeedsFixes,
            _ => ReviewVerdict.Unknown,
        };

    public static ReviewFixOutcome ParseFixOutcome(string? summary) =>
        LastMarkerValue(summary, "RESOLUTION:") switch
        {
            { } value when value.Contains("disputed", StringComparison.OrdinalIgnoreCase) => ReviewFixOutcome.Disputed,
            { } value when value.Contains("fixed", StringComparison.OrdinalIgnoreCase) => ReviewFixOutcome.Fixed,
            _ => ReviewFixOutcome.Unknown,
        };

    private static string? LastMarkerValue(string? summary, string marker)
    {
        if (summary.IsBlank())
        {
            return null;
        }

        string? value = null;
        foreach (string rawLine in summary.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                value = line[marker.Length..].Trim();
            }
        }

        return value;
    }
}
