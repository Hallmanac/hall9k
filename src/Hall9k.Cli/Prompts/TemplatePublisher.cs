using System.Security.Cryptography;
using System.Text;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Prompts;

/// <summary>
/// Fills the install's canonical prompt-template set (<see cref="TemplateLibraryPaths.CanonicalDirectory"/>)
/// from this repository's own <c>.claude/templates</c> (for <c>h9k install --repo</c>) or a
/// release payload's bundled <c>templates/</c> (for <c>--from-release</c>/<c>h9k update</c>) — the
/// identical publish/retire/override-by-name discipline <see cref="SkillSeeder.PublishCanonical"/>
/// already applies to the ordinary skill set, scoped to a different canonical home and a different
/// unit: a template <em>package</em> (one subdirectory per prompt builder, e.g.
/// <c>review-lap-prompt-builder/</c>) rather than a skill directory gated on carrying a
/// <c>SKILL.md</c> — every subdirectory under the template source is a template package by
/// construction, since nothing else is ever published there.
/// <para>
/// Deliberately has no <c>Seed</c>/<c>SeedNode</c> counterpart: a template is read by the daemon
/// and the CLI directly out of the canonical directory (<see cref="PromptTemplates"/>), and is
/// never linked into a project home's <c>skills/</c> or <c>.claude/skills</c> adapter, and never
/// carries a <c>SKILL.md</c> — publishing it into either would add its description to every
/// session's first turn, which this task's own token-cost rule puts out of bounds.
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

    private static string ComputeContentHash(string directory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
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
