using System.Text.RegularExpressions;

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

    private static readonly Regex DiffGitLine = new(@"^diff --git a/(?<a>.*) b/(?<b>.*)$", RegexOptions.Compiled);

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
            Match match = DiffGitLine.Match(line);
            if (match.Success)
            {
                files.Add(match.Groups["b"].Value);
            }
        }

        return files;
    }

    /// <summary>
    /// Every hunk belonging to a file in <paramref name="matchedFiles"/>, joined in the diff's own
    /// order, truncated to <paramref name="maxCharacters"/> with a trailing note when it was cut.
    /// Empty when there is nothing to extract — an empty match list, or a diff with no matching
    /// file blocks (a rename or an unusual path form <see cref="DiffGitLine"/> cannot parse) —
    /// never a guess standing in for a block this could not confidently identify.
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
            Match match = DiffGitLine.Match(line);
            if (match.Success)
            {
                Flush();
                currentWanted = wanted.Contains(match.Groups["b"].Value) || wanted.Contains(match.Groups["a"].Value);
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
}
