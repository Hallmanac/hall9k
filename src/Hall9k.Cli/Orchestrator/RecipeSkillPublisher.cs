using System.Security.Cryptography;
using System.Text;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Domain.Infrastructure.Storage;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// Publishes and seeds the one skill this platform ships into <c>recipes/</c> rather than
/// <c>skills/</c> (task: an operator starts a lean node or project orchestrator window):
/// <see cref="GeneratorSkillName"/>, which writes the actual recipe content the platform's own
/// <see cref="LaunchAnchorDocument"/> hands off to. Same path skills take today (canonical from
/// <c>.claude/skills</c> for <c>h9k install --repo</c>, a release payload's <c>skills/</c> for
/// <c>--from-release</c>/<c>h9k update</c>) and the same hash-manifest publish/shadow/retire
/// discipline <see cref="SkillSeeder.PublishCanonical"/> already uses for the ordinary skill set —
/// scoped to this one name, and writing into <see cref="RecipeLibraryPaths.CanonicalDirectory"/>
/// instead, because the generator belongs beside the anchor it writes recipes next to, not mixed
/// into the general-purpose skill set every build or review session also reads.
/// </summary>
public static class RecipeSkillPublisher
{
    public const string GeneratorSkillName = "orchestrator-recipe-generator";

    /// <summary>
    /// Fills <see cref="RecipeLibraryPaths.CanonicalDirectory"/> from <paramref name="sourceDirectory"/>
    /// (the same <c>.claude/skills</c>-shaped directory <see cref="SkillSeeder.PublishCanonical"/>
    /// reads), publishing, retiring, or reporting a shadow for exactly <see cref="GeneratorSkillName"/>.
    /// </summary>
    public static SkillPublication PublishCanonical(string sourceDirectory)
    {
        string canonical = RecipeLibraryPaths.CanonicalDirectory;
        Directory.CreateDirectory(canonical);

        (bool manifestConfirmed, string? recordedHash) = TryReadManifest();
        if (!manifestConfirmed)
        {
            return new SkillPublication([], [], [], ManifestUnconfirmed: true);
        }

        string skillSource = Path.Combine(sourceDirectory, GeneratorSkillName);
        string destination = Path.Combine(canonical, GeneratorSkillName);
        bool shipped = File.Exists(Path.Combine(skillSource, "SKILL.md"));

        if (!shipped)
        {
            if (recordedHash is not null && Directory.Exists(destination) && ComputeContentHash(destination) == recordedHash)
            {
                Directory.Delete(destination, recursive: true);
                File.Delete(RecipeLibraryPaths.PublishedManifest);
                return new SkillPublication([], [GeneratorSkillName], []);
            }

            return new SkillPublication([], [], []);
        }

        if (Directory.Exists(destination))
        {
            if (recordedHash is null || recordedHash != ComputeContentHash(destination))
            {
                return new SkillPublication([], [], [GeneratorSkillName]);
            }

            Directory.Delete(destination, recursive: true);
        }

        SkillSeeder.CopyDirectory(skillSource, destination);
        File.WriteAllText(RecipeLibraryPaths.PublishedManifest, $"{GeneratorSkillName}\t{ComputeContentHash(destination)}");
        return new SkillPublication([GeneratorSkillName], [], []);
    }

    /// <summary>
    /// Seeds <paramref name="home"/>'s <c>recipes/&lt;name&gt;</c> and <c>.claude/skills/&lt;name&gt;</c>
    /// from the node's own canonical copy, the same symlink-or-copy-with-marker discipline
    /// <see cref="SkillSeeder.Seed"/> uses for the ordinary skill set. Skipped honestly when the
    /// node has never published it (a fresh checkout before its first <c>h9k install --repo</c>).
    /// </summary>
    public static IReadOnlyList<ProjectHomeStep> Seed(string home)
    {
        string canonicalSkill = Path.Combine(RecipeLibraryPaths.CanonicalDirectory, GeneratorSkillName);
        if (!Directory.Exists(canonicalSkill))
        {
            return [ProjectHomeStep.Skipped(
                $"recipes/{GeneratorSkillName}/ not seeded: the install has not published it yet to "
                + $"{RecipeLibraryPaths.CanonicalDirectory}. Run h9k install, then h9k project init.")];
        }

        string link = ProjectHomePaths.RecipeSkillDirectory(home, GeneratorSkillName);
        string adapterDirectory = ProjectHomePaths.ClaudeSkillsDirectory(home);
        Directory.CreateDirectory(adapterDirectory);
        string adapterLink = Path.Combine(adapterDirectory, GeneratorSkillName);

        // Reported honestly rather than unconditionally (independent pre-PR review, cycle 1,
        // adversarial lens): Point declines to touch a real directory an operator wrote by hand,
        // and a green "seeded" line over that outcome would be exactly the guessed-at provenance
        // AGENTS.md's "never guess at unobserved facts" rule forbids.
        if (Point(link, canonicalSkill) == PointOutcome.LeftAlone)
        {
            return [ProjectHomeStep.Skipped(
                $"recipes/{GeneratorSkillName}/ left alone: a real directory is already there that this "
                + "did not seed.")];
        }

        if (Point(adapterLink, link) == PointOutcome.LeftAlone)
        {
            return [ProjectHomeStep.Created(
                $"recipes/{GeneratorSkillName}/ seeded from {RecipeLibraryPaths.CanonicalDirectory}, "
                + $".claude/skills/{GeneratorSkillName} left alone: a real directory is already there "
                + "that this did not seed.")];
        }

        return [ProjectHomeStep.Created(
            $"recipes/{GeneratorSkillName}/ seeded from {RecipeLibraryPaths.CanonicalDirectory}, "
            + $".claude/skills/{GeneratorSkillName} generated as its adapter")];
    }

    /// <summary>The identical seeding for the node's own <c>~/.hall9k/.claude/skills</c>, so a node orchestrator window can discover it too.</summary>
    public static void SeedNode()
    {
        string canonicalSkill = Path.Combine(RecipeLibraryPaths.CanonicalDirectory, GeneratorSkillName);
        if (!Directory.Exists(canonicalSkill))
        {
            return;
        }

        Directory.CreateDirectory(RecipeLibraryPaths.ClaudeSkillsDirectory);
        Point(Path.Combine(RecipeLibraryPaths.ClaudeSkillsDirectory, GeneratorSkillName), canonicalSkill);
    }

    /// <summary>
    /// Removes the <see cref="SeedNode"/> adapter link at
    /// <c>~/.hall9k/.claude/skills/orchestrator-recipe-generator</c>, so <c>h9k uninstall</c>
    /// does not leave a platform-authored symlink dangling once <see cref="RemovePublished"/>
    /// deletes what it points at. Only ever touches a symlink — a real directory there could
    /// only be an operator's own, and is left alone exactly like <see cref="Point"/> itself
    /// would leave it (independent pre-PR review, cycle 1, both lenses: this adapter had no
    /// removal counterpart at all before this).
    /// </summary>
    public static void RemoveNodeAdapter(List<string> stillPresent)
    {
        string link = Path.Combine(RecipeLibraryPaths.ClaudeSkillsDirectory, GeneratorSkillName);
        if (new DirectoryInfo(link).LinkTarget is null)
        {
            return;
        }

        try
        {
            SkillSeeder.Unlink(link);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stillPresent.Add(link);
        }
    }

    private enum PointOutcome
    {
        Linked,
        Copied,
        LeftAlone,
    }

    /// <summary>Makes <paramref name="link"/> point at <paramref name="target"/>, the same discipline <c>SkillSeeder</c>'s own private <c>Point</c> uses.</summary>
    private static PointOutcome Point(string link, string target)
    {
        FileSystemInfo entry = new DirectoryInfo(link);
        if (entry.LinkTarget is null && Directory.Exists(link))
        {
            if (!File.Exists(Path.Combine(link, SkillSeeder.CopyMarkerFile)))
            {
                return PointOutcome.LeftAlone;
            }

            Directory.Delete(link, recursive: true);
        }

        try
        {
            if (entry.LinkTarget is not null)
            {
                // SkillSeeder.Unlink, not a bare File.Delete: a directory reparse point on
                // Windows throws UnauthorizedAccessException from File.Delete, which used to
                // fall into the copy fallback below with link still resolving through the very
                // symlink this was trying to replace, corrupting a second Seed/SeedNode pass
                // (independent pre-PR review, cycle 1, adversarial lens).
                SkillSeeder.Unlink(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return PointOutcome.Linked;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            SkillSeeder.CopyDirectory(target, link);
            File.WriteAllText(Path.Combine(link, SkillSeeder.CopyMarkerFile), string.Empty);
            return PointOutcome.Copied;
        }
    }

    /// <summary>
    /// Removes the published copy at <see cref="RecipeLibraryPaths.CanonicalDirectory"/>, the
    /// <see cref="SkillSeeder.RemovePublished"/> discipline scoped to this one skill: an
    /// unmodified publish is deleted outright, an operator's own edit is left alone (recorded
    /// into <paramref name="stillPresent"/> so <c>h9k uninstall</c> does not claim a clean
    /// removal over it), and a manifest that cannot be confirmed leaves the whole directory
    /// untouched rather than guessed at (task: an operator starts a lean node or project
    /// orchestrator window — independent pre-PR review, cycle 1, both lenses: this skill had no
    /// removal counterpart at all before this).
    /// </summary>
    public static (IReadOnlyList<string> Removed, bool ManifestConfirmed) RemovePublished(List<string> stillPresent)
    {
        (bool manifestConfirmed, string? recordedHash) = TryReadManifest();
        if (!manifestConfirmed)
        {
            stillPresent.Add(RecipeLibraryPaths.PublishedManifest);
            return ([], false);
        }

        if (recordedHash is null)
        {
            return ([], true);
        }

        string directory = Path.Combine(RecipeLibraryPaths.CanonicalDirectory, GeneratorSkillName);
        if (!Directory.Exists(directory))
        {
            TryDeleteManifest(stillPresent);
            return ([], true);
        }

        string? currentHash = TryComputeContentHash(directory);
        if (currentHash is null)
        {
            // Unreadable, not confirmed unmodified — recorded as still present rather than
            // guessed at, the same discriminator SkillSeeder.RemovePublished uses for its own
            // identical read at the same point in h9k uninstall (bin/ and the PATH link already
            // gone by here, so this cannot afford to throw).
            stillPresent.Add(directory);
            return ([], true);
        }

        if (currentHash != recordedHash)
        {
            // An operator's own edit — left alone exactly like SkillSeeder.RemovePublished
            // leaves an edited ordinary skill alone.
            return ([], true);
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stillPresent.Add(directory);
            return ([], true);
        }

        TryDeleteManifest(stillPresent);
        return ([GeneratorSkillName], true);
    }

    private static void TryDeleteManifest(List<string> stillPresent)
    {
        try
        {
            File.Delete(RecipeLibraryPaths.PublishedManifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stillPresent.Add(RecipeLibraryPaths.PublishedManifest);
        }
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

    private static (bool Confirmed, string? Hash) TryReadManifest()
    {
        if (!File.Exists(RecipeLibraryPaths.PublishedManifest))
        {
            return (true, null);
        }

        try
        {
            string[] parts = File.ReadAllText(RecipeLibraryPaths.PublishedManifest).Split('\t', 2);
            return parts is [{ } name, { } hash] && name == GeneratorSkillName ? (true, hash) : (true, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

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
}
