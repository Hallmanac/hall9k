using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Installation;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// <see cref="GitDescribedVersion"/> is what <c>h9k install --repo</c> stamps a locally built
/// binary with instead of the csproj's checked-in placeholder — exercised here against a real
/// git repository (the same choice <c>GitWorktreeManagerTests</c> makes for the identical
/// reason: only a real <c>git</c> exercises what actually ships).
/// </summary>
public sealed class GitDescribedVersionTests : IDisposable
{
    private readonly string repository = Directory.CreateTempSubdirectory("h9k-git-describe-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(repository, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task A_tagged_commit_with_a_later_commit_describes_with_the_v_prefix_stripped()
    {
        Git(repository, "init -q -b main");
        Git(repository, "-c user.name=Test -c user.email=test@test commit --allow-empty -m first");
        Git(repository, "tag v0.2.0");
        Git(repository, "-c user.name=Test -c user.email=test@test commit --allow-empty -m second");

        GitDescribedVersion.Result result = await GitDescribedVersion.ResolveAsync(repository, CancellationToken.None);

        result.Version.Should().MatchRegex(@"^0\.2\.0-1-g[0-9a-f]+$",
            "the v prefix is stripped and the shape matches release.yml's own VERSION derivation");
        result.FallbackReason.Should().BeNull();
    }

    [Fact]
    public async Task A_dirty_worktree_carries_a_dirty_marker()
    {
        Git(repository, "init -q -b main");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "original\n");
        Git(repository, "add -A");
        Git(repository, "-c user.name=Test -c user.email=test@test commit -m first");
        Git(repository, "tag v0.3.0");
        File.WriteAllText(Path.Combine(repository, "tracked.txt"), "changed\n");

        GitDescribedVersion.Result result = await GitDescribedVersion.ResolveAsync(repository, CancellationToken.None);

        result.Version.Should().Be("0.3.0-dirty");
    }

    [Fact]
    public async Task No_tags_reachable_falls_back_with_a_named_reason()
    {
        Git(repository, "init -q -b main");
        Git(repository, "-c user.name=Test -c user.email=test@test commit --allow-empty -m first");

        GitDescribedVersion.Result result = await GitDescribedVersion.ResolveAsync(repository, CancellationToken.None);

        result.Version.Should().BeNull("a repo with no tag history must fall back to the checked-in csproj version");
        result.FallbackReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_empty_directory_that_is_not_a_git_repository_falls_back_with_a_named_reason()
    {
        GitDescribedVersion.Result result = await GitDescribedVersion.ResolveAsync(repository, CancellationToken.None);

        result.Version.Should().BeNull();
        result.FallbackReason.Should().NotBeNullOrWhiteSpace();
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.Start();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {arguments} failed: {standardError}");
    }
}
