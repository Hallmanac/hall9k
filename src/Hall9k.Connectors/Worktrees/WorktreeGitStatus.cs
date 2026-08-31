namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// The `git status --porcelain -z --untracked-files=all` parser shared by every caller that
/// needs an honest "what does this worktree hold uncommitted" — <c>VerificationRunner</c>'s own
/// pre-gate check and the interactive claim's on-demand checks (<c>h9k task verify</c>,
/// <c>handback</c>, <c>release</c>). Carries real subtlety: the <c>-z</c> NUL framing, the
/// <c>entry[3..]</c> status-column offset, and a rename/copy entry's second NUL-terminated field
/// (the old path, which no longer exists and must be consumed without being added to either
/// list). Two independently-maintained copies of exactly this parsing used to exist on this
/// branch's own refactor — whose whole premise was moving CLI/daemon-shared machinery into this
/// project — so a future correction to the rename handling landing on only one of them would
/// never show up as a build or test failure, only as a silently wrong classification the next
/// time a renamed path needed it (adversarial review, cycle 4).
/// </summary>
public static class WorktreeGitStatus
{
    public static (IReadOnlyList<string> Modified, IReadOnlyList<string> Untracked) ParsePorcelain(string output)
    {
        List<string> modified = [];
        List<string> untracked = [];
        string[] entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            char indexStatus = entry[0];
            char worktreeStatus = entry[1];
            string path = entry[3..];

            if (indexStatus is 'R' or 'C' || worktreeStatus is 'R' or 'C')
            {
                // The old path is the next NUL-terminated field; it no longer exists, so it is
                // consumed here and never added to either list.
                i++;
            }

            if (indexStatus == '?' && worktreeStatus == '?')
            {
                untracked.Add(path);
            }
            else
            {
                modified.Add(path);
            }
        }

        return (modified, untracked);
    }

    /// <summary>
    /// Whether an untracked path belongs to the project's own source or test tree rather than
    /// somewhere a gate's own build or test output tends to land. `git status`'s porcelain output
    /// always uses a forward slash as the path separator, on every OS, so a literal prefix check
    /// is safe without any platform-specific path handling. Shared by
    /// <c>VerificationRunner</c>'s pre-gate check and <c>h9k task deliver</c>'s own untracked-file
    /// warning, so the two say the same thing about the same path (independent pre-PR review,
    /// adversarial finding: the two used to disagree, one calling a path "not blocking" that the
    /// other would go on to fail the run over).
    /// </summary>
    public static bool IsUnderSourceOrTestTree(string path) =>
        path.StartsWith("src/", StringComparison.Ordinal) || path.StartsWith("tests/", StringComparison.Ordinal);

    /// <summary>
    /// A well-known .NET build or test output directory appearing anywhere in the path, matched
    /// by name the same way `bin/` and `obj/` are excluded whether or not a project's own
    /// `.gitignore` names them — `TestResults/` is VSTest's own default results directory
    /// (`dotnet test --logger trx`, `--collect:"XPlat Code Coverage"`), and it commonly lands
    /// inside a test project's own directory under tests/, so a run that regenerates it every
    /// gate pass must never be treated as agent work left behind (independent pre-PR review
    /// cycle 1).
    /// </summary>
    public static bool IsKnownBuildOrTestOutput(string path) =>
        path.Split('/').Any(segment => segment is "bin" or "obj" or "TestResults");

    /// <summary>
    /// Splits a list of untracked paths into strandable work (under src/ or tests/, and not a
    /// known build/test byproduct) and everything else, using <see cref="IsUnderSourceOrTestTree"/>
    /// and <see cref="IsKnownBuildOrTestOutput"/> together the one way every caller needs them.
    /// Previously this pairing was written out longhand at each call site
    /// (<c>VerificationRunner</c>, <c>h9k task deliver</c>, <c>h9k task verify</c>), so a future
    /// change to the rule risked landing on some of them and not others without any build or test
    /// failure to catch the drift (independent pre-PR review, cycle 3).
    /// </summary>
    public static (IReadOnlyList<string> Strandable, IReadOnlyList<string> Byproduct) SplitUntracked(
        IReadOnlyList<string> untracked)
    {
        IReadOnlyList<string> strandable =
            [.. untracked.Where(path => IsUnderSourceOrTestTree(path) && !IsKnownBuildOrTestOutput(path))];
        IReadOnlyList<string> byproduct = [.. untracked.Except(strandable)];
        return (strandable, byproduct);
    }
}
