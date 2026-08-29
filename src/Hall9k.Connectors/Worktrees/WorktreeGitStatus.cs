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
}
