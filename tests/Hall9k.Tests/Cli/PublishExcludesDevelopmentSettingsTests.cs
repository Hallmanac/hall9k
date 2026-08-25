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

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Publishing_the_daemon_excludes_its_development_settings_file()
    {
        string repoRoot = FindRepositoryRoot();
        ExecResult publish = await Exec.RunAsync(
            "dotnet",
            ["publish", Path.Combine(repoRoot, "src", "Hall9k.Daemon"), "-c", "Release", "-o", directory, "--nologo"],
            CancellationToken.None);

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
