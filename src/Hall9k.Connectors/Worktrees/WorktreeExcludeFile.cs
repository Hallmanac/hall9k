namespace Hall9k.Connectors.Worktrees;

/// <summary>
/// The repository-local ignore list a checkout's own untracked, platform-generated files go on
/// (idea d805fd8b, piece 2): git's <c>info/exclude</c>, which ignores paths without asking the
/// project to track a <c>.gitignore</c> line for a file the platform writes.
/// <para>
/// <b>It is the repository's, not the worktree's.</b> Git resolves <c>info/exclude</c> against
/// the <em>common</em> directory, so every worktree cut from one clone shares a single file. That
/// is the right granularity here (the same generated names are ignored in every worktree of the
/// same project) and it is why every write through <see cref="Compose"/> is idempotent and
/// append-only: two dispatches racing each other both read a file without the block and both
/// append the identical block, so the loser of the race rewrites the same bytes rather than
/// doubling anything or dropping a line somebody else added.
/// </para>
/// <para>
/// Resolved by reading git's own pointer files rather than by shelling out to
/// <c>git rev-parse --git-path info/exclude</c>. A dispatch already spawns plenty of git, but
/// this runs on the launch path for every run and the answer is two small file reads: a linked
/// worktree's <c>.git</c> is a file reading <c>gitdir: &lt;path&gt;</c>, and that directory holds
/// a <c>commondir</c> file naming the clone every worktree shares. Reading them keeps this
/// testable without a repository to run git against (the testing rule, Brian 2026-09-13).
/// </para>
/// </summary>
public static class WorktreeExcludeFile
{
    /// <summary>
    /// The comment heading the block this writes. It is also how <see cref="Compose"/> recognises
    /// its own earlier work, so it must stay stable: changing the wording makes every repository
    /// that already carries the old block gain a second one.
    /// </summary>
    public const string BlockHeading = "# hall9k: generated projections of platform data, never committed (idea d805fd8b)";

    /// <summary>
    /// Where <paramref name="checkoutPath"/>'s repository keeps its exclude list, or
    /// <see langword="null"/> when this directory is not a checkout at all or its pointer files
    /// say something this cannot read. Null is the honest answer rather than a guessed path: the
    /// caller skips the ignore and says so, instead of writing an exclude file into a directory
    /// git will never consult (AGENTS.md, never guess at unobserved facts).
    /// </summary>
    public static string? Resolve(string checkoutPath)
    {
        string dotGit = Path.Combine(checkoutPath, ".git");
        if (Directory.Exists(dotGit))
        {
            return Path.Combine(dotGit, "info", "exclude");
        }

        if (!File.Exists(dotGit) || ReadPointer(dotGit, "gitdir:") is not { } gitDirectory)
        {
            return null;
        }

        string resolvedGitDirectory = Path.GetFullPath(gitDirectory, checkoutPath);
        string commonDirectory = ReadCommonDirectory(resolvedGitDirectory) ?? resolvedGitDirectory;
        return Path.Combine(commonDirectory, "info", "exclude");
    }

    /// <summary>
    /// What <paramref name="existing"/> should become once <paramref name="relativePaths"/> are
    /// ignored, or <see langword="null"/> when this repository already carries the block and there
    /// is nothing to write. Every path is anchored with a leading slash, so
    /// <c>decisions.md</c> ignores the one at the checkout's root and not a
    /// <c>docs/decisions.md</c> the project actually tracks.
    /// <para>
    /// Recognised by <see cref="BlockHeading"/> alone rather than by checking each path: the block
    /// is written as a unit and re-written as a unit, and a per-path check would append a second
    /// heading the first time the set of generated files changes.
    /// </para>
    /// </summary>
    public static string? Compose(string? existing, IReadOnlyList<string> relativePaths)
    {
        string content = existing ?? string.Empty;
        if (content.Contains(BlockHeading, StringComparison.Ordinal))
        {
            return null;
        }

        // The existing bytes are carried through untouched, including their own line endings: this
        // file belongs to the repository and may well have been written by a human or by git
        // itself, and normalising somebody else's lines to make room for three of ours would show
        // up as an unexplained whole-file change the next time anyone looked at it.
        string separator = content.Length == 0 || content.EndsWith('\n') ? string.Empty : "\n";
        return content + separator + BlockHeading + "\n"
            + string.Concat(relativePaths.Select(path => $"/{path}\n"));
    }

    /// <summary>
    /// The clone every worktree of this one shares, from the <c>commondir</c> file git writes into
    /// each linked worktree's own git directory (its content is normally the relative
    /// <c>../..</c>). Absent for a plain, unlinked repository, where the git directory already is
    /// the common one.
    /// </summary>
    private static string? ReadCommonDirectory(string gitDirectory)
    {
        string commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
        return File.Exists(commonDirectoryFile) && ReadPointer(commonDirectoryFile, prefix: null) is { } pointer
            ? Path.GetFullPath(pointer, gitDirectory)
            : null;
    }

    /// <summary>
    /// The one path a git pointer file carries, with <paramref name="prefix"/> stripped when there
    /// is one. Null for anything that does not read as that, rather than a best guess at what the
    /// file might have meant.
    /// </summary>
    private static string? ReadPointer(string path, string? prefix)
    {
        string content;
        try
        {
            content = File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (prefix is not null)
        {
            if (!content.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            content = content[prefix.Length..].Trim();
        }

        return content.Length == 0 ? null : content;
    }
}
