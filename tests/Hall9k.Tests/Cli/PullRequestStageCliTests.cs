using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// The CLI half of iterating on a task whose pull request is open, over a fake worktree manager and a
/// scripted git: no test here creates a repository or a branch, and none touches GitHub.
/// </summary>
public sealed class PullRequestStageCliTests
{
    private const string Branch = "task/abc12345-thing";
    private const string Repo = "/repo";
    private const string Worktree = "/repo/wt-abc12345";
    private const string Tip = "1111111111111111111111111111111111111111";
    private const string OtherTip = "2222222222222222222222222222222222222222";
    private static readonly Guid TaskId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533657");
    private static readonly Guid RunId = Guid.Parse("01a18010-1cf3-7210-a7df-387dbd533658");

    /// <summary>Answers git from a table keyed on the joined arguments, and records every call.</summary>
    private sealed class ScriptedGit
    {
        private readonly Dictionary<string, ProcessResult> _answers = [];

        public List<(string WorkingDirectory, string Arguments)> Calls { get; } = [];

        public ScriptedGit Answer(string arguments, int exitCode = 0, string output = "")
        {
            _answers[arguments] = new ProcessResult(exitCode, output, string.Empty);
            return this;
        }

        public ProcessRunner Runner => (_, arguments, workingDirectory, _) =>
        {
            string joined = string.Join(' ', arguments);
            Calls.Add((workingDirectory, joined));
            return Task.FromResult(_answers.TryGetValue(joined, out ProcessResult? result)
                ? result
                : new ProcessResult(128, string.Empty, "unscripted"));
        };
    }

    private sealed class FakeWorktrees : IWorktreeManager
    {
        public List<FollowUpWorktreeRequest> Checkouts { get; } = [];

        public Task<Worktree> CheckoutExistingAsync(FollowUpWorktreeRequest request, CancellationToken cancellationToken)
        {
            Checkouts.Add(request);
            return Task.FromResult(new Worktree(Worktree, request.Branch, request.Branch));
        }

        public Task<Worktree> CreateAsync(WorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A PR-stage claim never cuts a fresh branch.");

        public Task<Worktree> CreatePrReviewCheckoutAsync(PrReviewWorktreeRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task RemoveAsync(string repositoryPath, string worktreePath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task DeletePrReviewTrackingRefAsync(string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task DeleteBranchEverywhereAsync(
            string repositoryPath, string branch, RemoteBranchDeletionOwner remoteDeletion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task PruneAsync(string repositoryPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task<CheckoutRefresh> RefreshReadingCheckoutAsync(string checkoutPath, string branch, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task<IAsyncDisposable> AcquireRepositoryLockAsync(string repositoryPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");

        public Task<IAsyncDisposable> AcquireCheckoutLockAsync(string checkoutPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not reached.");
    }

    private static string RevParse(string reference) => $"rev-parse --verify --quiet {reference}^{{commit}}";

    // ─── The checkout ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_claim_checks_out_the_follow_up_branch_with_the_worktree_managers_existing_checkout()
    {
        FakeWorktrees worktrees = new();
        ScriptedGit git = new();
        git.Answer(RevParse($"refs/heads/{Branch}"), output: Tip + "\n")
            .Answer(RevParse("HEAD"), output: Tip + "\n")
            .Answer(RevParse($"refs/remotes/origin/{Branch}"), output: Tip + "\n");

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            worktrees, git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        worktrees.Checkouts.Should().ContainSingle().Which.Should().Be(new FollowUpWorktreeRequest(Repo, Branch, TaskId, RunId));
        result.Worktree.Path.Should().Be(Worktree);
        result.Notice.Should().BeNull("a tree level with origin needs no notice");
    }

    [Fact]
    public async Task A_tree_ahead_of_origin_is_kept_and_nothing_is_said()
    {
        ScriptedGit git = new();
        git.Answer(RevParse($"refs/heads/{Branch}"), output: OtherTip)
            .Answer(RevParse("HEAD"), output: OtherTip)
            .Answer(RevParse($"refs/remotes/origin/{Branch}"), output: Tip)
            .Answer($"merge-base --is-ancestor {Tip} {OtherTip}", exitCode: 0);

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            new FakeWorktrees(), git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        result.Notice.Should().BeNull();
    }

    [Fact]
    public async Task A_tree_the_checkout_left_behind_origin_is_said_in_one_line()
    {
        ScriptedGit git = new();
        git.Answer(RevParse($"refs/heads/{Branch}"), output: Tip)
            .Answer(RevParse("HEAD"), output: Tip)
            .Answer(RevParse($"refs/remotes/origin/{Branch}"), output: OtherTip)
            .Answer($"merge-base --is-ancestor {OtherTip} {Tip}", exitCode: 1)
            .Answer($"rev-list --count {Tip}..{OtherTip}", output: "3\n")
            .Answer($"rev-list --count {OtherTip}..{Tip}", output: "0\n");

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            new FakeWorktrees(), git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        result.Notice.Should().Contain($"is behind origin/{Branch} by 3 commit(s)").And.NotContain("\n");
    }

    [Fact]
    public async Task A_tree_that_diverged_from_origin_says_both_sides()
    {
        ScriptedGit git = new();
        git.Answer(RevParse($"refs/heads/{Branch}"), output: Tip)
            .Answer(RevParse("HEAD"), output: Tip)
            .Answer(RevParse($"refs/remotes/origin/{Branch}"), output: OtherTip)
            .Answer($"merge-base --is-ancestor {OtherTip} {Tip}", exitCode: 1)
            .Answer($"rev-list --count {Tip}..{OtherTip}", output: "2\n")
            .Answer($"rev-list --count {OtherTip}..{Tip}", output: "1\n");

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            new FakeWorktrees(), git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        result.Notice.Should().Contain("has diverged").And.Contain("1 commit(s) here").And.Contain("2 on origin");
    }

    [Fact]
    public async Task A_tree_reset_to_origin_says_which_local_tip_was_replaced()
    {
        ScriptedGit git = new();
        git.Answer(RevParse($"refs/heads/{Branch}"), output: Tip)
            .Answer(RevParse("HEAD"), output: OtherTip)
            .Answer(RevParse($"refs/remotes/origin/{Branch}"), output: OtherTip)
            .Answer($"merge-base --is-ancestor {Tip} {OtherTip}", exitCode: 1);

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            new FakeWorktrees(), git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        result.Notice.Should().Contain("was reset to origin/").And.Contain(Tip[..10]);
    }

    [Fact]
    public async Task A_fast_forward_is_not_a_reset()
    {
        ScriptedGit git = new();
        git.Answer(RevParse($"refs/heads/{Branch}"), output: Tip)
            .Answer(RevParse("HEAD"), output: OtherTip)
            .Answer(RevParse($"refs/remotes/origin/{Branch}"), output: OtherTip)
            .Answer($"merge-base --is-ancestor {Tip} {OtherTip}", exitCode: 0);

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            new FakeWorktrees(), git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        result.Notice.Should().BeNull();
    }

    [Fact]
    public async Task A_ref_git_cannot_read_is_not_reported_as_up_to_date_or_behind()
    {
        ScriptedGit git = new();

        PullRequestStageCheckout.Result result = await PullRequestStageCheckout.CheckoutAsync(
            new FakeWorktrees(), git.Runner, Repo, Branch, TaskId, RunId, CancellationToken.None);

        result.Notice.Should().BeNull();
    }

    // ─── The refusal task start keeps ───────────────────────────────────────────────────────────

    [Fact]
    public void Task_start_keeps_its_refusal_of_a_follow_up_branch_and_its_hint_names_drive_beside_pr_resolve()
    {
        string refusal = TaskStartCommand.FollowUpRefusal(TaskId, "https://github.com/acme/web/pull/42");

        refusal.Should().Contain("deliberate kick-off cannot build the follow-up prompt");
        refusal.Should().Contain($"h9k task drive {TaskId}").And.Contain($"h9k pr resolve {TaskId}");
    }

    [Fact]
    public void The_paste_flow_is_refused_for_a_task_whose_pull_request_is_open()
    {
        TaskDriveCommand.PullRequestStagePromptRefusal(TaskId).Should()
            .Contain("--print-prompt and --direct-launch").And.Contain($"h9k task drive {TaskId} with neither flag");
    }

    // ─── What the record says about the pull request ────────────────────────────────────────────

    [Theory]
    [InlineData("AwaitingReview", null, true)]
    [InlineData("CloseoutParked", null, true)]
    [InlineData("Completed", null, false)]
    [InlineData("Failed", "PullRequestClosedWithoutMerge", false)]
    [InlineData("Failed", "some other failure", true)]
    public void A_pull_request_is_open_unless_its_run_merged_or_recorded_a_close(string state, string? reason, bool open)
    {
        RunDetails run = new()
        {
            State = state,
            FailureReason = reason == "PullRequestClosedWithoutMerge" ? RunDetails.PullRequestClosedWithoutMerge : reason,
        };

        PullRequestStageFacts.IsOpen(run).Should().Be(open);
    }

    [Theory]
    [InlineData("AwaitingReview", true)]
    [InlineData("CloseoutParked", true)]
    [InlineData("Failed", true)]
    [InlineData("Superseded", false)]
    public void Closeout_resumes_through_a_pull_request_run_unless_a_reopen_already_retired_it(string state, bool resumes)
    {
        RunDetails run = new() { State = state };

        PullRequestStageFacts.CloseoutResumesThrough(run).Should().Be(resumes);
    }

    [Fact]
    public void The_last_observation_names_the_state_threads_checks_and_decision()
    {
        RunDetails run = new()
        {
            State = RunState.AwaitingReview,
            UnresolvedReviewThreads = 2,
            FailingChecks = ["build", "lint"],
            ExternalReviewDecision = "CHANGES_REQUESTED",
        };

        PullRequestStageFacts.DescribeLastObservation(run).Should()
            .Be("the run watching it is AwaitingReview; 2 unresolved review threads; failing checks: build, lint; review decision CHANGES_REQUESTED.");
    }

    // ─── Deliver ────────────────────────────────────────────────────────────────────────────────

    private static ScriptedGit PushGit(string originTip, bool originTipIsAncestor, string reflog = "")
    {
        ScriptedGit git = new();
        git.Answer($"ls-remote --exit-code origin refs/heads/{Branch}", output: $"{originTip}\trefs/heads/{Branch}\n")
            .Answer($"merge-base --is-ancestor {originTip} HEAD", exitCode: originTipIsAncestor ? 0 : 1)
            .Answer($"reflog show {Branch} --format=%H", output: reflog)
            .Answer($"push --force-with-lease={Branch}:{originTip} origin {Branch}")
            .Answer($"rev-parse refs/heads/{Branch}", output: OtherTip + "\n");
        return git;
    }

    [Fact]
    public async Task Deliver_pushes_through_the_guard_with_the_lease_pinned_and_reports_the_pushed_tip()
    {
        ScriptedGit git = PushGit(Tip, originTipIsAncestor: true);

        TaskDeliveryPush.Outcome outcome = await TaskDeliveryPush.PushAsync(
            git.Runner, Worktree, Branch, null, null, CancellationToken.None);

        outcome.Pushed.Should().BeTrue();
        outcome.PushedTip.Should().Be(OtherTip, "recorded as TaskBranchPushed after the push");
        git.Calls.Select(call => call.Arguments).Should().Contain($"push --force-with-lease={Branch}:{Tip} origin {Branch}");
        git.Calls.Select(call => call.Arguments).Should().NotContain(arguments => arguments.StartsWith("push --force-with-lease -u", StringComparison.Ordinal),
            "never the bare lease");
    }

    [Fact]
    public async Task Deliver_refuses_with_the_reason_when_origin_holds_a_tip_the_branch_never_held()
    {
        ScriptedGit git = PushGit(Tip, originTipIsAncestor: false);

        TaskDeliveryPush.Outcome outcome = await TaskDeliveryPush.PushAsync(
            git.Runner, Worktree, Branch, null, null, CancellationToken.None);

        outcome.Pushed.Should().BeFalse();
        outcome.GuardRefused.Should().BeTrue();
        outcome.Refusal.Should().Contain($"origin/{Branch} is at {Tip}").And.Contain("Refusing to force-push");
        git.Calls.Select(call => call.Arguments).Should().NotContain(arguments => arguments.StartsWith("push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deliver_hands_the_recorded_pushed_tip_to_the_guard_when_the_reflog_no_longer_proves_it()
    {
        ScriptedGit git = PushGit(Tip, originTipIsAncestor: false, reflog: string.Empty);

        TaskDeliveryPush.Outcome outcome = await TaskDeliveryPush.PushAsync(
            git.Runner, Worktree, Branch, Branch, Tip, CancellationToken.None);

        outcome.Pushed.Should().BeTrue("the task's own record says this node pushed that tip");
    }

    [Fact]
    public async Task The_recorded_tip_counts_only_for_the_branch_it_was_recorded_for()
    {
        ScriptedGit git = PushGit(Tip, originTipIsAncestor: false);

        TaskDeliveryPush.Outcome outcome = await TaskDeliveryPush.PushAsync(
            git.Runner, Worktree, Branch, "task/some-other-branch", Tip, CancellationToken.None);

        outcome.Pushed.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_push_is_reported_without_being_called_a_guard_refusal()
    {
        ScriptedGit git = PushGit(Tip, originTipIsAncestor: true);
        git.Answer($"push --force-with-lease={Branch}:{Tip} origin {Branch}", exitCode: 1);

        TaskDeliveryPush.Outcome outcome = await TaskDeliveryPush.PushAsync(
            git.Runner, Worktree, Branch, null, null, CancellationToken.None);

        outcome.Pushed.Should().BeFalse();
        outcome.GuardRefused.Should().BeFalse();
        outcome.Refusal.Should().Contain("exited 1");
    }

    [Theory]
    [InlineData("0", "0", 0)]
    [InlineData("3", "2", 2)]
    [InlineData("0", "4", 0)]
    public async Task Commits_beyond_the_pushed_tip_are_the_smaller_count_past_the_recorded_tip_and_origin(
        string pastRecorded, string pastOrigin, int expected)
    {
        ScriptedGit git = new();
        git.Answer($"rev-list --count {Tip}..refs/heads/{Branch}", output: pastRecorded + "\n")
            .Answer($"rev-list --count origin/{Branch}..refs/heads/{Branch}", output: pastOrigin + "\n");

        int beyond = await BranchPushedTip.CountBeyondAsync(
            git.Runner, Worktree, Branch, Branch, Tip, CancellationToken.None);

        beyond.Should().Be(expected);
    }

    [Fact]
    public async Task Commits_beyond_the_pushed_tip_are_unknown_when_neither_boundary_resolves()
    {
        int beyond = await BranchPushedTip.CountBeyondAsync(
            new ScriptedGit().Runner, Worktree, Branch, null, null, CancellationToken.None);

        beyond.Should().Be(-1);
    }

    // ─── The refusals ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Deliver_with_nothing_beyond_the_pushed_tip_refuses_and_names_release()
    {
        TaskDeliverCommand.NothingBeyondPushedTipMessage(TaskId, Branch, "https://github.com/acme/web/pull/42")
            .Should().Contain("nothing to deliver").And.Contain($"h9k task release {TaskId}");
    }

    [Fact]
    public void Release_past_the_pushed_tip_refuses_and_names_deliver_and_handback()
    {
        string refusal = TaskReleaseCommand.PastPushedTipRefusal(TaskId, Branch, 2, worktreeExists: true);

        refusal.Should().Contain("2 commit(s) the pull request does not carry")
            .And.Contain($"h9k task deliver {TaskId}").And.Contain($"h9k task handback {TaskId}");
    }

    [Fact]
    public void Release_past_the_pushed_tip_offers_only_handback_when_the_worktree_is_gone()
    {
        string refusal = TaskReleaseCommand.PastPushedTipRefusal(TaskId, Branch, 1, worktreeExists: false);

        refusal.Should().Contain($"h9k task handback {TaskId}").And.NotContain("h9k task deliver");
    }

    [Fact]
    public void Release_refuses_unassign_on_a_claim_at_the_pull_request_stage()
    {
        TaskReleaseCommand.UnassignRefusal(TaskId, "https://github.com/acme/web/pull/42")
            .Should().Contain("--unassign").And.Contain($"h9k task release {TaskId} (without --unassign)");
    }

    [Fact]
    public void Delegate_is_refused_at_the_pull_request_stage_and_points_at_handback()
    {
        TaskDelegateCommand.PullRequestStageDelegateRefusal(TaskId)
            .Should().Contain("build prompt").And.Contain($"h9k task handback {TaskId}");
    }
}
