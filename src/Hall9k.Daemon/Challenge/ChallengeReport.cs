using System.Text;
using System.Text.RegularExpressions;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Challenge;

/// <summary>What a readable challenge report's closing block said: the verdict and how many changes of each class it listed.</summary>
public sealed record ChallengeReading(ChallengeVerdict Verdict, int MustCount, int ShouldCount, int ParkCount);

/// <summary>
/// Reads the closing block of a draft challenge's report file (<c>h9k task challenge</c>). The contract is one
/// verdict line and one line per change, at the end of the file, with the prose above free:
/// <code>
/// VERDICT: publish as written | publish as revised | do not publish
/// CHANGE: must|should|park - &lt;what&gt; - &lt;evidence&gt;
/// </code>
/// <para>
/// A report is readable only when the block closes the file, opens with its verdict line, names exactly one verdict, every change line is marked must, should, or
/// park and has non-blank text for both the change and its evidence, and the counts agree with the verdict:
/// <em>publish as written</em> holds no must or should, <em>publish as revised</em> holds at least one of the two, and
/// <em>do not publish</em> holds at least one must. Nothing is inferred from a report that fails any of it, because a
/// verdict the gate opens on has to be one a person could have read the same way.
/// </para>
/// <para>
/// Lines are normalised before they are matched, because the verdicts this contract was drawn from were written by
/// models that bold the verdict line and end sentences with a period: a leading list marker, emphasis
/// and backticks around the marker, and a trailing period on the verdict line do not make a line unreadable.
/// Other text on the verdict line does, so a condition or a question only a person can settle belongs on a must
/// change line and never after the verdict.
/// </para>
/// </summary>
public static partial class ChallengeReport
{
    /// <summary>The marker the verdict line starts with.</summary>
    public const string VerdictMarker = "VERDICT:";

    /// <summary>The marker each change line starts with.</summary>
    public const string ChangeMarker = "CHANGE:";

    /// <summary>
    /// The most of a report this reads, taken from its end because the block being read closes the file: a session
    /// can write anything, and reading the whole of it is not the job.
    /// </summary>
    internal const int MaxReportChars = 200_000;

    [GeneratedRegex(@"^(?:(?:[-*+]|\d+[.)])\s+)?[*_`]*(VERDICT|CHANGE)[*_`]*\s*:[*_`]*\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();

    /// <summary>The reading of a report file, or the closed reason it has none. The file is read from its end.</summary>
    public static async Task<(ChallengeReading? Reading, ChallengeFailureReason? Failure)> ReadAsync(
        string reportPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
        {
            return (null, ChallengeFailureReason.NoReport);
        }

        string text = await ReadTailAsync(reportPath, cancellationToken);
        return TryRead(text) is { } reading
            ? (reading, null)
            : (null, ChallengeFailureReason.UnreadableReport);
    }

    /// <summary>The closing block of <paramref name="report"/>, or null when it is not a readable one.</summary>
    public static ChallengeReading? TryRead(string report)
    {
        List<(string Marker, string Text)> block = ClosingBlock(report);
        List<string> verdictLines = [.. block.Where(line => IsMarker(line.Marker, "VERDICT")).Select(line => line.Text)];
        if (verdictLines.Count != 1)
        {
            return null;
        }

        if (!ChallengeVerdict.TryParse(StripVerdictDecoration(verdictLines[0]), out ChallengeVerdict? verdict)
            || !IsExactVerdictWords(StripVerdictDecoration(verdictLines[0])))
        {
            return null;
        }

        int must = 0;
        int should = 0;
        int park = 0;
        foreach ((string _, string text) in block.Where(line => IsMarker(line.Marker, "CHANGE")))
        {
            switch (ParseChange(text))
            {
                case "must":
                    must++;
                    break;
                case "should":
                    should++;
                    break;
                case "park":
                    park++;
                    break;
                default:
                    return null;
            }
        }

        bool consistent = verdict == ChallengeVerdict.PublishAsWritten
            ? must == 0 && should == 0
            : verdict == ChallengeVerdict.PublishAsRevised
                ? must + should > 0
                : must > 0;
        return consistent
            ? new ChallengeReading(verdict, must, should, park)
            : null;
    }

    /// <summary>
    /// The run of verdict and change lines that closes the file, or an empty list when there is none. The run has to be
    /// the end of the report (only blank lines may follow it) and has to open with its verdict line, because the
    /// prompt asks for the verdict first and for nothing after the block. Each of the three is a way a report could
    /// otherwise be read more kindly than it was written: prose after an indented example of the format would let
    /// the example stand as the verdict, a quoted line after the real block would replace it, and a verdict written
    /// last would let a wrapped change line fall outside the run and drop a must. Inside the run, a line that is neither
    /// a marker line nor blank ends it, so a change wrapped over two lines leaves its tail outside the block and the
    /// contract's one-line-per-change rule is what refuses it. A quoted example of the format earlier in the prose is
    /// outside the run for the same reason.
    /// </summary>
    private static List<(string Marker, string Text)> ClosingBlock(string report)
    {
        string[] lines = report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<(string Marker, string Text)> block = [];
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match match = MarkerLine().Match(line);
            if (!match.Success)
            {
                break;
            }

            block.Add((match.Groups[1].Value, match.Groups[2].Value));
        }

        block.Reverse();
        return block.Count > 0 && IsMarker(block[0].Marker, "VERDICT")
            ? block
            : [];
    }

    private static bool IsMarker(string marker, string expected) =>
        string.Equals(marker, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Emphasis, backticks, and a trailing period are not text; what is left is the verdict line's content.</summary>
    private static string StripVerdictDecoration(string value)
    {
        string current = value.Trim();
        while (true)
        {
            string next = current.Trim().Trim('*', '_', '`').TrimEnd('.').Trim();
            if (next == current)
            {
                return current;
            }

            current = next;
        }
    }

    /// <summary>
    /// Whether the text is the words of exactly one verdict, with spaces, hyphens, or underscores between them. The
    /// lenient letter-folding <see cref="ChallengeVerdict.TryParse"/> does for stored values would accept
    /// "publishaswritten" and "Publish, as written", which no person typed on purpose.
    /// </summary>
    private static bool IsExactVerdictWords(string text)
    {
        string words = Regex.Replace(text, @"[\s_-]+", " ").Trim().ToLowerInvariant();
        return words is "publish as written" or "publish as revised" or "do not publish";
    }

    /// <summary>The class of a change line (must, should, or park) when the line is well formed, else null.</summary>
    private static string? ParseChange(string rest)
    {
        string[] parts = rest.Split(" - ", 3, StringSplitOptions.None);
        if (parts.Length != 3)
        {
            return null;
        }

        string classification = StripVerdictDecoration(parts[0]).ToLowerInvariant();
        string what = parts[1].Trim().Trim('*', '_', '`').Trim();
        string evidence = parts[2].Trim().Trim('*', '_', '`').Trim();
        return classification is "must" or "should" or "park" && what.Length > 0 && evidence.Length > 0
            ? classification
            : null;
    }

    private static async Task<string> ReadTailAsync(string path, CancellationToken cancellationToken)
    {
        // Bytes, not characters, so a multi-byte report cannot make the cap larger than it says.
        const int maxBytes = MaxReportChars * 4;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool truncated = stream.Length > maxBytes;
        if (truncated)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
        }

        byte[] buffer = new byte[Math.Min(stream.Length, maxBytes)];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        string text = Encoding.UTF8.GetString(buffer, 0, read);
        if (!truncated)
        {
            return text;
        }

        // The cut can land inside a character or a line; the first line is dropped rather than read half.
        int firstBreak = text.IndexOf('\n');
        return firstBreak < 0
            ? string.Empty
            : text[(firstBreak + 1)..];
    }
}
