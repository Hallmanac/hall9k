using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Whether a fix cycle's `dotnet test` gate should run every test or only the ones reachable
/// from the fix's own commits (task: a fix cycle's verification gate). Scoping never costs
/// correctness for speed: every step <see cref="TestScopeResolver"/> cannot read confidently
/// degrades to <see cref="Full"/> rather than guessing (AGENTS.md's "never guess at unobserved
/// facts" rule), and nothing here ever produces an empty test run standing in for a passed one.
/// </summary>
public sealed record TestGateScope
{
    private TestGateScope(
        bool isScoped, string? filterExpression, IReadOnlyList<string> testClasses, string reason)
    {
        IsScoped = isScoped;
        FilterExpression = filterExpression;
        TestClasses = testClasses;
        Reason = reason;
    }

    /// <summary>True when a `dotnet test`-shaped gate should be narrowed with <see cref="FilterExpression"/>.</summary>
    public bool IsScoped { get; }

    /// <summary>The `--filter` expression to inject into a `dotnet test`-shaped gate. Null when unscoped.</summary>
    public string? FilterExpression { get; }

    public IReadOnlyList<string> TestClasses { get; }

    /// <summary>The human-readable "why", recorded on the verification pass and logged.</summary>
    public string Reason { get; }

    public static TestGateScope Full(string reason) => new(false, null, [], reason);

    public static TestGateScope Scoped(
        IReadOnlyList<string> touchedFiles, IReadOnlyList<string> testClasses, string cycleDescription)
    {
        string filter = string.Join('|', testClasses.Select(name => $"FullyQualifiedName~{name}"));

        // A filter the resolver can compute but the platform cannot execute must degrade to Full
        // the same as every other unmappable condition does, rather than fail the gate process at
        // start (conformance review finding): the composed filter is only PART of the eventual
        // command line — the gate's own command, the run directory's log-redirect path, and on
        // Windows the cmd.exe /c wrapper all add to it before cmd.exe's 8,191-character limit is
        // what actually governs — so the cap here is deliberately conservative headroom, not the
        // limit itself.
        if (filter.Length > MaxFilterExpressionLength)
        {
            return Full(
                $"the scoped filter for {testClasses.Count} test class(es) reachable from " +
                $"{touchedFiles.Count} touched file(s) is {filter.Length} characters, over the " +
                $"{MaxFilterExpressionLength}-character safety cap on how much a gate's own command " +
                $"line can absorb ({cycleDescription})");
        }

        string reason =
            $"scoped to {testClasses.Count} test class(es) reachable from {touchedFiles.Count} " +
            $"touched file(s) ({cycleDescription}): {Summarize(testClasses)}";
        return new TestGateScope(true, filter, testClasses, reason);
    }

    private const int MaxFilterExpressionLength = 4000;

    private const int MaxListedNames = 20;

    /// <summary>Capped the same way for every reason string this record hands back, scoped or full — a wide-rewrite fix's own file or class list must never blow out the one-line reason (<see cref="TestScopeResolver"/>'s own fallback reasons share this cap).</summary>
    internal static string Summarize(IReadOnlyList<string> names) =>
        names.Count <= MaxListedNames
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(MaxListedNames))}, and {names.Count - MaxListedNames} more";
}

/// <summary>
/// Computes a <see cref="TestGateScope"/> from the worktree's own git history and current source
/// tree — the same "read what's actually there" discipline <see cref="ReviewPacketAssembler"/>
/// already applies to the review packet, aimed at test selection instead. Mapping is deterministic
/// platform code (a git diff since the fix cycle's own head, then a static reference match against
/// the declared types in the test tree), never an agent's judgment call: <see cref="ResolveAsync"/>
/// only ever narrows the test run when every touched file resolved to at least one referencing
/// test class; anything it cannot read or map confidently returns <see cref="TestGateScope.Full"/>.
/// </summary>
public static partial class TestScopeResolver
{
    public static async Task<TestGateScope> ResolveAsync(
        string worktreePath, string sinceSha, string cycleDescription, CancellationToken cancellationToken)
    {
        string? nameOnly = await RunGitAsync(
            worktreePath, ["diff", "--name-only", "-z", $"{sinceSha}..HEAD"], cancellationToken);
        if (nameOnly is null)
        {
            return TestGateScope.Full(
                $"could not read the fix's touched files from git ({sinceSha}..HEAD) ({cycleDescription})");
        }

        IReadOnlyList<string> touchedFiles = [.. nameOnly.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        if (touchedFiles.Count == 0)
        {
            return TestGateScope.Full(
                $"no commits were found since the reviewed cycle's head ({sinceSha}..HEAD) ({cycleDescription})");
        }

        List<string> nonSourceFiles = [.. touchedFiles.Where(file => !file.EndsWith(".cs", StringComparison.Ordinal))];
        if (nonSourceFiles.Count > 0)
        {
            return TestGateScope.Full(
                $"non-C# file(s) touched, not statically mappable to tests: {TestGateScope.Summarize(nonSourceFiles)} ({cycleDescription})");
        }

        (IReadOnlyList<TestFile>? testFiles, string? testTreeLoadFailure) =
            await LoadTestFilesAsync(worktreePath, cancellationToken);
        if (testFiles is null)
        {
            return TestGateScope.Full($"{testTreeLoadFailure} ({cycleDescription})");
        }

        HashSet<string> testClasses = [];
        foreach (string file in touchedFiles)
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(Path.Combine(worktreePath, file), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return TestGateScope.Full($"could not read touched file {file} to map it to tests ({cycleDescription})");
            }

            if (file.StartsWith("tests/", StringComparison.Ordinal))
            {
                if (!file.EndsWith("Tests.cs", StringComparison.Ordinal))
                {
                    // A touched fake, fixture, or other shared test helper (tests/Hall9k.Tests/Fakes/*,
                    // Integration/*Fixture.cs) can be relied on by an unbounded number of test classes
                    // that never mention its name directly — its blast radius is not statically
                    // mappable from the file alone, so it falls back honestly rather than guessing.
                    return TestGateScope.Full(
                        $"touched shared test file outside the *Tests.cs convention: {file} ({cycleDescription})");
                }

                IReadOnlyList<string> declaredHere = ExtractTypeNames(content);
                if (declaredHere.Count == 0)
                {
                    return TestGateScope.Full($"could not find a declared test class in touched file {file} ({cycleDescription})");
                }

                foreach (string name in declaredHere)
                {
                    testClasses.Add(name);
                }

                continue;
            }

            if (!file.StartsWith("src/", StringComparison.Ordinal))
            {
                return TestGateScope.Full($"touched file outside src/ or tests/: {file} ({cycleDescription})");
            }

            IReadOnlyList<string> typeNames = ExtractTypeNames(content);
            if (typeNames.Count == 0)
            {
                return TestGateScope.Full($"could not determine the type(s) declared in touched file {file} ({cycleDescription})");
            }

            // Tracked per file, not just on the shared set below: a file whose own types no test
            // class references contributes nothing, and the class's own contract ("only ever
            // narrows when every touched file resolved to at least one referencing test class")
            // means that one unmapped file must fall the whole resolve back to Full even when a
            // sibling touched file in the same commits did map (independent pre-PR review,
            // cycle 1 — the aggregate-only `testClasses.Count == 0` check below silently dropped
            // a two-file fix's unmapped file whenever the other one mapped).
            bool matchedAnyTestClass = false;
            foreach (string typeName in typeNames)
            {
                Regex reference = TypeReferencePattern(typeName);
                foreach (TestFile testFile in testFiles)
                {
                    if (reference.IsMatch(testFile.Content))
                    {
                        testClasses.Add(testFile.ClassName);
                        matchedAnyTestClass = true;
                    }
                }
            }

            if (!matchedAnyTestClass)
            {
                return TestGateScope.Full(
                    $"no test class references any type declared in touched file {file} ({cycleDescription})");
            }
        }

        return testClasses.Count == 0
            ? TestGateScope.Full($"no test class references any type the fix's commits touched ({cycleDescription})")
            : TestGateScope.Scoped(touchedFiles, [.. testClasses.Order(StringComparer.Ordinal)], cycleDescription);
    }

    private sealed record TestFile(string ClassName, string Content);

    /// <summary>
    /// Every declared type in every `*Tests.cs` file under `tests/`, read once per resolve so the
    /// per-touched-file reference search below is an in-memory scan rather than a subprocess per
    /// file — `grep` is not guaranteed on every platform this daemon runs on (AGENTS.md: CI covers
    /// both ubuntu and windows). A null file list with a reason naming what was actually observed
    /// when the test tree itself is unreadable, whether that is enumeration itself or one
    /// particular file (independent pre-PR review, cycle 1 — a shared blanket "could not
    /// enumerate the test tree" reason previously asserted an enumeration failure even when
    /// enumeration succeeded and a single file's read was what actually failed, pointing an
    /// operator at directory permissions instead of the one bad file; AGENTS.md's "never guess at
    /// unobserved facts" rule).
    /// </summary>
    private static async Task<(IReadOnlyList<TestFile>? Files, string? FailureReason)> LoadTestFilesAsync(
        string worktreePath, CancellationToken cancellationToken)
    {
        string testsRoot = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return (null, "the worktree has no tests/ directory to enumerate");
        }

        List<TestFile> files = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(testsRoot, "*Tests.cs", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(path, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Skipping this one file and continuing (Copilot review, PR #62) would let the
                    // resolve run "confident" on an incomplete test tree: the one class this file
                    // would have declared is exactly the one that could reference the touched file
                    // being mapped, and its absence is indistinguishable from "nothing references
                    // it". An unreadable test file makes the WHOLE tree unreadable for this
                    // resolve's purposes, same as the enumeration failure just below — but the
                    // reason names this file, not enumeration, since enumeration is what found it.
                    return (null, $"could not read test file {Path.GetRelativePath(worktreePath, path)} while enumerating the test tree");
                }

                foreach (string className in ExtractTypeNames(content))
                {
                    files.Add(new TestFile(className, content));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A permission-denied file or a directory removed mid-enumeration must degrade to
            // the class's own documented Full fallback, the same as the touched-file read below
            // already does, rather than fault the run with a raw exception (adversarial review).
            return (null, "could not enumerate the test tree to map touched files against");
        }

        return (files, null);
    }

    private static Regex TypeReferencePattern(string typeName) => new($@"\b{Regex.Escape(typeName)}\b");

    private static IReadOnlyList<string> ExtractTypeNames(string content) =>
        [.. TypeDeclarationPattern().Matches(content).Select(match => match.Groups["name"].Value).Distinct()];

    /// <summary>
    /// The kind group tries <c>record class</c>/<c>record struct</c> before the bare <c>record</c>
    /// alternative, so <c>record class Widget</c> consumes both keywords instead of stopping after
    /// <c>record</c> and letting the name group capture the literal word <c>class</c> — a captured
    /// keyword's reference pattern (<see cref="TypeReferencePattern"/>) matches the word in nearly
    /// every test file, so a keyword-captured name does not fail to map, it silently over-maps
    /// (cycle-6 finding: a 6,905-character filter that blew Windows' cmd.exe 8,191-character limit).
    /// The line anchor takes no leading whitespace, so only a file's top-level (unindented)
    /// declarations match — this repo's file-scoped namespaces put every type a file is actually
    /// about at column 0, and an indented nested type (a CLI command's own
    /// <c>public sealed class Settings : CommandSettings</c>, a test file's private helper class)
    /// never captures. A nested type's own name is often generic enough (`Settings`) to match
    /// unrelated test files by coincidence, which previously let a touched file's own untested
    /// subject hide behind its nested type's false-positive match and scope the gate down when the
    /// class's contract calls for <see cref="TestGateScope.Full"/> (independent pre-PR review,
    /// cycle 1, adversarial lens) — and the same over-capture inflated a test file's declared
    /// "test classes" with nested helper types no `--filter` term ever selects. The accessibility
    /// group requires trailing whitespace when present (<c>(?:public|internal|...)\s+</c>, not a
    /// free-floating <c>\s*</c> after it) so an indented nested type that omits an accessibility
    /// keyword — legal, idiomatic C#, defaulting to private — cannot have its own leading
    /// indentation absorbed in place of a captured modifier and slip past the column-0 anchor
    /// (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [GeneratedRegex(
        """^(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+|file\s+)*(?:record\s+(?:class|struct)|class|record|interface|struct|enum)\s+(?<name>\w+)""",
        RegexOptions.Multiline)]
    private static partial Regex TypeDeclarationPattern();

    private static async Task<string?> RunGitAsync(
        string worktreePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await standardOutput;
        await standardError;
        return process.ExitCode == 0 ? output : null;
    }
}
