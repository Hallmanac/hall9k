using FluentAssertions;
using Hall9k.Domain.Shared.ValueObjects;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The one predicate every branch string carried by a replicated event is checked against before
/// it becomes a git argument (security review idea 6be68ee2, process-injection finding 2):
/// extracted from <c>BranchNameTemplate.EnsureLegalBranchName</c> so <c>GitWorktreeManager</c>,
/// <c>MergedBranchCleanup</c>, <c>StackedParentWatch</c> and <c>ReviewEngine</c> all refuse the
/// identical shapes a rendered template already could not produce.
/// </summary>
public sealed class GitArgumentValidationTests
{
    [Theory]
    [InlineData("--upload-pack=touch/tmp/pwned", "cannot begin with '-'")]
    [InlineData("-oProxyCommand=x", "cannot begin with '-'")]
    public void An_option_shaped_value_is_refused(string branch, string rule)
    {
        GitArgumentValidation.IsLegalBranchName(branch, out string? refusalReason).Should().BeFalse();
        refusalReason.Should().Contain(rule);
    }

    /// <summary>
    /// `git fetch origin -- <branch>` stops an option-shaped value from being read as one, but the
    /// predicate is what actually stops a refspec-shaped value: <c>--</c> alone does not, so this is
    /// the one real defence against it (see the AC's own note in <c>StackedParentWatch</c> and
    /// <c>ReviewEngine</c>).
    /// </summary>
    [Theory]
    [InlineData("+refs/heads/main:refs/heads/injected")]
    [InlineData("refs/heads/main:refs/heads/injected")]
    public void A_refspec_shaped_value_is_refused(string branch)
    {
        GitArgumentValidation.IsLegalBranchName(branch, out string? refusalReason).Should().BeFalse();
        refusalReason.Should().NotBeNull();
    }

    [Theory]
    [InlineData("task/has space")]
    [InlineData("task/has\ttab")]
    [InlineData("task/has\nnewline")]
    public void A_whitespace_bearing_value_is_refused(string branch)
    {
        GitArgumentValidation.IsLegalBranchName(branch, out string? refusalReason).Should().BeFalse();
        refusalReason.Should().Contain("git does not allow");
    }

    /// <summary>
    /// Legal to git — a leading <c>+</c> is an ordinary ref-name character — but refused anyway,
    /// because this exact value reaches git in a fetch or push refspec position, where a leading
    /// <c>+</c> forces the update instead of asking for a fast-forward. The one rule
    /// <c>BranchNameTemplate</c>'s own check never needed.
    /// </summary>
    [Fact]
    public void A_leading_plus_is_refused_even_though_git_allows_it_in_a_ref_name()
    {
        GitArgumentValidation.IsLegalBranchName("+task/feature", out string? refusalReason).Should().BeFalse();
        refusalReason.Should().Contain("cannot begin with '+'");
    }

    /// <summary>
    /// The one shape a rendered template can produce that this predicate must still accept: the
    /// run-suffixed collision retry name (<c>GitWorktreeManager.ResolveBranchNameAsync</c>). Proves
    /// the extraction dropped only the length ceiling, not a character rule that would also catch
    /// legitimate retried branches.
    /// </summary>
    [Fact]
    public void A_retry_suffixed_branch_is_accepted()
    {
        GitArgumentValidation.IsLegalBranchName("task/0abcdef1-add-rate-limiting-r4a1b", out string? refusalReason)
            .Should().BeTrue();
        refusalReason.Should().BeNull();
    }

    /// <summary>
    /// The length ceiling <c>BranchNameTemplate</c> still enforces for ITSELF is deliberately absent
    /// here: an inbound branch is never retried with that suffix, so a long-but-otherwise-legal value
    /// must not be refused for a reason that only applies to a project's own template.
    /// </summary>
    [Fact]
    public void A_long_value_past_branch_name_templates_own_ceiling_is_still_accepted()
    {
        string longBranch = "task/" + new string('a', 250);

        GitArgumentValidation.IsLegalBranchName(longBranch, out string? refusalReason).Should().BeTrue();
        refusalReason.Should().BeNull();
    }

    [Theory]
    [InlineData("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2", true)]
    [InlineData("A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2", true)]
    [InlineData(
        "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
        true)]
    [InlineData("a1b2c3", false)]
    [InlineData("--upload-pack=touch /tmp/pwned", false)]
    [InlineData("a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2g", false)]
    [InlineData("", false)]
    public void IsLegalCommitSha_accepts_only_40_or_64_hex_characters(string value, bool expected)
    {
        GitArgumentValidation.IsLegalCommitSha(value).Should().Be(expected);
    }

    [Fact]
    public void Printable_truncates_and_masks_control_characters_for_a_log_line()
    {
        string withControlCharacter = "task/\u0007hidden";

        string printed = GitArgumentValidation.Printable(withControlCharacter);

        printed.Should().NotContain("\u0007");
        printed.Should().Contain("?");
    }
}
