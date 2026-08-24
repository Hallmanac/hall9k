namespace Hall9k.Daemon.ProjectHomes;

/// <summary>
/// The other half of the daemon-start reconciliation pass (backlog 48): a directory under
/// <c>tasks/</c> or <c>ideas/</c> whose short-id prefix names nothing in the store any more —
/// the most likely cause is a partial move left behind by an interrupted rename, since every
/// live task and idea is rendered every sweep and therefore always has a current directory.
/// <para>
/// An empty shell (nothing but the generated file and an empty <c>workspace/</c>) is simply
/// removed: there was never anything here a human made. A directory that holds real material —
/// notes, attachments, anything dropped into <c>workspace/</c> — is never deleted; it is marked
/// with a note explaining why it no longer renders, so nothing a human put there is lost to a
/// sweep they never asked for.
/// </para>
/// </summary>
public static class HomeEntryReconciler
{
    private const string MarkerFileName = "ORPHANED.md";

    public static IReadOnlyList<string> RemoveOrMarkOrphans(
        string rootDirectory, IReadOnlySet<string> knownShortIds, string generatedFileName)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        List<string> handled = [];
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory))
        {
            string name = Path.GetFileName(directory);
            int dash = name.IndexOf('-');
            string prefix = dash > 0 ? name[..dash] : name;
            if (knownShortIds.Contains(prefix))
            {
                continue;
            }

            if (IsOnlyGeneratedContent(directory, generatedFileName))
            {
                Directory.Delete(directory, recursive: true);
            }
            else
            {
                Mark(directory);
            }

            handled.Add(directory);
        }

        return handled;
    }

    private static void Mark(string directory)
    {
        string markerPath = Path.Combine(directory, MarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        File.WriteAllText(markerPath,
            "This directory no longer matches any task or idea in the Hall9k store — the id it was "
            + "named for was not found on the last reconciliation pass, most likely because it was "
            + "renamed after an objective or note revision. Its contents were left in place rather "
            + "than deleted; move what is worth keeping and remove the directory by hand.\n");
    }

    private static bool IsOnlyGeneratedContent(string directory, string generatedFileName)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(entry);
            if (string.Equals(name, generatedFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(name, "workspace", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(entry)
                && !Directory.EnumerateFileSystemEntries(entry).Any())
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
