using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// An install or update run under a redirected home must leave the operator's real h9k link alone
/// (origin incident, hall9k-3f, 2026-09-23: a scratch run under HALL9K_HOME retargeted
/// ~/.local/bin/h9k at a scratch bin that was then deleted). The temporary profile and empty PATH
/// keep even a regression inside a directory this test owns.
/// </summary>
public sealed class InstallCommandRedirectedHomeTests : IDisposable
{
    private readonly ScopedTestHome _scopedHome = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"h9k-redirected-home-{Path.GetRandomFileName()}");

    public InstallCommandRedirectedHomeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _scopedHome.Dispose();
        InstallCommand.TryDelete(_root);
    }

    [Fact]
    public async Task A_redirected_home_skips_the_PATH_link_and_says_so()
    {
        string staging = Path.Combine(_root, "staging");
        string profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(staging, "marker.txt"), "release\n");

        int exitCode = 0;
        string output = await ScopedAnsiConsoleCapture.CaptureAsync(async () =>
            exitCode = await InstallCommand.FinishAsync(
                staging,
                skillsSource: null,
                version: "0.0.0-test",
                restart: false,
                noRestart: true,
                linkOntoPath: true,
                pathVariable: string.Empty,
                userProfileDirectory: profile,
                cancellationToken: CancellationToken.None));

        exitCode.Should().Be(0);
        output.Should().Contain("PATH link skipped").And.Contain("redirected").And.Contain(PlatformPaths.Home);
        Directory.EnumerateFileSystemEntries(profile, "*", SearchOption.AllDirectories)
            .Should().BeEmpty("a redirected home never writes a link under the profile");
    }

    [Theory]
    [InlineData("/home/a/.hall9k", "/home/a/.hall9k", false, false)]
    [InlineData("/home/a/.hall9k/", "/home/a/.hall9k", false, false)]
    [InlineData("/home/a/.hall9k", "/home/a/.hall9k/", false, false)]
    [InlineData("/home/a/scratch", "/home/a/.hall9k", false, true)]
    [InlineData("/home/a/.HALL9K", "/home/a/.hall9k", false, true)]
    [InlineData("/home/a/.HALL9K", "/home/a/.hall9k", true, false)]
    public void Redirection_compares_full_paths_ignoring_a_trailing_separator(
        string home, string defaultHome, bool ignoreCase, bool expected)
    {
        PlatformPaths.IsRedirected(NativePath(home), NativePath(defaultHome), ignoreCase).Should().Be(expected);
    }

    [Fact]
    public void A_relative_home_resolves_against_the_working_directory()
    {
        string defaultHome = Path.Combine(Directory.GetCurrentDirectory(), ".hall9k");

        PlatformPaths.IsRedirected(".hall9k", defaultHome, ignoreCase: false).Should().BeFalse();
        PlatformPaths.IsRedirected("scratch", defaultHome, ignoreCase: false).Should().BeTrue();
    }

    // Path.GetFullPath on Windows would prepend the current drive to a rooted POSIX-style literal
    // on both sides alike, so the comparison holds; separators are normalised for readability only.
    private static string NativePath(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
