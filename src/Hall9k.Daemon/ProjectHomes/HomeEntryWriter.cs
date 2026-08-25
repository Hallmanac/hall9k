using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Daemon.ProjectHomes;

/// <summary>What one write did, so the engine can count what actually changed without re-reading disk.</summary>
public readonly record struct HomeEntryWriteResult(bool Changed, string DirectoryPath);

/// <summary>
/// The filesystem half of a task or idea render (backlog 48): find the entry's directory (moving
/// it if the slug changed since the last render), and write the rendered file only when it
/// actually differs. Shared by tasks and ideas, but the <c>workspace/</c> sibling
/// (<paramref name="includeWorkspace"/> on <see cref="Write"/>) is conditional for an idea in a
/// way it never is for a task: a task always has one here, while an idea's real discovery
/// workspace only lives under this render directory when it was captured with a home already
/// materialised (<c>IdeaDetails.WorkspaceHome</c>, backlog 49) — an idea captured before that, or
/// with no project at all, keeps its workspace at the platform-global location forever, and the
/// caller passes <c>includeWorkspace: false</c> for exactly that idea so a same-looking-but-inert
/// <c>workspace/</c> is never created beside <c>idea.md</c> to invite a human to drop research
/// material into a folder nothing ever reads.
/// </summary>
public static class HomeEntryWriter
{
    /// <summary>
    /// A hidden per-directory marker carrying the entity's full id (backlog 48 cycle 2 review):
    /// the short id in a directory name is 32 bits, which two entities in one large project can
    /// collide on, so a prefix match alone is not enough to know a candidate directory actually
    /// belongs to the id being rendered. Never shown as part of the mirror's own content and
    /// excluded from <see cref="HomeEntryReconciler"/>'s "nothing but generated content" check.
    /// Defined on <see cref="HomeEntryLookup"/> in <c>Hall9k.Domain</c>, which owns the read-only
    /// lookup shared with <c>Hall9k.Cli</c>; re-exposed here under this type's existing name so
    /// every caller in this project keeps reading <c>HomeEntryWriter.IdentityMarkerFileName</c>.
    /// </summary>
    internal const string IdentityMarkerFileName = HomeEntryLookup.IdentityMarkerFileName;

    /// <summary>
    /// Writes <paramref name="renderedContent"/> as <paramref name="fileName"/> under the entry's
    /// directory inside <paramref name="rootDirectory"/> (<c>tasks/</c> or <c>ideas/</c>).
    /// <para>
    /// If a directory already exists for this id under a different name — the slug changed because
    /// the objective or note was revised — it is moved to the current name rather than left behind
    /// as a stale copy, carrying its <c>workspace/</c> with it. If a directory already sits at both
    /// the old and the new name (a previous move that could not complete), the existing one is left
    /// alone rather than silently merged into. <paramref name="alternateRoots"/> extends that same
    /// search to other roots this entity's directory might currently sit under — a task moving into
    /// or out of <c>tasks/_archive/</c> (2026-08-25, backlog 51) is the same "move it, don't leave a
    /// stale copy" rule as a slug rename, just across a different boundary: <paramref name="rootDirectory"/>
    /// is searched first (a same-root slug rename is the common case), then each alternate root in
    /// order, and the first stale directory found anywhere is the one moved.
    /// </para>
    /// <para>
    /// Throws <see cref="IOException"/> when a directory already sits at the target name but its
    /// identity marker names a different id: two distinct ids sharing a short-id/slug collision,
    /// which is a rendering failure for this entity rather than something safe to overwrite. The
    /// caller's existing "one entity's write failure never stops the sweep" handling covers it.
    /// </para>
    /// </summary>
    public static HomeEntryWriteResult Write(
        string rootDirectory, Guid id, string directoryName, string fileName, string renderedContent,
        bool includeWorkspace = true, IReadOnlyList<string>? alternateRoots = null)
    {
        string targetDirectory = Path.Combine(rootDirectory, directoryName);

        // The correctly-named directory wins outright when it already exists: no need to go
        // looking for a stale one, and no ambiguity about which of two same-prefix directories
        // (an interrupted move can leave both standing) is "the" existing one. But "already exists
        // under the right name" is not by itself proof it is *this* id's directory: the short id is
        // 32 bits and the slug is free text, so two distinct ids can compute the identical
        // "<shortid>-<slug>" name. A directory that already carries a marker for a different id is
        // refused rather than overwritten — the same rule FindExisting enforces for a stale-named
        // match, applied here to a same-named one.
        if (Directory.Exists(targetDirectory))
        {
            string existingMarkerPath = Path.Combine(targetDirectory, IdentityMarkerFileName);
            if (File.Exists(existingMarkerPath) && File.ReadAllText(existingMarkerPath).Trim() != id.ToString("N"))
            {
                throw new IOException(
                    $"'{targetDirectory}' already belongs to a different id (a short-id/slug "
                    + "collision); refusing to overwrite it.");
            }
        }
        else if (FindStale(rootDirectory, directoryName, id, alternateRoots) is { } stale)
        {
            Directory.Move(stale, targetDirectory);
        }

        Directory.CreateDirectory(targetDirectory);
        EnsureIdentityMarker(targetDirectory, id);
        if (includeWorkspace)
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, "workspace"));
        }

        string filePath = Path.Combine(targetDirectory, fileName);
        string? current = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        if (current == renderedContent)
        {
            return new HomeEntryWriteResult(false, targetDirectory);
        }

        File.WriteAllText(filePath, renderedContent);
        return new HomeEntryWriteResult(true, targetDirectory);
    }

    /// <summary>
    /// The directory already on disk for this id, under whatever name it currently carries, if
    /// any — the read-only half of <see cref="HomeEntryLookup.FindExisting"/> for callers that need
    /// to place new content under an entry's CURRENT directory without inventing a name the render sweep has
    /// not renamed onto yet (<c>RunLauncher</c>, adversarial review, backlog 49 cycle 1): a run
    /// directory resolved from the task's live objective can name a slug the sweep has not moved
    /// the directory to, and creating that not-yet-existing directory ahead of the sweep leaves
    /// the true, already-populated directory behind as an unmerged orphan the next reconciliation
    /// pass only marks rather than folds in. Resolving against whatever is already on disk instead
    /// keeps every caller pointed at the one directory that actually exists until the sweep itself
    /// performs the move.
    /// </summary>
    /// <paramref name="alternateRoots"/> extends the same search to other roots, exactly as
    /// <see cref="Write"/>'s own does — a caller resolving a task's current directory has to find
    /// it whether it is presently live or archived (<c>RunLauncher</c>, 2026-08-25, backlog 51).
    public static string? FindExistingDirectory(
        string rootDirectory, Guid id, IReadOnlyList<string>? alternateRoots = null) =>
        FindStale(rootDirectory, excludingName: null, id, alternateRoots);

    private static string? FindStale(
        string rootDirectory, string? excludingName, Guid id, IReadOnlyList<string>? alternateRoots)
    {
        if (HomeEntryLookup.FindExisting(rootDirectory, id, excludingName) is { } found)
        {
            return found;
        }

        if (alternateRoots is not null)
        {
            foreach (string alternateRoot in alternateRoots)
            {
                if (HomeEntryLookup.FindExisting(alternateRoot, id) is { } fromAlternate)
                {
                    return fromAlternate;
                }
            }
        }

        return null;
    }

    private static void EnsureIdentityMarker(string directoryPath, Guid id) =>
        HomeEntryLookup.EnsureIdentityMarker(directoryPath, id);
}
