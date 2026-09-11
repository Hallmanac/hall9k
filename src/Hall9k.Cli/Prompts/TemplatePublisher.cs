using System.Security.Cryptography;
using System.Text;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Prompts;

/// <summary>
/// Fills the install's canonical prompt-template set (<see cref="TemplateLibraryPaths.CanonicalDirectory"/>)
/// from this repository's own <c>.claude/templates</c> (for <c>h9k install --repo</c>) or a
/// release payload's bundled <c>templates/</c> (for <c>--from-release</c>/<c>h9k update</c>) — the
/// same publish/retire/override-by-name discipline <see cref="SkillSeeder.PublishCanonical"/>
/// already applies to the ordinary skill set, scoped to a different canonical home and a different
/// unit: a template <em>package</em> (one subdirectory per prompt builder, e.g.
/// <c>review-lap-prompt-builder/</c>) rather than a skill directory gated on carrying a
/// <c>SKILL.md</c> — every subdirectory under the template source is a template package by
/// construction, since nothing else is ever published there. One discipline this class adds on top
/// that <see cref="SkillSeeder"/> does not need: <see cref="FillMissingFiles"/> still writes a new
/// file or fragment into an operator's own override, since a template package a builder cannot find
/// every path or fragment in fails outright, where a skill an operator overrode simply keeps
/// whatever wording it already has.
/// <para>
/// Deliberately has no <c>Seed</c>/<c>SeedNode</c> counterpart: a template is read directly out of
/// the canonical directory (<see cref="PromptTemplates"/>) by whichever process assembles the
/// prompt — the CLI today, for the one builder this task moved — and is never linked into a
/// project home's <c>skills/</c> or <c>.claude/skills</c> adapter, and never carries a
/// <c>SKILL.md</c> — publishing it into either would add its description to every session's first
/// turn, which this task's own token-cost rule puts out of bounds.
/// </para>
/// </summary>
public static class TemplatePublisher
{
    public static SkillPublication PublishCanonical(string sourceDirectory)
    {
        string canonical = TemplateLibraryPaths.CanonicalDirectory;
        Directory.CreateDirectory(canonical);

        (bool manifestConfirmed, IReadOnlyDictionary<string, string> previously) = TryReadManifest();
        if (!manifestConfirmed)
        {
            // Mirrors SkillSeeder.PublishCanonical's own identical refusal: without the manifest,
            // an already-published package cannot be told apart from an operator's own directory
            // of the same name, so nothing here is published, retired, or classified as an
            // override this pass.
            return new SkillPublication([], [], [], ManifestUnconfirmed: true);
        }

        if (!Directory.Exists(sourceDirectory))
        {
            return new SkillPublication([], [], []);
        }

        List<string> published = [];
        List<string> leftAlone = [];
        Dictionary<string, string> hashes = [];
        foreach (string package in Directory.EnumerateDirectories(sourceDirectory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(package);
            string destination = Path.Combine(canonical, name);
            if (Directory.Exists(destination))
            {
                if (!previously.TryGetValue(name, out string? recordedHash)
                    || recordedHash != ComputeContentHash(destination))
                {
                    // An operator's own edit is theirs, not this pass's to overwrite — but a
                    // package they overrode before this source shipped a new file (or a builder's
                    // own later revision added a new named fragment to an existing file) must not
                    // leave a builder asking PromptTemplates.Load for a path or fragment that
                    // exists in the canonical source but was never in the operator's own snapshot.
                    // Filling in only what is missing, never touching a file the operator already
                    // has, keeps their edit intact while closing that gap (independent pre-PR
                    // review, cycle 1).
                    FillMissingFiles(package, destination);
                    leftAlone.Add(name);
                    continue;
                }

                Directory.Delete(destination, recursive: true);
            }

            SkillSeeder.CopyDirectory(package, destination);
            hashes[name] = ComputeContentHash(destination);
            published.Add(name);
        }

        List<string> retired = [];
        HashSet<string> handled = [.. published, .. leftAlone];
        foreach ((string name, string recordedHash) in previously.Where(entry => !handled.Contains(entry.Key)))
        {
            string directory = Path.Combine(canonical, name);
            if (!Directory.Exists(directory) || ComputeContentHash(directory) != recordedHash)
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
            retired.Add(name);
        }

        File.WriteAllLines(
            TemplateLibraryPaths.PublishedManifest,
            published.Select(name => $"{name}\t{hashes[name]}"));

        return new SkillPublication(published, retired, leftAlone);
    }

    /// <summary>
    /// Removes exactly what the last <c>h9k install</c> published into the canonical template
    /// set — <c>h9k uninstall</c>'s use of this class, held to the identical
    /// "only what still matches the recorded hash is install's" discipline
    /// <see cref="SkillSeeder.RemovePublished"/> already applies to the ordinary skill set.
    /// </summary>
    public static (IReadOnlyList<string> Removed, bool ManifestConfirmed) RemovePublished(List<string> stillPresent)
    {
        (bool manifestConfirmed, IReadOnlyDictionary<string, string> previously) = TryReadManifest();
        if (!manifestConfirmed)
        {
            stillPresent.Add(TemplateLibraryPaths.PublishedManifest);
            return ([], false);
        }

        List<string> removed = [];
        Dictionary<string, string> remaining = [];
        foreach ((string name, string recordedHash) in previously)
        {
            string directory = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, name);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            string? currentHash = TryComputeContentHash(directory);
            if (currentHash is null)
            {
                stillPresent.Add(directory);
                remaining[name] = recordedHash;
                continue;
            }

            if (currentHash != recordedHash)
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed.Add(name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                stillPresent.Add(directory);
                remaining[name] = TryComputeContentHash(directory) ?? recordedHash;
            }
        }

        try
        {
            if (remaining.Count > 0)
            {
                File.WriteAllLines(
                    TemplateLibraryPaths.PublishedManifest,
                    remaining.Select(entry => $"{entry.Key}\t{entry.Value}"));
            }
            else if (File.Exists(TemplateLibraryPaths.PublishedManifest))
            {
                File.Delete(TemplateLibraryPaths.PublishedManifest);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stillPresent.Add(TemplateLibraryPaths.PublishedManifest);
        }

        string canonical = TemplateLibraryPaths.CanonicalDirectory;
        if (Directory.Exists(canonical))
        {
            bool empty;
            try
            {
                empty = !Directory.EnumerateFileSystemEntries(canonical).Any();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                stillPresent.Add(canonical);
                empty = false;
            }

            if (empty)
            {
                try
                {
                    Directory.Delete(canonical);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    stillPresent.Add(canonical);
                }
            }
        }

        return (removed, true);
    }

    private static (bool Confirmed, IReadOnlyDictionary<string, string> Manifest) TryReadManifest()
    {
        if (!File.Exists(TemplateLibraryPaths.PublishedManifest))
        {
            return (true, new Dictionary<string, string>());
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(TemplateLibraryPaths.PublishedManifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, new Dictionary<string, string>());
        }

        Dictionary<string, string> manifest = [];
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t', 2);
            if (parts is [{ } name, { } hash] && name.IsNotBlank() && IsCanonicalChildName(name))
            {
                manifest[name] = hash;
            }
        }

        return (true, manifest);
    }

    /// <summary>
    /// A manifest entry is only ever combined onto <see cref="TemplateLibraryPaths.CanonicalDirectory"/>
    /// as-is, then hashed and, on a match, recursively deleted — so a corrupt or tampered
    /// <c>.published</c> file naming a rooted path or a <c>..</c> segment must never resolve
    /// outside that directory. The identical guard <see cref="SkillSeeder"/> applies to its own
    /// manifest for the same reason.
    /// </summary>
    private static bool IsCanonicalChildName(string name) =>
        name is not ("." or "..")
        && !Path.IsPathRooted(name)
        && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    /// <summary>
    /// Copies every file <paramref name="source"/> carries that <paramref name="destination"/>
    /// does not, at the same relative path, creating whatever subdirectories that path needs — and
    /// never overwrites a file already at <paramref name="destination"/>, since that file is
    /// exactly what made this package an operator's override in the first place. An OS metadata
    /// artifact (see <see cref="IsIgnorableArtifact"/>) is skipped on both sides, the same as
    /// <see cref="ComputeContentHash"/> ignores it, so a stray <c>.DS_Store</c> a file browser left
    /// in the operator's own directory is never copied in as though it were template content.
    /// <para>
    /// A file present on both sides is still checked for a missing named fragment (see
    /// <see cref="AppendMissingFragments"/>): a builder's own later revision can add a new
    /// <c>===name===</c> fragment to a file the operator already overrode, and that gap must close
    /// the same way a whole missing file's does, or <see cref="PromptTemplates.Load"/> throws
    /// looking for a fragment the operator's stale copy never received (independent pre-PR review,
    /// cycle 2).
    /// </para>
    /// </summary>
    private static void FillMissingFiles(string source, string destination)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, sourceFile);
            if (IsIgnorableArtifact(Path.GetFileName(sourceFile)))
            {
                continue;
            }

            string destinationFile = Path.Combine(destination, relative);
            if (!File.Exists(destinationFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? destination);
                File.Copy(sourceFile, destinationFile);
                continue;
            }

            AppendMissingFragments(sourceFile, destinationFile);
        }
    }

    /// <summary>
    /// Appends the verbatim marker-and-body block of every named fragment <paramref name="sourceFile"/>
    /// carries that <paramref name="destinationFile"/> does not — an operator's override keeps every
    /// fragment it already has untouched, exactly as <see cref="FillMissingFiles"/> never overwrites
    /// a whole file the operator already has, and only receives what a later canonical revision
    /// added since the operator's own snapshot.
    /// </summary>
    private static void AppendMissingFragments(string sourceFile, string destinationFile)
    {
        string sourceContent = File.ReadAllText(sourceFile);
        IReadOnlyList<string> sourceFragments = PromptTemplates.FragmentNames(sourceContent);
        if (sourceFragments.Count == 0)
        {
            return;
        }

        HashSet<string> destinationFragments = [.. PromptTemplates.FragmentNames(File.ReadAllText(destinationFile))];
        List<string> missing = [.. sourceFragments.Where(name => !destinationFragments.Contains(name))];
        if (missing.Count == 0)
        {
            return;
        }

        StringBuilder addition = new();
        foreach (string name in missing)
        {
            // ExtractFragmentBlock already ends in its own trailing '\n' when the fragment is the
            // source file's last one (the file's own final newline leaves an empty last line in
            // the split it is built from) and does not otherwise — trimming before adding exactly
            // one back keeps the file at a single trailing newline either way, rather than two
            // when the fragment happened to be last (independent pre-PR review, cycle 2).
            addition.Append('\n');
            addition.Append(PromptTemplates.ExtractFragmentBlock(sourceContent, name, sourceFile).TrimEnd('\n'));
            addition.Append('\n');
        }

        File.AppendAllText(destinationFile, addition.ToString());
    }

    /// <summary>
    /// An OS-generated file a directory can pick up just by being browsed (Finder's
    /// <c>.DS_Store</c>, Explorer's <c>desktop.ini</c> and <c>Thumbs.db</c>, and the AppleDouble
    /// <c>._*</c> sidecars <c>COPYFILE_DISABLE</c> already keeps out of a release archive) rather
    /// than anything an operator meant as template content. Included in a package's content hash,
    /// it would flip an untouched override into "edited" the moment somebody opened the folder in a
    /// file browser, which is not an edit this class's publish/retire/override-by-name discipline
    /// should ever see.
    /// </summary>
    private static bool IsIgnorableArtifact(string fileName) =>
        fileName is ".DS_Store" or "Thumbs.db" or "desktop.ini"
        || fileName.StartsWith("._", StringComparison.Ordinal);

    private static string ComputeContentHash(string directory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnorableArtifact(Path.GetFileName(path)))
            .Select(path => Path.GetRelativePath(directory, path))
            .Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file));
            hash.AppendData(File.ReadAllBytes(Path.Combine(directory, file)));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? TryComputeContentHash(string directory)
    {
        try
        {
            return ComputeContentHash(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
