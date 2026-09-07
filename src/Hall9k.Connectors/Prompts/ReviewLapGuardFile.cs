namespace Hall9k.Connectors.Prompts;

/// <summary>
/// What installing the lap's in-worktree push guard actually managed. An unpersisted in-process
/// outcome, so an enum (AGENTS.md's type discipline) — nothing here is ever written to a stream.
/// </summary>
public enum ReviewLapGuardOutcome
{
    /// <summary>The guard was written; any session started in this worktree is covered by it.</summary>
    Written,

    /// <summary>
    /// The guard this lap itself wrote on an earlier entry is already there, byte for byte, so
    /// the worktree is covered — nothing needed writing. Distinct from
    /// <see cref="AlreadyPresent"/> because they are opposite facts about whether the checkout is
    /// protected, and re-entry (closing the terminal and re-running <c>h9k pr review</c>) is an
    /// ordinary way to reach this one: reporting it as a foreign file told a reviewer the guard
    /// was missing on the ordinary path (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    AlreadyGuarded,

    /// <summary>The worktree already carried a local settings file that is not this guard, so nothing was overwritten — the run directory's own settings file is the vehicle instead.</summary>
    AlreadyPresent,

    /// <summary>There is no worktree to install a guard in (<c>--no-worktree</c>).</summary>
    NoWorktree,

    /// <summary>The write failed. The caller says so out loud rather than letting a guard be believed in that does not exist.</summary>
    Failed,
}

/// <summary>
/// The review lap's push guard as a file inside the read-only checkout itself
/// (<c>&lt;worktree&gt;/.claude/settings.local.json</c>, Decisions Log #149), carrying exactly
/// <see cref="ClaudeSettingsFile.ReviewLapDeniedTools"/>.
/// <para>
/// It exists because the lap's default connector hands the reviewer a prompt to paste into a
/// Claude Code session they start themselves (the same prompt-handoff shape <c>h9k task work</c>
/// settled on, PLAN.md §16 #126) — and a session started that way passes no <c>--settings</c>
/// flag unless the reviewer remembers to. Claude Code reads project settings out of the directory
/// it runs in with no flag at all, so a guard living in the checkout covers the session however
/// it was launched. The run directory's settings file still carries the same denials for the
/// <c>--no-worktree</c> case and for a session launched from elsewhere.
/// </para>
/// <para>
/// It never overwrites an existing file. The checkout is a fetch of somebody else's pull request
/// head, and a repository that tracks its own <c>.claude/settings.local.json</c> would have that
/// file's contents replaced here — silently editing the author's tree to install a guard, which
/// is a worse trade than saying the guard could not be installed and naming the other vehicle.
/// It does, however, recognize its OWN file: re-entering a lap finds the guard the first entry
/// wrote, and that is <see cref="ReviewLapGuardOutcome.AlreadyGuarded"/>, not somebody else's
/// settings (independent pre-PR review, cycle 1, adversarial lens — the existence check alone
/// warned every re-entering reviewer that the checkout was unprotected while the guard sat
/// there, active).
/// </para>
/// </summary>
public static class ReviewLapGuardFile
{
    /// <summary>Where the guard lives inside a checkout, relative to its root — the name to SAY, in Claude Code's own posix-slash spelling of it.</summary>
    public const string RelativePath = ".claude/settings.local.json";

    /// <summary>
    /// The guard's absolute path inside <paramref name="worktreePath"/>, composed the one way so
    /// the path this writes and the path a caller reports can never disagree. Deliberately not
    /// <c>Path.Combine(worktreePath, RelativePath)</c>: that concatenates the posix-slash spelling
    /// verbatim and produces a mixed-separator path on Windows, which is a real path there but an
    /// ugly one to print at somebody (self-review, round one).
    /// </summary>
    public static string PathIn(string worktreePath) =>
        Path.Combine(worktreePath, ".claude", "settings.local.json");

    /// <summary>The guard's own content: the deny list and nothing else, so it adds a restriction to whatever else a session resolves rather than replacing a configuration.</summary>
    public static string Content()
    {
        string deny = string.Join(", ", ClaudeSettingsFile.ReviewLapDeniedTools.Select(tool => $"\"{tool}\""));
        return $$$"""{"permissions": {"deny": [{{{deny}}}]}}""";
    }

    public static async Task<ReviewLapGuardOutcome> InstallAsync(
        string worktreePath, CancellationToken cancellationToken)
    {
        if (worktreePath.IsBlank())
        {
            return ReviewLapGuardOutcome.NoWorktree;
        }

        try
        {
            string path = PathIn(worktreePath);
            if (File.Exists(path))
            {
                // Whose file it is decides which of the two answers this is, so the content is
                // compared rather than the existence alone: the ordinary re-entry into a lap
                // finds THIS guard from the first entry, and reporting that as somebody else's
                // settings file warns a reviewer the checkout is unprotected when it is in fact
                // guarded. Compared against the current content exactly — a guard written by an
                // older version, with a shorter deny list, is honestly not the guard this run
                // would write, and the AlreadyPresent branch's advice (start the session with
                // --settings, which carries today's list) is the right advice for it.
                return await IsOurOwnGuardAsync(path, cancellationToken)
                    ? ReviewLapGuardOutcome.AlreadyGuarded
                    : ReviewLapGuardOutcome.AlreadyPresent;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, Content(), cancellationToken);
            return ReviewLapGuardOutcome.Written;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ReviewLapGuardOutcome.Failed;
        }
    }

    /// <summary>
    /// Whether the file already at <paramref name="path"/> is this guard. A file that cannot be
    /// read reads as not ours — the honest answer, since the whole question is whether the
    /// checkout is known to be covered, and an unreadable file cannot say it is.
    /// </summary>
    private static async Task<bool> IsOurOwnGuardAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string existing = await File.ReadAllTextAsync(path, cancellationToken);
            return existing.Trim() == Content();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
