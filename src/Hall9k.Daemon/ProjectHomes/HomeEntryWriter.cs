using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Daemon.ProjectHomes;

/// <summary>What one write did, so the engine can count what actually changed without re-reading disk.</summary>
public readonly record struct HomeEntryWriteResult(bool Changed, string DirectoryPath);

/// <summary>
/// The filesystem half of a task or idea render (backlog 48): find the entry's directory (moving
/// it if the slug changed since the last render), and write the rendered file only when it
/// actually differs. Shared by tasks and ideas, but the <c>workspace/</c> sibling is a task-only
/// affordance (<paramref name="includeWorkspace"/> on <see cref="Write"/>): an idea's real
/// discovery workspace lives at the global <c>~/.hall9k/ideas/&lt;id&gt;/workspace</c>, unrelated
/// to the project home, and creating a same-looking-but-inert <c>workspace/</c> beside
/// <c>idea.md</c> would invite a human to drop research material into a folder nothing ever reads
/// (backlog 48's own DISCOVERY.md slice 3, "Relocations", is where the idea workspace moves here
/// for real — deliberately not this task's).
/// </summary>
public static class HomeEntryWriter
{
    /// <summary>
    /// A hidden per-directory marker carrying the entity's full id (backlog 48 cycle 2 review):
    /// the short id in a directory name is 32 bits, which two entities in one large project can
    /// collide on, so a prefix match alone is not enough to know a candidate directory actually
    /// belongs to the id being rendered. Never shown as part of the mirror's own content and
    /// excluded from <see cref="HomeEntryReconciler"/>'s "nothing but generated content" check.
    /// </summary>
    internal const string IdentityMarkerFileName = ".hall9k-id";

    /// <summary>
    /// Writes <paramref name="renderedContent"/> as <paramref name="fileName"/> under the entry's
    /// directory inside <paramref name="rootDirectory"/> (<c>tasks/</c> or <c>ideas/</c>).
    /// <para>
    /// If a directory already exists for this id under a different name — the slug changed because
    /// the objective or note was revised — it is moved to the current name rather than left behind
    /// as a stale copy, carrying its <c>workspace/</c> with it. If a directory already sits at both
    /// the old and the new name (a previous move that could not complete), the existing one is left
    /// alone rather than silently merged into.
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
        bool includeWorkspace = true)
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
        else if (FindExisting(rootDirectory, id, excludingName: directoryName) is { } stale)
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
    /// A directory already on disk for this id under some other name — the slug changed since the
    /// last render. Matching is by short-id prefix first (cheap, and right in the overwhelming
    /// majority of cases) and then confirmed against the full id recorded in
    /// <see cref="IdentityMarkerFileName"/>, because the prefix alone is a 32-bit value and two
    /// unrelated entities in a project with enough history can share one — without the
    /// confirmation, a same-prefix directory belonging to a different task or idea would be
    /// <c>Directory.Move</c>d into this entry's slot, merging unrelated content silently. A
    /// same-prefix candidate with no marker, or a mismatched one, is not this entity's directory
    /// and is left alone; the caller then creates a fresh directory instead of moving into it, and
    /// reconciliation judges the untouched candidate on its own next sweep. Excludes
    /// <paramref name="excludingName"/> so a caller that already confirmed the target does not
    /// exist never matches itself.
    /// </summary>
    private static string? FindExisting(string rootDirectory, Guid id, string excludingName)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        string prefix = DomainId.Short(id) + "-";
        string fullId = id.ToString("N");
        return Directory.EnumerateDirectories(rootDirectory)
            .FirstOrDefault(directory =>
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)
                    || string.Equals(name, excludingName, StringComparison.Ordinal))
                {
                    return false;
                }

                string markerPath = Path.Combine(directory, IdentityMarkerFileName);
                return File.Exists(markerPath) && File.ReadAllText(markerPath).Trim() == fullId;
            });
    }

    private static void EnsureIdentityMarker(string directoryPath, Guid id)
    {
        string markerPath = Path.Combine(directoryPath, IdentityMarkerFileName);
        string fullId = id.ToString("N");
        if (!File.Exists(markerPath) || File.ReadAllText(markerPath).Trim() != fullId)
        {
            File.WriteAllText(markerPath, fullId);
        }
    }
}
