using System.Text;
using System.Text.RegularExpressions;

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
/// <c>h9k install</c> publishes) next, when running from an install with no checkout to read
/// instead, or when the checkout does not carry the template being asked for; then a
/// <c>templates</c> directory beside the running binary itself, a one-time bridge for an install
/// whose first <c>h9k update</c> onto this feature ran under the OLD binary and so never published
/// the canonical copy at all. None of the three are ever blended for a single template: resolution
/// stops at the first directory that actually has the file, so the test suite always reads the
/// checkout's own prose rather than whatever a prior install happened to publish to this machine.
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
        return parameters is null ? text : SubstitutePlaceholders(text, parameters);
    }

    /// <summary>
    /// Every <c>{{Key}}</c> placeholder replaced in a single pass over <paramref name="text"/>,
    /// rather than one <c>Replace</c> call per parameter run back to back — the one-at-a-time
    /// shape let an earlier substitution's own VALUE (relayed, externally authored text a caller
    /// never controls, such as a pull request's head branch name) accidentally match a LATER
    /// key's placeholder syntax and get rewritten a second time. A placeholder naming a key not in
    /// <paramref name="parameters"/> is left exactly as written, the same as the old code left it
    /// untouched (independent pre-PR review, cycle 2).
    /// </summary>
    private static string SubstitutePlaceholders(string text, IReadOnlyDictionary<string, string> parameters) =>
        Regex.Replace(
            text,
            "{{(?<key>[A-Za-z0-9_]+)}}",
            match => parameters.TryGetValue(match.Groups["key"].Value, out string? value) ? value : match.Value);

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
        (int start, int end, string[] lines) = LocateFragment(raw, fragment, relativePath);
        List<string> body = lines[(start + 1)..end].ToList();
        if (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join('\n', body);
    }

    /// <summary>
    /// Every named fragment marker in <paramref name="raw"/>, in file order — the same
    /// <c>===name===</c> syntax <see cref="Load"/> parses a single one of. Lets a caller (namely
    /// <see cref="Hall9k.Cli.Prompts.TemplatePublisher"/>'s fill-missing-files pass) tell whether a
    /// canonical revision added a fragment to a file an operator's own override already carries,
    /// without duplicating the marker syntax here.
    /// </summary>
    public static IReadOnlyList<string> FragmentNames(string raw)
    {
        string[] lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return [.. lines.Where(IsFragmentMarker).Select(line => line[3..^3])];
    }

    /// <summary>
    /// One named fragment's marker line together with its body, verbatim, from the marker to the
    /// next marker or the end of <paramref name="raw"/> — unlike <see cref="Load"/>, the marker
    /// line itself is kept and no trailing blank line is dropped, so the result can be appended
    /// straight onto a file that already ends in one.
    /// </summary>
    public static string ExtractFragmentBlock(string raw, string fragment, string relativePath)
    {
        (int start, int end, string[] lines) = LocateFragment(raw, fragment, relativePath);
        return string.Join('\n', lines[start..end]);
    }

    private static (int Start, int End, string[] Lines) LocateFragment(string raw, string fragment, string relativePath)
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

        return (start, end, lines);
    }

    /// <summary>
    /// A fragment terminator line: <c>===</c>, a non-empty name that is not itself all <c>=</c>
    /// characters, then <c>===</c>. The plain <c>StartsWith("===") &amp;&amp; EndsWith("===")</c>
    /// this replaced also matched a bare Markdown setext heading underline (<c>===</c> on its own
    /// line, or any longer run of just <c>=</c>) — content a fragment's own body is free to carry —
    /// which would have cut the fragment short at that line instead of treating it as prose
    /// (independent pre-PR review, cycle 1).
    /// <para>
    /// Internal rather than private so <c>PromptTemplateContractTests</c> can skip these lines
    /// before scanning a template for a banned contract token (independent pre-PR review, cycle 1,
    /// conformance finding): a marker line is never rendered into an assembled prompt — <see cref="Load"/>
    /// strips it before a fragment's body is used at all — so an incidental substring collision
    /// between a hyphenated fragment name and a tag key (<c>===discovery-lap-scope===</c> containing
    /// <c>scope=</c>) is not a real violation the way the same text sitting in a fragment's own body
    /// would be.
    /// </para>
    /// </summary>
    internal static bool IsFragmentMarker(string line)
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

        // Bridges the one-time transition an install still carrying a pre-templates binary hits:
        // that old h9k runs h9k update itself, so it is the OLD FinishAsync — with no
        // PublishTemplates call — that finishes the swap, and the old StageFromRelease has no
        // "templates" entry in its own skip list, so the payload's templates/ lands beside the
        // fresh binaries at ~/.hall9k/bin/templates instead of at the canonical directory above.
        // The new h9k that just got swapped into place runs from that same bin directory, so
        // checking beside itself finds exactly what that old update run left there, without
        // requiring the operator to know to run install or update a second time (independent
        // pre-PR review, cycle 1, high).
        string besideBinary = Path.Combine(AppContext.BaseDirectory, "templates", relativePath);
        if (File.Exists(besideBinary))
        {
            return besideBinary;
        }

        throw new FileNotFoundException(
            $"No prompt template found for '{relativePath}' — checked this checkout's .claude/templates "
            + "(the source copy, preferred when a checkout is present), " + canonical + " (the install's "
            + "own canonical copy, checked when running from an install with no checkout to read instead), "
            + "and " + besideBinary + " (a same-directory fallback for a pre-templates install's first "
            + "h9k update). Run h9k update (or h9k install --repo/--from-release) again to publish the "
            + "canonical copy.",
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
