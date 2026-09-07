using FluentAssertions;
using Hall9k.Daemon.Execution;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The base a stacked child's pull request actually opens against, when its parent's branch is
/// already gone from origin (adversarial review, cycle 6). This is the stacked edge's mainline
/// race: the child dispatches at the parent's Delivered, so the parent's merge — and the branch
/// deletion its closeout performs unconditionally — routinely lands during the child's own
/// hours-long build and review pipeline. <c>gh pr create --base &lt;deleted branch&gt;</c> is a raw
/// 422 that failed the run and the task, and the retry inherited the same frozen base and failed
/// identically, forever: every retarget and replay mechanism this feature has is reachable only
/// from closeout's inspection of an already-open pull request.
/// <para>
/// The decision is asserted here rather than the whole open path, deliberately: reaching
/// <c>gh pr create</c> at all needs a real GitHub origin, while what regresses is the direction
/// this maps an unreadable remote in. A remote git could not READ is not a branch that is gone
/// (AGENTS.md's never-guess rule), and guessing a live parent's branch away would open a stacked
/// pull request against the wrong base — the same defect in the opposite direction.
/// </para>
/// </summary>
public sealed class StackedPullRequestOpenBaseTests
{
    private const string ParentBranch = "task/parent-slice-one";

    /// <summary>Exit 2 is git's own documented "no matching ref" from <c>ls-remote --exit-code</c>.</summary>
    [Fact]
    public void A_parent_branch_missing_from_origin_moves_the_pull_request_onto_the_project_base()
        => PullRequestOpener.OpenBaseFor(ParentBranch, "main", lsRemoteExitCode: 2)
            .Should().Be("main",
                "the parent merged mid-build and its closeout deleted the branch, so opening against it is a "
                + "422 — and the project's base is exactly where the retarget would have put this pull request");

    [Fact]
    public void A_parent_branch_still_on_origin_keeps_the_recorded_base()
        => PullRequestOpener.OpenBaseFor(ParentBranch, "main", lsRemoteExitCode: 0)
            .Should().Be(ParentBranch, "which is what forms the stack on GitHub");

    /// <summary>
    /// Every other exit code is git failing to answer — an unreachable origin, a credential prompt,
    /// a repository it cannot read — which is not the same fact as a missing branch. The recorded
    /// base stands, gh fails the run honestly, and h9k task retry asks again.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public void An_unreadable_origin_is_not_a_missing_branch(int exitCode)
        => PullRequestOpener.OpenBaseFor(ParentBranch, "main", exitCode)
            .Should().Be(ParentBranch,
                "a failed read is not evidence the parent's branch is gone, and retargeting on a guess would "
                + "aim a stacked pull request at the wrong base while the parent is still open");
}
