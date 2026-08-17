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
    public async Task Checkout_existing_resumes_the_local_branch_and_fast_forwards_to_origin()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        // First run: branch created, work committed and pushed, worktree removed (the
        // PullRequestOpener lifecycle).
        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Follow up on me"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(first.Path, $"push -q origin {first.Branch}");
        await _manager.RemoveAsync(_repositoryPath, first.Path, cts.Token);

        // Review feedback lands as a commit on the PR branch remotely (web-applied suggestion).
        string reviewer = Path.Combine(_root, "reviewer");
        Git(_root, $"clone \"{Path.Combine(_root, "origin.git")}\" \"{reviewer}\"");
        Git(reviewer, $"checkout -q {first.Branch}");
        File.WriteAllText(Path.Combine(reviewer, "SUGGESTION.md"), "applied suggestion\n");
        Git(reviewer, "add -A");
        Git(reviewer, "-c user.name=Rev -c user.email=r@r commit -qm suggestion");
        Git(reviewer, $"push -q origin {first.Branch}");

        Worktree followUp = await _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, first.Branch, taskId, DomainId.New()), cts.Token);

        followUp.Branch.Should().Be(first.Branch, "a follow-up resumes the PR branch, never cuts a new one");
        followUp.Path.Should().NotBe(first.Path, "each run gets its own worktree");
        (_, string current) = TryGit(followUp.Path, "branch --show-current");
        current.Trim().Should().Be(first.Branch);
        File.Exists(Path.Combine(followUp.Path, "WORK.md")).Should().BeTrue("the first run's commits are present");
        File.Exists(Path.Combine(followUp.Path, "SUGGESTION.md")).Should().BeTrue(
            "commits landed on the PR remotely fast-forward into the follow-up worktree");
    }

    [Fact]
    public async Task Checkout_existing_reuses_the_retained_worktree_still_holding_the_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        // First run's worktree is retained through closeout (log #21) — the branch is
        // still checked out there, so a fresh worktree add would be refused by git anyway.
        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Reuse my worktree"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(first.Path, $"push -q origin {first.Branch}");

        // Review feedback lands as a commit on the PR branch remotely (web-applied suggestion).
        string reviewer = Path.Combine(_root, "reviewer-reuse");
        Git(_root, $"clone \"{Path.Combine(_root, "origin.git")}\" \"{reviewer}\"");
        Git(reviewer, $"checkout -q {first.Branch}");
        File.WriteAllText(Path.Combine(reviewer, "SUGGESTION.md"), "applied suggestion\n");
        Git(reviewer, "add -A");
        Git(reviewer, "-c user.name=Rev -c user.email=r@r commit -qm suggestion");
        Git(reviewer, $"push -q origin {first.Branch}");

        Worktree followUp = await _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, first.Branch, taskId, DomainId.New()), cts.Token);

        // Path equality is by name: on macOS git resolves /var through the /private symlink.
        Path.GetFileName(followUp.Path).Should().Be(
            Path.GetFileName(first.Path), "the retained worktree IS the follow-up workspace");
        followUp.Branch.Should().Be(first.Branch);
        (_, string list) = TryGit(_repositoryPath, "worktree list");
        list.Trim().Split('\n').Should().HaveCount(2, "no second worktree is created for the follow-up");
        File.Exists(Path.Combine(followUp.Path, "SUGGESTION.md")).Should().BeTrue(
            "the reused worktree fast-forwards to commits landed on the PR remotely");
    }

    [Fact]
    public async Task Delete_branch_everywhere_removes_local_remote_and_tracking_refs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        Worktree worktree = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Delete me everywhere"), cts.Token);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "merged work\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(worktree.Path, $"push -q origin {worktree.Branch}");
        Git(_repositoryPath, "fetch -q origin");

        // Closeout order: worktree first (a checked-out branch cannot be deleted), then branches.
        await _manager.RemoveAsync(_repositoryPath, worktree.Path, cts.Token);
        await _manager.DeleteBranchEverywhereAsync(_repositoryPath, worktree.Branch, cts.Token);

        TryGit(_repositoryPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the local branch is deleted");
        TryGit(_repositoryPath, $"rev-parse --verify refs/remotes/origin/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the remote-tracking ref is pruned");
        TryGit(Path.Combine(_root, "origin.git"), $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the remote branch is deleted");
    }

    [Fact]
    public async Task Checkout_existing_recreates_from_origin_when_only_the_remote_has_the_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Remote only branch"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(first.Path, $"push -q origin {first.Branch}");
        await _manager.RemoveAsync(_repositoryPath, first.Path, cts.Token);
        Git(_repositoryPath, $"branch -D {first.Branch}");

        Worktree followUp = await _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, first.Branch, taskId, DomainId.New()), cts.Token);

        followUp.StartPoint.Should().Be($"origin/{first.Branch}");
        (_, string current) = TryGit(followUp.Path, "branch --show-current");
        current.Trim().Should().Be(first.Branch);
        File.Exists(Path.Combine(followUp.Path, "WORK.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Checkout_existing_of_an_unknown_branch_throws()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));

        Func<Task> act = () => _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, "task/never-existed", DomainId.New(), DomainId.New()), cts.Token);

        await act.Should().ThrowAsync<WorktreeException>().WithMessage("*neither locally nor on origin*");
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
        // git marks object/pack files read-only; Windows refuses to recursively delete
        // them until the attribute is cleared. Cleanup stays best-effort either way.
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
