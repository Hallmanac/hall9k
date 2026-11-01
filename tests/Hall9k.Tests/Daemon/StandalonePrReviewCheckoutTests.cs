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
/// origin, as GitHub does. Nothing here reaches GitHub.
/// <para>
/// This class is the stated exception to "no new test creates a branch": the throwaway origin is
/// initialised with a <c>main</c> branch because a commit needs one, and no test cuts a branch of its
/// own. It covers only what needs a real directory: the checkout's shape (criteria 1 and 2), removal
/// (4), the delete guard refusing any other path (5), and a failed cut leaving nothing behind (6).
/// The command sequence itself is pinned process-free in <c>PrReviewCheckoutPlanTests</c>. Not covered
/// by any test: that the pin keeps a pruned pull request head alive, that <c>PruneAsync</c> sweeps the
/// pin of a checkout deleted by hand, and that cancelling a cut ends its git process.
/// </para>
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
        Git(checkout.Path, "config --local core.longpaths").Trim().Should().Be("true", "a fresh init has an empty config, and Windows needs this at checkout");
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
        Git(project, $"rev-parse {PrReviewCheckoutPlan.HeadPinRef(checkout.Path)}").Trim().Should().Be(
            _headSha, "the project's repository holds a ref that keeps the checkout's head alive");
        Git(checkout.Path, "show HEAD:README.md").Should().Contain("needle in the readme");
        Git(checkout.Path, "grep needle HEAD").Should().Contain("README.md");
        Git(checkout.Path, "ls-files").Should().Contain("README.md");
    }

    [Fact]
    public async Task A_force_pushed_base_that_the_project_then_garbage_collects_still_diffs_in_the_open_checkout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        // A base tip that is not an ancestor of the head: a commit on main that the pull request's
        // head (the seed commit) does not contain.
        File.WriteAllText(Path.Combine(_seedPath, "base-only.txt"), "base\n");
        Git(_seedPath, "add -A");
        Git(_seedPath, "-c user.name=Test -c user.email=test@test commit -m base-only");
        Git(_seedPath, "push origin main");
        string oldBase = Git(_seedPath, "rev-parse HEAD").Trim();
        Git(_clonePath, "fetch origin");
        Worktree checkout = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_clonePath, 7, DomainId.New(), DomainId.New(), _headSha, "main"), cts.Token);
        Git(_clonePath, $"rev-parse {PrReviewCheckoutPlan.BasePinRef(checkout.Path)}").Trim().Should().Be(oldBase);

        // The base is force-pushed back to the head and the project's repository forgets the old tip.
        Git(_clonePath, $"update-ref refs/remotes/origin/main {_headSha}");
        Git(_clonePath, "reflog expire --expire=now --all");
        Git(_clonePath, "gc --prune=now --quiet");

        Git(checkout.Path, "rev-parse origin/main").Trim().Should().Be(oldBase);
        TryGit(checkout.Path, "diff --stat origin/main...HEAD").ExitCode.Should().Be(0, "the old base tip is still reachable through its pin");

        await _manager.RemoveAsync(_clonePath, checkout.Path, cts.Token);
        Git(_clonePath, $"for-each-ref {PrReviewCheckoutPlan.PinRefPrefix}").Should().BeEmpty("both pins are released with the checkout");
    }

    [Fact]
    public async Task A_hand_cut_bare_clone_with_no_fetch_refspec_still_gives_the_checkout_an_origin_base_ref()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        // `git clone --bare` writes no remote.origin.fetch, so its branches live under refs/heads/
        // and `git fetch origin` maps nothing: the shape a project registered with --repo can be.
        string handCut = Path.Combine(_root, "handcut", "repo", "project.git");
        Git(_root, $"clone --bare \"{_originPath}\" \"{handCut}\"");
        TryGit(handCut, "config --get remote.origin.fetch").ExitCode.Should().NotBe(0, "the fixture must have no refspec");

        Worktree checkout = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(handCut, 7, DomainId.New(), DomainId.New(), _headSha), cts.Token);

        Git(checkout.Path, "rev-parse origin/main").Trim().Should().Be(_headSha);
        Git(checkout.Path, "diff origin/main...HEAD").Should().BeEmpty("the head and the base are the same commit here");
        Git(checkout.Path, "for-each-ref --format=%(refname)").Should().NotContain("refs/heads/", "no local branch exists to commit onto");
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
        Git(_clonePath, $"for-each-ref {PrReviewCheckoutPlan.PinRefPrefix}").Should().BeEmpty("a failed cut leaves no pin behind either");
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
        Git(_barePath, $"for-each-ref {PrReviewCheckoutPlan.PinRefPrefix}").Should().BeEmpty("removing the checkout releases its pin");
        Directory.Exists(linked).Should().BeTrue("a sibling linked worktree is not touched");
        Directory.Exists(_barePath).Should().BeTrue();
        Git(_barePath, "rev-parse refs/remotes/origin/main").Trim().Should().Be(_headSha, "the repository's refs are untouched");
        Git(_barePath, "worktree list").Should().Contain("wt-linked-sibling");
    }

    [Fact]
    public async Task Any_path_that_is_not_a_checkout_cut_for_this_repository_is_refused_and_survives()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string beside = Path.GetDirectoryName(_clonePath)!;

        // A sibling that looks like a standalone clone but borrows no objects from this repository.
        string lookalike = Path.Combine(beside, "wt-lookalike");
        Git(_root, $"clone \"{_originPath}\" \"{lookalike}\"");
        // A sibling a person cut with `git clone --shared`: a real .git directory and the very same
        // alternates entry, but this manager never made it, so the repository holds no pin for it.
        string sharedSibling = Path.Combine(beside, "wt-personal");
        Git(_root, $"clone --shared \"{_clonePath}\" \"{sharedSibling}\"");
        File.ReadAllText(PrReviewCheckoutPlan.AlternatesFile(sharedSibling)).Should().Contain("objects", "the fixture must carry an alternates entry");
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

        foreach (string path in new[] { _clonePath, _barePath, lookalike, sharedSibling, elsewhere, plain, _root })
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
