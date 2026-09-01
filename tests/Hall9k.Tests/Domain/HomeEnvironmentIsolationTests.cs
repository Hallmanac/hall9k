using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// PlatformPaths.Home resolves the process-wide HALL9K_HOME environment variable on every call,
/// and xUnit runs distinct collections in parallel within the one test process — so any test
/// that sets HALL9K_HOME (the h9k-update scratch-home tests, chief among them) races any test
/// that resolves a path built on it, unless both share one xUnit collection, which xUnit runs
/// serially within itself. That collection is "Hall9kHome" (see e.g. <see
/// cref="Hall9k.Tests.Cli.UpdateCommandTests"/>), and this test is the guard that keeps every
/// class touching that surface inside it: it scans this project's own sources for the risky
/// members (<c>Environment.GetEnvironmentVariable</c>/<c>SetEnvironmentVariable</c>,
/// <c>PlatformPaths.Home</c>, every <c>RunPaths</c> member that reads it, and the other
/// production accessors that resolve <c>PlatformPaths.Home</c> transitively, directly or through
/// another such accessor —
/// <c>ProjectHomePaths.ProjectsRoot</c>, <c>ProjectHomePaths.DefaultFor</c>,
/// <c>SkillLibraryPaths.CanonicalDirectory</c>,
/// <c>CredentialVault.Directory</c> and the members that compose on it this list can express
/// (<c>FileFor</c>, <c>StoreAsync</c>, <c>Holds</c>, <c>Discard</c> — <c>ResolveAsync</c> also
/// composes on <c>FileFor</c>, transitively through <c>FromFileAsync</c>, but as an instance
/// member its call sites read <c>CredentialVault.Default.ResolveAsync(…)</c>, a shape this list's
/// <c>Type.Member</c> text scheme cannot itself name, so it is not listed here — see the coverage
/// note below),
/// <c>PostgresRuntime.ComposeDirectory</c>,
/// <c>IdeaPaths.GlobalDirectory</c>, <c>Hall9kDatabase.ConfigFile</c>,
/// <c>PlatformConfigFile.ReadOperatingSettingsAsync</c>,
/// <c>PlatformConfigFile.TryReadOperatingSettingsAsync</c>,
/// <c>PlatformConfigFile.WriteOperatingSettingsAsync</c>,
/// <c>WindowsDaemonAutostart.TaskXmlContent</c>, and every
/// <c>DaemonRuntime</c> member that reads <c>RunPaths.Root</c> — see <see
/// cref="RiskyMembers"/>) and fails the build for any CLASS that uses one without itself
/// carrying <c>[Collection("Hall9kHome")]</c>, rather than trusting every future test author to
/// remember the rule. Origin: <c>RunPathsTests.With_no_home_a_new_run_falls_back_to_the_platform_global_location</c>
/// intermittently observed an <c>h9k-update-*</c> scratch home under parallel execution — gate
/// strikes on 34a618a6 (2026-08-29) and cea5ae6e (2026-08-30) — because it lived outside this
/// collection while <c>UpdateCommandTests</c> mutated HALL9K_HOME from inside it.
/// <para>
/// The check is per CLASS, not per file (<see cref="FindClassFrames"/>): xUnit does not inherit
/// <c>[Collection]</c> from a containing type, so a nested class racing HALL9K_HOME needs its
/// own attribute even when the file's outer class already carries one — see
/// <c>RunPathsTests</c>'s nested <c>ResolveCurrentDirectoryTests</c> and
/// <c>AnticipateDirectoryAfterSweepTests</c>, which carry none because, unlike their enclosing
/// class, neither reads <c>PlatformPaths.Home</c>.
/// </para>
/// <para>
/// The scan strips comments and string literals before matching (see
/// <see cref="TestSourceTree.StripCommentsAndStrings"/>) — <c>ReviewVerdictValidationTests</c> is dense with
/// <c>InlineData</c> fixtures whose prose literally quotes <c>PlatformPaths.Home</c> and
/// <c>RunPaths.Root</c> as example finding text, without either ever being called from that
/// file. A naive substring search over the raw source would falsely conscript it into this
/// collection for content that never touches the environment.
/// </para>
/// <para>
/// This is a per-CLASS check on that class's own source text, which is where it stops: a future
/// refactor that pulls a repeated risky call out into a shared helper or base class (a
/// <c>TemporaryHome</c> fixture, say) moves the requirement onto a type with no test methods of
/// its own, where <c>[Collection]</c> has no runtime effect — every class that then merely *uses*
/// that helper carries no risky-member text and so needs no attribute as far as this scan can
/// tell, even though it still races HALL9K_HOME through the helper exactly as before. Extracting
/// a risky call this way needs a human to re-derive the collection requirement for every caller;
/// this guard cannot follow it there. The same blind spot reaches ordinary production code, not
/// only extracted test helpers: <c>WindowsDaemonAutostart.TaskXmlContent</c> raced undetected
/// until review named it explicitly, because it reached <c>RunPaths.Root</c> through a private
/// property this list had not yet enumerated. An instance member composing on an already-listed
/// static one is the same gap in a different shape — <c>CredentialVault.ResolveAsync</c> reaches
/// the listed <c>FileFor</c>, but a class calling only <c>ResolveAsync</c> carries none of this
/// list's text and passes the guard unflagged; there is no live miss today because every current
/// caller already carries the attribute for an independent reason, but a future one would not be
/// caught here.
/// </para>
/// </summary>
public sealed class HomeEnvironmentIsolationTests
{
    private const string SelfFileName = "HomeEnvironmentIsolationTests.cs";
    private const string CollectionAttribute = "[Collection(\"Hall9kHome\")]";

    private static readonly string[] RiskyMembers =
    [
        "Environment.SetEnvironmentVariable",
        "Environment.GetEnvironmentVariable",
        "PlatformPaths.Home",
        "RunPaths.Root",
        "RunPaths.GlobalDirectory",
        "RunPaths.ResolveDirectory(",
        // The rest resolve PlatformPaths.Home transitively and take no home parameter of their
        // own to redirect instead, so reaching HALL9K_HOME through one of these is just as racy
        // as calling PlatformPaths.Home directly. This list is bounded, not provably closed: a
        // grep of src/ for "PlatformPaths." finds every accessor that reads it *directly*, but
        // not one that reaches it through another accessor already on this list (as
        // ProjectHomePaths.DefaultFor reaches it through ProjectsRoot, and every DaemonRuntime
        // member below reaches it through RunPaths.Root) — that chain can run arbitrarily deep,
        // so each entry here was found by reading callers of an already-listed member, not by a
        // search guaranteed to terminate. Cycle-2 review found the seven below unlisted; treat a
        // future one found the same way as a gap in this list, not a false alarm.
        "ProjectHomePaths.ProjectsRoot",
        "ProjectHomePaths.DefaultFor",
        "SkillLibraryPaths.CanonicalDirectory",
        "SkillLibraryPaths.Skill(",
        "SkillLibraryPaths.PublishedManifest",
        "SkillLibraryPaths.Published(",
        "CredentialVault.Directory",
        "CredentialVault.FileFor(",
        "CredentialVault.StoreAsync(",
        "CredentialVault.Holds(",
        "CredentialVault.Discard(",
        "PostgresRuntime.ComposeDirectory",
        "PostgresRuntime.ComposeFile",
        "PostgresRuntime.WriteComposeFile(",
        "IdeaPaths.GlobalDirectory",
        "IdeaPaths.ResolveDirectory(",
        "Hall9kDatabase.ConfigFile",
        "Hall9kDatabase.Resolve",
        "Hall9kDatabase.ConnectionStringStateAndValueInConfigFile",
        "Hall9kDatabase.WriteConfiguredConnectionStringAsync",
        "PlatformConfigFile.ReadOperatingSettingsAsync",
        "PlatformConfigFile.TryReadOperatingSettingsAsync",
        "PlatformConfigFile.WriteOperatingSettingsAsync",
        "DaemonRuntime.BinDirectory",
        "DaemonRuntime.StagingBinDirectory",
        "DaemonRuntime.LogFile",
        "DaemonRuntime.PidFile",
        "DaemonRuntime.LockFile",
        "DaemonRuntime.StopRequestFile",
        // Reaches PlatformPaths.Home through RunPaths.Root by way of the private
        // LaunchScriptFile property, not through anything already on this list — a production
        // accessor found the same way as everything above, not a test-side helper.
        "WindowsDaemonAutostart.TaskXmlContent(",
    ];

    private static readonly Regex ClassDeclaration = new(
        @"^[ \t]*(?:(?:public|private|internal|protected|sealed|abstract|static|partial|new|unsafe)\s+)*(?<kw>class)\s+(?<name>\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Anchored to a line start so a comment or doc comment that merely quotes the attribute text
    // (e.g. "// needs no [Collection(\"Hall9kHome\")] of its own") is never mistaken for a real
    // one: a "//" or "///" line never matches this, only a line whose first non-blank characters
    // are the attribute itself. FindClassFrames matches this against raw source, not the
    // comment/string-stripped code — stripping drops string literal content entirely, which would
    // erase the very "Hall9kHome" text this regex needs to see — but then requires the matched
    // line's own first character (attributeMatch.Index, which the ^[ \t]*\[... pattern anchors to
    // wherever the line's leading whitespace begins, not to the "[" itself) to have survived
    // stripping into real code, so a raw or verbatim string literal that quotes the attribute at a
    // line start (e.g. a const fixture whose text is itself a code sample) still cannot credit a
    // class that carries no real attribute: that line sits inside the literal's skipped interior
    // and never reaches the stripped output.
    private static readonly Regex CollectionAttributeLine = new(
        @"^[ \t]*\[Collection\(""Hall9kHome""\)\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Every_test_class_touching_the_platform_home_environment_shares_the_serialized_collection()
    {
        string testsDirectory = TestSourceTree.RootDirectory();

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(Path.GetFileName(file), SelfFileName, StringComparison.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        List<string> offenders = [];
        int riskyMemberHits = 0;

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);

            (IEnumerable<string> classOffenders, string code) = ClassesUsingRiskyMembersWithoutTheCollection(source);

            foreach (string className in classOffenders)
            {
                offenders.Add($"{Path.GetRelativePath(testsDirectory, file)} -> {className}");
            }

            // Counted over the stripped code, not the raw source: this floor exists to catch the
            // scan going dark (StripCommentsAndStrings regressing to return empty or truncated
            // code, which would make ClassesUsingRiskyMembersWithoutTheCollection above find
            // nothing to search), and counting the raw source instead would make this floor stay
            // comfortably clear even while that exact failure mode was happening.
            foreach (string member in RiskyMembers)
            {
                riskyMemberHits += CountOccurrences(code, member);
            }
        }

        offenders.Should().BeEmpty(
            "every test class that sets or resolves a HALL9K_HOME-derived path races every other " +
            $"one unless it shares the {CollectionAttribute} xUnit collection — xUnit does not " +
            "inherit [Collection] from a containing type, so a nested class needs its own " +
            "attribute even when its enclosing class already carries one; add it directly above " +
            "the offending class (see RunPathsTests or UpdateCommandTests) rather than special-casing it here");

        // A floor on what the scan actually saw, so this test can pass green while checking
        // nothing — TestSourceTree.RootDirectory() no longer resolving to tests/Hall9k.Tests, or
        // the whole scan going dark some other way — turns into a failing assertion here instead of a guard that
        // reports success while protecting nothing. This is a wholesale-breakage check, not a
        // per-entry one: renaming any single RiskyMembers entry (or the production member it
        // names) still leaves the aggregate count comfortably clear of the floor, since it is the
        // total across every entry, not any one entry's own count. The floors sit well below
        // today's actual counts (well over a hundred files, several hundred hits) so ordinary
        // test-tree growth or shrinkage never brushes them.
        files.Length.Should().BeGreaterThan(
            100,
            "this is far fewer .cs files than the test tree actually holds — TestSourceTree.RootDirectory() is " +
            "probably no longer resolving to tests/Hall9k.Tests");

        riskyMemberHits.Should().BeGreaterThan(
            100,
            "this is far fewer risky-member hits than the test tree actually contains — the scan " +
            "itself is probably broken (TestSourceTree.RootDirectory() misresolving, or the whole RiskyMembers " +
            "list gone stale at once) rather than working as intended; a single renamed entry can " +
            "still leave this floor comfortably clear, so this only catches wholesale breakage");
    }

    private static int CountOccurrences(string source, string member)
    {
        int count = 0;
        int start = 0;

        while (IndexOfMemberBoundary(source, member, start) is int index)
        {
            count++;
            start = index + member.Length;
        }

        return count;
    }

    /// <summary>
    /// Same as <see cref="string.IndexOf(string, int, StringComparison)"/> except a match is
    /// rejected when the character right after it is itself an identifier character — otherwise a
    /// bare-property entry like <c>PostgresRuntime.ComposeFile</c> also matches inside
    /// <c>PostgresRuntime.ComposeFileContents</c>, a plain string constant that never resolves
    /// <c>PlatformPaths.Home</c>, and needlessly conscripts any class that reads only the
    /// contents constant. A rejected match still advances the search by one character rather than
    /// by the whole member length, so a shorter real match starting inside the false one is not
    /// skipped over. The check applies only when <paramref name="member"/> is a bare name: a
    /// call-shaped entry like <c>CredentialVault.Discard(</c> already ends in <c>(</c>, which is
    /// itself an unambiguous boundary — the character after the match is the call's first
    /// argument, not more of the member name — so applying the identifier check there would
    /// reject a real call whose first argument starts with an identifier character (e.g.
    /// <c>Discard(reference)</c>), which is most of them. Cycle-2 review found this: it silently
    /// dropped ten call-shaped entries down to matching only zero-argument calls.
    /// </summary>
    private static int? IndexOfMemberBoundary(string source, string member, int start)
    {
        bool memberEndsAtCall = member.Length > 0 && member[^1] == '(';

        int index;
        while ((index = source.IndexOf(member, start, StringComparison.Ordinal)) >= 0)
        {
            int after = index + member.Length;
            if (memberEndsAtCall || after >= source.Length || !IsIdentifierChar(source[after]))
            {
                return index;
            }

            start = index + 1;
        }

        return null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The names of every class in <paramref name="source"/> whose body uses a member from
    /// <see cref="RiskyMembers"/> without that same class carrying <see cref="CollectionAttribute"/>,
    /// alongside the comment/string-stripped code the scan searched — the caller's own risky-member
    /// floor counts hits over this same stripped text, since counting the raw source instead would
    /// leave the floor comfortably clear even if the scan itself had gone dark.
    /// <see cref="TestSourceTree.StripCommentsAndStrings"/> is a heuristic, not a parser, and a
    /// source shape it desyncs on drops coverage silently unless something notices — so a
    /// risky-member hit that lands inside no class frame at all, or a file whose stripped brace
    /// depth never returns to zero, is itself reported as an offender (a synthetic one, not a
    /// real class name) rather than dropped: the origin cycle-2 review found exactly this gap in
    /// a multi-line interpolation-hole file whose desync silently excused it from the guard
    /// entirely.
    /// </summary>
    private static (IEnumerable<string> Offenders, string Code) ClassesUsingRiskyMembersWithoutTheCollection(string source)
    {
        (string code, int[] originalIndex, bool balanced) = TestSourceTree.StripCommentsAndStrings(source);
        List<ClassFrame> frames = FindClassFrames(code, originalIndex, source);

        HashSet<string> offenders = [];

        if (!balanced)
        {
            offenders.Add(
                "<StripCommentsAndStrings desynced on this file: stripped brace depth never " +
                "returned to zero, so class boundaries here cannot be trusted>");
        }

        foreach (string member in RiskyMembers)
        {
            int start = 0;
            while (IndexOfMemberBoundary(code, member, start) is int index)
            {
                ClassFrame? frame = InnermostFrame(frames, index);
                if (frame is null)
                {
                    offenders.Add(
                        $"<'{member}' at stripped offset {index} landed inside no class frame — " +
                        "either it sits in a non-class top-level type (a record, struct, or " +
                        "interface, none of which FindClassFrames tracks) that needs its own " +
                        "place to carry [Collection(\"Hall9kHome\")], or StripCommentsAndStrings " +
                        "or FindClassFrames desynced on this file>");
                }
                else if (!frame.HasAttribute)
                {
                    offenders.Add(frame.Name);
                }

                start = index + member.Length;
            }
        }

        return (offenders, code);
    }

    private sealed record ClassFrame(string Name, int BodyStart, int BodyEnd, bool HasAttribute);

    private static ClassFrame? InnermostFrame(List<ClassFrame> frames, int codeIndex)
    {
        ClassFrame? best = null;

        foreach (ClassFrame frame in frames)
        {
            bool contains = codeIndex >= frame.BodyStart && codeIndex < frame.BodyEnd;
            bool narrower = best is null || frame.BodyEnd - frame.BodyStart < best.BodyEnd - best.BodyStart;

            if (contains && narrower)
            {
                best = frame;
            }
        }

        return best;
    }

    /// <summary>
    /// Walks <paramref name="code"/> (already comment/string stripped, so every brace is a real
    /// one) tracking brace depth to find each class's own body range and whether that specific
    /// class — not just the file somewhere — carries <see cref="CollectionAttribute"/>
    /// immediately above its declaration. A class declaration is recognised at the start of a
    /// line (this project's own formatting always puts one there), which is also what keeps a
    /// generic constraint like <c>where T : class</c> — "class" mid-line, not a declaration —
    /// from being mistaken for one.
    /// <para>
    /// The attribute search window for a class at brace depth <c>d</c> runs from the end of the
    /// previous sibling declaration at that same depth (or the enclosing scope's own opening
    /// brace, for the first child; or file start, at depth 0) up to the class keyword itself —
    /// tracked in <c>lastBoundaryAtDepth</c> in <paramref name="originalIndex"/>'s coordinates and
    /// updated every time a <c>}</c> returns to depth <c>d</c> — so an outer class's own attribute
    /// is never mistaken for a nested or sibling class's. The window is matched against
    /// <paramref name="source"/>, not <paramref name="code"/> (see <see
    /// cref="CollectionAttributeLine"/>), with the surviving code positions derived from
    /// <paramref name="originalIndex"/> as the guard against a match landing inside a comment or
    /// string literal that the stripped text would have hidden: a real attribute's own opening
    /// <c>[</c> always survives stripping, while one quoted inside a string or comment never does.
    /// </para>
    /// </summary>
    private static List<ClassFrame> FindClassFrames(string code, int[] originalIndex, string source)
    {
        HashSet<int> codePositions = [.. originalIndex];
        List<ClassFrame> frames = [];
        Stack<(string Name, bool HasAttribute, int BodyDepth, int BodyStart)> open = [];
        Dictionary<int, int> lastBoundaryAtDepth = new() { [0] = 0 };
        Match[] declarations = [.. ClassDeclaration.Matches(code).Cast<Match>()];
        int nextDeclaration = 0;
        (string Name, bool HasAttribute)? armed = null;
        int depth = 0;

        for (int i = 0; i < code.Length; i++)
        {
            if (armed is null && nextDeclaration < declarations.Length
                && declarations[nextDeclaration].Groups["kw"].Index == i)
            {
                Match match = declarations[nextDeclaration];
                nextDeclaration++;

                int windowStart = lastBoundaryAtDepth.GetValueOrDefault(depth, 0);
                int keywordOriginalIndex = originalIndex[i];
                string window = source.Substring(windowStart, keywordOriginalIndex - windowStart);
                bool hasAttribute = CollectionAttributeLine.Matches(window).Any(
                    attributeMatch => codePositions.Contains(windowStart + attributeMatch.Index));

                armed = (match.Groups["name"].Value, hasAttribute);
            }

            char c = code[i];

            if (c == '{')
            {
                depth++;
                if (armed is { } pendingClass)
                {
                    open.Push((pendingClass.Name, pendingClass.HasAttribute, depth, i + 1));
                    armed = null;
                }

                lastBoundaryAtDepth[depth] = originalIndex[i] + 1;
                continue;
            }

            if (c == '}')
            {
                if (open.Count > 0 && open.Peek().BodyDepth == depth)
                {
                    (string name, bool hasAttribute, _, int bodyStart) = open.Pop();
                    frames.Add(new ClassFrame(name, bodyStart, i, hasAttribute));
                }

                depth--;
                lastBoundaryAtDepth[depth] = originalIndex[i] + 1;
                continue;
            }

            // A body-less class declaration ("sealed class Foo;", valid C# and already in use at
            // CommandTreeHelpTests.cs) has no '{' to disarm it: without this, `armed` stays set
            // past the declaration's own terminator, the file's next unrelated '{' is misread as
            // that class's body, and — since disarming only happens inside the `armed is null`
            // branch above — every declaration after it is silently skipped for the rest of the
            // file. It has no body a risky-member call could land in, so clearing `armed` here
            // without emitting a frame is correct: nothing is lost, only the dangling state.
            if (c == ';' && armed is not null)
            {
                armed = null;
            }
        }

        return frames;
    }

}
