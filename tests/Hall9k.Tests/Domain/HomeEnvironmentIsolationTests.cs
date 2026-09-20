using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// <see cref="Hall9k.Domain.Infrastructure.Storage.PlatformPaths.Home"/> and
/// <see cref="Hall9k.Domain.Infrastructure.Persistence.Hall9kDatabase.Resolve"/> each consult a
/// flow-scoped <see cref="AsyncLocal{T}"/> override before their own process-wide environment
/// variable (<c>HALL9K_HOME</c>, <c>HALL9K_CONNECTION_STRING</c>), and
/// <c>Hall9k.Tests.TestSupport.ScopedTestHome</c>/<c>ScopedConnectionString</c> are the one place
/// that ever opens either override — so, unlike before this seam existed, two tests that each
/// redirect their own home or connection string never race each other, and a class that only
/// <em>reads</em> one of those two production members needs no serialization at all. What can
/// still race is a literal write of the real environment variable itself — genuinely process-wide,
/// with no flow-scoped alternative — which is what this guard's first fact still polices, narrowed
/// from the old wide "every class that reads a home-derived path" scan down to exactly that.
/// <para>
/// Two rules, over this whole test project:
/// </para>
/// <para>
/// 1. <see cref="Every_direct_write_of_home_or_connection_string_sits_in_the_environment_collection"/> —
/// a class that still calls <c>Environment.SetEnvironmentVariable</c> naming <c>HALL9K_HOME</c> or
/// <c>HALL9K_CONNECTION_STRING</c> directly (rather than going through
/// <c>ScopedTestHome</c>/<c>ScopedConnectionString</c>) races every other one unless both carry
/// <c>[Collection("Environment")]</c>, the one collection left for a process-wide environment
/// variable — a test never sets
/// either variable itself except to hand <c>HALL9K_HOME</c> to a real child process, which still
/// needs the literal environment variable, since a child inherits its parent's environment, not
/// its parent's <see cref="AsyncLocal{T}"/> state.
/// </para>
/// <para>
/// 2. <see cref="No_scope_is_opened_inside_an_async_lifetime_method"/> — a class that constructs
/// <c>ScopedTestHome</c> or <c>ScopedConnectionString</c> inside an <c>async Task
/// InitializeAsync()</c> (xUnit's <c>IAsyncLifetime</c> method) opens a scope that is already gone
/// by the time the test method itself runs: <c>await</c> unwinds the
/// <see cref="ExecutionContext"/> a callee mutated back to what the caller held once that callee's
/// own task completes, so an override set inside <c>InitializeAsync</c> never reaches the test xUnit
/// invokes afterward. Open it from a constructor or a field initializer instead — a plain
/// synchronous call, which mutates the *same* context the test method goes on to run in, rather
/// than a separately awaited one. See <c>ScopedTestHome</c>'s own doc comment for the precise
/// mechanism.
/// </para>
/// <para>
/// Both facts are per-CLASS, not per-file: xUnit does not inherit <c>[Collection]</c> from a
/// containing type, so a nested class needs its own attribute even when its enclosing class
/// already carries one. Both match the call itself against comment/string-stripped code (see
/// <see cref="TestSourceTree.StripCommentsAndStrings"/>), so prose that merely quotes one of
/// these members is never mistaken for a real call; rule 1 alone then reads its own argument back
/// out of the raw, unstripped source, deliberately, since stripping removes a string literal's
/// content entirely and a stripped argument could never be compared against
/// <see cref="HomeOrConnectionStringArguments"/> at all.
/// </para>
/// </summary>
public sealed class HomeEnvironmentIsolationTests
{
    private const string SelfFileName = "HomeEnvironmentIsolationTests.cs";
    private const string CollectionAttribute = "[Collection(\"Environment\")]";
    private const string TraitAttribute = """[Trait("Category", "Environment")]""";

    // Deliberately narrow, unlike the old RiskyMembers list this guard replaces: with the
    // flow-scoped seam in place, a *read* of PlatformPaths.Home or Hall9kDatabase.Resolve can
    // never observe another test's redirected value (each lives on that test's own AsyncLocal
    // flow), so only a literal write of the real environment variable is still genuinely
    // process-wide and worth catching here.
    //
    // The call itself, unquoted, is what gets matched against the comment/string-stripped code —
    // its argument (what distinguishes a HALL9K_HOME/HALL9K_CONNECTION_STRING write from any
    // other Environment.SetEnvironmentVariable call) is a string literal or a dotted member
    // reference, and StripCommentsAndStrings removes string literal *content* entirely, so a
    // marker embedding one (as an earlier draft of this guard did) can never match the stripped
    // text at all — it would silently never fire. Matching the call alone in stripped code, then
    // reading its raw, unstripped argument text directly (see the scan below), is what actually
    // works for both a quoted argument and a member reference.
    private const string SetEnvironmentVariableCall = "Environment.SetEnvironmentVariable(";

    private static readonly string[] HomeOrConnectionStringArguments =
    [
        "\"HALL9K_HOME\"",
        "\"HALL9K_CONNECTION_STRING\"",
        "Hall9kDatabase.EnvironmentVariableName",
    ];

    // Two shapes construct either scope type, and a class inside InitializeAsync can reach for
    // either one: the explicitly-typed "new ScopedTestHome(" form, and the target-typed
    // "ScopedTestHome ident = new(" idiom this project's own README teaches and the large
    // majority of real call sites actually use ("private readonly ScopedTestHome _scopedHome =
    // new();", "using ScopedConnectionString scope = new(postgres.ConnectionString);"). Matching
    // only the first form left this guard blind to the idiom nearly every class in the tree
    // relies on — see the two positive controls below, one per shape, so neither can go quietly
    // dark again.
    private static readonly Regex[] ScopeConstructionPatterns =
    [
        new(@"\bnew\s+ScopedTestHome\s*\(", RegexOptions.Compiled),
        new(@"\bnew\s+ScopedConnectionString\s*\(", RegexOptions.Compiled),
        new(@"\bScopedTestHome\s+\w+\s*=\s*new\s*\(", RegexOptions.Compiled),
        new(@"\bScopedConnectionString\s+\w+\s*=\s*new\s*\(", RegexOptions.Compiled),
    ];

    private static readonly Regex ClassDeclaration = new(
        @"^[ \t]*(?:(?:public|private|internal|protected|sealed|abstract|static|partial|new|unsafe)\s+)*(?<kw>class)\s+(?<name>\w+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Same anchoring rationale as the guard this replaces: a "//"/"///" line quoting the attribute
    // never matches, only a line whose first non-blank characters are the attribute itself, and the
    // match is required to have survived comment/string stripping into real code (see
    // FindClassFrames below) so a string literal that merely quotes the attribute cannot credit a
    // class that carries no real one.
    private static readonly Regex CollectionAttributeLine = new(
        @"^[ \t]*\[Collection\(""Environment""\)\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TraitAttributeLine = new(
        @"^[ \t]*\[Trait\(""Category"",\s*""Environment""\)\]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Matches an async InitializeAsync method's own signature up to its opening brace — an
    // expression-bodied InitializeAsync ("=> Task.CompletedTask;") has no brace to match and is
    // skipped, which is correct: there is no block body there for a scope construction to hide
    // inside. A plain call site ("await fixture.InitializeAsync()") is followed by ';' or ')'
    // rather than '{' and is likewise skipped. The leading "async" is required: only an async
    // method's own await unwinds the ExecutionContext a scope construction mutated, so a
    // synchronous "Task InitializeAsync() { ... }" (no await, mutating the same context the test
    // method goes on to run in) has none of this rule's own bug to catch.
    private static readonly Regex InitializeAsyncSignature = new(
        @"\basync\s+Task\s+InitializeAsync\s*\(\s*\)\s*",
        RegexOptions.Compiled);

    [Fact]
    public void Every_direct_write_of_home_or_connection_string_sits_in_the_environment_collection()
    {
        (string testsDirectory, string[] files) = TestSources();

        List<string> offenders = [];
        int hits = 0;

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            (string code, int[] originalIndex, bool balanced) = TestSourceTree.StripCommentsAndStrings(source);

            if (!balanced)
            {
                offenders.Add(
                    $"{Path.GetRelativePath(testsDirectory, file)} -> <StripCommentsAndStrings " +
                    "desynced on this file: stripped brace depth never returned to zero, so class " +
                    "boundaries here cannot be trusted>");
                continue;
            }

            List<ClassFrame> frames = FindClassFrames(code, originalIndex, source);

            int start = 0;
            int callIndex;
            while ((callIndex = code.IndexOf(SetEnvironmentVariableCall, start, StringComparison.Ordinal)) >= 0)
            {
                start = callIndex + SetEnvironmentVariableCall.Length;

                // The argument is read from raw source, not stripped code: it is either a string
                // literal (whose content stripping removed entirely) or a dotted member reference
                // (which survives stripping unchanged, but reading it the same way as the string
                // case keeps this one rule rather than two).
                int rawArgumentStart = originalIndex[callIndex] + SetEnvironmentVariableCall.Length;
                while (rawArgumentStart < source.Length && char.IsWhiteSpace(source[rawArgumentStart]))
                {
                    rawArgumentStart++;
                }

                bool isHomeOrConnectionString = HomeOrConnectionStringArguments.Any(argument =>
                    rawArgumentStart + argument.Length <= source.Length
                    && string.CompareOrdinal(source, rawArgumentStart, argument, 0, argument.Length) == 0);

                if (!isHomeOrConnectionString)
                {
                    continue;
                }

                hits++;
                ClassFrame? frame = InnermostFrame(frames, callIndex);

                if (frame is null)
                {
                    offenders.Add(
                        $"{Path.GetRelativePath(testsDirectory, file)} -> <'{SetEnvironmentVariableCall}' at " +
                        $"stripped offset {callIndex} landed inside no class frame>");
                }
                else if (!frame.HasAttribute)
                {
                    offenders.Add($"{Path.GetRelativePath(testsDirectory, file)} -> {frame.Name}");
                }
                else if (!frame.HasTrait)
                {
                    offenders.Add(
                        $"{Path.GetRelativePath(testsDirectory, file)} -> {frame.Name} " +
                        $"(carries {CollectionAttribute} but not {TraitAttribute})");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a class that writes HALL9K_HOME or HALL9K_CONNECTION_STRING directly (rather than " +
            $"through ScopedTestHome/ScopedConnectionString) races every other one unless it carries " +
            $"{CollectionAttribute} and {TraitAttribute} — the one collection left for a " +
            "process-wide environment variable; " +
            "either route the write through the shared test helper instead, or add both attributes " +
            "if this class genuinely hands the variable to a real child process");

        files.Length.Should().BeGreaterThan(
            100,
            "this is far fewer .cs files than the test tree actually holds — TestSourceTree.RootDirectory() is " +
            "probably no longer resolving to tests/Hall9k.Tests");

        // A positive control: DatabaseDoctorTests and a handful of other classes still redirect
        // HALL9K_CONNECTION_STRING through the shared helper's own file, which itself never
        // matches these markers (it writes the AsyncLocal override, not the environment variable)
        // — but the environment-only classes (RunSupervisorTests' HALL9K_CLAUDE_PATH, and the rest
        // of the small Environment collection) still write a literal environment variable other
        // than these two, which this fact does not scan for at all. So the floor here is small and
        // exists only to catch the scan going dark, not to describe today's exact count.
        hits.Should().BeGreaterThan(
            0,
            "no direct HALL9K_HOME/HALL9K_CONNECTION_STRING write was found anywhere in the test " +
            "tree — a class that hands one to a real child process should still have one; the scan " +
            "itself is more likely broken (TestSourceTree.RootDirectory() misresolving, or " +
            "StripCommentsAndStrings regressed) than every such write having genuinely disappeared");
    }

    [Fact]
    public void No_scope_is_opened_inside_an_async_lifetime_method()
    {
        // Two positive controls on the detection mechanism itself, independent of whatever the
        // tree currently contains: synthetic samples, one per construction shape, the regex/
        // brace-walk below must catch, so this fact cannot pass green while its own scan has
        // quietly gone dark on either shape — the explicit form, or the target-typed idiom almost
        // every real call site in this tree actually uses.
        const string explicitSample = """
            public async Task InitializeAsync()
            {
                _scope = new ScopedTestHome();
            }
            """;
        const string targetTypedSample = """
            public async Task InitializeAsync()
            {
                ScopedConnectionString scope = new(postgres.ConnectionString);
            }
            """;

        foreach (string sample in new[] { explicitSample, targetTypedSample })
        {
            (string sampleCode, _, bool sampleBalanced) = TestSourceTree.StripCommentsAndStrings(sample);
            sampleBalanced.Should().BeTrue();
            List<(int Start, int End)> sampleBodies = [.. FindInitializeAsyncBodies(sampleCode)];
            sampleBodies.Should().ContainSingle();
            ScopeConstructionPatterns.Any(pattern =>
                pattern.Matches(sampleCode, sampleBodies[0].Start).Cast<Match>()
                    .Any(match => match.Index < sampleBodies[0].End)).Should().BeTrue(
                "the detection helper below must catch this synthetic sample, or this fact is " +
                "protecting nothing against the real tree");
        }

        (string testsDirectory, string[] files) = TestSources();

        List<string> offenders = [];

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            (string code, int[] originalIndex, bool balanced) = TestSourceTree.StripCommentsAndStrings(source);

            if (!balanced)
            {
                // Already reported by the sibling fact above; this fact's own coverage over the
                // same file cannot be trusted either, but re-reporting the identical file twice
                // would only be noise.
                continue;
            }

            List<ClassFrame> frames = FindClassFrames(code, originalIndex, source);

            foreach ((int start, int end) in FindInitializeAsyncBodies(code))
            {
                foreach (Regex pattern in ScopeConstructionPatterns)
                {
                    foreach (Match match in pattern.Matches(code, start))
                    {
                        if (match.Index >= end)
                        {
                            break;
                        }

                        ClassFrame? frame = InnermostFrame(frames, match.Index);
                        string className = frame?.Name ?? "<unknown class>";
                        offenders.Add($"{Path.GetRelativePath(testsDirectory, file)} -> {className}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "a scope constructed inside async Task InitializeAsync() is gone by the time the test " +
            "method itself runs (await unwinds the ExecutionContext a callee mutated back to what " +
            "the caller held once that callee's task completes) — open ScopedTestHome/" +
            "ScopedConnectionString from a constructor or a field initializer instead, a plain " +
            "synchronous call that mutates the same context the test method goes on to run in");
    }

    /// <summary>
    /// This project's own sources, minus this file and build output. Shared by both facts so a
    /// change to what counts as a test source cannot land on one and not the other.
    /// </summary>
    private static (string TestsDirectory, string[] Files) TestSources()
    {
        string testsDirectory = TestSourceTree.RootDirectory();

        string[] files =
        [
            .. Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
               .Where(file => !string.Equals(Path.GetFileName(file), SelfFileName, StringComparison.Ordinal))
               .Where(file => !TestSourceTree.IsBuildOutput(testsDirectory, file)),
        ];

        return (testsDirectory, files);
    }

    /// <summary>
    /// Walks <paramref name="code"/> (already comment/string stripped) tracking an
    /// <c>InitializeAsync ( )</c> signature followed immediately by <c>{</c>, then tracks brace
    /// depth to that block's own closing <c>}</c>. An expression-bodied
    /// <c>InitializeAsync() => ...;</c> or a plain call site is never followed by <c>{</c> and is
    /// correctly skipped — there is no block body there for a scope construction to hide inside.
    /// </summary>
    private static IEnumerable<(int Start, int End)> FindInitializeAsyncBodies(string code)
    {
        foreach (Match match in InitializeAsyncSignature.Matches(code))
        {
            int i = match.Index + match.Length;
            while (i < code.Length && char.IsWhiteSpace(code[i]))
            {
                i++;
            }

            if (i >= code.Length || code[i] != '{')
            {
                continue;
            }

            int depth = 0;
            int start = i;
            for (; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return (start, i);
                        break;
                    }
                }
            }
        }
    }

    private sealed record ClassFrame(string Name, int BodyStart, int BodyEnd, bool HasAttribute, bool HasTrait);

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
    /// Walks <paramref name="code"/> tracking brace depth to find each class's own body range and
    /// whether that specific class carries <see cref="CollectionAttribute"/>/<see cref="TraitAttribute"/>
    /// immediately above its declaration — ported from the guard this file replaces, minus the
    /// PostgresFixture-specific tracking that guard also carried, which this one has no use for.
    /// </summary>
    private static List<ClassFrame> FindClassFrames(string code, int[] originalIndex, string source)
    {
        HashSet<int> codePositions = [.. originalIndex];
        List<ClassFrame> frames = [];
        Stack<(string Name, bool HasAttribute, bool HasTrait, int BodyDepth, int BodyStart)> open = [];
        Dictionary<int, int> lastBoundaryAtDepth = new() { [0] = 0 };
        Match[] declarations = [.. ClassDeclaration.Matches(code).Cast<Match>()];
        int nextDeclaration = 0;
        (string Name, bool HasAttribute, bool HasTrait, int KeywordOriginalIndex)? armed = null;
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
                bool hasTrait = TraitAttributeLine.Matches(window).Any(
                    attributeMatch => codePositions.Contains(windowStart + attributeMatch.Index));

                armed = (match.Groups["name"].Value, hasAttribute, hasTrait, keywordOriginalIndex);
            }

            char c = code[i];

            if (c == '{')
            {
                depth++;
                if (armed is { } pendingClass)
                {
                    open.Push((pendingClass.Name, pendingClass.HasAttribute, pendingClass.HasTrait, depth, i + 1));
                    armed = null;
                }

                lastBoundaryAtDepth[depth] = originalIndex[i] + 1;
                continue;
            }

            if (c == '}')
            {
                if (open.Count > 0 && open.Peek().BodyDepth == depth)
                {
                    (string name, bool hasAttribute, bool hasTrait, _, int bodyStart) = open.Pop();
                    frames.Add(new ClassFrame(name, bodyStart, i, hasAttribute, hasTrait));
                }

                depth--;
                lastBoundaryAtDepth[depth] = originalIndex[i] + 1;
                continue;
            }

            // A body-less class declaration ("sealed class Foo;") has no '{' to disarm it — see
            // the guard this replaces for the full rationale.
            if (c == ';' && armed is not null)
            {
                armed = null;
            }
        }

        return frames;
    }
}
