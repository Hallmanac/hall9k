using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// Computes the executable surfaces a pull request's diff touches (idea 6be68ee2, finding 1,
/// phase one, the acceptance criterion's own list) as a pure function over the changed-file list
/// <c>gh pr diff --name-only</c> reports — no I/O, no classification. This list points a
/// pre-flight session's attention at what is worth reading closely; it is not itself a security
/// verdict, and an empty match never skips the session — the acceptance criterion is explicit that
/// a safe verdict is a layer, never the lock.
/// <para>
/// A pattern is either a basename glob (no directory component, or the <c>**/</c> gitignore
/// idiom for "any depth"), matched against the file's own basename, or a directory-anchored glob
/// (<c>dir/**</c>), matched against the full repository-relative path — the same two shapes
/// <c>NonExecutablePathClassifier</c>'s own rule set distinguishes, kept independent here since
/// this list is fixed by the acceptance criterion rather than project-configurable.
/// </para>
/// </summary>
public static class PrReviewPreflightSurfaceMatcher
{
    /// <summary>
    /// The fixed surface list (idea 6be68ee2, finding 1's own acceptance criterion): every changed
    /// path touching one of these is worth a pre-flight session's attention. Fixed rather than
    /// project-configurable, unlike <c>ProjectAggregate.EffectiveNonExecutablePaths</c> — this is a
    /// pointer into a diff, not a safety classification a project can widen or narrow.
    /// </summary>
    public static readonly IReadOnlyList<string> Patterns =
    [
        ".github/**",
        "**/*.csproj",
        "**/*.props",
        "**/*.targets",
        "Directory.Build.*",
        "Directory.Packages.props",
        "global.json",
        "nuget.config",
        "package.json",
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "scripts/**",
        "install.*",
        ".githooks/**",
        "Makefile",
        "Dockerfile*",
        "*compose*.y*ml",
        ".claude/**",
        "CLAUDE.md",
        "AGENTS.md",
        ".gitattributes",
        ".gitmodules",
    ];

    private static readonly IReadOnlyList<Regex> BasenamePatterns =
    [
        .. Patterns
            .Where(pattern => !pattern.Contains('/') || pattern.StartsWith("**/", StringComparison.Ordinal))
            .Select(pattern => pattern.StartsWith("**/", StringComparison.Ordinal) ? pattern[3..] : pattern)
            .Select(Compile),
    ];

    private static readonly IReadOnlyList<Regex> PathPatterns =
    [
        .. Patterns
            .Where(pattern => pattern.Contains('/') && !pattern.StartsWith("**/", StringComparison.Ordinal))
            .Select(Compile),
    ];

    /// <summary>
    /// The subset of <paramref name="changedFiles"/> that touches one of <see cref="Patterns"/>,
    /// in the order given, never invented or reordered — this is a filter, not a summary.
    /// </summary>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> changedFiles)
    {
        List<string> matched = [];
        foreach (string path in changedFiles)
        {
            string normalized = path.Replace('\\', '/');
            string basename = normalized.Contains('/') ? normalized[(normalized.LastIndexOf('/') + 1)..] : normalized;
            if (BasenamePatterns.Any(pattern => pattern.IsMatch(basename))
                || PathPatterns.Any(pattern => pattern.IsMatch(normalized)))
            {
                matched.Add(path);
            }
        }

        return matched;
    }

    private static Regex Compile(string pattern)
    {
        StringBuilder builder = new("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                builder.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                builder.Append("[^/]*");
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
