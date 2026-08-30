using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Infrastructure.Ids;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Daemon;

public sealed class GitWorktreeManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _originPath;
    private readonly string _seedPath;
    private readonly string _repositoryPath;
    private readonly GitWorktreeManager _manager = new(NullLogger<GitWorktreeManager>.Instance);

    public GitWorktreeManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hall9k-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // A bare "origin" with one commit on main, cloned locally — so origin/main exists,
        // mirroring the real layout (bare repo + sibling worktrees).
        _originPath = Path.Combine(_root, "origin.git");
        _seedPath = Path.Combine(_root, "seed");
        Git(_root, $"init --bare -b main \"{_originPath}\"");
        Git(_root, $"clone \"{_originPath}\" \"{_seedPath}\"");
        File.WriteAllText(Path.Combine(_seedPath, "README.md"), "# seed\n");
        Git(_seedPath, "add -A");
        Git(_seedPath, "-c user.name=Test -c user.email=test@test commit -m init");
        Git(_seedPath, "push origin main");

        _repositoryPath = Path.Combine(_root, "repo");
        Git(_root, $"clone \"{_originPath}\" \"{_repositoryPath}\"");
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

    /// <summary>
    /// h9k task work runs a second, unsynchronized GitWorktreeManager in the CLI process
    /// against the same repository the daemon's own singleton touches (adversarial review,
    /// cycle 4) — the in-process semaphore alone cannot serialize two separate instances, so
    /// this proves the cross-process file lock does.
    /// </summary>
    [Fact]
    public async Task Five_parallel_creations_from_two_unsynchronized_manager_instances_all_succeed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        GitWorktreeManager other = new(NullLogger<GitWorktreeManager>.Instance);

        Worktree[] worktrees = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => (i % 2 == 0 ? _manager : other).CreateAsync(Request($"Cross process task {i}"), cts.Token)));

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
    public async Task Checkout_existing_resets_a_retained_worktree_to_a_rewritten_remote_tip()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Rewrite me remotely"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(first.Path, $"push -q origin {first.Branch}");

        // Another node's narrative follow-up rebased the branch and force-pushed the
        // rewritten history (Decisions Log #26) — the retained worktree's tip is stale.
        string rewriter = Path.Combine(_root, "rewriter");
        Git(_root, $"clone \"{Path.Combine(_root, "origin.git")}\" \"{rewriter}\"");
        Git(rewriter, $"checkout -q {first.Branch}");
        File.WriteAllText(Path.Combine(rewriter, "WORK.md"), "first run, review fix folded in\n");
        Git(rewriter, "add -A");
        Git(rewriter, "-c user.name=Rev -c user.email=r@r commit -q --amend -m \"work, absorbed\"");
        Git(rewriter, $"push -q --force origin {first.Branch}");

        Worktree followUp = await _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, first.Branch, taskId, DomainId.New()), cts.Token);

        Path.GetFileName(followUp.Path).Should().Be(Path.GetFileName(first.Path), "the retained worktree is reused");
        (_, string localTip) = TryGit(followUp.Path, "rev-parse HEAD");
        (_, string remoteTip) = TryGit(followUp.Path, $"rev-parse origin/{first.Branch}");
        localTip.Trim().Should().Be(remoteTip.Trim(),
            "a diverged branch was rewritten on origin, and the remote tip is the pull request's truth");
        File.ReadAllText(Path.Combine(followUp.Path, "WORK.md")).Should().Contain("folded in");
    }

    [Fact]
    public async Task Checkout_existing_never_touches_a_worktree_holding_uncommitted_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Rescue my stranded work"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(first.Path, $"push -q origin {first.Branch}");

        // The branch was rewritten on origin, AND the retained worktree holds a stranded
        // attempt's uncommitted work (the retry-rescue case). A reset here would destroy
        // exactly what the resume came back for — uncommitted work vetoes every
        // destructive path (PR #10 review finding).
        string rewriter = Path.Combine(_root, $"rewriter-{Guid.NewGuid():N}");
        Git(_root, $"clone \"{Path.Combine(_root, "origin.git")}\" \"{rewriter}\"");
        Git(rewriter, $"checkout -q {first.Branch}");
        File.WriteAllText(Path.Combine(rewriter, "WORK.md"), "rewritten remotely\n");
        Git(rewriter, "add -A");
        Git(rewriter, "-c user.name=Rev -c user.email=r@r commit -q --amend -m \"work, absorbed\"");
        Git(rewriter, $"push -q --force origin {first.Branch}");

        File.WriteAllText(Path.Combine(first.Path, "STRANDED.md"), "uncommitted rescue target\n");

        Worktree followUp = await _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, first.Branch, taskId, DomainId.New()), cts.Token);

        File.Exists(Path.Combine(followUp.Path, "STRANDED.md")).Should().BeTrue(
            "uncommitted work is never discarded silently, whatever origin did");
        File.ReadAllText(Path.Combine(followUp.Path, "WORK.md")).Should().Be("first run\n",
            "a dirty worktree is kept untouched — no reset, no fast-forward");
    }

    [Fact]
    public async Task Checkout_existing_keeps_local_commits_that_never_reached_origin()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        Guid taskId = DomainId.New();

        Worktree first = await _manager.CreateAsync(
            new WorktreeRequest(_repositoryPath, "main", taskId, DomainId.New(), "Strand my work"), cts.Token);
        File.WriteAllText(Path.Combine(first.Path, "WORK.md"), "first run\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(first.Path, $"push -q origin {first.Branch}");

        // A follow-up committed a fix but its push never landed (the 2026-08-17 stranded-
        // work incident): the local tip is strictly ahead of origin, not diverged.
        File.WriteAllText(Path.Combine(first.Path, "FIX.md"), "unpushed fix\n");
        Git(first.Path, "add -A");
        Git(first.Path, "-c user.name=Test -c user.email=t@t commit -qm fix");

        Worktree followUp = await _manager.CheckoutExistingAsync(
            new FollowUpWorktreeRequest(_repositoryPath, first.Branch, taskId, DomainId.New()), cts.Token);

        File.Exists(Path.Combine(followUp.Path, "FIX.md")).Should().BeTrue(
            "a local tip strictly ahead of origin holds unpushed work; resetting would destroy it");
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

    /// <summary>
    /// No test at all covered this before (cycle-1 conformance finding, `PrReviewEngine.cs:50`):
    /// the fetch reads GitHub's own synthetic <c>refs/pull/&lt;n&gt;/head</c>, simulated here by
    /// writing that ref directly into the bare origin, exactly as GitHub itself maintains it.
    /// </summary>
    [Fact]
    public async Task Creates_a_detached_read_only_checkout_of_the_pull_requests_head()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string sha = SeedHeadSha();
        Git(_originPath, $"update-ref refs/pull/7/head {sha}");

        Worktree worktree = await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_repositoryPath, 7, DomainId.New(), DomainId.New()), cts.Token);

        worktree.Branch.Should().Be("pr/7");
        Directory.Exists(worktree.Path).Should().BeTrue();
        (_, string headSha) = TryGit(worktree.Path, "rev-parse HEAD");
        headSha.Trim().Should().Be(sha);

        (int exitCode, _) = TryGit(worktree.Path, "symbolic-ref -q HEAD");
        exitCode.Should().NotBe(0, "the checkout must be detached, never on a branch a session could commit onto");

        (_, string refs) = TryGit(_repositoryPath, "for-each-ref refs/remotes/origin/pr-review");
        refs.Should().Contain("refs/remotes/origin/pr-review/7", "the fetch names its own tracking ref, never a local branch");
    }

    /// <summary>
    /// The ref-leak fix (adversarial review, cycle 1, `GitWorktreeManager.cs:117`): nothing used
    /// to delete this ref at all, so a project used for routine pr-review work accumulated one
    /// per pull request ever reviewed. Deleting one leaves an unrelated pull request's own ref
    /// alone.
    /// </summary>
    [Fact]
    public async Task Deleting_a_pr_reviews_tracking_ref_leaves_every_other_one_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string sha = SeedHeadSha();
        Git(_originPath, $"update-ref refs/pull/7/head {sha}");
        Git(_originPath, $"update-ref refs/pull/9/head {sha}");
        await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_repositoryPath, 7, DomainId.New(), DomainId.New()), cts.Token);
        await _manager.CreatePrReviewCheckoutAsync(
            new PrReviewWorktreeRequest(_repositoryPath, 9, DomainId.New(), DomainId.New()), cts.Token);

        await _manager.DeletePrReviewTrackingRefAsync(_repositoryPath, 7, cts.Token);

        (_, string refs) = TryGit(_repositoryPath, "for-each-ref refs/remotes/origin/pr-review");
        refs.Should().NotContain("refs/remotes/origin/pr-review/7");
        refs.Should().Contain("refs/remotes/origin/pr-review/9", "only the named pull request's ref is deleted");
    }

    private string SeedHeadSha() => TryGit(_seedPath, "rev-parse HEAD").Output.Trim();

    /// <summary>
    /// The reading checkout is fetched through the repository it actually resolves refs through,
    /// which is the home's bare clone for a <c>repo/dev</c> cut from one — never whatever
    /// repository path the project happens to record, since <c>--keep-repo-path</c> and
    /// <c>h9k project set --repo</c> both leave those two naming different clones. Origin incident
    /// (2026-08-23): the third cycle of the project-home branch's pre-PR review found the fetch
    /// going to the recorded path, so the freshness reported for repo/dev was computed from refs
    /// nothing had updated and logged as current.
    /// </summary>
    [Fact]
    public async Task Refreshing_a_reading_checkout_fetches_the_repository_that_checkout_reads()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string bare = Path.Combine(_root, "home", "repo", "project.git");
        Git(_root, $"clone --bare \"{_originPath}\" \"{bare}\"");
        Git(bare, "config remote.origin.fetch +refs/heads/*:refs/remotes/origin/*");
        Git(bare, "fetch origin");
        string dev = Path.Combine(_root, "home", "repo", "dev");
        Git(bare, $"worktree add \"{dev}\" main");

        PushToOrigin("RULES.md", "# the card rules, as of today\n");

        CheckoutRefresh refresh = await _manager.RefreshReadingCheckoutAsync(dev, "main", cts.Token);

        refresh.UpToDate.Should().BeTrue();
        refresh.Detail.Should().Contain("fast-forwarded 1 commit(s) to origin/main");
        File.Exists(Path.Combine(dev, "RULES.md")).Should().BeTrue();

        // The unrelated clone a project may still record as its repository path. Nothing here
        // touched it, which is the point: fetching there is what left the reading checkout stale.
        (_, string elsewhere) = TryGit(_repositoryPath, "rev-list --count HEAD..origin/main");
        elsewhere.Trim().Should().Be("0", "that clone was never fetched, so it still sees the old tip");
    }

    /// <summary>
    /// The same, for the ordinary clone a project registered before homes existed points at. Its
    /// repository is its own root rather than the <c>.git</c> directory inside it, so the lock
    /// taken here is the one every other method takes for the same repository.
    /// </summary>
    [Fact]
    public async Task Refreshing_an_ordinary_clone_fast_forwards_it_too()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        PushToOrigin("RULES.md", "# the card rules, as of today\n");

        CheckoutRefresh refresh = await _manager.RefreshReadingCheckoutAsync(_repositoryPath, "main", cts.Token);

        refresh.UpToDate.Should().BeTrue();
        File.Exists(Path.Combine(_repositoryPath, "RULES.md")).Should().BeTrue();
    }

    /// <summary>
    /// A path that is not a checkout at all is reported as unobserved rather than answered for:
    /// the caller logs what came back, and "up to date" would be a claim nothing here can make.
    /// </summary>
    [Fact]
    public async Task Refreshing_something_that_is_not_a_checkout_says_so()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(1));
        string notARepository = Path.Combine(_root, "not-a-repository");
        Directory.CreateDirectory(notARepository);

        CheckoutRefresh refresh = await _manager.RefreshReadingCheckoutAsync(notARepository, "main", cts.Token);

        refresh.UpToDate.Should().BeFalse();
        refresh.Detail.Should().Contain("unobserved");
    }

    /// <summary>Moves the remote's primary branch on, the way anybody else's merge would.</summary>
    private void PushToOrigin(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_seedPath, fileName), content);
        Git(_seedPath, "add -A");
        Git(_seedPath, "-c user.name=Test -c user.email=test@test commit -m \"rules moved on\"");
        Git(_seedPath, "push origin main");
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
