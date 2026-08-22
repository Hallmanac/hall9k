using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Review;

/// <summary>
/// Tolerant reader of the review loop's marker lines (Decisions Log #23). The reviewer
/// ends with "VERDICT: merge-ready | needs-fixes", the fix session with
/// "RESOLUTION: fixed | disputed". The last marker in the summary wins (agents sometimes
/// quote the instructions before answering); anything unparseable maps to the Unknown
/// sentinel — the engine decides what honesty requires, never this parser.
/// <para>
/// The adversarial pass additionally opens each finding with a "FINDING:" header carrying its
/// grade and scope tag (Decisions Log #63), which <see cref="ParseFindings"/> reads. Same
/// discipline: an absent or unrecognized tag becomes Unknown here, and the gate — not this
/// parser — decides what an ungraded finding costs.
/// </para>
/// </summary>
public static class ReviewResultParser
{
    /// <summary>The header line that opens one structured finding block.</summary>
    public const string FindingMarker = "FINDING:";

    private const string VerdictMarker = "VERDICT:";

    /// <summary>
    /// The structured findings in a review pass's output, in the order they were written. A
    /// block runs from its FINDING header to the next header or the verdict line, and its text
    /// is carried whole so the fix session and any routed draft read exactly what the reviewer
    /// wrote rather than a reconstruction of it.
    /// <para>
    /// Returns empty when the output carries no headers at all. That is not "no findings" — it
    /// is "no findings this parser can read", and the caller is the one that knows whether the
    /// verdict said there were any.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ReviewFinding> ParseFindings(string? summary)
    {
        if (summary.IsBlank())
        {
            return [];
        }

        List<ReviewFinding> findings = [];
        List<string>? block = null;
        foreach (string rawLine in summary.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();
            bool opensFinding = trimmed.StartsWith(FindingMarker, StringComparison.OrdinalIgnoreCase);
            bool endsFindings = trimmed.StartsWith(VerdictMarker, StringComparison.OrdinalIgnoreCase);
            if (opensFinding || endsFindings)
            {
                Close(findings, block);
                block = opensFinding ? [trimmed] : null;
                continue;
            }

            block?.Add(line);
        }

        Close(findings, block);
        return findings;
    }

    private static void Close(List<ReviewFinding> findings, List<string>? block)
    {
        if (block is null)
        {
            return;
        }

        Dictionary<string, string> header = HeaderTags(block[0][FindingMarker.Length..]);
        findings.Add(new ReviewFinding(
            ReviewSeverity.Parse(Tag(header, "severity")),
            ReviewFindingScope.Parse(Tag(header, "scope")),
            Tag(header, "at") ?? Tag(header, "file") ?? Tag(header, "location") ?? string.Empty,
            string.Join('\n', block).Trim()));
    }

    /// <summary>
    /// The header's `key=value` tags, separated by semicolons or commas — both are accepted
    /// because both are what agents actually write, and a `file.cs:123` value contains neither.
    /// A repeated key keeps the first, which is the one the reviewer wrote first.
    /// </summary>
    private static Dictionary<string, string> HeaderTags(string header)
    {
        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in header.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = part[..separator].Trim();
            string value = part[(separator + 1)..].Trim().Trim('`');
            if (key.IsNotBlank() && value.IsNotBlank())
            {
                tags.TryAdd(key, value);
            }
        }

        return tags;
    }

    private static string? Tag(Dictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out string? value) ? value : null;

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
