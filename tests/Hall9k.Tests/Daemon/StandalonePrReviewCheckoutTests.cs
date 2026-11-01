using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The pull-request review checkout as a standalone clone, against real repositories in a temporary
/// directory. Pull request heads are simulated by writing <c>refs/pull/&lt;n&gt;/head</c> into the bare
/// origin, as GitHub does. Nothing here cuts a branch or reaches GitHub.
/// </summary>
[Collection("RealProcessSpawn")]
[Trait("Category", "RealProcessSpawn")]
public sealed class StandalonePrReviewCheckoutTests : IDisposable
{
    private readonly string _root;
    private readonly string _originPath;
    private readonly string _seedPath;
    private readonly string _clonePath;
    private readonly string _barePath;
    private readonly string _headSha;
    private readonly GitWorktreeManager _manager = new(NullLogger<GitWorktreeManager>.Instance);

    public StandalonePrReviewCheckoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hall9k-standalone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _originPath = Path.Combine(_root, "origin.git");
        _seedPath = Path.Combine(_root, "seed");
        Git(_root, $"init --bare -b main \"{_originPath}\"");
        Git(_root, $"clone \"{_originPath}\" \"{_seedPath}\"");
        File.WriteAllText(Path.Combine(_seedPath, "README.md"), "# seed\nneedle in the readme\n");
        Git(_seedPath, "add -A");
        Git(_seedPath, "-c user.name=Test -c user.email=test@test commit -m init");
        Git(_seedPath, "tag v1");
        Git(_seedPath, "push origin main v1");
        _headSha = Git(_seedPath, "rev-parse HEAD").Trim();
        Git(_originPath, $"update-ref refs/pull/7/head {_headSha}");

        // The project's repository, in both shapes the platform meets: an ordinary clone, and a
        // bare clone carrying the remote-tracking refspec the home's bare clone is configured with.
        // Each sits in its own directory so its sibling checkouts do not mix.
        _clonePath = Path.Combine(_root, "ordinary", "repo");
        Git(_root, $"clone \"{_originPath}\" \"{_clonePath}\"");
        _barePath = Path.Combine(_root, "home", "repo", "project.git");
        Git(_root, $"clone --bare \"{_originPath}\" \"{_barePath}\"");
        Git(_barePath, "config remote.origin.fetch +refs/heads/*:refs/remotes/origin/*");
        Git(_barePath, "fetch origin");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_checkout_is_a_standalone_repository_borrowing_the_project_objects(bool bareProject)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string project = bareProject ? _barePath : _clonePath;

        Worktree checkout = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(project, 7, DomainId.New(), DomainId.New(), _headSha), cts.Token);

        Directory.Exists(Path.Combine(checkout.Path, ".git")).Should().BeTrue("a real .git directory, not the file a linked worktree has");
        Path.GetDirectoryName(checkout.Path).Should().Be(Path.GetDirectoryName(project), "the checkout sits directly beside the repository");
        Git(checkout.Path, "remote").Trim().Should().BeEmpty("the checkout has no remote");
        string hooks = Path.Combine(checkout.Path, ".git", "hooks");
        (!Directory.Exists(hooks) || Directory.GetFileSystemEntries(hooks).Length == 0).Should().BeTrue("the checkout has no hooks");
        Git(checkout.Path, "rev-parse HEAD").Trim().Should().Be(_headSha);
        TryGit(checkout.Path, "symbolic-ref -q HEAD").ExitCode.Should().NotBe(0, "the checkout is detached");

        string refs = Git(checkout.Path, "for-each-ref --format=%(refname)");
        refs.Should().Contain("refs/remotes/origin/main", "every remote-tracking ref the project's repository holds comes along");
        refs.Should().Contain("refs/remotes/origin/pr-review/7", "the pull request's head is held as a ref, not only as HEAD");
        refs.Should().Contain("refs/tags/v1", "tags come along too");
        refs.Should().NotContain("refs/heads/", "no local branch exists to commit onto");

        string alternates = File.ReadAllText(PrReviewCheckoutPlan.AlternatesFile(checkout.Path));
        alternates.Trim().Should().EndWith(Path.Combine(bareProject ? "project.git" : Path.Combine("repo", ".git"), "objects"));
        Git(checkout.Path, "show HEAD:README.md").Should().Contain("needle in the readme");
        Git(checkout.Path, "grep needle HEAD").Should().Contain("README.md");
        Git(checkout.Path, "ls-files").Should().Contain("README.md");
    }

    [Fact]
    public async Task A_failed_creation_leaves_no_directory_behind()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        // The project's repository refuses to serve the pull request's ref to the clone, so init and
        // the fetch succeed and the checkout step then finds nothing to check out: the failure lands
        // after the directory exists.
        Git(_clonePath, "config uploadpack.hideRefs refs/remotes/origin/pr-review");
        string parent = Path.GetDirectoryName(_clonePath)!;

        Func<Task> act = () => _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_clonePath, 7, DomainId.New(), DomainId.New()), cts.Token);

        await act.Should().ThrowAsync<WorktreeException>();
        Directory.GetDirectories(parent, "wt-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_standalone_checkout_leaves_the_repository_and_its_worktrees_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string linked = Path.Combine(Path.GetDirectoryName(_barePath)!, "wt-linked-sibling");
        Git(_barePath, $"worktree add --detach \"{linked}\" {_headSha}");
        Worktree checkout = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_barePath, 7, DomainId.New(), DomainId.New()), cts.Token);

        await _manager.RemoveAsync(_barePath, checkout.Path, cts.Token);

        Directory.Exists(checkout.Path).Should().BeFalse();
        Directory.Exists(linked).Should().BeTrue("a sibling linked worktree is not touched");
        Directory.Exists(_barePath).Should().BeTrue();
        Git(_barePath, "rev-parse refs/remotes/origin/main").Trim().Should().Be(_headSha, "the repository's refs are untouched");
        Git(_barePath, "worktree list").Should().Contain("wt-linked-sibling");
    }

    [Fact]
    public async Task A_linked_worktree_cut_before_this_change_is_still_removed_through_git()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string linked = Path.Combine(Path.GetDirectoryName(_barePath)!, "wt-11111111-22222222");
        Git(_barePath, $"worktree add --detach \"{linked}\" {_headSha}");
        File.WriteAllText(Path.Combine(linked, "debris.tmp"), "x");

        await _manager.RemoveAsync(_barePath, linked, cts.Token);

        Directory.Exists(linked).Should().BeFalse();
        Git(_barePath, "worktree list").Should().NotContain("wt-11111111-22222222", "git's own record of it is gone too");
        Directory.Exists(_barePath).Should().BeTrue();
    }

    [Fact]
    public async Task Any_path_that_is_not_a_checkout_cut_for_this_repository_is_refused_and_survives()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string beside = Path.GetDirectoryName(_clonePath)!;

        // A sibling that looks like a standalone clone but borrows no objects from this repository.
        string lookalike = Path.Combine(beside, "wt-lookalike");
        Git(_root, $"clone \"{_originPath}\" \"{lookalike}\"");
        // A standalone checkout cut for the repository, but moved out from beside it.
        Worktree genuine = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_clonePath, 7, DomainId.New(), DomainId.New()), cts.Token);
        string elsewhere = Path.Combine(_root, "elsewhere", Path.GetFileName(genuine.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        Directory.Move(genuine.Path, elsewhere);
        // A directory that is not a repository at all, and a path that does not exist.
        string plain = Path.Combine(beside, "wt-plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "keep.txt"), "x");

        foreach (string path in new[] { _clonePath, _barePath, lookalike, elsewhere, plain, _root })
        {
            Func<Task> act = () => _manager.RemoveAsync(_clonePath, path, cts.Token);

            await act.Should().ThrowAsync<WorktreeException>($"{path} must not be deleted as a directory");
            Directory.Exists(path).Should().BeTrue($"{path} is not a checkout cut for this repository");
        }

        File.Exists(Path.Combine(plain, "keep.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(_clonePath, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task Cutting_a_checkout_over_an_existing_directory_refuses_and_leaves_it_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Worktree first = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_clonePath, 7, taskId, runId), cts.Token);

        Func<Task> act = () => _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_clonePath, 7, taskId, runId), cts.Token);

        await act.Should().ThrowAsync<WorktreeException>().WithMessage("*already exists*");
        Directory.Exists(first.Path).Should().BeTrue("a failure must only delete a directory it made");
    }

    private static string Git(string workingDirectory, string arguments)
    {
        (int exitCode, string output) = TryGit(workingDirectory, arguments);
        return exitCode == 0
            ? output
            : throw new InvalidOperationException($"git {arguments} failed: {output}");
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
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? output : output + error);
    }

    public void Dispose() => TemporaryTree.TryDelete(_root);
}
