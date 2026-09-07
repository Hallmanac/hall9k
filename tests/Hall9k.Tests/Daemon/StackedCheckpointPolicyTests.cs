using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The rule a stacked child's rebase checkpoint follows (task: a stacked child absorbs its
/// parent's post-delivery churn safely) — asserted here rather than through the engine because it
/// is a pure function of an observation and a budget, and because the interesting half is which
/// observations park a run rather than let it carry on. The mechanics the verdict then drives (the
/// `git rebase --onto`, the recorded spend, the gate over the moved tip) need a real repository and
/// live in <c>ReviewEngineTests</c>.
/// </summary>
public sealed class StackedCheckpointPolicyTests
{
    private const string ParentBranch = "task/parent-slice";

    [Fact]
    public void A_child_still_on_its_parents_head_owes_nothing()
    {
        StackedCheckpointVerdict verdict = Decide(StackedParentObservation.Aligned("nothing to replay"));

        verdict.Action.Should().Be(StackedCheckpointAction.Proceed);
        verdict.UpstreamCommit.Should().BeEmpty("nothing is replayed, so no boundary is claimed");
    }

    /// <summary>
    /// A failed read is not a fact and is not this run's fault — the identical stance the unstacked
    /// half of the same gate takes on a failed fetch. Parking here would park a run over a network
    /// blip, and closeout's own replay is the second reader once the branch is pushed.
    /// </summary>
    [Fact]
    public void A_read_that_failed_proceeds_rather_than_parking()
    {
        StackedCheckpointVerdict verdict = Decide(
            StackedParentObservation.Unobservable("a git call exceeded its deadline"));

        verdict.Action.Should().Be(StackedCheckpointAction.Proceed);
    }

    /// <summary>
    /// The one place this policy deliberately answers differently from closeout's reading of the
    /// same observation: closeout can wait for the next sweep, a checkpoint cannot — the final
    /// full pass, or cycle 1's reviewers, are what run next.
    /// </summary>
    [Fact]
    public void A_parent_head_that_cannot_be_resolved_at_all_parks()
    {
        StackedCheckpointVerdict verdict = Decide(StackedParentObservation.ParentUnresolvable(
            ParentBranch, "neither origin/task/parent-slice nor the parent's own pull-request head could be resolved"));

        verdict.Action.Should().Be(StackedCheckpointAction.Park);
        verdict.Reason.Should().Contain(ParentBranch, "the park names the stack the human has to finish");
        verdict.Reason.Should().Contain("before this run's first review cycle", "and which checkpoint owed the rebase");
        verdict.Reason.Should().Contain("h9k review resolve", "a park always names the lever that resumes it");
        verdict.Reason.Should().Contain("has not pushed",
            "one shape of this verdict is a parent still in flight (a child claimed ahead of it with "
            + "--acknowledge-unmet-dependencies), which resolves the moment the parent pushes — so waiting is "
            + "named as a path rather than only unstacking a branch whose parent is about to deliver");
    }

    [Fact]
    public void A_parent_that_can_no_longer_deliver_parks_instead_of_being_followed()
    {
        StackedCheckpointVerdict verdict = Decide(StackedParentObservation.ParentDead(
            ParentBranch, "the parent task 1a2b3c4d \"Parent slice\" (Abandoned) can no longer reach even Delivered"));

        verdict.Action.Should().Be(StackedCheckpointAction.Park);
        verdict.Reason.Should().Contain("Abandoned");
        verdict.Reason.Should().Contain("h9k review resolve",
            "a resolve does carry this run onward — once the parent itself can reach Delivered again, which is "
            + "what the park tells the human to see to first");
    }

    [Fact]
    public void A_parent_that_merged_somewhere_other_than_the_base_parks()
    {
        StackedCheckpointVerdict verdict = Decide(StackedParentObservation.ParentMergedElsewhere(
            ParentBranch, "the parent task's pull request merged into task/grandparent"));

        verdict.Action.Should().Be(StackedCheckpointAction.Park);
        verdict.Reason.Should().Contain("task/grandparent");
    }

    /// <summary>
    /// Every park that ends in "this branch belongs on a different base" says what a resolve
    /// cannot do, because the alternative reads as though one carried the run onward: the base a
    /// run watches is frozen on its own dispatch and a claimed task's stacked edge cannot be
    /// revised, so a human rebasing the branch by hand meets this same checkpoint and the same
    /// account of the same observation (the class sweep of the budget park's own misnamed lever —
    /// independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Theory]
    [InlineData(StackedParentVerdict.ParentUnresolvable)]
    [InlineData(StackedParentVerdict.ParentDead)]
    [InlineData(StackedParentVerdict.ParentMergedElsewhere)]
    public void A_park_that_needs_a_different_base_says_a_resolve_cannot_move_this_run_onto_one(
        StackedParentVerdict needsAnotherBase)
    {
        StackedCheckpointVerdict verdict = Decide(new StackedParentObservation(
            needsAnotherBase, ParentBranch, string.Empty, string.Empty, "whatever was observed"));

        verdict.Action.Should().Be(StackedCheckpointAction.Park);
        verdict.Reason.Should().Contain("frozen when it was dispatched")
            .And.Contain("continues as a fresh task",
                "the honest path when the work belongs elsewhere, and the platform's own rule for it");
    }

    [Theory]
    [InlineData(StackedParentVerdict.ParentMoved)]
    [InlineData(StackedParentVerdict.ParentMerged)]
    public void A_parent_that_moved_is_replayed_from_the_recorded_fork_point_onto_the_observed_head(
        StackedParentVerdict moved)
    {
        StackedCheckpointVerdict verdict = Decide(new StackedParentObservation(
            moved, ParentBranch, "aaaaaaaa1111", "bbbbbbbb2222", "the parent branch has moved"));

        verdict.Action.Should().Be(StackedCheckpointAction.Replay);
        verdict.UpstreamCommit.Should().Be("aaaaaaaa1111", "the boundary is the observation's, never re-derived here");
        verdict.OntoCommit.Should().Be("bbbbbbbb2222");
    }

    /// <summary>
    /// The budget is the child's own, shared with closeout's replay follow-ups
    /// (<c>TaskAggregate.StackReplaysDispatched</c>, <c>DaemonOptions.MaxStackReplayRuns</c>): a
    /// parent that keeps moving faster than the child can follow it is exactly the case that wants
    /// a human, and past the cap the branch is left untouched so what the human inherits is a
    /// coherent stack.
    /// </summary>
    [Fact]
    public void A_child_past_its_rebase_budget_parks_with_the_branch_untouched()
    {
        StackedCheckpointVerdict verdict = Decide(
            new StackedParentObservation(
                StackedParentVerdict.ParentMoved, ParentBranch, "aaaaaaaa1111", "bbbbbbbb2222",
                "the parent branch has moved"),
            rebasesSpent: 12);

        verdict.Action.Should().Be(StackedCheckpointAction.Park);
        verdict.Reason.Should().Contain("12/12", "the park says how much was spent, not just that it was");
        verdict.UpstreamCommit.Should().BeEmpty("nothing is replayed, so the branch keeps the base it had");
        verdict.Reason.Should().Contain("git rebase --onto bbbbbbbb2222 aaaaaaaa1111",
            "the park hands over the exact rebase it would have run, so the human is not left deriving it");
        verdict.Reason.Should().Contain("h9k review resolve --needs-fixes",
            "the other lever that actually moves this run: a fix session handed the rebase");
        verdict.Reason.Should().Contain("grants no further mechanical attempt",
            "and the park says so plainly (independent pre-PR review, cycle 1, adversarial lens): this budget is "
            + "counted on the TASK's stream, h9k review resolve appends to the RUN's, so a bare grant re-enters "
            + "the loop and meets this same checkpoint over the same spent budget");
        verdict.Reason.Should().NotContain("grant another attempt with h9k review resolve",
            "closeout's own wording names h9k pr resolve, which does refill the pool — copying it here named a "
            + "lever that grants nothing");
    }

    /// <summary>
    /// The guard that keeps this switch honest as the vocabulary grows: every verdict
    /// <see cref="StackedParentVerdict"/> declares has an arm of its own, so a seventh added later
    /// cannot quietly inherit the unrouted fallback — which proceeds without a rebase, the safest
    /// thing to do with an observation nothing has read, and the wrong thing to leave a new verdict
    /// sitting in.
    /// </summary>
    [Fact]
    public void Every_declared_verdict_is_routed_deliberately()
    {
        List<string> unrouted = [];
        foreach (StackedParentVerdict declared in Enum.GetValues<StackedParentVerdict>())
        {
            StackedCheckpointVerdict verdict = Decide(new StackedParentObservation(
                declared, ParentBranch, "aaaaaaaa1111", "bbbbbbbb2222", "whatever was observed"));
            if (verdict.Reason.Contains("has no rule for the verdict", StringComparison.Ordinal))
            {
                unrouted.Add(declared.ToString());
            }
        }

        unrouted.Should().BeEmpty(
            "a verdict added to StackedParentVerdict needs an arm in StackedCheckpointPolicy.Decide — decide "
            + "whether it parks the child or lets it carry on rather than letting the fallback decide by default");
    }

    private static StackedCheckpointVerdict Decide(StackedParentObservation observation, int rebasesSpent = 0) =>
        StackedCheckpointPolicy.Decide(
            observation, StackedCheckpoint.BeforeFirstReviewCycle, rebasesSpent, rebaseBudget: 12);
}
