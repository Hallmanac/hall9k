using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// A fix cycle's test gate (task: a fix cycle's verification gate) — the same "never guess, fall
/// back full" discipline applied to test selection.
/// </summary>
public sealed class TestScopeResolverTests : IDisposable
{
    private readonly string _root;
    private readonly string _repositoryPath;

    /// <summary>
    /// A repo shaped like this one — `src/Hall9k.Domain/Widget.cs` declaring `Widget`,
    /// `tests/Hall9k.Tests/WidgetTests.cs` referencing it, a non-`*Tests.cs` file under
    /// `tests/` standing in for a shared fake, and `OtherTests.cs`, a test class that never
    /// mentions `Widget` but (like almost every C# file) contains the bare word "class" — with
    /// the cycle boundary tagged so each test can diff a fix's own commits against it. Any test
    /// below that expects a scope containing only `WidgetTests` fails loudly if a keyword ever
    /// gets captured as a type name again, since `OtherTests` would map in too (cycle-6 finding).
    /// </summary>
    public TestScopeResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hall9k-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _repositoryPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repositoryPath);
        Git(_repositoryPath, "init -q -b main");
        Commit("src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n}\n", "add widget");
        Commit(
            "tests/Hall9k.Tests/WidgetTests.cs",
            "public sealed class WidgetTests\n{\n    private readonly Widget _widget = new();\n}\n",
            "add widget tests");
        Commit("tests/Hall9k.Tests/Fakes/FakeClock.cs", "public sealed class FakeClock\n{\n}\n", "add shared fake");
        Commit(
            "tests/Hall9k.Tests/OtherTests.cs",
            "public sealed class OtherTests\n{\n    private readonly int _x = 1;\n}\n",
            "add unrelated test class");
    }

    private string CycleHeadSha => TryGit(_repositoryPath, "rev-parse HEAD").Output.Trim();

    [Fact]
    public async Task A_touched_source_file_scopes_to_the_test_class_that_references_its_type()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeTrue();
        scope.TestClasses.Should().ContainSingle().Which.Should().Be("WidgetTests");
        scope.FilterExpression.Should().Be("FullyQualifiedName~WidgetTests");
        scope.Reason.Should().Contain("cycle 2 fix");
    }

    /// <summary>
    /// The type-declaration pattern's name group must capture the type identifier, never a
    /// keyword the kind alternation stopped short of consuming (cycle-6 finding): `record class`
    /// and `record struct` must not be split at `record`, `readonly` must not block the `record
    /// struct` kind match, and a `file`-scoped type must resolve like any other. Each case
    /// asserts the scope narrows to exactly `WidgetTests` — `OtherTests`'s presence in the
    /// fixture means a keyword capture would pull it in too, failing `ContainSingle`.
    /// </summary>
    [Theory]
    [InlineData("public sealed record class Widget")]
    [InlineData("public sealed record struct Widget")]
    [InlineData("public readonly record struct Widget")]
    [InlineData("file class Widget")]
    public async Task A_touched_type_declared_with_record_readonly_or_file_modifiers_scopes_to_its_own_referencing_test(
        string declaration)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("src/Hall9k.Domain/Widget.cs", $"{declaration}\n{{\n    public int Count;\n}}\n", "restyle widget");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeTrue();
        scope.TestClasses.Should().ContainSingle().Which.Should().Be("WidgetTests");
    }

    [Fact]
    public async Task A_touched_test_file_scopes_to_its_own_declared_class_directly()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit(
            "tests/Hall9k.Tests/WidgetTests.cs",
            "public sealed class WidgetTests\n{\n    private readonly Widget _widget = new();\n    private readonly int _extra = 1;\n}\n",
            "tighten widget test");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeTrue();
        scope.TestClasses.Should().ContainSingle().Which.Should().Be("WidgetTests");
    }

    [Fact]
    public async Task No_commits_since_the_cycle_head_falls_back_to_full()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("no commits");
    }

    [Fact]
    public async Task An_unreadable_git_range_falls_back_to_full()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        TestGateScope scope = await TestScopeResolver.ResolveAsync(
            _repositoryPath, "0000000000000000000000000000000000000000", "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("could not read");
    }

    /// <summary>
    /// Copilot review, PR #62: continuing past a per-file read failure while loading the test
    /// tree (the prior behavior) could leave the resolve "confident" on an incomplete map — the
    /// unreadable class is exactly as likely as any other to be the one that references the
    /// touched file, and its silent absence reads as "nothing references it" rather than "this
    /// could not be checked". An unreadable <c>*Tests.cs</c> file must fall the whole resolve
    /// back to full, the same as the enumeration-level failure <see
    /// cref="An_unreadable_git_range_falls_back_to_full"/> already covers.
    /// </summary>
    [Fact]
    public async Task An_unreadable_test_file_falls_back_to_full_rather_than_an_incomplete_map()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget");

        string widgetTestsPath = Path.Combine(_repositoryPath, "tests", "Hall9k.Tests", "WidgetTests.cs");
        if (!MadeUnreadable(widgetTestsPath))
        {
            // Windows has no POSIX mode, and root reads through one; the case this test
            // describes cannot be staged on either, so there is nothing to assert.
            return;
        }

        try
        {
            TestGateScope scope =
                await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

            scope.IsScoped.Should().BeFalse(
                "WidgetTests.cs could not be read — it might be the very class that references the "
                    + "touched file, and a scoped result here would be a guess rather than a fact");
            // Continuing past the unreadable file (the prior behavior) reaches the identical
            // IsScoped == false outcome by a different route: OtherTests never references Widget,
            // so matchedAnyTestClass stays false and the fallback reason blames "no test class
            // references any type declared in touched file" rather than the unreadable file.
            // Asserting the reason names the file this change actually reads — not enumeration,
            // not "nothing references it" — is what distinguishes the new behavior from the old
            // one (independent pre-PR review, cycle 3, conformance lens).
            scope.Reason.Should().Contain("could not read test file").And.Contain("WidgetTests.cs");
        }
        finally
        {
            // MadeUnreadable already returned false, and returned early, on Windows — this only
            // ever runs having actually stripped the mode below, on a platform that has one.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(widgetTestsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    /// <summary>
    /// Strips every permission bit and confirms the read is actually denied. False when the
    /// platform or the caller's privileges make the denial impossible to stage — in which case
    /// the stripped mode is restored before returning, since a caller getting false back never
    /// reaches its own restoring `finally` (Copilot review, PR #86: running as root reads
    /// straight through the stripped mode, and the mode would otherwise stay stripped on the
    /// repo's temp file rather than only for the duration of a denial that was never staged).
    /// </summary>
    private static bool MadeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        UnixFileMode originalMode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.ReadAllText(path);
            File.SetUnixFileMode(path, originalMode);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [Fact]
    public async Task A_touched_non_csharp_file_falls_back_to_full()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("AGENTS.md", "doctrine\n", "touch doctrine");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("non-C# file(s)").And.Contain("AGENTS.md");
    }

    /// <summary>
    /// The fallback reason's own file list must never blow out the one-line reason any wider than
    /// <see cref="TestGateScope.Scoped"/>'s own <c>Summarize</c> already bounds a matched-class
    /// list to (cycle-3 finding): a wide-rewrite fix touching dozens of non-C# files gets the same
    /// 20-file cap and trailing "and N more" as a scoped reason would.
    /// </summary>
    [Fact]
    public async Task A_touched_non_csharp_file_lists_reason_is_capped_like_a_scoped_reason()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Dictionary<string, string> notes = [];
        for (int i = 0; i < 25; i++)
        {
            notes[$"docs/note-{i:00}.md"] = $"note {i}\n";
        }

        // One commit for all 25 files rather than 25 separate ones (each spawning its own `git
        // add`/`git commit` process pair): the diff this test asserts on lists touched files in
        // git's own path order regardless of how many commits produced them, and a slow CI
        // runner's process-spawn overhead had room to blow past this test's 30-second deadline
        // when every file got its own pair.
        CommitMany(notes, "add 25 docs notes");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("note-00.md").And.Contain("note-19.md").And.Contain("and 5 more");
        scope.Reason.Should().NotContain("note-20.md");
    }

    [Fact]
    public async Task A_touched_shared_test_helper_outside_the_Tests_convention_falls_back_to_full()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("tests/Hall9k.Tests/Fakes/FakeClock.cs", "public sealed class FakeClock\n{\n    public int Ticks;\n}\n", "touch shared fake");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("shared test file").And.Contain("Fakes/FakeClock.cs");
    }

    [Fact]
    public async Task A_touched_type_with_no_referencing_test_falls_back_to_full()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("src/Hall9k.Domain/Orphan.cs", "public sealed class Orphan\n{\n}\n", "add untested type");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("no test class references");
    }

    /// <summary>
    /// A mapped file must never smuggle an unmapped sibling into a narrowed run (independent
    /// pre-PR review, cycle 1): the class's own doc promises narrowing only when EVERY touched
    /// file resolved to at least one referencing test class, so a commit touching both a mapped
    /// and an unmapped file falls back to full exactly like the unmapped file would alone.
    /// </summary>
    [Fact]
    public async Task A_mix_of_a_mapped_and_an_unmapped_touched_file_falls_back_to_full()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string sinceSha = CycleHeadSha;
        Commit("src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget");
        Commit("src/Hall9k.Domain/Orphan.cs", "public sealed class Orphan\n{\n}\n", "add untested type");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("no test class references").And.Contain("Orphan.cs");
    }

    /// <summary>
    /// A touched type's own nested type (a CLI command's own `public sealed class Settings :
    /// CommandSettings`, the shape every command file in this repo has) must never stand in for
    /// the file's real subject: the nested type's generic name coincidentally appearing in an
    /// unrelated test file's own unrelated content must not let the untested outer type scope the
    /// gate down (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_touched_types_own_nested_type_never_stands_in_for_the_outer_type()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit(
            "tests/Hall9k.Tests/OtherTests.cs",
            "public sealed class OtherTests\n{\n    private readonly int Settings = 1;\n}\n",
            "give OtherTests an unrelated Settings field");
        string sinceSha = CycleHeadSha;
        Commit(
            "src/Hall9k.Domain/Orphan.cs",
            "public sealed class Orphan\n{\n    public sealed class Settings\n    {\n    }\n}\n",
            "add untested command-shaped type");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("no test class references").And.Contain("Orphan.cs");
    }

    /// <summary>
    /// The same nested-type exclusion holds when the nested type declares no explicit accessibility
    /// keyword (legal, idiomatic C# — it defaults to private): a free-floating `\s*` in place of the
    /// accessibility keyword must not let the indentation itself substitute for a captured modifier
    /// (independent pre-PR review, cycle 1, conformance lens).
    /// </summary>
    [Fact]
    public async Task A_touched_types_own_unmodified_nested_type_never_stands_in_for_the_outer_type()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit(
            "tests/Hall9k.Tests/OtherTests.cs",
            "public sealed class OtherTests\n{\n    private readonly int Settings = 1;\n}\n",
            "give OtherTests an unrelated Settings field");
        string sinceSha = CycleHeadSha;
        Commit(
            "src/Hall9k.Domain/Orphan.cs",
            "public sealed class Orphan\n{\n    sealed class Settings\n    {\n    }\n}\n",
            "add untested type with an unmodified nested type");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeFalse();
        scope.Reason.Should().Contain("no test class references").And.Contain("Orphan.cs");
    }

    /// <summary>
    /// A test file's own nested private helper class (`tests/Integration/ReviewEngineTests.cs`'s
    /// `ScriptedExecutor`, the shape a scripted-executor fixture takes) must never be registered as
    /// its own selectable test class: no xunit `--filter FullyQualifiedName~` term can select a
    /// type that declares no tests of its own, so counting it only inflates the filter with inert
    /// terms (conformance review finding).
    /// </summary>
    [Fact]
    public async Task A_test_files_nested_private_helper_class_is_never_registered_as_a_test_class()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Commit(
            "tests/Hall9k.Tests/WidgetTests.cs",
            "public sealed class WidgetTests\n{\n    private readonly Widget _widget = new();\n\n    private sealed class Helper\n    {\n    }\n}\n",
            "add nested helper to widget tests");
        string sinceSha = CycleHeadSha;
        Commit("src/Hall9k.Domain/Widget.cs", "public sealed class Widget\n{\n    public int Count;\n}\n", "fix widget");

        TestGateScope scope = await TestScopeResolver.ResolveAsync(_repositoryPath, sinceSha, "cycle 2 fix", cts.Token);

        scope.IsScoped.Should().BeTrue();
        scope.TestClasses.Should().ContainSingle().Which.Should().Be("WidgetTests");
    }

    private void Commit(string relativePath, string content, string message)
    {
        string fullPath = Path.Combine(_repositoryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        Git(_repositoryPath, "add -A");
        Git(_repositoryPath, $"-c user.name=Test -c user.email=test@test commit -q -m \"{message}\"");
    }

    private void CommitMany(IReadOnlyDictionary<string, string> filesByRelativePath, string message)
    {
        foreach ((string relativePath, string content) in filesByRelativePath)
        {
            string fullPath = Path.Combine(_repositoryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        Git(_repositoryPath, "add -A");
        Git(_repositoryPath, $"-c user.name=Test -c user.email=test@test commit -q -m \"{message}\"");
    }

    private static void Git(string workingDirectory, string arguments)
    {
        (int exitCode, string output) = TryGit(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    private static (int ExitCode, string Output) TryGit(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a locked pack file on some platforms is not worth failing the
            // test run over.
        }
    }
}
