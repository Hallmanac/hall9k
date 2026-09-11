using System.Text;

namespace Hall9k.Domain.Infrastructure.Storage;

/// <summary>
/// Loads a prompt template's markdown body and substitutes its <c>{{Token}}</c> placeholders —
/// the one mechanism a prompt builder, the CLI, and the test suite all use to find template
/// prose, so where a template lives is never a second thing to keep in sync with the builder that
/// reads it.
/// <para>
/// Resolution order: this repository's own source copy under <c>.claude/templates</c> first, found
/// by walking up from the running assembly to the checkout root, when a checkout is present at all
/// (a dev loop, a <c>--repo</c> install, or the test suite reading its own checkout's prose); the
/// install's own canonical copy (<see cref="TemplateLibraryPaths.CanonicalDirectory"/>, what
/// <c>h9k install</c> publishes) only when running from an install with no checkout to read
/// instead, or when the checkout does not carry the template being asked for. The two are never
/// blended for a single template: resolution stops at the first directory that actually has the
/// file, so the test suite always reads the checkout's own prose rather than whatever a prior
/// install happened to publish to this machine.
/// </para>
/// <para>
/// A builder never hands a session a path into either copy — see this task's own turn-one-cost
/// rule: the prompt is assembled here, in full, before the session ever starts.
/// </para>
/// </summary>
public static class PromptTemplates
{
    /// <summary>
    /// Loads a template's substituted text, without appending it anywhere. When
    /// <paramref name="fragment"/> is given, a file holds several named fragments — one prompt
    /// section's whole family of small, closely-related prose variants (a sentence's ending
    /// under each branch of a switch, say) kept in one reviewable file instead of scattered across
    /// many single-line ones — delimited by a line reading exactly <c>===name===</c>; the fragment
    /// runs from its own marker to the next marker or the end of the file, with the single blank
    /// line an author leaves before the next marker for readability dropped from its end.
    /// </summary>
    public static string Load(string relativePath, string? fragment = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        string raw = File.ReadAllText(ResolvePath(relativePath));
        string text = fragment is null ? raw : ExtractFragment(raw, fragment, relativePath);
        if (parameters is null)
        {
            return text;
        }

        foreach ((string key, string value) in parameters)
        {
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Loads a template (or, with <paramref name="fragment"/>, one of its named fragments — see
    /// <see cref="Load"/>) and appends it line by line, exactly as a sequence of discrete
    /// <c>AppendLine</c> calls over the same static text would have — so a template's own line
    /// endings, whatever this checkout's <c>.gitattributes</c> normalizes them to, never leak into
    /// the assembled prompt in place of <see cref="Environment.NewLine"/>. A single trailing
    /// newline (the normal shape of a saved text file, or of a fragment's own blank separator
    /// line) is treated as the file's own terminator, not an extra blank line.
    /// </summary>
    public static void AppendTemplate(
        StringBuilder builder, string relativePath, string? fragment = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        string text = Load(relativePath, fragment, parameters).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = text.Split('\n');
        int count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        for (int index = 0; index < count; index++)
        {
            builder.AppendLine(lines[index]);
        }
    }

    private static string ExtractFragment(string raw, string fragment, string relativePath)
    {
        string[] lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string marker = $"==={fragment}===";
        int start = Array.IndexOf(lines, marker);
        if (start < 0)
        {
            throw new FileNotFoundException(
                $"No fragment '{fragment}' (looked for a line reading '{marker}') in template {relativePath}.",
                relativePath);
        }

        int end = lines.Length;
        for (int index = start + 1; index < lines.Length; index++)
        {
            if (IsFragmentMarker(lines[index]))
            {
                end = index;
                break;
            }
        }

        List<string> body = lines[(start + 1)..end].ToList();
        if (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join('\n', body);
    }

    /// <summary>
    /// A fragment terminator line: <c>===</c>, a non-empty name that is not itself all <c>=</c>
    /// characters, then <c>===</c>. The plain <c>StartsWith("===") &amp;&amp; EndsWith("===")</c>
    /// this replaced also matched a bare Markdown setext heading underline (<c>===</c> on its own
    /// line, or any longer run of just <c>=</c>) — content a fragment's own body is free to carry —
    /// which would have cut the fragment short at that line instead of treating it as prose
    /// (independent pre-PR review, cycle 1).
    /// </summary>
    private static bool IsFragmentMarker(string line)
    {
        if (line.Length < 7
            || !line.StartsWith("===", StringComparison.Ordinal)
            || !line.EndsWith("===", StringComparison.Ordinal))
        {
            return false;
        }

        string middle = line[3..^3];
        return middle.Any(character => character != '=');
    }

    private static string ResolvePath(string relativePath)
    {
        if (FindRepositoryRoot(AppContext.BaseDirectory) is { } repoRoot)
        {
            string source = Path.Combine(repoRoot, ".claude", "templates", relativePath);
            if (File.Exists(source))
            {
                return source;
            }
        }

        string canonical = Path.Combine(TemplateLibraryPaths.CanonicalDirectory, relativePath);
        if (File.Exists(canonical))
        {
            return canonical;
        }

        throw new FileNotFoundException(
            $"No prompt template found for '{relativePath}' — checked this checkout's .claude/templates "
            + "(the source copy, preferred when a checkout is present) and " + canonical + " (the install's "
            + "own canonical copy, checked when running from an install with no checkout to read instead).",
            relativePath);
    }

    /// <summary>Walks up from a running assembly's own directory to the checkout that carries <c>Hall9k.slnx</c>.</summary>
    private static string? FindRepositoryRoot(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hall9k.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
