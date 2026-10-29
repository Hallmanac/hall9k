namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The shape of a solution's thin home on a node: <c>~/.hall9k/solutions/&lt;name&gt;</c>. The path
/// is a convention derived from the solution's current name, never a recorded setting, so nothing
/// in the store has to carry it and every node that holds the solution lays it out identically.
/// <para>
/// It is deliberately thinner than <see cref="ProjectHomePaths"/>: a generated <c>AGENTS.md</c>,
/// <c>skills/</c>, <c>recipes/</c> and the <c>.claude/</c> adapter, and no <c>repo/</c>,
/// <c>ideas/</c> or <c>tasks/</c>, because ideas stay in member projects and tasks are cut into a
/// member. The file helpers on <see cref="ProjectHomePaths"/> all take a bare home path, so a
/// solution home reuses them for the anchor, the settings file and the skill directories rather
/// than naming them a second time.
/// </para>
/// </summary>
public static class SolutionHomePaths
{
    /// <summary>Every solution home lives under here.</summary>
    public static string SolutionsRoot => Path.Combine(PlatformPaths.Home, "solutions");

    /// <summary>Where the home of the solution called <paramref name="solutionName"/> goes: its name reduced to a directory-safe slug.</summary>
    public static string DefaultFor(string solutionName) => Path.Combine(SolutionsRoot, ProjectHomePaths.Slug(solutionName, maxLength: 64));

    /// <summary>Every directory the thin shape is made of, in creation order.</summary>
    public static IReadOnlyList<string> Directories(string home) =>
    [
        home,
        ProjectHomePaths.SkillsDirectory(home),
        ProjectHomePaths.ClaudeDirectory(home),
        ProjectHomePaths.ClaudeSkillsDirectory(home),
        ProjectHomePaths.RecipesDirectory(home),
    ];

    /// <summary>
    /// The solution homes that exist on this node, as directories directly under
    /// <see cref="SolutionsRoot"/>. Found by listing the directory, so a home the registry no
    /// longer names (a renamed solution's old path) is still seen.
    /// </summary>
    public static IReadOnlyList<string> Existing() =>
        Directory.Exists(SolutionsRoot)
            ? [.. Directory.EnumerateDirectories(SolutionsRoot).Order(StringComparer.Ordinal)]
            : [];
}
