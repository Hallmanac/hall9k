using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Infrastructure.Ids;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class GitWorktreeManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _repositoryPath;
    private readonly GitWorktreeManager _manager = new(NullLogger<GitWorktreeManager>.Instance);

    public GitWorktreeManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hall9k-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // A bare "origin" with one commit on main, cloned locally — so origin/main exists,
        // mirroring the real layout (bare repo + sibling worktrees).
        string originPath = Path.Combine(_root, "origin.git");
        string seedPath = Path.Combine(_root, "seed");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{seedPath}\"");
        File.WriteAllText(Path.Combine(seedPath, "README.md"), "# seed\n");
        Git(seedPath, "add -A");
        Git(seedPath, "-c user.name=Test -c user.email=test@test commit -m init");
        Git(seedPath, "push origin main");

        _repositoryPath = Path.Combine(_root, "repo");
        Git(_root, $"clone \"{originPath}\" \"{_repositoryPath}\"");
    }

    [Fact]
    public async Task Creates_worktree_on_a_no_track_branch_from_origin_main()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        WorktreeRequest request = Request("Add rate limiting to auth endpoints");

        Worktree worktree = await _manager.CreateAsync(request, cts.Token);

        worktree.StartPoint.Should().Be("origin/main");
        worktree.Branch.Should().StartWith("task/").And.Contain("add-rate-limiting");
        Directory.Exists(worktree.Path).Should().BeTrue();
        Path.GetFileName(worktree.Path).Should().StartWith("wt-");

        // --no-track: the branch must have no upstream configured.
        (int exitCode, string _) = TryGit(worktree.Path, $"config branch.{worktree.Branch}.remote");
        exitCode.Should().NotBe(0, "the task branch must not track origin/main");

        (_, string current) = TryGit(worktree.Path, "branch --show-current");
        current.Trim().Should().Be(worktree.Branch);
    }

    [Fact]
    public async Task Five_parallel_creations_against_one_repo_all_succeed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        Worktree[] worktrees = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => _manager.CreateAsync(Request($"Parallel task {i}"), cts.Token)));

        worktrees.Select(w => w.Path).Distinct().Should().HaveCount(5);
        worktrees.Should().OnlyContain(w => Directory.Exists(w.Path));
    }

    [Fact]
    public async Task Retry_of_the_same_task_gets_a_run_suffixed_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Same task twice"), cts.Token);
        Worktree second = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Same task twice"), cts.Token);

        second.Branch.Should().NotBe(first.Branch);
        second.Branch.Should().StartWith(first.Branch + "-r");
    }

    [Fact]
    public async Task Remove_and_prune_leave_the_repository_clean()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Worktree worktree = await _manager.CreateAsync(Request("Short lived"), cts.Token);

        // Debris in the worktree must not block removal (done worktrees hold build output).
        File.WriteAllText(Path.Combine(worktree.Path, "debris.tmp"), "x");

        await _manager.RemoveAsync(_repositoryPath, worktree.Path, cts.Token);
        Directory.Exists(worktree.Path).Should().BeFalse();

        await _manager.PruneAsync(_repositoryPath, cts.Token);
        (_, string list) = TryGit(_repositoryPath, "worktree list");
        list.Trim().Split('\n').Should().HaveCount(1, "only the repo itself remains after remove + prune");
    }

    private WorktreeRequest Request(string objective) =>
        new(_repositoryPath, "main", DomainId.New(), DomainId.New(), objective);

    private static void Git(string workingDirectory, string arguments)
    {
        (int exitCode, string output) = TryGit(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    private static (int ExitCode, string Output) TryGit(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
