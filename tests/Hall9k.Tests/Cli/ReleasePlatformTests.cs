using FluentAssertions;
using Hall9k.Cli.Installation;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The RID-to-asset-name mapping backlog 42's release.yml and h9k update both have to
/// agree on, tested as pure functions of a RID rather than of the machine running the
/// test — CurrentRid() itself is exercised implicitly by CommandTreeHelpTests and
/// UpdateCommandTests running on whatever platform CI happens to be.
/// </summary>
public sealed class ReleasePlatformTests
{
    [Theory]
    [InlineData("osx-arm64", ".tar.gz")]
    [InlineData("linux-x64", ".tar.gz")]
    [InlineData("win-x64", ".zip")]
    public void The_archive_extension_matches_the_platforms_own_unpacking_tools(string rid, string extension)
    {
        ReleasePlatform.ArchiveExtension(rid).Should().Be(extension);
    }

    [Theory]
    [InlineData("osx-arm64", "hall9k-osx-arm64.tar.gz")]
    [InlineData("win-x64", "hall9k-win-x64.zip")]
    public void The_archive_file_name_is_what_release_yml_publishes(string rid, string fileName)
    {
        ReleasePlatform.ArchiveFileName(rid).Should().Be(fileName);
    }

    [Fact]
    public void This_machine_either_has_a_known_rid_or_names_itself_unsupported()
    {
        // Not asserting a specific value — CI runs this on more than one OS — only that the
        // seam returns something meaningful rather than throwing.
        string? rid = ReleasePlatform.CurrentRid();
        (rid is null or "osx-arm64" or "win-x64" or "linux-x64").Should().BeTrue();
    }
}
