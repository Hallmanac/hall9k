using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// An idea's discovery workspace, where research notes, gathered files, and prototypes
/// accumulate while the idea is being figured out (Decisions Log #35).
/// <para>
/// WHETHER it lives under a project's home is decided once, at capture, and recorded on
/// <c>IdeaCaptured</c> as <c>IdeaDetails.WorkspaceHome</c>: an idea captured with a project whose
/// home already existed on this machine gets its workspace under that home
/// (<c>&lt;home&gt;/ideas/&lt;shortid&gt;-&lt;slug&gt;/workspace</c>, ruled 2026-08-23, backlog
/// 49); everything else — no project yet, or a project with no home yet — is permanently on the
/// platform-global location instead. That decision never changes after capture, which is what
/// keeps a project gaining a home later from silently redirecting an older idea's already-
/// materialised workspace to a directory nothing ever put files in.
/// </para>
/// <para>
/// WHERE, exactly, under that decision is recomputed on every read from the idea's CURRENT
/// directory name rather than frozen as an absolute string: unlike a run's worktree, an idea's
/// note is meant to be revised repeatedly during discovery ("sharpen it later"), and the render
/// sweep renames a home-resident idea's mirror directory — carrying its <c>workspace/</c> along
/// — every time the note's slug changes. Recomputing the leaf from the current directory name
/// tracks wherever that rename actually left it; a frozen absolute path would not.
/// </para>
/// </summary>
public static class IdeaPaths
{
    /// <summary>The location an idea with no recorded home workspace uses: ~/.hall9k/ideas/&lt;idea-id&gt;/.</summary>
    public static string GlobalDirectory(Guid ideaId) => Path.Combine(PlatformPaths.Home, "ideas", ideaId.ToString());

    /// <summary>
    /// The idea's own directory, given the home decision recorded at capture
    /// (<c>IdeaDetails.WorkspaceHome</c>) and its CURRENT directory name (recompute this fresh
    /// from current state — <c>IdeaDocumentRenderer.DirectoryName</c> — every time; see the type
    /// doc for why).
    /// <para>
    /// For a home-resident idea, the freshly computed <paramref name="ideaDirectoryName"/> is only
    /// a candidate: no CLI command rings the render sweep's doorbell for an idea (backlog 49 cycle
    /// 2 review), so a revise that changes the slug can land well ahead of the sweep that actually
    /// renames the directory. Resolving against whatever directory already exists on disk for this
    /// id — the same read <c>Hall9k.Daemon</c>'s <c>RunLauncher</c> already does via
    /// <c>HomeEntryWriter.FindExistingDirectory</c> — keeps a caller pointed at the one directory
    /// that is actually there; falling back to the computed name only when none is found yet
    /// covers the idea's very first render, before any directory exists to find.
    /// </para>
    /// </summary>
    public static string ResolveDirectory(ProjectHome workspaceHome, string ideaDirectoryName, Guid ideaId) =>
        workspaceHome.HasValue
            ? HomeEntryLookup.FindExistingDirectory(ProjectHomePaths.IdeasDirectory(workspaceHome.Value), ideaId)
                ?? ProjectHomePaths.IdeaDirectory(workspaceHome.Value, ideaDirectoryName)
            : GlobalDirectory(ideaId);

    public static string WorkspaceDirectory(string ideaDirectory) => Path.Combine(ideaDirectory, "workspace");

    /// <summary>
    /// Creates the workspace if it is not there and returns it. Capture calls this so the
    /// directory is real the moment the idea exists: a path that has to be created before it
    /// can be used is a path nobody drops a file into.
    /// </summary>
    public static string EnsureWorkspace(string ideaDirectory)
    {
        string workspace = WorkspaceDirectory(ideaDirectory);
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    /// <summary>
    /// How much has accumulated, counted at read time: the workspace is a plain directory a
    /// human fills by hand, so the only honest count is the one taken when someone looks.
    /// Null means the directory is not there — nothing was ever put in it, or it was removed.
    /// </summary>
    public static int? FileCount(string ideaDirectory)
    {
        string workspace = WorkspaceDirectory(ideaDirectory);
        return Directory.Exists(workspace)
            ? Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Count()
            : null;
    }
}
