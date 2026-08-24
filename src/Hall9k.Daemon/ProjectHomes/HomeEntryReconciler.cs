namespace Hall9k.Daemon.ProjectHomes;

/// <summary>
/// The other half of the daemon-start reconciliation pass (backlog 48): a directory under
/// <c>tasks/</c> or <c>ideas/</c> whose exact name matches no task or idea currently rendering —
/// the most likely cause is a partial move left behind by an interrupted rename, since every
/// live task and idea is rendered every sweep and therefore always has a directory at its current
/// name. Matching is by the whole <c>&lt;shortid&gt;-&lt;slug&gt;</c> name rather than just the
/// short-id prefix on purpose: when a slug rename cannot complete because a directory already
/// sits at both the old and the new name, <c>HomeEntryWriter</c> deliberately leaves the old one
/// standing (never silently merged), and a prefix match would treat that stale duplicate as live
/// because it shares an id with the one directory that is actually current.
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

    /// <summary>
    /// Reconciles orphans in <paramref name="rootDirectory"/>. <paramref name="failedShortIds"/>
    /// names the entities whose render threw earlier in *this same sweep* (backlog 48 cycle 2
    /// review): a failed <c>Directory.Move</c> can leave a live entry's directory standing under
    /// its old name while <paramref name="knownDirectoryNames"/> only knows the new one, and
    /// without this exclusion the very sweep that failed to rename the directory would then judge
    /// it orphaned and delete or mark it — a live entry, mistaken for dead, in the same pass that
    /// caused the mistake. Any directory whose short-id prefix is in this set is left untouched
    /// regardless of its full name, on the same reasoning <c>HomeEntryWriter</c> uses when it finds
    /// a directory already sitting at both an old and a new name: when disk state cannot be trusted
    /// this cycle, the safe move is to leave it for the next sweep to judge honestly.
    /// </summary>
    public static IReadOnlyList<string> RemoveOrMarkOrphans(
        string rootDirectory, IReadOnlySet<string> knownDirectoryNames, string generatedFileName,
        IReadOnlySet<string>? failedShortIds = null)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return [];
        }

        List<string> handled = [];
        foreach (string directory in Directory.EnumerateDirectories(rootDirectory))
        {
            string name = Path.GetFileName(directory);
            if (knownDirectoryNames.Contains(name))
            {
                continue;
            }

            if (failedShortIds is { Count: > 0 } && failedShortIds.Contains(ShortIdPrefix(name)))
            {
                continue;
            }

            if (IsOnlyGeneratedContent(directory, generatedFileName))
            {
                Directory.Delete(directory, recursive: true);
                handled.Add(directory);
            }
            else if (Mark(directory))
            {
                handled.Add(directory);
            }
        }

        return handled;
    }

    /// <summary>
    /// Writes the marker if it is not already there, and reports whether it did — a directory
    /// marked on a prior sweep and never touched since must not keep counting as "handled" on
    /// every sweep after, or <see cref="ProjectHomes.ProjectHomeRenderLoop"/>'s activity log
    /// would repeat forever for a directory nothing new happened to.
    /// </summary>
    private static bool Mark(string directory)
    {
        string markerPath = Path.Combine(directory, MarkerFileName);
        if (File.Exists(markerPath))
        {
            return false;
        }

        File.WriteAllText(markerPath,
            "This directory no longer matches any task or idea in the Hall9k store — the id it was "
            + "named for was not found on the last reconciliation pass, most likely because it was "
            + "renamed after an objective or note revision. Its contents were left in place rather "
            + "than deleted; move what is worth keeping and remove the directory by hand.\n");
        return true;
    }

    private static string ShortIdPrefix(string directoryName)
    {
        int separator = directoryName.IndexOf('-');
        return separator < 0 ? directoryName : directoryName[..separator];
    }

    private static bool IsOnlyGeneratedContent(string directory, string generatedFileName)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(entry);
            if (string.Equals(name, generatedFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HomeEntryWriter.IdentityMarkerFileName, StringComparison.OrdinalIgnoreCase))
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
