using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.ProjectHomes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The bare-clone recipe absorbed from backlog 43, proved against a real repository on this
/// disk: the refspec correction, the dev/ worktree, worktree creation working with no further
/// setup, and idempotence.
/// <para>
/// The remote is a bare repository in a temp directory, which is a perfectly ordinary git
/// remote — the recipe never assumes a network, and neither does this.
/// </para>
/// </summary>
public sealed class RepoMaterialiserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-mat-{Guid.NewGuid():N}");
    private readonly string _home;
    private readonly string _seed;
    private readonly Uri _remote;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(3));

    public RepoMaterialiserTests()
    {
        Directory.CreateDirectory(_root);
        _home = Path.Combine(_root, "home");

        string origin = Path.Combine(_root, "origin.git");
        _seed = Path.Combine(_root, "seed");
        Git(_root, $"init --bare -b main \"{origin}\"");
        Git(_root, $"clone \"{origin}\" \"{_seed}\"");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "# seed\n");
        File.WriteAllText(Path.Combine(_seed, "AGENTS.md"), "# the repo's own deep layer\n");
        Git(_seed, "add -A");
        Git(_seed, "-c user.name=Test -c user.email=test@test commit -qm init");
        Git(_seed, "push -q origin main");

        // A second branch upstream, so the local-branch assertion has something to catch: an
        // uncorrected bare clone maps every remote branch onto a local one, and with a
        // single-branch remote that is indistinguishable from the dev worktree's own main.
        Git(_seed, "checkout -q -b feature/second");
        Git(_seed, "push -q origin feature/second");
        Git(_seed, "checkout -q main");
        _remote = new Uri(origin);
    }

    [Fact]
    public async Task The_clone_maps_remote_branches_into_refs_remotes_origin()
    {
        IReadOnlyList<ProjectHomeStep> steps = await MaterialiseAsync();

        steps.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);

        string bare = ProjectHomePaths.BareRepository(_home, "hall9k");
        (int _, string refspec) = TryGit(bare, "config remote.origin.fetch");
        refspec.Trim().Should().Be(
            "+refs/heads/*:refs/remotes/origin/*",
            "git clone --bare writes +refs/heads/*:refs/heads/*, which is what makes a bare clone behave "
            + "unlike a checkout");

        (int originMain, _) = TryGit(bare, "rev-parse --verify --quiet refs/remotes/origin/main^{commit}");
        originMain.Should().Be(0, "origin/main is what --no-track branch creation starts from");

        (_, string localBranches) = TryGit(bare, "for-each-ref --format=%(refname:short) refs/heads/");
        string[] locals = localBranches.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        locals.Should().ContainSingle(
            "the local branches the bare clone invented are deleted; the only one left is the one the "
            + "dev worktree checked out")
            .Which.Should().Be("main");
    }

    [Fact]
    public async Task A_dev_worktree_on_the_primary_branch_is_checked_out()
    {
        await MaterialiseAsync();

        string dev = ProjectHomePaths.DevWorktree(_home);
        File.Exists(Path.Combine(dev, "README.md")).Should().BeTrue();
        (_, string current) = TryGit(dev, "branch --show-current");
        current.Trim().Should().Be("main");
    }

    [Fact]
    public async Task Worktree_creation_works_against_it_with_no_further_setup()
    {
        await MaterialiseAsync();

        GitWorktreeManager manager = new(NullLogger<GitWorktreeManager>.Instance);
        Worktree worktree = await manager.CreateAsync(
            new WorktreeRequest(
                ProjectHomePaths.BareRepository(_home, "hall9k"),
                "main",
                DomainId.New(),
                DomainId.New(),
                "Prove the materialised repo dispatches",
                BranchNameTemplate.Default,
                ExternalReference: null),
            _cancellation.Token);

        worktree.StartPoint.Should().Be("origin/main", "the corrected refspec is what makes this possible");
        Directory.Exists(worktree.Path).Should().BeTrue();
        Path.GetDirectoryName(worktree.Path).Should().Be(
            ProjectHomePaths.RepoDirectory(_home), "worktrees land beside the bare clone, inside the home");
        File.Exists(Path.Combine(worktree.Path, "README.md")).Should().BeTrue();
    }

    [Fact]
    public async Task Rerunning_reports_and_exits_rather_than_re_cloning()
    {
        await MaterialiseAsync();
        string bare = ProjectHomePaths.BareRepository(_home, "hall9k");
        File.WriteAllText(Path.Combine(bare, "MARKER"), "the same clone, not a fresh one");

        IReadOnlyList<ProjectHomeStep> second = await MaterialiseAsync();

        second.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.AlreadyThere && step.Message.Contains("not re-cloned"));
        second.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.AlreadyThere
            && step.Message.Contains("repo/dev already checked out")
            && step.Message.Contains("already up to date with origin/main"));
        File.Exists(Path.Combine(bare, "MARKER")).Should().BeTrue("the clone was left exactly where it was");
    }

    /// <summary>
    /// The repair the class claims to be, against the state a deleted <c>dev/</c> actually leaves:
    /// the directory is gone but the bare clone still has it registered, and git refuses to create
    /// it again ("is a missing but already registered worktree") until something prunes. Nothing
    /// here prunes by hand, because <c>h9k project init</c> is the only command a person is told
    /// to run. Origin incident (2026-08-23): the second cycle of this branch's pre-PR review
    /// deleted repo/dev, ran the documented repair, and got a Failed step blaming the base branch
    /// on that run and on every re-run after it.
    /// </summary>
    [Fact]
    public async Task A_missing_dev_worktree_beside_an_existing_clone_is_repaired_rather_than_re_cloned()
    {
        await MaterialiseAsync();
        string bare = ProjectHomePaths.BareRepository(_home, "hall9k");
        string dev = ProjectHomePaths.DevWorktree(_home);
        File.WriteAllText(Path.Combine(bare, "MARKER"), "the same clone, not a fresh one");
        Directory.Delete(dev, recursive: true);

        IReadOnlyList<ProjectHomeStep> repaired = await MaterialiseAsync();

        repaired.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        File.Exists(Path.Combine(dev, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(bare, "MARKER")).Should().BeTrue("the clone was repaired in place, not started over");
    }

    /// <summary>
    /// <c>dev/</c> is the working directory a reading session is spawned into and the checkout the
    /// generated AGENTS.md sends a human to, so cutting it once and never touching it again serves
    /// months-old code to both. Origin incident (2026-08-23): the second cycle of this branch's
    /// pre-PR review followed a card being authored from rules as of the commit repo/dev was
    /// created at, with nothing anywhere reporting the checkout was behind.
    /// </summary>
    [Fact]
    public async Task An_existing_dev_worktree_is_fast_forwarded_to_the_remote()
    {
        await MaterialiseAsync();
        PushToMain("SKILL.md", "# the card rules, as of today\n", "rules moved on");

        IReadOnlyList<ProjectHomeStep> refreshed = await MaterialiseAsync();

        refreshed.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.Created
            && step.Message.Contains("fast-forwarded 1 commit(s) to origin/main"));
        File.Exists(Path.Combine(ProjectHomePaths.DevWorktree(_home), "SKILL.md")).Should().BeTrue();
    }

    /// <summary>
    /// The recreate path owes the same catch-up the refresh path does. A deleted <c>dev/</c>
    /// leaves <c>refs/heads/&lt;base&gt;</c> behind in the bare clone at whatever commit it was
    /// abandoned on, so <c>git worktree add</c> puts the new checkout back on that commit rather
    /// than on the remote — under a step that reads as a successful repair. Origin incident
    /// (2026-08-23): the third cycle of this branch's pre-PR review found init recreating dev/
    /// beside an existing clone with neither a fetch nor a fast-forward in front of it.
    /// </summary>
    [Fact]
    public async Task A_recreated_dev_worktree_catches_up_with_the_remote_not_the_local_branch()
    {
        await MaterialiseAsync();
        string dev = ProjectHomePaths.DevWorktree(_home);
        Directory.Delete(dev, recursive: true);
        PushToMain("SKILL.md", "# the card rules, as of today\n", "rules moved on");

        IReadOnlyList<ProjectHomeStep> recreated = await MaterialiseAsync();

        recreated.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        recreated.Should().Contain(step =>
            step.Outcome == ProjectHomeOutcome.Created
            && step.Message.Contains("repo/dev checked out on main")
            && step.Message.Contains("fast-forwarded 1 commit(s) to origin/main"));
        File.Exists(Path.Combine(dev, "SKILL.md")).Should().BeTrue(
            "the local main the deleted worktree left behind is not the remote");
    }

    /// <summary>
    /// A refresh, never a reset: whatever is under <c>dev/</c> uncommitted is somebody's, and the
    /// step that cannot fast-forward past it says how far behind the checkout is and which commit
    /// it is therefore serving. An unreported stale checkout is the whole defect.
    /// </summary>
    [Fact]
    public async Task A_dev_worktree_that_cannot_be_fast_forwarded_is_left_alone_and_says_how_far_behind_it_is()
    {
        await MaterialiseAsync();
        string dev = ProjectHomePaths.DevWorktree(_home);
        File.WriteAllText(Path.Combine(dev, "README.md"), "# edited by hand, never committed\n");
        PushToMain("README.md", "# moved on upstream\n", "conflicting change");

        IReadOnlyList<ProjectHomeStep> refreshed = await MaterialiseAsync();

        refreshed.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
        ProjectHomeStep left = refreshed.Should()
            .ContainSingle(step => step.Outcome == ProjectHomeOutcome.Skipped).Subject;
        left.Message.Should().Contain("1 commit(s) behind origin/main");
        left.Message.Should().Contain("left exactly as it is");
        File.ReadAllText(Path.Combine(dev, "README.md")).Should().Contain("edited by hand");
    }

    [Fact]
    public async Task A_remote_that_is_not_there_fails_with_the_command_that_fixes_it()
    {
        IReadOnlyList<ProjectHomeStep> steps = await RepoMaterialiser.MaterialiseAsync(
            _home, "hall9k", new Uri(Path.Combine(_root, "nothing-here.git")), "main", _cancellation.Token);

        steps.Should().ContainSingle(step => step.Outcome == ProjectHomeOutcome.Failed);
        steps[^1].Message.Should().Contain("could not clone");
        Directory.Exists(ProjectHomePaths.BareRepository(_home, "hall9k")).Should().BeFalse(
            "a failed clone takes its own half-written directory with it, so a re-run starts clean");
    }

    [Fact]
    public async Task A_project_with_no_remote_is_reported_rather_than_failed()
    {
        IReadOnlyList<ProjectHomeStep> steps = await RepoMaterialiser.MaterialiseAsync(
            _home, "hall9k", remote: null, "main", _cancellation.Token);

        steps.Should().ContainSingle(step => step.Outcome == ProjectHomeOutcome.Skipped);
        steps.Should().NotContain(step => step.Outcome == ProjectHomeOutcome.Failed);
    }

    private Task<IReadOnlyList<ProjectHomeStep>> MaterialiseAsync() =>
        RepoMaterialiser.MaterialiseAsync(_home, "hall9k", _remote, "main", _cancellation.Token);

    /// <summary>Moves the remote's primary branch on, the way anybody else's merge would.</summary>
    private void PushToMain(string fileName, string content, string message)
    {
        File.WriteAllText(Path.Combine(_seed, fileName), content);
        Git(_seed, "add -A");
        Git(_seed, $"-c user.name=Test -c user.email=test@test commit -qm \"{message}\"");
        Git(_seed, "push -q origin main");
    }

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
        _cancellation.Dispose();

        // git marks object/pack files read-only; Windows refuses to recursively delete them
        // until the attribute is cleared. Cleanup stays best-effort either way.
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
