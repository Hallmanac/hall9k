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

        Point(link, canonicalSkill);
        Point(adapterLink, link);

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

    /// <summary>Makes <paramref name="link"/> point at <paramref name="target"/>, the same discipline <c>SkillSeeder</c>'s own private <c>Point</c> uses.</summary>
    private static void Point(string link, string target)
    {
        FileSystemInfo entry = new DirectoryInfo(link);
        if (entry.LinkTarget is null && Directory.Exists(link))
        {
            if (!File.Exists(Path.Combine(link, SkillSeeder.CopyMarkerFile)))
            {
                return;
            }

            Directory.Delete(link, recursive: true);
        }

        try
        {
            if (entry.LinkTarget is not null)
            {
                File.Delete(link);
            }

            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            SkillSeeder.CopyDirectory(target, link);
            File.WriteAllText(Path.Combine(link, SkillSeeder.CopyMarkerFile), string.Empty);
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
