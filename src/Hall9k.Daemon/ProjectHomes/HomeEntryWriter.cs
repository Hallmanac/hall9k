using Hall9k.Domain.Infrastructure.Ids;

namespace Hall9k.Daemon.ProjectHomes;

/// <summary>What one write did, so the engine can count what actually changed without re-reading disk.</summary>
public readonly record struct HomeEntryWriteResult(bool Changed, string DirectoryPath);

/// <summary>
/// The filesystem half of a task or idea render (backlog 48): find the entry's directory (moving
/// it if the slug changed since the last render), make sure <c>workspace/</c> exists beside it,
/// and write the rendered file only when it actually differs. Shared by tasks and ideas because
/// the shape is identical at both lifecycle stages — an id-plus-slug directory holding one
/// generated file and a workspace for whatever accumulates beside it.
/// </summary>
public static class HomeEntryWriter
{
    /// <summary>
    /// Writes <paramref name="renderedContent"/> as <paramref name="fileName"/> under the entry's
    /// directory inside <paramref name="rootDirectory"/> (<c>tasks/</c> or <c>ideas/</c>).
    /// <para>
    /// If a directory already exists for this id under a different name — the slug changed because
    /// the objective or note was revised — it is moved to the current name rather than left behind
    /// as a stale copy, carrying its <c>workspace/</c> with it. If a directory already sits at both
    /// the old and the new name (a previous move that could not complete, or two ids colliding on
    /// one short-id prefix), the existing one is left alone rather than silently merged into.
    /// </para>
    /// </summary>
    public static HomeEntryWriteResult Write(
        string rootDirectory, Guid id, string directoryName, string fileName, string renderedContent)
    {
        string targetDirectory = Path.Combine(rootDirectory, directoryName);

        // The correctly-named directory wins outright when it already exists: no need to go
        // looking for a stale one, and no ambiguity about which of two same-prefix directories
        // (an interrupted move can leave both standing) is "the" existing one.
        if (!Directory.Exists(targetDirectory)
            && FindExisting(rootDirectory, DomainId.Short(id), excludingName: directoryName) is { } stale)
        {
            Directory.Move(stale, targetDirectory);
        }

        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(Path.Combine(targetDirectory, "workspace"));

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
    /// A directory already on disk for this short id under some other name — the slug changed
    /// since the last render. Excludes <paramref name="excludingName"/> so a caller that already
    /// confirmed the target does not exist never matches itself.
    /// </summary>
    private static string? FindExisting(string rootDirectory, string shortId, string excludingName)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        string prefix = shortId + "-";
        return Directory.EnumerateDirectories(rootDirectory)
            .FirstOrDefault(directory =>
            {
                string name = Path.GetFileName(directory);
                return name.StartsWith(prefix, StringComparison.Ordinal)
                    && !string.Equals(name, excludingName, StringComparison.Ordinal);
            });
    }
}
