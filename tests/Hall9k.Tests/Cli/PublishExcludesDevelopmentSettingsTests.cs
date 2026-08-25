using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The actual publish exclusion lives in Directory.Build.targets, not in
/// <see cref="Hall9k.Cli.Commands.InstallCommand"/>'s own defense-in-depth check — that check
/// only ever sees a payload staged from a prior publish, so it cannot catch the glob itself
/// going inert. Only a real <c>dotnet publish</c> exercises the MSBuild item that matters, and
/// nothing in the rest of the suite runs one, so this is the only regression net before a tag
/// push finds out at release time instead.
/// </summary>
public sealed class PublishExcludesDevelopmentSettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("h9k-publish-").FullName;
    private readonly string artifactsPath = Directory.CreateTempSubdirectory("h9k-publish-artifacts-").FullName;

    public void Dispose()
    {
        // Best-effort, matching InstallCommand.TryDelete: a `dotnet publish` this test just
        // killed on timeout can leave a child process (or the OS) holding a file open for a
        // moment after Kill returns, and an unguarded delete here would throw and report that
        // instead of the timeout that actually failed the test.
        TryDelete(directory);
        TryDelete(artifactsPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Publishing_the_daemon_excludes_its_development_settings_file()
    {
        string repoRoot = FindRepositoryRoot();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));

        // UseArtifactsOutput/ArtifactsPath (global properties, so they propagate to every
        // project in the graph — Domain, Connectors and ServiceDefaults included) redirect
        // every project's obj/bin into this test's own temp directory instead of the repo's
        // own src/Hall9k.*/obj|bin/Release. Without that, this publish races any other MSBuild
        // touching the same project directories (a concurrent dotnet build, a second run of
        // this test) for the underlying obj cache files, and CI's `dotnet test --no-build`
        // would otherwise come after a build step that left those directories in a state this
        // publish then rewrites. The timeout bounds the other risk this test carries: a
        // `dotnet publish` blocked on NuGet's machine-wide global-packages lock hangs
        // indefinitely rather than failing.
        ExecResult publish = await RunPublishAsync(repoRoot, timeout.Token);

        publish.Succeeded.Should().BeTrue(
            $"dotnet publish should succeed:\n{publish.StandardOutput}\n{publish.StandardError}");
        File.Exists(Path.Combine(directory, "appsettings.json")).Should().BeTrue(
            "production settings still have to ship");
        File.Exists(Path.Combine(directory, "appsettings.Development.json")).Should().BeFalse(
            "Directory.Build.targets excludes a Development settings file from publish output — "
            + "if this regresses, every install ships one until the next tagged release catches it");
    }

    /// <summary>Runs `dotnet publish` directly rather than through the shared Exec.RunAsync,
    /// because on timeout this test must kill the whole process tree, not just the `dotnet`
    /// parent: `dotnet publish` hands off to MSBuild worker nodes that outlive a bare
    /// WaitForExitAsync cancellation and keep the NuGet global-packages lock held, which would
    /// otherwise wedge the NEXT run of this test (or a concurrent `dotnet publish` anywhere else
    /// on the machine) behind the same lock this one was trying to escape.</summary>
    private async Task<ExecResult> RunPublishAsync(string repoRoot, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "publish", Path.Combine(repoRoot, "src", "Hall9k.Daemon"), "-c", "Release", "-o", directory, "--nologo",
            "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={artifactsPath}",
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ExecResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record ExecResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "Hall9k.slnx")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException($"No Hall9k.slnx found above {AppContext.BaseDirectory}.");
    }
}
