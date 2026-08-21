namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// An idea's discovery workspace: ~/.hall9k/ideas/&lt;idea-id&gt;/workspace, where research
/// notes, gathered files, and prototypes accumulate while the idea is being figured out
/// (Decisions Log #35).
/// <para>
/// Derived from the id exactly as <see cref="RunPaths"/> derives a run's directory, so the
/// stream never records a path it can recompute — and never records what lands inside it. The
/// events carry milestones; the bytes stay on disk (the transcripts-on-disk discipline).
/// Per-file provenance is the attachments feature's job (backlog IDEA-task-attachments), not
/// something this pretends to have.
/// </para>
/// </summary>
public static class IdeaPaths
{
    public static string IdeaDirectory(Guid ideaId) => Path.Combine(PlatformPaths.Home, "ideas", ideaId.ToString());

    public static string WorkspaceDirectory(Guid ideaId) => Path.Combine(IdeaDirectory(ideaId), "workspace");

    /// <summary>
    /// Creates the workspace if it is not there and returns it. Capture calls this so the
    /// directory is real the moment the idea exists: a path that has to be created before it
    /// can be used is a path nobody drops a file into.
    /// </summary>
    public static string EnsureWorkspace(Guid ideaId)
    {
        string workspace = WorkspaceDirectory(ideaId);
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    /// <summary>
    /// How much has accumulated, counted at read time: the workspace is a plain directory a
    /// human fills by hand, so the only honest count is the one taken when someone looks.
    /// Null means the directory is not there — nothing was ever put in it, or it was removed.
    /// </summary>
    public static int? FileCount(Guid ideaId)
    {
        string workspace = WorkspaceDirectory(ideaId);
        return Directory.Exists(workspace)
            ? Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Count()
            : null;
    }
}
