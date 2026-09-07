namespace Hall9k.Connectors.Prompts;

/// <summary>
/// What installing the lap's in-worktree push guard actually managed. An unpersisted in-process
/// outcome, so an enum (AGENTS.md's type discipline) — nothing here is ever written to a stream.
/// </summary>
public enum ReviewLapGuardOutcome
{
    /// <summary>The guard was written; any session started in this worktree is covered by it.</summary>
    Written,

    /// <summary>The worktree already carried a local settings file, so nothing was overwritten — the run directory's own settings file is the vehicle instead.</summary>
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
                return ReviewLapGuardOutcome.AlreadyPresent;
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
}
