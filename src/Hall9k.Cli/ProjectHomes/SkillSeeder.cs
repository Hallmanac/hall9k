using System.Security.Cryptography;
using System.Text;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.ProjectHomes;

/// <summary>
/// Seeds a project home's <c>skills/</c> from the install's canonical set and generates the
/// <c>.claude/skills/</c> adapter beside it.
/// <para>
/// Two links, deliberately. <c>skills/&lt;name&gt;</c> points at
/// <c>~/.hall9k/skills/&lt;name&gt;</c>, which is what makes <c>h9k install</c> update every
/// project's platform skills in one move instead of leaving N copies to go stale.
/// <c>.claude/skills/&lt;name&gt;</c> points at <c>skills/&lt;name&gt;</c>, so Claude Code's
/// native discovery finds project-specific skills on exactly the same footing as seeded ones.
/// Side-by-side duplicate content directories were rejected outright at the discovery: two
/// copies that can drift means one of them is lying.
/// </para>
/// <para>
/// Re-seeding is safe by construction. A seeded entry is a symlink and this owns it; a
/// project-specific skill is a real directory and this never touches one, so the two live
/// beside each other and only the seeded half is refreshed. A symlink left dangling by a skill
/// the install stopped publishing is removed, because a broken pointer teaches an agent nothing.
/// A seed that fell back to a copy (<see cref="Point"/>, a symlink-refusing filesystem) is a real
/// directory too, so it carries <see cref="CopyMarkerFile"/> to stay tellable apart from a
/// project-specific one across passes — without it, the next re-seed cannot distinguish its own
/// stale copy from an override and would freeze it at whatever the install shipped the day the
/// symlink first failed.
/// </para>
/// </summary>
public static class SkillSeeder
{
    /// <summary>
    /// Sits inside a seed that was copied rather than linked, so a later pass can tell it apart
    /// from a project-specific skill (a real directory this never wrote) and refresh it instead of
    /// leaving it alone forever. Never appears in a symlinked seed or in a hand-authored one.
    /// </summary>
    internal const string CopyMarkerFile = ".h9k-seeded-copy";
    /// <summary>
    /// Brings <paramref name="home"/>'s <c>skills/</c> and <c>.claude/skills/</c> in line with
    /// the canonical set. Never fails the caller: a filesystem that refuses symlinks (Windows
    /// without developer mode is the case that exists) falls back to copying, and says so, since
    /// a copied skill is a working skill that simply will not follow the next install.
    /// </summary>
    public static IReadOnlyList<ProjectHomeStep> Seed(string home)
    {
        List<ProjectHomeStep> steps = [];
        string skills = ProjectHomePaths.SkillsDirectory(home);
        string adapter = ProjectHomePaths.ClaudeSkillsDirectory(home);
        Directory.CreateDirectory(skills);
        Directory.CreateDirectory(adapter);

        IReadOnlyList<string> canonical = SkillLibraryPaths.Published();
        if (canonical.Count == 0)
        {
            steps.Add(ProjectHomeStep.Skipped(
                $"skills/ is empty: the install has published no canonical skills to "
                + $"{SkillLibraryPaths.CanonicalDirectory} yet. Run h9k install to publish them, then "
                + "h9k project init to seed this home from them."));
        }
        else
        {
            int linked = 0;
            int copied = 0;
            foreach (string name in canonical)
            {
                switch (Point(Path.Combine(skills, name), SkillLibraryPaths.Skill(name)))
                {
                    case PointOutcome.Linked:
                        linked++;
                        break;
                    case PointOutcome.Copied:
                        copied++;
                        break;
                    case PointOutcome.LeftAlone:
                        break;
                }
            }

            steps.Add(ProjectHomeStep.Created(
                $"skills/ seeded from {SkillLibraryPaths.CanonicalDirectory}: {linked} linked"
                + (copied > 0 ? $", {copied} copied because this filesystem refused a symlink" : string.Empty)));
        }

        RemoveDanglingSeeds(skills);
        (int adapterLinks, int adapterCopies) = GenerateAdapters(skills, adapter);
        steps.Add(ProjectHomeStep.Created(
            $".claude/skills/ generated as {adapterLinks} symlink(s) into skills/ — the vendor adapter, "
            + "never a second copy"
            + (adapterCopies > 0
                ? $" (plus {adapterCopies} copied: this filesystem refused a symlink)"
                : string.Empty)));
        return steps;
    }

    private enum PointOutcome
    {
        Linked,
        Copied,
        LeftAlone,
    }

    /// <summary>
    /// Makes <paramref name="link"/> point at <paramref name="target"/>, unless something that
    /// is not ours is already sitting there. A real directory without <see cref="CopyMarkerFile"/>
    /// is a project-specific skill (or a pinned copy somebody made deliberately) and outranks the
    /// seed: the canonical set is a default, and a project that overrode it meant to. A real
    /// directory carrying the marker is this method's own earlier copy fallback, so it is
    /// refreshed exactly like a symlink is, rather than left alone forever.
    /// </summary>
    private static PointOutcome Point(string link, string target)
    {
        FileSystemInfo entry = new DirectoryInfo(link);
        if (entry.LinkTarget is null && Directory.Exists(link))
        {
            if (!File.Exists(Path.Combine(link, CopyMarkerFile)))
            {
                return PointOutcome.LeftAlone;
            }

            Directory.Delete(link, recursive: true);
        }

        try
        {
            if (entry.LinkTarget is not null)
            {
                Unlink(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return PointOutcome.Linked;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            CopyDirectory(target, link);
            File.WriteAllText(Path.Combine(link, CopyMarkerFile), string.Empty);
            return PointOutcome.Copied;
        }
    }

    /// <summary>
    /// Symlinks under <c>skills/</c> whose target has gone — a skill the install stopped
    /// publishing. Only links into the canonical directory are collected: a link somebody made
    /// by hand to somewhere else is theirs, and a dangling one of those is theirs to explain.
    /// </summary>
    private static void RemoveDanglingSeeds(string skills)
    {
        // EnumerateFileSystemEntries rather than EnumerateDirectories: a symlink whose target
        // is gone does not stat as a directory, so the enumeration that would have been the
        // obvious one is exactly blind to the entries this exists to collect.
        foreach (string entry in Directory.EnumerateFileSystemEntries(skills))
        {
            DirectoryInfo candidate = new(entry);
            if (candidate.LinkTarget is not { } target
                || !PointsIntoCanonicalDirectory(target)
                || Directory.Exists(target))
            {
                continue;
            }

            Unlink(entry);
        }
    }

    /// <summary>
    /// Whether a link target is an entry <em>inside</em> the canonical directory, which is what
    /// makes it a seed this owns. The boundary is a path separator rather than a bare prefix:
    /// <c>~/.hall9k/skills-local/x</c> starts with <c>~/.hall9k/skills</c> character for
    /// character while being nobody's seed, and reading it as one has this delete a link somebody
    /// made by hand — precisely the case the summary above promises to leave alone. Origin
    /// incident (2026-08-23): the third cycle of this branch's pre-PR review read the prefix test
    /// as written and found the sibling directory it admits.
    /// </summary>
    private static bool PointsIntoCanonicalDirectory(string target)
    {
        string canonical = Path.TrimEndingDirectorySeparator(SkillLibraryPaths.CanonicalDirectory);
        return target.Length > canonical.Length
            && target.StartsWith(canonical, StringComparison.Ordinal)
            && (target[canonical.Length] == Path.DirectorySeparatorChar
                || target[canonical.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// The vendor adapter: one symlink per entry in <c>skills/</c>, seeded or not. Stale links
    /// are cleared first so a removed skill does not leave a pointer behind.
    /// </summary>
    private static (int Linked, int Copied) GenerateAdapters(string skills, string adapter)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(adapter))
        {
            if (new DirectoryInfo(entry).LinkTarget is not null)
            {
                Unlink(entry);
            }
        }

        int linked = 0;
        int copied = 0;
        foreach (string entry in Directory.EnumerateDirectories(skills).Order(StringComparer.Ordinal))
        {
            string link = Path.Combine(adapter, Path.GetFileName(entry));

            // Only a copy from an earlier pass on a symlink-refusing filesystem is meant to survive
            // the clearing above, and it carries CopyMarkerFile as proof this method wrote it — the
            // same discriminator Point uses for the identical question in skills/ itself. A real
            // directory without the marker was never this method's: a --home pointed at an existing
            // checkout can leave one here (a tracked .claude/skills/<name> the checkout already
            // had), and that is the operator's to keep, not a stale copy to overwrite.
            if (Directory.Exists(link))
            {
                if (!File.Exists(Path.Combine(link, CopyMarkerFile)))
                {
                    continue;
                }

                CopyDirectory(entry, link);
                File.WriteAllText(Path.Combine(link, CopyMarkerFile), string.Empty);
                copied++;
                continue;
            }

            try
            {
                Directory.CreateSymbolicLink(link, entry);
                linked++;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                CopyDirectory(entry, link);
                File.WriteAllText(Path.Combine(link, CopyMarkerFile), string.Empty);
                copied++;
            }
        }

        return (linked, copied);
    }

    /// <summary>
    /// Fills the install's canonical directory from a source set, and answers what it published.
    /// <c>h9k install</c> is the caller; the source is this repository's own <c>.claude/skills</c>
    /// today and a release artefact once backlog 42 lands, and only that argument changes.
    /// <para>
    /// A skill the source no longer ships is removed rather than left behind. A project's symlink
    /// into it then dangles, which <see cref="Seed"/> collects on the next pass — whereas leaving
    /// it would go on offering a retired skill to every agent forever.
    /// </para>
    /// <para>
    /// Only what a previous install published <em>and that nobody has touched since</em> is ever
    /// removed or overwritten, which is what <see cref="SkillLibraryPaths.PublishedManifest"/>
    /// records: not just the name, but a content hash of what this method wrote there. A skill
    /// somebody wrote straight into the canonical directory was never the install's to delete: this
    /// is the operator's own machine, and a skill the platform never shipped is not a stale copy of
    /// anything. When one carries the same name as a shipped skill, theirs stays and shadows the
    /// platform's, which is the same way a project-specific skill outranks a seeded one
    /// (<see cref="Point"/>) — an override is a thing somebody meant, whether it arrived as a fresh
    /// name or as an edit to one this method shipped first.
    /// </para>
    /// <para>
    /// Origin incident (2026-08-23): the pre-PR review of the project-home branch found a
    /// hand-written <c>~/.hall9k/skills/my-team-conventions</c> being recursively deleted by the
    /// next ordinary <c>h9k install</c>, with no prompt, no backup, and no line naming what went.
    /// The second cycle found the retiring half fixed and the publishing half still doing it to
    /// any hand-written skill whose name happened to collide with a shipped one. Cycle 2's
    /// adversarial pass then found a third shape of the same defect: a name recorded as published
    /// was treated as forever safe to overwrite even after an operator rewrote that skill's body in
    /// place, because the manifest recorded only which names this method had ever written, not
    /// whether the content it wrote was still there. The hash closes that gap — a name is safe to
    /// overwrite only while the directory still matches what was recorded for it.
    /// </para>
    /// </summary>
    public static SkillPublication PublishCanonical(string sourceDirectory)
    {
        string canonical = SkillLibraryPaths.CanonicalDirectory;
        Directory.CreateDirectory(canonical);

        IReadOnlyDictionary<string, string> previously = ReadManifest();

        List<string> published = [];
        List<string> leftAlone = [];
        Dictionary<string, string> hashes = [];
        foreach (string skill in Directory.EnumerateDirectories(sourceDirectory).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(skill, "SKILL.md")))
            {
                continue;
            }

            string name = Path.GetFileName(skill);
            string destination = Path.Combine(canonical, name);
            if (Directory.Exists(destination))
            {
                // Safe to overwrite only when this method published the name before *and* the
                // directory still hashes to exactly what it wrote — otherwise either nobody here
                // ever published it, or somebody has edited it since, and either way it is not
                // this pass's to touch. It stays out of the manifest too: a name recorded here is
                // a name a later install may delete or overwrite, and writing one in for content
                // this pass declined to touch would hand the operator's own work to that fate next
                // time.
                if (!previously.TryGetValue(name, out string? recordedHash)
                    || recordedHash != ComputeContentHash(destination))
                {
                    leftAlone.Add(name);
                    continue;
                }

                Directory.Delete(destination, recursive: true);
            }

            CopyDirectory(skill, destination);
            hashes[name] = ComputeContentHash(destination);
            published.Add(name);
        }

        List<string> retired = [];
        // A name this pass saw at all — published fresh or left alone as somebody's override — is
        // handled; only a name previously published and absent from source this time is a
        // candidate for retirement, and only while its content still matches what was recorded is
        // it this pass's to delete rather than somebody's edit of a skill the platform dropped.
        HashSet<string> handled = [.. published, .. leftAlone];
        foreach ((string name, string recordedHash) in previously.Where(entry => !handled.Contains(entry.Key)))
        {
            string directory = SkillLibraryPaths.Skill(name);
            if (!Directory.Exists(directory) || ComputeContentHash(directory) != recordedHash)
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
            retired.Add(name);
        }

        File.WriteAllLines(
            SkillLibraryPaths.PublishedManifest,
            published.Select(name => $"{name}\t{hashes[name]}"));
        return new SkillPublication(published, retired, leftAlone);
    }

    /// <summary>
    /// Removes exactly what the last <c>h9k install</c> published into the canonical skill set —
    /// <c>h9k uninstall</c>'s use of this class, and held to the identical "only what still
    /// matches the recorded hash is install's" discipline <see cref="PublishCanonical"/> already
    /// applies to its own retiring pass (see that method's origin incident, 2026-08-23): a
    /// hand-written skill was never in the manifest to begin with, and one an operator has
    /// edited since it was published no longer matches its recorded hash, so both are left
    /// alone here exactly as a later install would leave them. Returns the names actually
    /// removed. A locked file is recorded into <paramref name="stillPresent"/> rather than
    /// aborting the rest, the same best-effort discipline
    /// <see cref="Hall9k.Cli.Commands.UninstallCommand.RemoveInstallOwnedEntries"/> uses — and its
    /// entry stays in the manifest rather than being dropped with the rest, so a locked skill
    /// is still recognisable as install-owned on the next uninstall attempt (or the next
    /// install's own retiring pass) instead of being silently reclassified as an operator
    /// override once its lock clears.
    /// </summary>
    public static IReadOnlyList<string> RemovePublished(List<string> stillPresent)
    {
        IReadOnlyDictionary<string, string> previously = ReadManifest();
        List<string> removed = [];
        Dictionary<string, string> remaining = [];
        foreach ((string name, string recordedHash) in previously)
        {
            string directory = SkillLibraryPaths.Skill(name);
            if (!Directory.Exists(directory) || ComputeContentHash(directory) != recordedHash)
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
                remaining[name] = recordedHash;
            }
        }

        try
        {
            if (remaining.Count > 0)
            {
                File.WriteAllLines(
                    SkillLibraryPaths.PublishedManifest,
                    remaining.Select(entry => $"{entry.Key}\t{entry.Value}"));
            }
            else if (File.Exists(SkillLibraryPaths.PublishedManifest))
            {
                File.Delete(SkillLibraryPaths.PublishedManifest);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stillPresent.Add(SkillLibraryPaths.PublishedManifest);
        }

        string canonical = SkillLibraryPaths.CanonicalDirectory;
        if (Directory.Exists(canonical) && !Directory.EnumerateFileSystemEntries(canonical).Any())
        {
            try
            {
                Directory.Delete(canonical);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left as an empty directory; nothing install-owned remains inside it either way.
            }
        }

        return removed;
    }

    /// <summary>
    /// What the last install published here, by name, with a content hash of what it wrote — the
    /// discriminator <see cref="PublishCanonical"/> uses to tell "still exactly what this method
    /// shipped" apart from "somebody has edited this since". An absent manifest is read as empty,
    /// which is the safe direction: nothing is removed or overwritten on the pass that first writes
    /// it, so at worst a skill retired before this record existed lingers one install longer than
    /// it might have.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadManifest()
    {
        if (!File.Exists(SkillLibraryPaths.PublishedManifest))
        {
            return new Dictionary<string, string>();
        }

        Dictionary<string, string> manifest = [];
        foreach (string line in File.ReadAllLines(SkillLibraryPaths.PublishedManifest))
        {
            string[] parts = line.Split('\t', 2);
            if (parts is [{ } name, { } hash] && name.IsNotBlank())
            {
                manifest[name] = hash;
            }
        }

        return manifest;
    }

    /// <summary>
    /// A deterministic content hash of every file under <paramref name="directory"/>, relative path
    /// and bytes both folded in so a rename or a body edit both move the hash. What
    /// <see cref="PublishCanonical"/> compares against the manifest to tell its own unmodified
    /// output apart from an operator's edit to it.
    /// </summary>
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

    /// <summary>
    /// Removes a symlink without following it. <c>DirectoryInfo.Delete</c> resolves the link
    /// first and so throws on a dangling one — which is precisely the case this is used for —
    /// while <c>File.Delete</c> unlinks. Windows needs the other call for a directory reparse
    /// point, so both are here rather than one being assumed portable.
    /// </summary>
    private static void Unlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path);
        }
    }

    /// <summary>
    /// The fallback when a symlink is refused, and the publishing path <c>h9k install</c> uses
    /// to fill the canonical directory from the repository's own <c>.claude/skills</c>.
    /// </summary>
    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
