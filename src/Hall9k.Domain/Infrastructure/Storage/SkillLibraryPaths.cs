namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The install's canonical skill set: <c>~/.hall9k/skills</c>, published by <c>h9k install</c>
/// beside the binaries it publishes.
/// <para>
/// The ruling behind it (IDEA-skill-layer, Tension 8): a skill the machinery requires in order
/// to work is part of the machinery, so it ships with the install and is as self-contained as
/// <c>h9k</c> itself. The source is this repository's own <c>.claude/skills</c> for <c>h9k
/// install --repo</c>, or a release payload's bundled <c>skills/</c> for <c>h9k install
/// --from-release</c> and <c>h9k update</c> (backlog 42) — either way only the publishing half
/// changes; this path is the seam, and it does not.
/// </para>
/// <para>
/// A project's <c>skills/</c> is seeded from here by symlink, so republishing the install
/// updates every project's platform skills in one move rather than leaving N stale copies.
/// </para>
/// </summary>
public static class SkillLibraryPaths
{
    public static string CanonicalDirectory => Path.Combine(PlatformPaths.Home, "skills");

    /// <summary>One canonical skill's directory; the manifest inside it is <c>SKILL.md</c>.</summary>
    public static string Skill(string name) => Path.Combine(CanonicalDirectory, name);

    /// <summary>
    /// What the last <c>h9k install</c> published here, one name per line. It is the record that
    /// separates a skill the install owns from one somebody put here by hand, which is the only
    /// way the publishing pass can retire the former without deleting the latter. A file rather
    /// than an inference: nothing about a directory's contents says who created it.
    /// </summary>
    public static string PublishedManifest => Path.Combine(CanonicalDirectory, ".published");

    /// <summary>
    /// The skills actually published here, by name, in a stable order. Empty when the install
    /// has never published any — which is a fact worth reporting rather than a failure, since a
    /// project home is still perfectly usable with nothing seeded into it.
    /// </summary>
    public static IReadOnlyList<string> Published() =>
        Directory.Exists(CanonicalDirectory)
            ? [.. Directory.EnumerateDirectories(CanonicalDirectory)
                .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)]
            : [];
}
