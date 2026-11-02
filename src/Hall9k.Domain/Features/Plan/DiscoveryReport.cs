using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Plan;

/// <summary>The gate's answer a discovery report names.</summary>
public enum DiscoveryDecision
{
    /// <summary>Pursue the idea: the report proposes one or more tasks.</summary>
    Pursue,

    /// <summary>Do not pursue it: the report proposes none.</summary>
    Stop,
}

/// <summary>What a readable discovery report says: its answer and, for a pursue, the objectives it proposes in order.</summary>
public sealed record DiscoveryReadout(DiscoveryDecision Decision, IReadOnlyList<string> Objectives);

/// <summary>
/// What reading a report file came to: it is not there, it is there and not readable, or it is readable. Read the same way
/// by the daemon when a session ends, by <c>h9k plan show</c>, and by <c>h9k plan pursue</c>, so the three cannot disagree
/// about the file.
/// </summary>
public sealed record DiscoveryReading(bool Missing, DiscoveryReadout? Readout)
{
    public bool Readable => Readout is not null;
}

/// <summary>
/// Reads the closing block of a plan's discovery report, the way a refinement report's closing line is read: from the end of
/// the file, forgiving about decoration and strict about meaning. The contract is a block of marker lines at the end, with only
/// blank lines or a closing code fence after it and the prose above it free (the block itself holds no blank line, and a blank
/// line ends it from below): <c>DISCOVERY: pursue</c> and one or more
/// <c>TASK: &lt;objective&gt;</c> lines, or <c>DISCOVERY: stop</c> and no <c>TASK:</c> line.
/// <para>
/// A report is readable only when its closing block holds exactly one <c>DISCOVERY:</c> line naming exactly pursue or stop,
/// and agrees with it: a stop with a task, a pursue with none, both words, neither, or no block at all is unreadable, because
/// the platform cuts drafts from this block and a block a person could read two ways is not one to act on. A list marker,
/// emphasis, backticks, and a trailing period on a line are forgiven. A quoted example of the markers earlier in the prose is
/// outside the closing block and counts for nothing. Every objective must be one <c>h9k task add --objective</c> would accept: not blank.
/// </para>
/// </summary>
public static partial class DiscoveryReport
{
    /// <summary>The marker the answer line starts with.</summary>
    public const string DecisionMarker = "DISCOVERY:";

    /// <summary>The marker each proposed task's line starts with.</summary>
    public const string TaskMarker = "TASK:";

    /// <summary>How much of a report's end is read: the closing block is at the end, so a long report is never read whole.</summary>
    private const int MaxTailBytes = 256 * 1024;

    [GeneratedRegex(@"^(?:(?:[-*+]|\d+[.)])\s+)?([*_`]*)(DISCOVERY|TASK)[*_`]*\s*:[*_`]*\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();

    [GeneratedRegex(@"^`{3,}[\w+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLine();

    /// <summary>Reads a report file from its end. Missing and unreadable are told apart, because <c>h9k plan show</c> says which.</summary>
    public static async Task<DiscoveryReading> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new DiscoveryReading(Missing: true, null);
        }

        try
        {
            return new DiscoveryReading(Missing: false, TryRead(await ReadTailAsync(path, cancellationToken)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiscoveryReading(Missing: false, null);
        }
    }

    /// <summary>The answer and objectives the closing block of <paramref name="report"/> names, or null when it is not a readable one.</summary>
    public static DiscoveryReadout? TryRead(string report)
    {
        string[] lines = report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<(string Marker, string Text)> block = [];
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string line = lines[index].Trim();
            if (line.Length == 0)
            {
                // Blank lines after the block are skipped; one inside it ends it, so a marker-shaped line of prose above a blank
                // line is not pulled into the block below it.
                if (block.Count == 0)
                {
                    continue;
                }

                break;
            }

            // A code fence that wraps the closing block is formatting, not prose, so one that ends the file is skipped like a
            // blank line. Only a trailing one: a fence between the markers or before the first ends the run.
            if (block.Count == 0 && FenceLine().IsMatch(line))
            {
                continue;
            }

            Match match = MarkerLine().Match(line);
            if (!match.Success)
            {
                break;
            }

            block.Add((match.Groups[2].Value, CloseWrapper(match.Groups[1].Value, match.Groups[3].Value)));
        }

        block.Reverse();
        string[] decisions = [.. block
            .Where(entry => entry.Marker.Equals("DISCOVERY", StringComparison.OrdinalIgnoreCase))
            .Select(entry => StripDecoration(entry.Text).ToLowerInvariant())];
        string[] objectives = [.. block
            .Where(entry => entry.Marker.Equals("TASK", StringComparison.OrdinalIgnoreCase))
            .Select(entry => CleanObjective(entry.Text))];
        if (decisions.Length != 1 || objectives.Any(objective => objective.Length == 0))
        {
            return null;
        }

        return decisions[0] switch
        {
            "pursue" when objectives.Length > 0 => new DiscoveryReadout(DiscoveryDecision.Pursue, objectives),
            "stop" when objectives.Length == 0 => new DiscoveryReadout(DiscoveryDecision.Stop, []),
            _ => null,
        };
    }

    /// <summary>
    /// A line a person wrapped whole in emphasis or backticks (<c>`TASK: do it.`</c>) opened the wrapper before the marker and
    /// closes it at the end of the text, where it would otherwise read as part of the objective. Dropped only when the text holds
    /// an odd number of the wrapper, so a wrapper the text balances for itself (<c>**TASK:** **do it**</c>) is left alone.
    /// </summary>
    private static string CloseWrapper(string lead, string text)
    {
        string wrapper = lead.Trim();
        string trimmed = text.TrimEnd();
        if (wrapper.Length == 0 || !trimmed.EndsWith(wrapper, StringComparison.Ordinal))
        {
            return text;
        }

        int count = 0;
        for (int index = trimmed.IndexOf(wrapper, StringComparison.Ordinal); index >= 0; index = trimmed.IndexOf(wrapper, index + wrapper.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count % 2 == 1
            ? trimmed[..^wrapper.Length]
            : text;
    }

    /// <summary>Emphasis, backticks, and a trailing period are not text; what is left is the answer line's content.</summary>
    private static string StripDecoration(string value)
    {
        string current = value.Trim();
        while (true)
        {
            string next = current.Trim('*', '_', '`').TrimEnd('.').Trim();
            if (next == current)
            {
                return current;
            }

            current = next;
        }
    }

    /// <summary>
    /// An objective as the line states it: emphasis that wraps the whole objective and a trailing period are formatting, but
    /// a backtick that closes a code span inside the sentence is the author's and stays.
    /// </summary>
    private static string CleanObjective(string value)
    {
        string current = value.Trim();
        while (true)
        {
            string next = current;
            foreach (string wrapper in new[] { "**", "__", "*", "_", "`" })
            {
                if (next.Length > wrapper.Length * 2 && next.StartsWith(wrapper, StringComparison.Ordinal)
                    && next.EndsWith(wrapper, StringComparison.Ordinal)
                    && !next[wrapper.Length..^wrapper.Length].Contains(wrapper, StringComparison.Ordinal))
                {
                    next = next[wrapper.Length..^wrapper.Length].Trim();
                    break;
                }
            }

            next = next.TrimEnd('.').Trim();
            if (next == current)
            {
                return current;
            }

            current = next;
        }
    }

    private static async Task<string> ReadTailAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxTailBytes)
        {
            stream.Seek(-MaxTailBytes, SeekOrigin.End);
        }

        byte[] buffer = new byte[Math.Min(stream.Length, MaxTailBytes)];
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

        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
