namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The node's own orchestrator recipe folder: <c>~/.hall9k/recipes</c> (task: an operator starts
/// a lean node or project orchestrator window). A project home's <see cref="ProjectHomePaths"/>
/// has the identical shape at <c>&lt;home&gt;/recipes</c>; the file names are shared here so
/// neither copy can name them differently.
/// <para>
/// Two files in this folder are platform-owned and overwritten outright on every
/// <c>h9k install</c>, <c>h9k update</c>, or project-home render: <see cref="LaunchAnchorFileName"/>
/// (the tiny file every launch line appends as the window's system prompt) and
/// <see cref="SettingsFileName"/> (the generated settings the launch line rides on with
/// <c>--settings</c> — command-line scope, never <c>.claude/settings.json</c>, because
/// <c>crossSessionInbound</c> is a security exception honored only when a project or local value
/// is stricter than user scope, and <c>--setting-sources project</c> drops user scope entirely).
/// Everything else here — <see cref="OrchestratorRecipeFileName"/> and its siblings, the journal —
/// is written once by the <c>orchestrator-recipe-generator</c> skill and never touched by the
/// platform again; a re-run of the skill writes a <c>.new</c> file beside an existing one rather
/// than overwriting it (the anchor's own body states this rule so every window reads it fresh).
/// </para>
/// </summary>
public static class RecipeLibraryPaths
{
    public const string LaunchAnchorFileName = "launch-anchor.md";
    public const string SettingsFileName = "settings.json";
    public const string OrchestratorRecipeFileName = "orchestrator.md";
    public const string JournalFileName = "journal.md";

    public static string CanonicalDirectory => Path.Combine(PlatformPaths.Home, "recipes");

    public static string LaunchAnchorFile => Path.Combine(CanonicalDirectory, LaunchAnchorFileName);

    public static string SettingsFile => Path.Combine(CanonicalDirectory, SettingsFileName);

    public static string OrchestratorRecipeFile => Path.Combine(CanonicalDirectory, OrchestratorRecipeFileName);

    public static string JournalFile => Path.Combine(CanonicalDirectory, JournalFileName);

    /// <summary>
    /// What the last publish wrote into <see cref="CanonicalDirectory"/>, by name with a content
    /// hash — the same discriminator <c>SkillLibraryPaths.PublishedManifest</c> uses, kept as its
    /// own file here because this canonical set and the ordinary skill set are two different
    /// directories with two different owners of what is "install's to overwrite".
    /// </summary>
    public static string PublishedManifest => Path.Combine(CanonicalDirectory, ".published");

    /// <summary>
    /// The node's own vendor adapter, the exact shape <c>ProjectHomePaths.ClaudeDirectory</c>/
    /// <c>ClaudeSkillsDirectory</c> already give a project home — the node orchestrator window
    /// runs with <c>~/.hall9k</c> as its working directory and needs the identical discovery path
    /// for the orchestrator-recipe-generator skill.
    /// </summary>
    public static string ClaudeDirectory => Path.Combine(PlatformPaths.Home, ".claude");

    public static string ClaudeSkillsDirectory => Path.Combine(ClaudeDirectory, "skills");
}
