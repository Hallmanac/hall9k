using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// Pins the git command sequence that cuts a pull-request review checkout, with no process and no
/// repository: the shape the checkout takes is decided entirely by these four strings.
/// </summary>
public sealed class PrReviewCheckoutPlanTests
{
    private const string Checkout = "/home/p/repo/wt-aaaaaaaa-bbbbbbbb";
    private const string Repository = "/home/p/repo/project.git";
    private const string TrackingRef = "refs/remotes/origin/pr-review/7";

    [Fact]
    public void The_sequence_is_init_then_long_paths_then_one_fetch_by_path_then_a_detached_checkout()
    {
        IReadOnlyList<PrReviewCheckoutStep> steps = PrReviewCheckoutPlan.Steps(Checkout, Repository, TrackingRef);

        steps.Should().Equal(
            new PrReviewCheckoutStep(Repository, $"init --quiet --template= \"{Checkout}\""),
            new PrReviewCheckoutStep(Checkout, "config core.longpaths true"),
            new PrReviewCheckoutStep(
                Checkout,
                $"-c gc.auto=0 fetch --quiet --no-tags \"{Repository}\" \"+refs/remotes/*:refs/remotes/*\" \"+refs/tags/*:refs/tags/*\""),
            new PrReviewCheckoutStep(
                Checkout,
                "-c filter.lfs.smudge= -c filter.lfs.process= -c filter.lfs.required=false "
                + $"checkout --quiet --detach {TrackingRef}"));
    }

    [Fact]
    public void The_alternates_file_lives_in_the_checkouts_own_git_directory()
    {
        PrReviewCheckoutPlan.AlternatesFile(Checkout).Should().Be(
            Path.Combine(Checkout, ".git", "objects", "info", "alternates"));
    }

    [Fact]
    public void The_pin_ref_is_named_for_the_checkout_directory_outside_the_remote_tracking_namespace()
    {
        PrReviewCheckoutPlan.PinRef(Checkout).Should().Be("refs/hall9k/pr-review-checkouts/wt-aaaaaaaa-bbbbbbbb");
        PrReviewCheckoutPlan.PinRef(Checkout + "/").Should().Be(PrReviewCheckoutPlan.PinRef(Checkout));
        PrReviewCheckoutPlan.PinRef(Checkout).Should().NotStartWith("refs/remotes/", "fetch --prune origin removes refs under it");
    }
}
