using System.Text;

namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// Pulls the hunks for one set of files out of a full unified diff (idea 6be68ee2, finding 1,
/// phase one) — tools before tokens: the pre-flight prompt hands the session only the matched
/// surfaces' own hunks from <c>gh pr diff</c>, fenced as data and capped, never the whole diff.
/// Pure text processing, no I/O: the diff text and the matched-file list are both already read by
/// the time this runs.
/// </summary>
public static class PrReviewPreflightDiffExtractor
{
    /// <summary>
    /// The prompt-size ceiling on the extracted hunks (tools before tokens, the acceptance
    /// criterion's own "capped"): generous enough for the fixed surface list's own files, which
    /// are ordinarily manifests and workflow files rather than large source trees, and small next
    /// to a pre-flight's own short, cheap-by-design job.
    /// </summary>
    public const int DefaultMaxCharacters = 20_000;

    private const string HeaderPrefix = "diff --git ";

    /// <summary>
    /// Every path a unified diff touches, read back out of the diff's own <c>diff --git a/... b/...</c>
    /// headers rather than a second, separately-fetched name list (independent pre-PR review, cycle
    /// 5, adversarial lens): a pre-flight bound to one commit's diff (<see
    /// cref="Hall9k.Connectors.WorkItems.GitHubPullRequestProvider.FetchDiffForCommitAsync"/>) must
    /// never re-derive its changed-file list from a second call that could observe a different head.
    /// The new (<c>b/</c>) path is reported for every entry, including a rename, since that is where
    /// the changed content now lives.
    /// </summary>
    public static IReadOnlyList<string> ExtractChangedFiles(string diff)
    {
        if (diff.IsBlank())
        {
            return [];
        }

        List<string> files = [];
        foreach (string line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (TryParseDiffGitLine(line, out _, out string bPath))
            {
                files.Add(bPath);
            }
        }

        return files;
    }

    /// <summary>
    /// Every hunk belonging to a file in <paramref name="matchedFiles"/>, joined in the diff's own
    /// order, truncated to <paramref name="maxCharacters"/> with a trailing note when it was cut.
    /// Empty when there is nothing to extract — an empty match list, or a diff with no matching
    /// file blocks — never a guess standing in for a block this could not confidently identify.
    /// </summary>
    public static string ExtractMatchedHunks(
        string diff, IReadOnlyList<string> matchedFiles, int maxCharacters = DefaultMaxCharacters)
    {
        if (diff.IsBlank() || matchedFiles.Count == 0)
        {
            return string.Empty;
        }

        HashSet<string> wanted = new(matchedFiles, StringComparer.Ordinal);
        string[] lines = diff.Replace("\r\n", "\n").Split('\n');

        List<string> blocks = [];
        List<string> currentBlock = [];
        bool currentWanted = false;

        void Flush()
        {
            if (currentWanted && currentBlock.Count > 0)
            {
                blocks.Add(string.Join('\n', currentBlock));
            }

            currentBlock = [];
        }

        foreach (string line in lines)
        {
            if (TryParseDiffGitLine(line, out string aPath, out string bPath))
            {
                Flush();
                currentWanted = wanted.Contains(bPath) || wanted.Contains(aPath);
            }

            currentBlock.Add(line);
        }

        Flush();

        string joined = string.Join("\n\n", blocks);
        return joined.Length <= maxCharacters
            ? joined
            : joined[..maxCharacters]
                + "\n\n[... capped at "
                + maxCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " characters; the matched diff ran longer than this ...]";
    }

    /// <summary>
    /// Parses one <c>diff --git a/... b/...</c> header, handling both the plain form and the
    /// quoted form git falls back to whenever a path holds a character that needs escaping — a
    /// double quote, a backslash, a tab, a newline, or (under the default <c>core.quotePath</c>) a
    /// non-ASCII byte. A regex anchored on the literal <c>" b/"</c> separator dropped every quoted
    /// path outright (it never matches the leading quote) and misread a plain path that itself
    /// contains that separator, by always splitting at the first — greedy — match no matter which
    /// half actually held it (independent pre-PR review, cycle 7, adversarial lens). The plain form
    /// resolves that ambiguity by preferring the split where the two path halves are identical —
    /// true for every header except a rename, which is the overwhelming majority of headers this
    /// ever needs to read.
    /// </summary>
    private static bool TryParseDiffGitLine(string line, out string aPath, out string bPath)
    {
        aPath = string.Empty;
        bPath = string.Empty;

        if (!line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string rest = line[HeaderPrefix.Length..];
        if (rest.StartsWith('"'))
        {
            return TryParseQuotedPair(rest, out aPath, out bPath);
        }

        if (!rest.StartsWith("a/", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = rest[2..].Split(" b/", StringSplitOptions.None);
        if (parts.Length < 2)
        {
            return false;
        }

        for (int i = 1; i < parts.Length; i++)
        {
            string candidateA = string.Join(" b/", parts[..i]);
            string candidateB = string.Join(" b/", parts[i..]);
            if (candidateA == candidateB)
            {
                aPath = candidateA;
                bPath = candidateB;
                return true;
            }
        }

        // No half matches exactly — a rename whose old or new name itself contains the literal
        // " b/" separator. The narrowest possible old-name reading is kept as the best available
        // guess, rather than the widest (the prior regex's own greedy bug): an unusual enough
        // shape that no caller asserts a specific outcome for it, only that parsing never throws.
        aPath = parts[0];
        bPath = string.Join(" b/", parts[1..]);
        return true;
    }

    private static bool TryParseQuotedPair(string rest, out string aPath, out string bPath)
    {
        aPath = string.Empty;
        bPath = string.Empty;

        if (!TryParseQuotedToken(rest, 0, out string first, out int afterFirst))
        {
            return false;
        }

        if (afterFirst >= rest.Length || rest[afterFirst] != ' ')
        {
            return false;
        }

        int secondStart = afterFirst + 1;
        string second;
        if (secondStart < rest.Length && rest[secondStart] == '"')
        {
            if (!TryParseQuotedToken(rest, secondStart, out second, out int afterSecond)
                || afterSecond != rest.Length)
            {
                return false;
            }
        }
        else
        {
            second = rest[secondStart..];
        }

        if (!first.StartsWith("a/", StringComparison.Ordinal) || !second.StartsWith("b/", StringComparison.Ordinal))
        {
            return false;
        }

        aPath = first[2..];
        bPath = second[2..];
        return true;
    }

    /// <summary>
    /// One C-style quoted path, exactly the form git's own <c>quote_c_style</c> writes: <c>\"</c>,
    /// <c>\\</c>, <c>\t</c> and <c>\n</c> as two-character escapes, every other byte outside the
    /// printable ASCII range as a three-digit octal escape (<c>\NNN</c>) — consecutive octal
    /// escapes are the individual bytes of one multi-byte UTF-8 character, so they are accumulated
    /// and decoded together rather than one at a time.
    /// </summary>
    private static bool TryParseQuotedToken(string s, int start, out string value, out int end)
    {
        StringBuilder result = new();
        List<byte> pendingBytes = [];

        void FlushBytes()
        {
            if (pendingBytes.Count > 0)
            {
                result.Append(Encoding.UTF8.GetString([.. pendingBytes]));
                pendingBytes.Clear();
            }
        }

        int i = start + 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                FlushBytes();
                value = result.ToString();
                end = i + 1;
                return true;
            }

            if (c == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                if (next is >= '0' and <= '7' && i + 3 < s.Length
                    && s[i + 2] is >= '0' and <= '7' && s[i + 3] is >= '0' and <= '7')
                {
                    int octal = ((next - '0') * 64) + ((s[i + 2] - '0') * 8) + (s[i + 3] - '0');
                    pendingBytes.Add((byte)octal);
                    i += 4;
                    continue;
                }

                FlushBytes();
                switch (next)
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case 't': result.Append('\t'); break;
                    case 'n': result.Append('\n'); break;
                    default: result.Append(next); break;
                }

                i += 2;
                continue;
            }

            FlushBytes();
            result.Append(c);
            i++;
        }

        value = string.Empty;
        end = s.Length;
        return false;
    }
}
