using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Domain.Features.Project;

/// <summary>
/// Classifies every path a run's branch changed against its base — deletions and renames
/// included, both sides of a rename or copy recorded separately — against a project's
/// non-executable-path rule set (task: a delivered diff that touches no buildable or testable
/// source skips the build and test gates). A pure function over a path list and a rule list, the
/// same "read what's actually there, never guess" discipline the daemon's own
/// <c>TestScopeResolver</c> applies to test selection: nothing here reads git or the filesystem,
/// so <c>VerificationRunner</c> alone decides which paths and which rules this classifies.
/// </summary>
public static class NonExecutablePathClassifier
{
    /// <summary>One changed path and the rule it matched, or null when nothing in the rule set matched it.</summary>
    public readonly record struct ClassifiedPath(string Path, string? MatchedRule);

    /// <summary>
    /// <see cref="AllMatched"/> is true only when every changed path matched some rule AND at
    /// least one path was actually classified — an empty <paramref name="Paths"/> list is never
    /// read as "vacuously all non-executable", since a run with nothing changed never reaches this
    /// classifier at all (VerificationRunner's own pre-gate stranded-work check already fails a
    /// branch with zero commits before any gate, this one included).
    /// </summary>
    public readonly record struct ClassificationResult(bool AllMatched, IReadOnlyList<ClassifiedPath> Paths);

    public static ClassificationResult Classify(IReadOnlyList<string> changedPaths, IReadOnlyList<string> rules)
    {
        List<ClassifiedPath> classified = [];
        bool allMatched = changedPaths.Count > 0;
        foreach (string path in changedPaths)
        {
            string? matchedRule = MatchRule(path, rules);
            classified.Add(new ClassifiedPath(path, matchedRule));
            if (matchedRule is null)
            {
                allMatched = false;
            }
        }

        return new ClassificationResult(allMatched, classified);
    }

    /// <summary>
    /// A rule prefixed with <c>!</c> (<see cref="NonExecutablePathDefaults.Rules"/>'s own
    /// <c>!.claude/templates/</c>) is an exclusion, not another kind of match: it names a path that
    /// LOOKS non-executable under some other rule here but is actually tested content, and it wins
    /// over every ordinary rule regardless of which one comes first in the list (independent pre-PR
    /// review, cycle 1, adversarial lens, high — the bare <c>*.md</c> default used to swallow
    /// <c>.claude/templates/**/*.md</c>, which `AgentPromptBuilderGoldenTests` and
    /// `PromptTemplateContractTests` actually check, and skip the very gates that would catch a
    /// broken template edit). Checked against every rule up front, independent of iteration order,
    /// so an exclusion can never lose to a same-path positive rule purely because of list position.
    /// </summary>
    private static string? MatchRule(string path, IReadOnlyList<string> rules)
    {
        if (rules.Any(rule => rule.StartsWith('!') && Matches(path, rule[1..])))
        {
            return null;
        }

        foreach (string rule in rules)
        {
            if (!rule.StartsWith('!') && Matches(path, rule))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>
    /// A rule ending in <c>/</c> is a whole-directory match (<c>docs/</c>, <c>.claude/skills/</c>):
    /// any path starting with it, at any depth, matches. A rule with no <c>/</c> at all
    /// (<c>*.md</c>) is a filename glob — the .gitignore convention of matching a bare pattern
    /// against the file's own name at any depth, not just the repository root, which is what lets
    /// one default rule cover a markdown file anywhere in the tree. A rule that carries a
    /// <c>/</c> but does not end in one (<c>assets/**/*.png</c>) is a full-path glob evaluated
    /// against the whole relative path instead.
    /// </summary>
    internal static bool Matches(string path, string rule)
    {
        string normalizedPath = path.Replace('\\', '/');
        if (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        if (rule.EndsWith('/'))
        {
            return normalizedPath.StartsWith(rule, StringComparison.Ordinal);
        }

        string candidate = rule.Contains('/') ? normalizedPath : FileName(normalizedPath);
        return CompileGlob(rule).IsMatch(candidate);
    }

    private static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// <c>**</c> matches any run of characters including path separators, a single <c>*</c> stops
    /// at a <c>/</c>, and <c>?</c> matches exactly one non-separator character — the same three
    /// wildcards every shell glob supports, translated to an anchored regex. Compiled fresh per
    /// call rather than cached: the rule lists this classifier ever sees are the compiled
    /// defaults plus a handful of project additions, called once per gate entry, not a hot path.
    /// </summary>
    private static Regex CompileGlob(string glob)
    {
        StringBuilder pattern = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString());
    }
}
