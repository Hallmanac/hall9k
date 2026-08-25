using FluentAssertions;
using Hall9k.Cli.DaemonControl;
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
        Directory.Delete(directory, recursive: true);
        Directory.Delete(artifactsPath, recursive: true);
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
        // indefinitely rather than failing, since Exec.RunAsync has no process-level timeout
        // of its own.
        ExecResult publish = await Exec.RunAsync(
            "dotnet",
            [
                "publish", Path.Combine(repoRoot, "src", "Hall9k.Daemon"), "-c", "Release", "-o", directory, "--nologo",
                "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={artifactsPath}",
            ],
            timeout.Token);

        publish.Succeeded.Should().BeTrue(
            $"dotnet publish should succeed:\n{publish.StandardOutput}\n{publish.StandardError}");
        File.Exists(Path.Combine(directory, "appsettings.json")).Should().BeTrue(
            "production settings still have to ship");
        File.Exists(Path.Combine(directory, "appsettings.Development.json")).Should().BeFalse(
            "Directory.Build.targets excludes a Development settings file from publish output — "
            + "if this regresses, every install ships one until the next tagged release catches it");
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
