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
        bool isScoped, string? filterExpression, IReadOnlyList<string> touchedFiles,
        IReadOnlyList<string> testClasses, string reason)
    {
        IsScoped = isScoped;
        FilterExpression = filterExpression;
        TouchedFiles = touchedFiles;
        TestClasses = testClasses;
        Reason = reason;
    }

    /// <summary>True when a `dotnet test`-shaped gate should be narrowed with <see cref="FilterExpression"/>.</summary>
    public bool IsScoped { get; }

    /// <summary>The `--filter` expression to inject into a `dotnet test`-shaped gate. Null when unscoped.</summary>
    public string? FilterExpression { get; }

    public IReadOnlyList<string> TouchedFiles { get; }

    public IReadOnlyList<string> TestClasses { get; }

    /// <summary>The human-readable "why", recorded on the verification pass and logged.</summary>
    public string Reason { get; }

    public static TestGateScope Full(string reason) => new(false, null, [], [], reason);

    public static TestGateScope Scoped(
        IReadOnlyList<string> touchedFiles, IReadOnlyList<string> testClasses, string cycleDescription)
    {
        string filter = string.Join('|', testClasses.Select(name => $"FullyQualifiedName~{name}"));
        string reason =
            $"scoped to {testClasses.Count} test class(es) reachable from {touchedFiles.Count} " +
            $"touched file(s) ({cycleDescription}): {Summarize(testClasses)}";
        return new TestGateScope(true, filter, touchedFiles, testClasses, reason);
    }

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

        IReadOnlyList<TestFile>? testFiles = LoadTestFiles(worktreePath);
        if (testFiles is null)
        {
            return TestGateScope.Full($"could not enumerate the test tree to map touched files against ({cycleDescription})");
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
    /// both ubuntu and windows). Null when the test tree itself is unreadable.
    /// </summary>
    private static IReadOnlyList<TestFile>? LoadTestFiles(string worktreePath)
    {
        string testsRoot = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return null;
        }

        List<TestFile> files = [];
        try
        {
            foreach (string path in Directory.EnumerateFiles(testsRoot, "*Tests.cs", SearchOption.AllDirectories))
            {
                string content;
                try
                {
                    content = File.ReadAllText(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
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
            return null;
        }

        return files;
    }

    private static Regex TypeReferencePattern(string typeName) => new($@"\b{Regex.Escape(typeName)}\b");

    private static IReadOnlyList<string> ExtractTypeNames(string content) =>
        [.. TypeDeclarationPattern().Matches(content).Select(match => match.Groups["name"].Value).Distinct()];

    [GeneratedRegex(
        """^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|record|interface|struct|enum)\s+(?<name>\w+)""",
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
