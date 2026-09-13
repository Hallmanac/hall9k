using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The one decision closeout makes before it deletes a merged run's branch: whether the deletion on
/// origin is this platform's to make at all (task: a stacked child's pull request survives its
/// parent's merge; Decisions Log #PLACEHOLDER-c9a3a6c8). Asserted over the generated git sequence
/// rather than by running it, which is the whole reason that sequence is a pure function — the
/// behaviour being pinned is "no <c>push --delete</c> is issued", and proving that against a real
/// repository would mean standing up branches and a remote to watch something NOT happen, while
/// proving it against GitHub would mean merging a real pull request.
/// <para>
/// The deleted-branch end of it, under the ordinary owner, is already covered end-to-end against a
/// real repository by <c>GitWorktreeManagerTests</c>.
/// </para>
/// </summary>
public sealed class MergedBranchCleanupTests
{
    private const string Branch = "task/7228d4c7-a-project-can-be-archived-list";

    /// <summary>
    /// Today's behaviour, unchanged: the local branch, the origin probe, the remote deletion, the
    /// prune — in that order, with the same arguments the manager has always run.
    /// </summary>
    private static readonly string[] TodaysSequence =
    [
        $"branch -D \"{Branch}\"",
        "remote get-url origin",
        $"push origin --delete \"{Branch}\"",
        "fetch --prune origin",
    ];

    [Fact]
    public void A_repository_that_deletes_head_branches_on_merge_leaves_the_remote_deletion_to_github()
    {
        IReadOnlyList<MergedBranchCleanupStep> plan = PlanFor(repositoryDeletesHeadBranchOnMerge: true);

        plan.Should().NotContain(
            step => step.Stage == MergedBranchCleanupStage.DeleteRemoteBranch,
            "GitHub's own deletion is what retargets the pull requests stacked on this branch; a raw "
            + "push --delete closes them instead");
        plan.Select(step => step.Arguments).Should().NotContain(
            argument => argument.Contains("push", StringComparison.Ordinal),
            "nothing in the sequence may push anything to origin under this owner");
    }

    /// <summary>
    /// The local deletion and the prune are not GitHub's to make and still run: the branch is gone
    /// from origin whoever deleted it, so the stale tracking ref is still this repository's to drop.
    /// </summary>
    [Fact]
    public void Everything_but_the_remote_deletion_still_runs_when_github_owns_it()
    {
        IReadOnlyList<MergedBranchCleanupStep> plan = PlanFor(repositoryDeletesHeadBranchOnMerge: true);

        plan.Select(step => step.Arguments).Should().Equal(
            $"branch -D \"{Branch}\"",
            "remote get-url origin",
            "fetch --prune origin");
    }

    [Fact]
    public void A_repository_that_does_not_delete_head_branches_on_merge_gets_todays_sequence()
    {
        IReadOnlyList<MergedBranchCleanupStep> plan = PlanFor(repositoryDeletesHeadBranchOnMerge: false);

        plan.Select(step => step.Arguments).Should().Equal(TodaysSequence);
    }

    /// <summary>
    /// A setting nobody could read is not a setting turned on (AGENTS.md's never-guess rule), so the
    /// fallback is the behaviour every repository had before this decision existed — including for a
    /// project whose remote is not GitHub at all, where the read can never succeed.
    /// </summary>
    [Fact]
    public void A_setting_that_could_not_be_read_gets_todays_sequence_too()
    {
        IReadOnlyList<MergedBranchCleanupStep> plan = PlanFor(repositoryDeletesHeadBranchOnMerge: null);

        MergedBranchCleanup.OwnerOf(null).Should().Be(RemoteBranchDeletionOwner.Daemon);
        plan.Select(step => step.Arguments).Should().Equal(TodaysSequence);
    }

    /// <summary>The decision and the sequence it produces, joined exactly as closeout joins them.</summary>
    private static IReadOnlyList<MergedBranchCleanupStep> PlanFor(bool? repositoryDeletesHeadBranchOnMerge) =>
        MergedBranchCleanup.Plan(Branch, MergedBranchCleanup.OwnerOf(repositoryDeletesHeadBranchOnMerge));
}
