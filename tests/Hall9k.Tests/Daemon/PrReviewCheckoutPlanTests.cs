using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Pins the git command sequence that cuts a pull-request review checkout, with no process and no
/// repository: the shape the checkout takes is decided entirely by these three strings.
/// </summary>
public sealed class PrReviewCheckoutPlanTests
{
    private const string Checkout = "/home/p/repo/wt-aaaaaaaa-bbbbbbbb";
    private const string Repository = "/home/p/repo/project.git";
    private const string TrackingRef = "refs/remotes/origin/pr-review/7";

    [Fact]
    public void The_sequence_is_init_then_one_fetch_by_path_then_a_detached_checkout()
    {
        IReadOnlyList<PrReviewCheckoutStep> steps = PrReviewCheckoutPlan.Steps(Checkout, Repository, TrackingRef);

        steps.Should().Equal(
            new PrReviewCheckoutStep(Repository, $"init --quiet --template= \"{Checkout}\""),
            new PrReviewCheckoutStep(
                Checkout,
                $"-c gc.auto=0 fetch --quiet --no-tags \"{Repository}\" \"+refs/remotes/*:refs/remotes/*\" \"+refs/tags/*:refs/tags/*\""),
            new PrReviewCheckoutStep(
                Checkout,
                "-c filter.lfs.smudge= -c filter.lfs.process= -c filter.lfs.required=false "
                + $"checkout --quiet --detach {TrackingRef}"));
    }

    [Fact]
    public void No_step_configures_a_remote_or_creates_a_branch_or_installs_a_hook()
    {
        string all = string.Join('\n', PrReviewCheckoutPlan.Steps(Checkout, Repository, TrackingRef).Select(step => step.Arguments));

        all.Should().NotContain("remote add").And.NotContain("clone").And.NotContain("branch").And.NotContain("checkout -b")
            .And.NotContain("worktree");
        all.Should().Contain("--template=", "an empty template is what leaves the new repository without hooks");
        all.Should().Contain("filter.lfs.smudge=", "there is no remote to download large files from");
    }

    [Fact]
    public void The_alternates_file_lives_in_the_checkouts_own_git_directory()
    {
        PrReviewCheckoutPlan.AlternatesFile(Checkout).Should().Be(
            Path.Combine(Checkout, ".git", "objects", "info", "alternates"));
    }
}
