using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <c>InstallCommand.ExecuteAsync</c>'s --repo branch passes <c>-p:InformationalVersion=&lt;git
/// describe&gt;</c> to `dotnet publish` instead of the csproj's checked-in placeholder, and
/// trusts <see cref="InstallCommand.ReadPublishedVersion"/> to read back whatever landed in the
/// binary rather than assuming the override took effect. Only a real `dotnet publish` proves
/// that assumption — the SDK's own git-sha-append machinery
/// (<c>AddSourceRevisionToInformationalVersion</c>) runs after a command-line override is
/// applied and could in principle clobber it, so this is the regression net for that, mirroring
/// <see cref="PublishExcludesDevelopmentSettingsTests"/>'s own reasoning for the identical kind
/// of assumption about Development settings files.
/// </summary>
public sealed class PublishStampsInformationalVersionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("h9k-publish-version-").FullName;
    private readonly string artifactsPath = Directory.CreateTempSubdirectory("h9k-publish-version-artifacts-").FullName;

    public void Dispose()
    {
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
    public async Task A_published_binary_reports_the_informational_version_override_with_metadata_stripped()
    {
        string repoRoot = FindRepositoryRoot();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));

        ExecResult publish = await RunPublishAsync(repoRoot, "0.2.0-12-gabc1234", timeout.Token);

        publish.Succeeded.Should().BeTrue(
            $"dotnet publish should succeed:\n{publish.StandardOutput}\n{publish.StandardError}");
        InstallCommand.ReadPublishedVersion(directory).Should().Be("0.2.0-12-gabc1234",
            "the override must win over the checked-in csproj <Version>, and any +<sha> build "
            + "metadata the SDK appends afterward must be stripped, exactly as InstallCommand's "
            + "own install output relies on");
    }

    private async Task<ExecResult> RunPublishAsync(
        string repoRoot, string informationalVersion, CancellationToken cancellationToken)
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
            "publish", Path.Combine(repoRoot, "src", "Hall9k.Cli"), "-c", "Release", "-o", directory, "--nologo",
            $"-p:InformationalVersion={informationalVersion}",
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
