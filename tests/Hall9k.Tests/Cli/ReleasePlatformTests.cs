using System.Runtime.InteropServices;
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
    [InlineData("win-arm64", ".zip")]
    public void The_archive_extension_matches_the_platforms_own_unpacking_tools(string rid, string extension)
    {
        ReleasePlatform.ArchiveExtension(rid).Should().Be(extension);
    }

    [Theory]
    [InlineData("osx-arm64", "hall9k-osx-arm64.tar.gz")]
    [InlineData("win-x64", "hall9k-win-x64.zip")]
    [InlineData("win-arm64", "hall9k-win-arm64.zip")]
    public void The_archive_file_name_is_what_release_yml_publishes(string rid, string fileName)
    {
        ReleasePlatform.ArchiveFileName(rid).Should().Be(fileName);
    }

    [Fact]
    public void Windows_on_arm64_maps_to_the_win_arm64_release()
    {
        ReleasePlatform.RidFor(Architecture.Arm64, OSPlatform.Windows).Should().Be("win-arm64");
    }

    [Theory]
    [InlineData(Architecture.Arm64, "OSX", "osx-arm64")]
    [InlineData(Architecture.X64, "WINDOWS", "win-x64")]
    [InlineData(Architecture.Arm64, "WINDOWS", "win-arm64")]
    [InlineData(Architecture.X64, "LINUX", "linux-x64")]
    public void Each_supported_architecture_and_os_pair_maps_to_its_rid(Architecture architecture, string os, string rid)
    {
        ReleasePlatform.RidFor(architecture, OSPlatform.Create(os)).Should().Be(rid);
    }

    [Theory]
    [InlineData(Architecture.X64, "OSX")]
    [InlineData(Architecture.Arm64, "LINUX")]
    [InlineData(Architecture.X86, "WINDOWS")]
    [InlineData(Architecture.Arm, "LINUX")]
    public void An_unbuilt_architecture_and_os_pair_has_no_rid(Architecture architecture, string os)
    {
        ReleasePlatform.RidFor(architecture, OSPlatform.Create(os)).Should().BeNull();
    }

    [Fact]
    public void The_supported_rid_list_is_the_four_platforms_release_yml_builds()
    {
        ReleasePlatform.SupportedRids.Should().BeEquivalentTo("osx-arm64", "win-x64", "win-arm64", "linux-x64");
    }

    [Fact]
    public void This_machine_either_has_a_known_rid_or_names_itself_unsupported()
    {
        // Not asserting a specific value — CI runs this on more than one OS — only that the
        // seam returns something meaningful rather than throwing.
        string? rid = ReleasePlatform.CurrentRid();
        (rid is null || ReleasePlatform.SupportedRids.Contains(rid)).Should().BeTrue();
    }
}
