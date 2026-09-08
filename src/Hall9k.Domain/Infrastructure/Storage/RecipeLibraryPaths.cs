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
/// Everything else in this folder — <see cref="OrchestratorRecipeFileName"/> and its siblings —
/// is written once by the <c>orchestrator-recipe-generator</c> skill and never touched by the
/// platform again; a re-run of the skill writes a <c>.new</c> file beside an existing one rather
/// than overwriting it (the anchor's own body states this rule so every window reads it fresh).
/// The journal is the one piece of a window's own state this type still names
/// (<see cref="JournalFile"/>), and it deliberately does not live in this folder: Brian's own
/// ruling (task: an operator starts a lean node or project orchestrator window, 2026-09-07 16:10
/// EDT) is one rule per folder — <c>recipes/</c> is the platform-owned launch folder <c>install</c>
/// and <c>init</c> overwrite and the anchor's own <c>.new</c> rule governs, while the journal
/// (and its sibling <c>sessions.md</c>, which no platform code path names) is the window's own
/// live state, seeded once by the generator and never regenerated. It sits at the home's own
/// root instead, beside <c>notes/</c>, exactly where the hand-written prototypes already kept it.
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

    /// <summary>The node's own open-loop state, at the node home's root — never under <see cref="CanonicalDirectory"/>; see this type's own doc for why.</summary>
    public static string JournalFile => Path.Combine(PlatformPaths.Home, JournalFileName);

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
