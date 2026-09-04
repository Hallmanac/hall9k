using System.Text;

namespace Hall9k.Tests.Domain;

/// <summary>
/// Shared by every source-scanning guard test — <see cref="Hall9k.Tests.Domain.ContainerRoutingGuardTests"/>,
/// <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>,
/// <see cref="Hall9k.Tests.Domain.ProcessTerminationGuardTests"/> and
/// <see cref="Hall9k.Tests.Domain.NodeBootstrapConventionGuardTests"/> — each walks a whole tree
/// from its own file's location: <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>
/// alone still walks this test project only; <see cref="Hall9k.Tests.Domain.ProcessTerminationGuardTests"/>
/// walks both <c>src/</c> and the whole <c>tests/</c> directory (decision #110 widened its scan
/// to close a gap the narrower scan left open); <see cref="Hall9k.Tests.Domain.ContainerRoutingGuardTests"/>
/// and <see cref="Hall9k.Tests.Domain.NodeBootstrapConventionGuardTests"/> each walk the whole
/// <c>tests/</c> directory for the same reason (decision #130 widened
/// <see cref="Hall9k.Tests.Domain.ContainerRoutingGuardTests"/>'s own scan the identical way).
/// Each needs to tell a real source file from build output, and each strips comments and string
/// literals before matching so quoted prose cannot be mistaken for real code.
/// <para>
/// <see cref="Hall9k.Tests.Domain.DecisionsLogNumberingGuardTests"/> is a fifth consumer, and
/// keeps this list from being "every tree-walking guard" alone: it uses <see cref="SourceDirectory"/>
/// only, to resolve the repository root and locate PLAN.md, rather than walking a tree of source
/// files, so it needs neither <see cref="IsBuildOutput"/> nor
/// <see cref="StripCommentsAndStrings"/>. This is now the full list of consumers and is meant to
/// be kept current whenever a new one is added.
/// </para>
/// </summary>
internal static class TestSourceTree
{
    /// <summary>
    /// The <c>tests/Hall9k.Tests</c> root, found by walking up from the caller's own file path
    /// until a directory literally named <c>Hall9k.Tests</c> is reached — rather than a hardcoded
    /// relative segment count, which would be correct only for a caller at the one depth it was
    /// tuned for — so it stays correct regardless of which guard's file calls it or how deep that
    /// file sits under <c>tests/Hall9k.Tests</c>.
    /// </summary>
    public static string RootDirectory([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        string? directory = Path.GetDirectoryName(here);

        while (directory is not null && !string.Equals(Path.GetFileName(directory), "Hall9k.Tests", StringComparison.Ordinal))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException(
            $"could not find a 'Hall9k.Tests' ancestor directory above caller path '{here}'");
    }

    /// <summary>
    /// The repository's <c>src</c> directory, found by continuing <see cref="RootDirectory"/>'s
    /// own walk upward from <c>tests/Hall9k.Tests</c> until an ancestor holding a <c>src</c>
    /// child is reached — rather than a hardcoded relative ascent from the test project, which
    /// would be correct only for the one nesting depth it was tuned for — so a guard scanning
    /// production source (<see cref="Hall9k.Tests.Domain.ProcessTerminationGuardTests"/>) stays
    /// correct if the test project is ever moved deeper or shallower under <c>tests/</c>.
    /// <para>
    /// The nearest such ancestor wins, which is this repository's own root; an outer directory
    /// that happened to hold a <c>src</c> of its own is never reached, because the walk stops
    /// before it. If none is found at all, that is reported as the walk failing rather than as a
    /// missing directory, since the ascent is the part a layout change breaks first.
    /// </para>
    /// </summary>
    public static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        string testsRoot = RootDirectory(here);
        string? directory = testsRoot;

        while (directory is not null && !Directory.Exists(Path.Combine(directory, "src")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory is null
            ? throw new InvalidOperationException(
                $"walked up from '{testsRoot}' to the filesystem root without finding a directory "
                + "holding a 'src' child — the repository layout this guard resolves against has changed")
            : Path.Combine(directory, "src");
    }

    /// <summary>
    /// True for a file under a <c>bin</c> or <c>obj</c> build-output directory, which
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>'s recursive search
    /// otherwise walks right along with the real sources — generated files there
    /// (<c>*.GlobalUsings.g.cs</c>, <c>*.AssemblyInfo.cs</c>, and the like) would make a scan's
    /// file and hit counts depend on which configurations happen to be built locally.
    /// </summary>
    /// <param name="rootDirectory">The tree being scanned — this test project for
    /// <see cref="HomeEnvironmentIsolationTests"/> alone, the whole <c>tests/</c> directory for
    /// <see cref="ContainerRoutingGuardTests"/> (since decision #130) and
    /// <see cref="NodeBootstrapConventionGuardTests"/>, or <c>src/</c> and (separately)
    /// <c>tests/</c> for <see cref="ProcessTerminationGuardTests"/>, which since decision #110
    /// scans both, one call each.</param>
    /// <param name="file">A file found under <paramref name="rootDirectory"/>.</param>
    public static bool IsBuildOutput(string rootDirectory, string file)
    {
        string relative = Path.GetRelativePath(rootDirectory, file);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.Ordinal) ||
            string.Equals(segment, "obj", StringComparison.Ordinal));
    }

    /// <summary>
    /// Blanks out comment and string-literal content so a source-text scan for API usage cannot
    /// be fooled by prose that merely quotes the API's name, and returns alongside it a map from
    /// each surviving character's index in the returned text back to its index in
    /// <paramref name="source"/> (comments and string bodies contribute no output characters, so
    /// the two texts are not the same length), plus whether the stripped text's own brace count
    /// balanced back to zero — a signal a caller matching class/brace structure against the
    /// result should check, since an unbalanced result means every structural boundary read from
    /// it downstream is unreliable. A caller that only needs the stripped text itself (a flat
    /// marker search with no structural matching) can ignore <c>OriginalIndex</c> and
    /// <c>Balanced</c> and use <c>Code</c> alone.
    /// <para>
    /// Handles block comments, line and doc comments, regular string literals (closed on the same
    /// physical line, which C# guarantees), verbatim string literals (which, unlike regular
    /// string literals, may span multiple lines), character literals, triple-quote-or-wider raw
    /// string literals (which may span several lines), and interpolated string literals
    /// (<c>$"..."</c>): an interpolation hole (<c>{Expr}</c>) is real code, not text, so
    /// <see cref="ScanCode"/> scans it exactly as it does the rest of the file — nested braces,
    /// nested string/char literals and a nested interpolated string all included — rather than
    /// treating it as inert. This is what closes a real-marker-inside-a-hole gap: a hole whose
    /// argument list ran onto a second physical line would desync a text-only handling of
    /// <c>$"</c> that stopped at the interpolation's opening quote instead of scanning its hole,
    /// which read that second line as bare code and lost a brace, silently dropping guard
    /// coverage for the rest of the file with no signal.
    /// </para>
    /// <para>
    /// This is still a heuristic tuned to this project's actual source, not a C# parser: a
    /// verbatim-interpolated literal opened as <c>$@"</c> is handled imprecisely but silently —
    /// it falls through to the plain verbatim-string path (<see cref="SkipVerbatimString"/>), so
    /// the whole literal is skipped as one atomic unit, no interpolation hole inside it is ever
    /// scanned as code, and no partial brace ever reaches the stripped output, so the stripped
    /// brace count still balances to zero and the miss stays silent. The other spelling,
    /// <c>@$"</c>, is not atomic the same way: the leading <c>@</c> falls through as an ordinary
    /// code character and the <c>$"</c> that follows sends the rest of the literal through
    /// <see cref="ScanInterpolatedString"/>, which does scan its interpolation holes as real code
    /// but assumes backslash escaping (verbatim's own doubled-quote escaping confuses it) and
    /// bails at the literal's first newline — so a single-line <c>@$"</c> usage closes correctly,
    /// while a multi-line one desyncs the scan for the rest of the file, which the balance check
    /// above can catch when the desync leaves a brace unmatched, and cannot when it does not. A
    /// raw interpolated literal (<c>$"""..."""</c>) is treated as an inert raw string, holes
    /// included. None of these shapes currently combines with any risky member or container
    /// marker, so none causes a false result today, but a future file that does would need this
    /// taught the combined form.
    /// </para>
    /// </summary>
    public static (string Code, int[] OriginalIndex, bool Balanced) StripCommentsAndStrings(string source)
    {
        StringBuilder result = new(source.Length);
        List<int> originalIndex = new(source.Length);

        ScanCode(source, 0, result, originalIndex, isHole: false);

        string code = result.ToString();
        int braceBalance = 0;
        foreach (char c in code)
        {
            braceBalance += c switch { '{' => 1, '}' => -1, _ => 0 };
        }

        return (code, [.. originalIndex], braceBalance == 0);
    }

    /// <summary>
    /// Scans real code starting at <paramref name="start"/>, appending every surviving character
    /// (with its original-source index) to <paramref name="result"/>/<paramref name="originalIndex"/>
    /// and skipping comments and string/char literal content exactly as the top-level scan does.
    /// Doubles as the interpolation-hole scanner: when <paramref name="isHole"/> is <c>true</c>,
    /// <paramref name="start"/> must point just past the hole's opening <c>{</c> (already
    /// appended by <see cref="ScanInterpolatedString"/>), brace depth starts at 1 to account for
    /// it, and this returns the index just past the hole's matching <c>}</c> the moment depth
    /// returns to 0 rather than running to the end of <paramref name="source"/> — which is how a
    /// hole containing its own nested braces, strings, or interpolated strings still closes at
    /// the right place instead of over- or under-consuming the file.
    /// </summary>
    private static int ScanCode(string source, int start, StringBuilder result, List<int> originalIndex, bool isHole)
    {
        int i = start;
        int holeDepth = isHole ? 1 : 0;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
                continue;
            }

            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                i = SkipRawString(source, i);
                continue;
            }

            if (c == '$' && i + 1 < source.Length && source[i + 1] == '"'
                && !(i + 3 < source.Length && source[i + 2] == '"' && source[i + 3] == '"'))
            {
                i = ScanInterpolatedString(source, i + 1, result, originalIndex);
                continue;
            }

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i = SkipVerbatimString(source, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(source, i);
                continue;
            }

            if (c == '"')
            {
                i = SkipRegularString(source, i);
                continue;
            }

            if (isHole && c == '{')
            {
                holeDepth++;
            }
            else if (isHole && c == '}')
            {
                holeDepth--;
            }

            result.Append(c);
            originalIndex.Add(i);
            i++;

            if (isHole && holeDepth == 0)
            {
                return i;
            }
        }

        return i;
    }

    /// <summary>
    /// Scans a non-raw interpolated string body starting at its opening quote
    /// (<paramref name="quoteIndex"/>): literal text is discarded exactly like a regular string's
    /// body, an escaped brace (<c>{{</c>/<c>}}</c>) stays literal text, and an unescaped
    /// <c>{</c> hands off to <see cref="ScanCode"/> as real code — which is what lets a hole's
    /// argument list run onto a second physical line without desyncing the scan. Returns the
    /// index just past the closing quote.
    /// </summary>
    private static int ScanInterpolatedString(string source, int quoteIndex, StringBuilder result, List<int> originalIndex)
    {
        int i = quoteIndex + 1;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\\' && i + 1 < source.Length)
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                return i + 1;
            }

            if (c == '\n')
            {
                return i;
            }

            if (c == '{' && i + 1 < source.Length && source[i + 1] == '{')
            {
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < source.Length && source[i + 1] == '}')
            {
                i += 2;
                continue;
            }

            if (c == '{')
            {
                result.Append('{');
                originalIndex.Add(i);
                i = ScanCode(source, i + 1, result, originalIndex, isHole: true);
                continue;
            }

            i++;
        }

        return i;
    }

    private static int SkipRawString(string source, int i)
    {
        int quoteRun = 0;
        while (i + quoteRun < source.Length && source[i + quoteRun] == '"')
        {
            quoteRun++;
        }

        string delimiter = new('"', quoteRun);
        int end = source.IndexOf(delimiter, i + quoteRun, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + quoteRun;
    }

    private static int SkipVerbatimString(string source, int i)
    {
        int j = i + 2;
        while (j < source.Length)
        {
            if (source[j] == '"')
            {
                if (j + 1 < source.Length && source[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return j;
    }

    private static int SkipCharLiteral(string source, int i)
    {
        int j = i + 1;
        while (j < source.Length && source[j] != '\'' && source[j] != '\n')
        {
            j += source[j] == '\\' && j + 1 < source.Length ? 2 : 1;
        }

        return j < source.Length && source[j] == '\'' ? j + 1 : j;
    }

    private static int SkipRegularString(string source, int i)
    {
        int j = i + 1;
        while (j < source.Length && source[j] != '"' && source[j] != '\n')
        {
            j += source[j] == '\\' && j + 1 < source.Length ? 2 : 1;
        }

        return j < source.Length && source[j] == '"' ? j + 1 : j;
    }
}
