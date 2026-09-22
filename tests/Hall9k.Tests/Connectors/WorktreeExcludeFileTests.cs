using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The repository-local ignore list the generated decisions and lessons documents go on (idea
/// d805fd8b, piece 2). Two halves worth proving separately: composing the block, which is where
/// idempotence and anchoring live, and resolving which file it belongs in, which is where a
/// linked worktree's two pointer files are read.
/// <para>
/// The resolve half builds git's pointer files by hand in a temporary directory rather than
/// creating a repository to run git against (the testing rule, Brian 2026-09-13). That is the
/// whole reason <see cref="WorktreeExcludeFile.Resolve"/> reads them instead of shelling out to
/// <c>git rev-parse --git-path info/exclude</c>.
/// </para>
/// </summary>
public sealed class WorktreeExcludeFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("h9k-exclude-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Composing_into_an_empty_list_anchors_every_path_at_the_checkout_root()
    {
        string? composed = WorktreeExcludeFile.Compose(existing: null, ["decisions.md", "lessons.md"]);

        composed.Should().Be(
            WorktreeExcludeFile.BlockHeading + "\n/decisions.md\n/lessons.md\n",
            "an unanchored 'decisions.md' would also ignore a docs/decisions.md the project tracks");
    }

    [Fact]
    public void An_existing_list_is_carried_through_untouched_with_the_block_appended()
    {
        string? composed = WorktreeExcludeFile.Compose("# somebody else's line\r\n*.local\r\n", ["decisions.md"]);

        composed.Should().StartWith("# somebody else's line\r\n*.local\r\n",
            "this file belongs to the repository, and reflowing its lines to make room for ours would "
            + "read as an unexplained whole-file change");
        composed.Should().EndWith(WorktreeExcludeFile.BlockHeading + "\n/decisions.md\n");
    }

    [Fact]
    public void A_list_not_ending_in_a_newline_gets_one_before_the_block()
    {
        string? composed = WorktreeExcludeFile.Compose("*.local", ["decisions.md"]);

        composed.Should().Be("*.local\n" + WorktreeExcludeFile.BlockHeading + "\n/decisions.md\n");
    }

    /// <summary>
    /// Every dispatch composes, and every worktree of one clone shares the file, so "already
    /// there" has to be the common answer rather than a second block each time.
    /// </summary>
    [Fact]
    public void A_list_that_already_carries_the_block_needs_no_write_at_all()
    {
        string first = WorktreeExcludeFile.Compose(null, ["decisions.md", "lessons.md"])!;

        WorktreeExcludeFile.Compose(first, ["decisions.md", "lessons.md"]).Should().BeNull();
    }

    [Fact]
    public void A_plain_repository_excludes_through_its_own_git_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        WorktreeExcludeFile.Resolve(_root).Should().Be(Path.Combine(_root, ".git", "info", "exclude"));
    }

    /// <summary>
    /// The case every dispatched run is in. Git resolves <c>info/exclude</c> against the common
    /// directory, so the answer is the shared clone's file rather than one inside this worktree's
    /// own git directory — which is exactly why the block is written idempotently.
    /// </summary>
    [Fact]
    public void A_linked_worktree_excludes_through_the_clone_every_worktree_shares()
    {
        string clone = Path.Combine(_root, "repo", "project.git");
        string gitDirectory = Path.Combine(clone, "worktrees", "wt-abc");
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(gitDirectory, "commondir"), "../..\n");
        string worktree = Path.Combine(_root, "repo", "wt-abc");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDirectory}\n");

        WorktreeExcludeFile.Resolve(worktree).Should().Be(Path.Combine(clone, "info", "exclude"));
    }

    [Fact]
    public void A_directory_that_is_not_a_checkout_at_all_resolves_to_nothing()
    {
        WorktreeExcludeFile.Resolve(_root).Should().BeNull(
            "a guessed path would be an exclude file git never reads, which is worse than saying so");
    }

    [Fact]
    public void A_git_pointer_file_that_does_not_read_as_one_resolves_to_nothing()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "this is not a gitdir pointer\n");

        WorktreeExcludeFile.Resolve(_root).Should().BeNull();
    }
}
