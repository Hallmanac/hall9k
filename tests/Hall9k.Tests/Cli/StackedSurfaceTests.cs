using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What the board says about a stacked pull request (task: the bar machinery treats an
/// un-retargeted stacked PR as not at the bar). The hazard is one-directional and expensive: a
/// Delivered stacked child reads green on every ordinary signal, so "the merge is yours" would hand
/// a human a merge that lands this branch's commits on its parent's branch rather than on anything
/// the project ships from. The phase line and the attention line under it also have to agree, which
/// is why both are asserted together (the origin incident for that pairing is on
/// <c>AttentionComposer.AwaitingReviewAttention</c>'s own doc).
/// </summary>
public sealed class StackedSurfaceTests
{
    private const string ParentBranch = "task/parent-slice-one";

    private const string PullRequest = "https://github.com/x/y/pull/8";

    [Fact]
    public void A_stacked_child_awaiting_review_is_never_told_the_merge_is_its_readers()
    {
        TaskStatusRow row = ComposeDelivered(StackedRun());

        row.Attention.Cause.Should().NotContain("the merge is yours");
        row.Attention.Cause.Should().Contain($"stacked on `{ParentBranch}`");
        row.Attention.Cause.Should().Contain("until its parent merges");
        row.Attention.Level.Should().Be(AttentionLevel.WaitingHandled,
            "nothing is wrong — something is simply owed first, and the retarget arrives on its own");
    }

    [Fact]
    public void The_same_row_unstacked_does_hand_the_reader_the_merge()
    {
        TaskStatusRow row = ComposeDelivered(UnstackedRun());

        row.Attention.Cause.Should().Contain("the merge is yours",
            "an ordinary Delivered row with nothing recorded against it is the reader's turn");
        row.Attention.Level.Should().Be(AttentionLevel.NeedsYou);
    }

    /// <summary>
    /// Pre-approval is what removes the human gate, so it is exactly the case where a wrong reading
    /// is silent: the composer must not promise the daemon will merge a pull request the daemon's
    /// own gate refuses.
    /// </summary>
    [Fact]
    public void A_pre_approved_stacked_child_is_not_promised_an_automatic_merge()
    {
        TaskListItem task = StatusFixtures.Task(
            TaskState.Done, StackedRunId, PullRequest, preApproved: true);
        TaskStatusRow row = StatusFixtures.Compose(task, StackedRun());

        row.Attention.Cause.Should().NotContain("the daemon merges it on its own");
        row.Attention.Cause.Should().Contain($"stacked on `{ParentBranch}`");
    }

    [Fact]
    public void The_phase_line_agrees_with_the_attention_line_about_the_stack()
    {
        TaskStatusRow row = ComposeDelivered(StackedRun());

        row.Phase.Text.Should().Contain($"stacked on {ParentBranch}");
        row.Phase.Detail.Should().Contain("retargets and replays when the parent merges");
    }

    /// <summary>
    /// A blocked stacked child waits for its parent's Delivered, not its merge, so the derived-facts
    /// line must not promise the stricter bar — a human reading "to close out" would wait for a
    /// merge that is not what releases this task.
    /// </summary>
    [Fact]
    public void A_blocked_stacked_child_names_the_delivered_bar_rather_than_closeout()
    {
        Guid parentId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Blocked);
        task.StackedOnTaskId = parentId;
        task.BlockedBy = [parentId];
        task.UnmetDependencies = [parentId];

        string facts = string.Join(" · ", PublishedFacts.Compose(task, LifecycleState.Published));
        facts.Should().Contain("reach Delivered (stacked)");
        facts.Should().NotContain("to close out");
    }

    /// <summary>
    /// The phase line's own copy of the same reading, which speaks for a DELIVERED follow-up held
    /// by a dependency — the one row where the derived-facts line above is not composed at all
    /// (<c>TaskPhaseComposer.BlockedDetail</c>'s own doc). The two must not disagree about the bar.
    /// </summary>
    [Fact]
    public void A_blocked_stacked_follow_up_names_the_delivered_bar_on_the_phase_line_too()
    {
        Guid parentId = DomainId.New();
        RunDetails run = StackedRun();
        TaskListItem task = StatusFixtures.Task(TaskState.Blocked, run.Id, PullRequest);
        task.StackedOnTaskId = parentId;
        task.BlockedBy = [parentId];
        task.UnmetDependencies = [parentId];

        TaskStatusRow row = StatusFixtures.Compose(task, run);

        row.Phase.Detail.Should().Contain("waiting on its stacked parent to reach Delivered");
    }

    [Fact]
    public void A_blocked_plain_dependent_still_names_true_closeout()
    {
        Guid blockerId = DomainId.New();
        TaskListItem task = StatusFixtures.Task(TaskState.Blocked);
        task.BlockedBy = [blockerId];
        task.UnmetDependencies = [blockerId];

        string.Join(" · ", PublishedFacts.Compose(task, LifecycleState.Published))
            .Should().Contain("to close out", "a plain blocked-by task behaves exactly as before");
    }

    /// <summary>
    /// The mark beside a blocker on <c>h9k task show</c> and <c>h9k task assign</c>: a Delivered
    /// stacked parent is met, the same Delivered blocker on a plain edge is not, and the stacked one
    /// says which bar it is waiting on rather than leaving a reader to assume the stricter one.
    /// A met stacked parent is marked at the bar it actually reached, never "closed out": its pull
    /// request is still open, the row's own state word beside the mark reads Delivered, and the
    /// sentence above the list says this edge is met at Delivered — so a closeout word here would
    /// contradict all three (independent pre-PR review, 2026-09-07, adversarial lens; the same
    /// 2026-08-22 word-versus-mark incident one bar further down).
    /// </summary>
    [Fact]
    public void The_dependency_mark_reads_the_declared_edge()
    {
        Guid parentId = DomainId.New();
        TaskDependency delivered = new(
            parentId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.AwaitingReview,
            PullRequest, TaskType.Feature, []);

        TaskStatusComposer.DependencyMark(delivered, parentId).Should().Contain("met at Delivered",
            "a stacked parent at Delivered no longer holds its child back")
            .And.NotContain("closed out", "nothing observed a closeout — its pull request is still open");
        TaskStatusComposer.DependencyMark(delivered).Should().Contain("waiting",
            "the same blocker on a plain edge still waits for the merge");
    }

    /// <summary>
    /// The closeout word is still the right one where a closeout was actually observed: a merged
    /// parent is past Delivered rather than short of it, on either kind of edge.
    /// </summary>
    [Fact]
    public void A_merged_parent_is_still_marked_closed_out_on_the_stacked_edge()
    {
        Guid parentId = DomainId.New();
        TaskDependency merged = new(
            parentId, "Parent slice", TaskState.Done, IsClosedOut: true, RunState.Completed,
            PullRequest, TaskType.Feature, []);

        TaskStatusComposer.DependencyMark(merged, parentId).Should().Contain("closed out");
        TaskStatusComposer.DependencyMark(merged).Should().Contain("closed out");
    }

    [Fact]
    public void A_stacked_parent_that_will_never_merge_is_still_waiting_rather_than_dead()
    {
        Guid parentId = DomainId.New();
        TaskDependency stranded = new(
            parentId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.Superseded,
            PullRequest, TaskType.Feature, []);

        TaskStatusComposer.DependencyMark(stranded, parentId).Should().Contain("met at Delivered",
            "it delivered the branch and pull request the child is already built on");
        TaskStatusComposer.DependencyMark(stranded).Should().Contain("never closes out",
            "the same blocker on a plain edge strands its dependent, exactly as before");
    }

    /// <summary>
    /// The mark for the shape that made <c>IsDelivered</c> narrower: a parent whose pull request was
    /// closed without merging never delivered, so its stacked child is held and told rather than
    /// released — and the mark says the one thing the human has to know.
    /// </summary>
    [Fact]
    public void A_stacked_parent_whose_pull_request_closed_unmerged_is_marked_dead()
    {
        Guid parentId = DomainId.New();
        TaskDependency closed = new(
            parentId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.Failed,
            PullRequest, TaskType.Feature, [], RunFailureReason: RunDetails.PullRequestClosedWithoutMerge);

        TaskStatusComposer.DependencyMark(closed, parentId).Should().Contain("never closes out",
            "a closed pull request cannot reach Delivered either, so the child needs a human");
    }

    private static readonly Guid StackedRunId = DomainId.New();

    private static TaskStatusRow ComposeDelivered(RunDetails run) =>
        StatusFixtures.Compose(StatusFixtures.Task(TaskState.Done, run.Id, PullRequest), run);

    private static RunDetails StackedRun()
    {
        RunDetails run = StatusFixtures.Run(
            StackedRunId, RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 8,
            branch: "task/child-slice-two");
        run.PullRequestUrl = PullRequest;
        run.BaseBranch = ParentBranch;
        return run;
    }

    private static RunDetails UnstackedRun()
    {
        RunDetails run = StatusFixtures.Run(
            DomainId.New(), RunState.AwaitingReview, sessionProcessId: null, pullRequestNumber: 8,
            branch: "task/ordinary");
        run.PullRequestUrl = PullRequest;
        return run;
    }
}
