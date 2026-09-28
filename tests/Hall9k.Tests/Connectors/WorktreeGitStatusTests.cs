using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// <see cref="WorktreeGitStatus.ParseNameStatus"/>'s own <c>git diff --name-status -z</c> parsing
/// — extracted out of <c>VerificationRunner</c> and shared with <c>RunLauncher</c>'s Security
/// docs-only skip (independent pre-PR review, cycle 1, adversarial lens, high: the Security skip
/// used to read <c>--name-only</c> instead, which reports only a rename's new path and let a file
/// moved out of a buildable tree into <c>docs/</c> read as docs-only).
/// </summary>
public sealed class WorktreeGitStatusTests
{
    [Fact]
    public void An_ordinary_change_yields_its_one_path()
    {
        string output = string.Join('\0', "M", "src/Auth.cs", "") ;

        WorktreeGitStatus.ParseNameStatus(output).Should().Equal("src/Auth.cs");
    }

    [Fact]
    public void A_rename_yields_both_its_old_and_new_path()
    {
        string output = string.Join('\0', "R100", "src/Auth/RequireOwner.cs", "docs/RequireOwner.cs", "");

        WorktreeGitStatus.ParseNameStatus(output).Should().Equal(
            ["src/Auth/RequireOwner.cs", "docs/RequireOwner.cs"],
            "a rename must be classified by both its old and new path, not the new path alone");
    }

    [Fact]
    public void A_copy_yields_both_its_source_and_new_path()
    {
        string output = string.Join('\0', "C100", "src/Base.cs", "src/Derived.cs", "");

        WorktreeGitStatus.ParseNameStatus(output).Should().Equal("src/Base.cs", "src/Derived.cs");
    }

    [Fact]
    public void A_mix_of_ordinary_changes_and_a_rename_keeps_every_path()
    {
        string output = string.Join(
            '\0', "M", "docs/readme.md", "R100", "src/old.cs", "src/new.cs", "D", "src/gone.cs", "");

        WorktreeGitStatus.ParseNameStatus(output).Should().Equal(
            "docs/readme.md", "src/old.cs", "src/new.cs", "src/gone.cs");
    }

    [Fact]
    public void A_truncated_trailing_record_is_dropped_rather_than_guessed_at()
    {
        string output = string.Join('\0', "M", "src/a.cs", "R100", "src/b.cs", "");

        WorktreeGitStatus.ParseNameStatus(output).Should().Equal(
            ["src/a.cs"],
            "the trailing rename record has no new-path field yet, so it is dropped rather than guessed at");
    }

    [Fact]
    public void An_empty_diff_yields_no_paths() =>
        WorktreeGitStatus.ParseNameStatus(string.Empty).Should().BeEmpty();
}
