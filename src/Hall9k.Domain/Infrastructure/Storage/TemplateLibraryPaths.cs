namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The install's canonical prompt-template set: <c>~/.hall9k/templates</c>, a sibling of
/// <see cref="SkillLibraryPaths.CanonicalDirectory"/> rather than a member of it — published by
/// <c>h9k install</c> under the identical hash-manifest discipline <see cref="SkillLibraryPaths"/>
/// already uses, but never seeded into a project home, never carrying a <c>SKILL.md</c>, and
/// never appearing in any skill discovery a session's own prompt renders.
/// <para>
/// A template holds the judgment-layer prose a prompt builder assembles into an agent session's
/// prompt — readable, diffable, and overridable exactly like a skill is — while the parsed
/// contracts the daemon depends on (a finding's line grammar, a verdict's vocabulary, a handoff's
/// marker) stay fixed in code. Doctrine: Hall9k is prescriptive about the lifecycle and permissive
/// about judgment; templates and skills are the judgment side.
/// </para>
/// </summary>
public static class TemplateLibraryPaths
{
    public static string CanonicalDirectory => Path.Combine(PlatformPaths.Home, "templates");

    /// <summary>
    /// What the last publish wrote into <see cref="CanonicalDirectory"/>, by name with a content
    /// hash — the same discriminator <see cref="SkillLibraryPaths.PublishedManifest"/> uses, kept
    /// as its own file here because this canonical set and the ordinary skill set are two
    /// different directories with two different owners of what is "install's to overwrite".
    /// </summary>
    public static string PublishedManifest => Path.Combine(CanonicalDirectory, ".published");
}
