using FluentAssertions;
using Hall9k.Connectors.Processes;
using Hall9k.Daemon.Execution;
using Hall9k.Tests.TestSupport;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Covers the fix for the 2026-09-15 origin incident (task 450b9d84, PR #382): a retried stacked
/// replay rebuilt its prompt against <c>TaskDetails.StackReplayOntoCommit</c>, the commit its
/// failed attempt recorded, even though the base had moved in the meantime — landing short of a
/// number the base had taken for a different entry and failing the same numbering guard a second
/// time. <see cref="StackReplayOntoResolver"/> is the fix: a retry reads the base's own current
/// tip fresh from git instead of trusting the recording.
/// <para>
/// Runs against a real, local, throwaway git repository (a git "origin" and a worktree with it
/// configured as a remote) rather than any GitHub-facing fixture — no network, no provider, just
/// two temp directories and the real <c>git</c> binary, the same fake-repo style
/// <c>DecisionsLogRenumbererTransitionTests</c> already uses for the identical reason: this is a
/// git-mechanics question, not one this project's own Testcontainers integration tier needs to
/// answer.
/// </para>
/// <c>[Collection("RealProcessSpawn")]</c> (Decisions Log #172): joins the other
/// real-<c>git</c>-subprocess test classes so they never run concurrently with each other.
/// </summary>
[Collection("RealProcessSpawn")]
public sealed class StackReplayOntoResolverTests : IDisposable
{
    private readonly string _originPath = Path.Combine(Path.GetTempPath(), $"hall9k-sror-origin-{Guid.NewGuid():N}");
    private readonly string _worktreePath = Path.Combine(Path.GetTempPath(), $"hall9k-sror-branch-{Guid.NewGuid():N}");

    public StackReplayOntoResolverTests()
    {
        Directory.CreateDirectory(_originPath);
        Directory.CreateDirectory(_worktreePath);
    }

    public void Dispose()
    {
        TemporaryTree.TryDelete(_originPath);
        TemporaryTree.TryDelete(_worktreePath);
    }

    [Fact]
    public async Task A_retry_resolves_the_bases_current_tip_when_the_base_moved_since_the_recorded_commit()
    {
        string recordedOntoCommit = await InitRepoWithOneCommitAsync(_originPath);
        await InitRepoWithRemoteAsync(_worktreePath, _originPath);

        // The base moved between the failed attempt (which recorded recordedOntoCommit) and this retry.
        string currentBaseTip = await CommitAsync(_originPath, "main moved on");

        StackReplayOntoResolver.Resolution result = await StackReplayOntoResolver.ResolveAsync(
            ExternalProcess.Runner, _worktreePath, "main", recordedOntoCommit, retryPending: true,
            CancellationToken.None);

        result.Commit.Should().Be(currentBaseTip, "a retry must land on the base's own current tip, not a stale prediction");
        result.Commit.Should().NotBe(recordedOntoCommit);
        result.ResolvedFromCurrentBaseTip.Should().BeTrue();
    }

    [Fact]
    public async Task A_first_dispatch_keeps_the_recorded_onto_commit_even_though_the_base_later_moved()
    {
        string recordedOntoCommit = await InitRepoWithOneCommitAsync(_originPath);
        await InitRepoWithRemoteAsync(_worktreePath, _originPath);
        await CommitAsync(_originPath, "main moved on");

        StackReplayOntoResolver.Resolution result = await StackReplayOntoResolver.ResolveAsync(
            ExternalProcess.Runner, _worktreePath, "main", recordedOntoCommit, retryPending: false,
            CancellationToken.None);

        result.Commit.Should().Be(recordedOntoCommit, "an ordinary, first dispatch trusts its own dispatch-time prediction");
        result.ResolvedFromCurrentBaseTip.Should().BeFalse();
    }

    [Fact]
    public async Task A_retry_falls_back_to_the_recorded_commit_when_the_base_cannot_be_fetched()
    {
        // No origin remote is configured at all, so the fetch this resolver's own retry path
        // needs must fail honestly rather than resolve anything.
        await InitRepoWithOneCommitAsync(_worktreePath);
        const string recordedOntoCommit = "0123456789abcdef0123456789abcdef01234567";

        StackReplayOntoResolver.Resolution result = await StackReplayOntoResolver.ResolveAsync(
            ExternalProcess.Runner, _worktreePath, "main", recordedOntoCommit, retryPending: true,
            CancellationToken.None);

        result.Commit.Should().Be(recordedOntoCommit, "an unreachable origin is not a reason to hand the session an empty boundary");
        result.ResolvedFromCurrentBaseTip.Should().BeFalse();
    }

    private static async Task<string> InitRepoWithOneCommitAsync(string path)
    {
        await RunGitAsync(path, ["init", "-q", "-b", "main"]);
        await RunGitAsync(path, ["config", "user.email", "test@hall9k.local"]);
        await RunGitAsync(path, ["config", "user.name", "Hall9k Test"]);
        return await CommitAsync(path, "initial commit");
    }

    private static async Task InitRepoWithRemoteAsync(string path, string originPath)
    {
        await InitRepoWithOneCommitAsync(path);
        await RunGitAsync(path, ["remote", "add", "origin", originPath]);
    }

    private static async Task<string> CommitAsync(string path, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(path, $"{Guid.NewGuid():N}.txt"), message);
        await RunGitAsync(path, ["add", "-A"]);
        await RunGitAsync(path, ["commit", "-q", "-m", message]);
        return (await RunGitCapturingAsync(path, ["rev-parse", "HEAD"])).Trim();
    }

    private static async Task RunGitAsync(string path, IReadOnlyList<string> arguments) =>
        await RunGitCapturingAsync(path, arguments);

    private static async Task<string> RunGitCapturingAsync(string path, IReadOnlyList<string> arguments)
    {
        ProcessResult result = await ExternalProcess.Runner("git", arguments, path, CancellationToken.None);
        result.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} must succeed: {result.StandardError}");
        return result.StandardOutput;
    }
}
