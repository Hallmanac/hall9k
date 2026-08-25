using System.Text;
using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The shape of a project's home directory, in one place, so every machine lays a project out
/// identically and a session that walks into one already knows where everything is (ruled at the
/// project-home discovery, 2026-08-23: location is a setting, shape is the contract).
/// <para>
/// Six entries at the top level, all speaking the platform's own vocabulary:
/// <c>AGENTS.md</c> (a generated render of project facts), <c>repo/</c> (the bare clone, its
/// <c>dev/</c> worktree, and the <c>wt-*</c> worktrees dispatch creates beside them),
/// <c>ideas/</c>, <c>tasks/</c>, and <c>skills/</c> — plus <c>.claude/</c>, which is generated
/// vendor plumbing rather than a noun of its own.
/// </para>
/// <para>
/// There is deliberately no top-level <c>runs/</c>: a run belongs to exactly one task, so it
/// lands under that task's directory and the task directory is the task's whole story (ruled
/// 2026-08-23, backlog 49, which is the slice that actually relocates run directories — this
/// type only states where they will go).
/// </para>
/// </summary>
public static class ProjectHomePaths
{
    /// <summary>The generated briefing at the root of every home.</summary>
    public const string AgentsFileName = "AGENTS.md";

    /// <summary>Every project's home lives under here unless the project overrides its own.</summary>
    public static string ProjectsRoot => Path.Combine(PlatformPaths.Home, "projects");

    /// <summary>
    /// Where a project's home goes when nobody says otherwise: <c>~/.hall9k/projects/&lt;name&gt;</c>,
    /// the name reduced to something every filesystem accepts.
    /// </summary>
    public static string DefaultFor(string projectName) => Path.Combine(ProjectsRoot, DirectoryName(projectName));

    public static string AgentsFile(string home) => Path.Combine(home, AgentsFileName);

    /// <summary>The repo half of the home: the bare clone plus every worktree cut from it.</summary>
    public static string RepoDirectory(string home) => Path.Combine(home, "repo");

    /// <summary>
    /// The bare clone itself, named <c>&lt;project&gt;.git</c> by the house bare-worktree
    /// convention. Worktrees are created as its siblings, which is what
    /// <c>GitWorktreeManager</c> already does with a repository path.
    /// </summary>
    public static string BareRepository(string home, string projectName) =>
        Path.Combine(RepoDirectory(home), $"{DirectoryName(projectName)}.git");

    /// <summary>The always-there worktree on the project's primary branch — where a human reads code.</summary>
    public static string DevWorktree(string home) => Path.Combine(RepoDirectory(home), "dev");

    public static string IdeasDirectory(string home) => Path.Combine(home, "ideas");

    public static string TasksDirectory(string home) => Path.Combine(home, "tasks");

    /// <summary>
    /// Where a task's directory moves once it reaches true closeout (merged, observed) or is
    /// abandoned — a leading underscore so it sorts to the top of an editor's file explorer,
    /// ahead of every live task, and honestly covers both endings (2026-08-25, backlog 51). Not
    /// a directory name a task or idea can ever compute for itself (<see cref="EntryDirectoryName"/>
    /// always starts with a short id, never an underscore), so it can never collide with one.
    /// </summary>
    public const string ArchiveDirectoryName = "_archive";

    /// <summary>The terminal home for a task's directory: <c>tasks/_archive/</c>.</summary>
    public static string ArchivedTasksDirectory(string home) => Path.Combine(TasksDirectory(home), ArchiveDirectoryName);

    /// <summary>
    /// A task's own directory, named by short id plus a slug from its objective (backlog 48):
    /// <c>tasks/98ac05ef-project-home/</c>. Holds <c>task.md</c> (the rendered contract) and a
    /// <c>workspace/</c> for whatever refinement accumulates beside it. Rooted directly under
    /// <see cref="TasksDirectory"/> while the task is live; a terminal task's directory is this
    /// same shape rooted under <see cref="ArchivedTasksDirectory"/> instead (the render sweep
    /// decides which root a task's directory belongs under, not this helper).
    /// </summary>
    public static string TaskDirectory(string home, string directoryName) => Path.Combine(TasksDirectory(home), directoryName);

    public static string TaskFile(string taskDirectory) => Path.Combine(taskDirectory, "task.md");

    public static string TaskWorkspaceDirectory(string taskDirectory) => Path.Combine(taskDirectory, "workspace");

    /// <summary>The same shape as <see cref="TaskDirectory"/>, one lifecycle stage earlier (backlog 48).</summary>
    public static string IdeaDirectory(string home, string directoryName) => Path.Combine(IdeasDirectory(home), directoryName);

    public static string IdeaFile(string ideaDirectory) => Path.Combine(ideaDirectory, "idea.md");

    /// <summary>
    /// The <c>&lt;shortid&gt;-&lt;slug&gt;</c> directory name a task or idea renders under: stable
    /// across a rename of the underlying objective/text only in that it is recomputed from whatever
    /// the text reads now — a caller that finds an existing directory under the old name is
    /// expected to move it here rather than leave a stale copy behind.
    /// </summary>
    public static string EntryDirectoryName(Guid id, string slugSource) => $"{DomainId.Short(id)}-{Slug(slugSource)}";

    /// <summary>
    /// Free text reduced to a directory-safe slug: lowercase, ASCII letters and digits, single
    /// dashes for everything else, cut to <paramref name="maxLength"/> so a long objective does not
    /// become an unusable path segment. Falls back to "untitled" rather than an empty segment.
    /// </summary>
    public static string Slug(string text, int maxLength = 60)
    {
        StringBuilder slug = new();
        foreach (char character in text.ToLowerInvariant())
        {
            if (slug.Length >= maxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-') is { Length: > 0 } value ? value : "untitled";
    }

    /// <summary>
    /// Plain markdown skill docs with no vendor framing — the canonical copy, seeded from the
    /// install's own set and extended with whatever this project needs.
    /// </summary>
    public static string SkillsDirectory(string home) => Path.Combine(home, "skills");

    /// <summary>Generated Claude Code plumbing; the same shape a second runtime would get its own of.</summary>
    public static string ClaudeDirectory(string home) => Path.Combine(home, ".claude");

    /// <summary>Symlinks into <see cref="SkillsDirectory"/>, never a second copy of the content.</summary>
    public static string ClaudeSkillsDirectory(string home) => Path.Combine(ClaudeDirectory(home), "skills");

    /// <summary>
    /// Every directory the shape is made of, in creation order. One list so the recipe that
    /// creates a home and the render that describes it cannot drift apart —
    /// <see cref="ArchivedTasksDirectory"/> included, even though nothing lands there until a
    /// task first goes terminal, because the render advertises it as part of the always-there
    /// layout (backlog 51, conformance review) and an empty directory costs nothing to create
    /// up front.
    /// </summary>
    public static IReadOnlyList<string> Directories(string home) =>
    [
        home,
        RepoDirectory(home),
        IdeasDirectory(home),
        TasksDirectory(home),
        ArchivedTasksDirectory(home),
        SkillsDirectory(home),
        ClaudeDirectory(home),
        ClaudeSkillsDirectory(home),
    ];

    /// <summary>
    /// Whether two recorded paths name the same directory. Blank never matches anything, not even
    /// another blank: an unrecorded path is an absence rather than a place, and treating two
    /// absences as the same place would have the first project with no home claim every other's.
    /// Case sensitivity follows the platform's usual filesystem rather than being assumed either
    /// way, since macOS and Windows both fold case by default and Linux does not.
    /// </summary>
    public static bool SameDirectory(string? left, string? right) =>
        left.IsNotBlank()
        && right.IsNotBlank()
        && string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A project name reduced to a directory name: lowercase, ASCII letters and digits, single
    /// dashes for everything else. A project called "Hall9k Platform" gets
    /// <c>hall9k-platform</c> rather than a path with a space in it that every quoted shell
    /// invocation downstream has to get right. A name with nothing usable in it falls back to
    /// "project", which is a directory somebody can find rather than an empty path segment.
    /// </summary>
    public static string DirectoryName(string projectName)
    {
        StringBuilder slug = new();
        foreach (char character in projectName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-') is { Length: > 0 } name ? name : "project";
    }
}
