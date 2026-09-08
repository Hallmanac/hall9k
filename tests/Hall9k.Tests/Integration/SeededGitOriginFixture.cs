using System.Diagnostics;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// One seeded bare "origin" repository per test class, built once rather than once per test.
/// Every origin-backed seed in <see cref="ReviewEngineTests"/> starts from the identical state —
/// a <c>main</c> branch carrying a single <c>base.txt</c> commit — and reaching it costs six git
/// process starts (<c>init --bare</c>, <c>clone</c>, <c>add</c>, <c>commit</c>, <c>push</c>, and
/// the directory create around them). Seeding it per test pays that per test, for a result that
/// never differs; this pays it once for the class.
/// <para>
/// <see cref="OriginPath"/> is for a test that only reads the origin. A test that pushes to it
/// takes <see cref="CopyTo"/> instead, which is one <c>git clone --bare</c> in place of the whole
/// build-it-again sequence: git hardlinks the object store for a local clone, and a push only ever
/// adds objects and moves refs in the copy, so nothing a test does to its own copy can reach back
/// into the template. Pushing into the template itself would hand every later test in the class an
/// origin its own seed's doc comment no longer describes, which is why
/// <see cref="ReviewEngineTests"/>' push helpers refuse <see cref="OriginPath"/> by name rather
/// than trusting each call site to have remembered.
/// </para>
/// <para>
/// The git runner below is this fixture's own rather than borrowed from the class it serves: a
/// fixture that reached into its own consumer for a helper would be the wrong way round, and
/// xUnit builds the fixture before any instance of that class exists.
/// </para>
/// </summary>
public sealed class SeededGitOriginFixture : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"hall9k-seeded-origin-{Guid.NewGuid():N}");

    /// <summary>
    /// The shared origin: a bare repository whose <c>main</c> carries one <c>base.txt</c> commit.
    /// Read-only — see the type's own remarks for why, and <see cref="CopyTo"/> for what a test
    /// that pushes uses instead.
    /// </summary>
    public string OriginPath => Path.Combine(_root, "template.git");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        string scratch = Path.Combine(_root, "scratch");
        Git(_root, $"init -q --bare -b main \"{OriginPath}\"");
        Git(_root, $"clone -q \"{OriginPath}\" \"{scratch}\"");
        File.WriteAllText(Path.Combine(scratch, "base.txt"), "base\n");
        Git(scratch, "add -A");
        Git(scratch, "-c user.name=Test -c user.email=test@test commit -q -m init");
        Git(scratch, "push -q origin main");
        DeleteBestEffort(scratch);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies the template to <paramref name="destination"/> as a bare repository of the caller's
    /// own — for a test that pushes to its origin. The destination's parent directory is created
    /// if it does not exist yet, since callers put these under a per-test home that their own
    /// teardown deletes.
    /// </summary>
    public void CopyTo(string destination)
    {
        string parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException($"{destination} has no parent directory", nameof(destination));
        Directory.CreateDirectory(parent);
        Git(parent, $"clone -q --bare \"{OriginPath}\" \"{destination}\"");
    }

    public Task DisposeAsync()
    {
        DeleteBestEffort(_root);
        return Task.CompletedTask;
    }

    private static void DeleteBestEffort(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            // git leaves its loose object files read-only, which Directory.Delete refuses on
            // Windows — the same thing ReviewEngineTests.Dispose already had to widen for.
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    private static void Git(string workingDirectory, string arguments)
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

        // Both pipes drained concurrently, not one after the other: reading stdout to EOF first
        // blocks this thread until git exits, and a git invocation that fills the ~64 KB stderr
        // pipe buffer in the meantime blocks on the write nobody is reading — a deadlock that
        // hangs the whole class until the runner's own timeout (independent pre-PR review, cycle
        // 1, adversarial lens). Every invocation here is a -q operation on a local repository
        // that emits almost nothing, so this is the shape being made safe rather than a failure
        // observed.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        string output = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }
}
