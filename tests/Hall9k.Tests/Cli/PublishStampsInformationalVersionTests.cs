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
        PublishTestSupport.TryDelete(directory);
        PublishTestSupport.TryDelete(artifactsPath);
    }

    [Fact]
    public async Task A_published_binary_reports_the_informational_version_override_with_metadata_stripped()
    {
        string repoRoot = PublishTestSupport.FindRepositoryRoot();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));

        PublishTestSupport.ExecResult publish = await PublishTestSupport.RunPublishAsync(
            repoRoot,
            "Hall9k.Cli",
            directory,
            artifactsPath,
            ["-p:InformationalVersion=0.2.0-12-gabc1234"],
            timeout.Token);

        publish.AssertSucceeded();
        InstallCommand.ReadPublishedVersion(directory).Should().Be("0.2.0-12-gabc1234",
            "the override must win over the checked-in csproj <Version>, and any +<sha> build "
            + "metadata the SDK appends afterward must be stripped, exactly as InstallCommand's "
            + "own install output relies on");
    }
}
