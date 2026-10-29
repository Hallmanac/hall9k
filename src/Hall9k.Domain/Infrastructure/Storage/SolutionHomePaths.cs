using Hall9k.Domain.Features.Project.Rendering;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// The shape of a solution's thin home on a node: <c>~/.hall9k/solutions/&lt;name&gt;</c>. The path
/// is a convention, never a recorded setting, so nothing in the store has to carry it: a home is
/// made at the slug of the solution's name and afterwards found by the solution key its
/// <c>AGENTS.md</c> names (<see cref="For"/>), so a rename does not strand it.
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

    /// <summary>Where a new home for the solution called <paramref name="solutionName"/> goes: its name reduced to a directory-safe slug.</summary>
    public static string DefaultFor(string solutionName) => Path.Combine(SolutionsRoot, ProjectHomePaths.Slug(solutionName, maxLength: 64));

    /// <summary>
    /// The home of the solution with <paramref name="key"/> on this node, which every command and
    /// the daemon's render sweep go through rather than <see cref="DefaultFor"/>. The slug of a
    /// name is lossy and a rename changes it, so the home is found by the key its
    /// <c>AGENTS.md</c> names: a renamed solution keeps the directory it was given, and the
    /// solution that does not hold a directory never reads another's.
    /// <para>
    /// With no home naming the key, the answer is where one would be made: the slug of
    /// <paramref name="solutionName"/>, unless another solution's render already sits there (two
    /// names slugging alike), in which case the slug carries a suffix from the key.
    /// </para>
    /// </summary>
    public static string For(string solutionName, string key)
    {
        foreach (string existing in Existing())
        {
            if (string.Equals(SolutionAgentsDocument.RenderedKeyIn(existing), key, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        string preferred = DefaultFor(solutionName);
        return SolutionAgentsDocument.RenderedKeyIn(preferred) is not null
            ? Path.Combine(SolutionsRoot, $"{ProjectHomePaths.Slug(solutionName, maxLength: 50)}-{KeySuffix(key)}")
            : preferred;
    }

    private static string KeySuffix(string key) =>
        ProjectHomePaths.Slug(key.Length > 8 ? key[^8..] : key);

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
