using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The closeout monitor's decision table against a real store and a real repo, with the
/// gh seam faked: merge completes the run and cleans the workspace, failing checks and
/// review feedback dispatch follow-ups through the reopen pipeline, an errored Copilot
/// review holds at ReviewPending and re-requests through the API, a spent budget parks,
/// and a closed PR fails the run but keeps the branch.
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class CloseoutEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/7";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-closeout-{Guid.NewGuid():N}");

    private sealed class FakeInspector : IPullRequestInspector
    {
        public PullRequestSnapshot Snapshot { get; set; } = Quiet();

        public int Inspections { get; private set; }

        /// <summary>Reviewer logins passed to RerequestReviewAsync, in call order.</summary>
        public List<string> ReviewRerequests { get; } = [];

        /// <summary>Runs inside the inspection — the seam for writes landing mid-gh-call.</summary>
        public Func<Task>? OnInspect { get; set; }

        public async Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
        {
            Inspections++;
            if (OnInspect is { } hook)
            {
                OnInspect = null;
                await hook();
            }

            return Snapshot;
        }

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string reviewer,
            CancellationToken cancellationToken)
        {
            ReviewRerequests.Add(reviewer);
            return Task.CompletedTask;
        }

        public static PullRequestSnapshot Quiet() => new(
            IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
            FailingChecks: [], HasPendingChecks: false, UnresolvedCopilotThreadCount: 0,
            ErroredCopilotReview: null);
    }

    [Fact]
    public async Task Merge_completes_the_run_removes_the_worktree_and_deletes_the_branch_everywhere()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, string originPath, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) =
            await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult sweep = await engine.PollOnceAsync(cts.Token);
        sweep.Should().Be(new CloseoutSweepResult(RunsInspected: 1, MergesObserved: 1),
            "the sweep tally feeds the startup catch-up report (Decisions Log #31)");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Completed, "the observed merge finally gives RunCompleted its meaning");
        run.PullRequestMergedAt.Should().Be(Now.AddHours(2));

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Done);

        Directory.Exists(worktree.Path).Should().BeFalse("closeout completion removes the retained worktree");
        TryGit(repoPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the local branch is deleted (git branch -D, rebase-merge safe)");
        TryGit(originPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the remote branch is deleted");
        TryGit(repoPath, $"rev-parse --verify refs/remotes/origin/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "stale remote-tracking refs are pruned");

        // A completed run leaves the watch set: the next sweep inspects nothing.
        int before = inspector.Inspections;
        await engine.PollOnceAsync(cts.Token);
        inspector.Inspections.Should().Be(before, "a merged PR is never polled again");
    }

    /// <summary>
    /// True closeout is the only completion signal a dependency chain accepts (Decisions Log
    /// #34), so the node that observes the merge is the node that unblocks the dependents.
    /// </summary>
    [Fact]
    public async Task An_observed_merge_unblocks_the_tasks_that_were_waiting_on_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, _, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);
        Guid dependentId = await SeedBlockedDependentAsync(store, node.OwnerId, taskId, cts.Token);

        await using (IQuerySession before = store.QuerySession())
        {
            TaskListItem blocked = (await before.LoadAsync<TaskListItem>(dependentId, cts.Token))!;
            blocked.State.Should().Be(TaskState.Blocked, "a Done-but-unmerged dependency still blocks");
        }

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        TaskListItem dependent = (await query.LoadAsync<TaskListItem>(dependentId, cts.Token))!;
        dependent.State.Should().Be(TaskState.Queued, "its last blocker reached true closeout");
        dependent.UnmetDependencies.Should().BeEmpty();
        dependent.AssignedOwnerId.Should().Be(node.OwnerId, "unblocking never changes whose work it is");
    }

    /// <summary>A published, assigned task waiting on <paramref name="dependencyId"/> and nothing else.</summary>
    private static async Task<Guid> SeedBlockedDependentAsync(
        DocumentStore store, Guid ownerId, Guid dependencyId, CancellationToken cancellationToken)
    {
        Guid dependentId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        TaskAggregate dependent = new();
        Hall9k.Domain.Features.Tasks.Events.TaskAdded added = TaskDecider.Add(
            dependentId, DomainId.New(), "Wait for the merge", ["it runs after"], TaskType.Chore,
            null, null, null, Now, ownerId, blockedBy: [dependencyId]);
        dependent.Apply(added);

        TaskDependency blocker = new(
            dependencyId, "Close me out", TaskState.Done, IsClosedOut: false, CurrentRunState: null, []);
        Hall9k.Domain.Features.Tasks.Events.TaskPublished published =
            TaskDecider.Publish(dependent, new TaskDependencyGraph([blocker]), Now, ownerId);
        dependent.Apply(published);

        Hall9k.Domain.Features.Tasks.Events.TaskAssigned assigned =
            TaskDecider.Assign(dependent, ownerId, [blocker], Now, ownerId);

        session.Events.StartStream<TaskAggregate>(dependentId, added, published, assigned);
        await session.SaveChangesAsync(cancellationToken);
        return dependentId;
    }

    [Fact]
    public async Task Failing_checks_dispatch_an_automatic_fix_follow_up_through_the_reopen_pipeline()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) =
            await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { FailingChecks = ["build (windows-latest)"] },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Superseded,
            "the reopen hands the PR to a successor, so the observed run retires in the same transaction");
        run.FailingChecks.Should().Equal("build (windows-latest)");

        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued, "the follow-up flows through the standard dispatch pipeline");
        task.FollowUpBranch.Should().Be(worktree.Branch);
        task.FollowUpKind.Should().Be(FollowUpKind.FailingChecks, "the launcher picks the fix-the-CI prompt from it");
        task.FollowUpReason.Should().Contain("build (windows-latest)");

        TaskAggregate aggregate = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        aggregate.CloseoutAttempts.Should().Be(1, "automatic reopens spend the bounded budget");

        Directory.Exists(worktree.Path).Should().BeTrue("the worktree is the follow-up workspace — never removed here");
    }

    [Fact]
    public async Task Unresolved_copilot_threads_dispatch_a_review_follow_up()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { UnresolvedCopilotThreadCount = 2 },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.Superseded);
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewFeedback);
    }

    [Fact]
    public async Task An_errored_copilot_review_holds_the_run_at_review_pending_and_rerequests_once()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        ErroredReview errored = new("copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1");
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { ErroredCopilotReview = errored },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.State.Should().Be(RunState.ReviewPending,
                "an errored review produced zero threads — that must never read as review-clean");
            run.ErroredReviewUrl.Should().Be(errored.Url);
            run.ReviewRerequestCount.Should().Be(1);

            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
                TaskState.Done, "a re-request needs no agent, so no reopen is dispatched");
            inspector.ReviewRerequests.Should().Equal("copilot-pull-request-reviewer");
        }

        // The same errored review on the next sweep is never re-requested again — the
        // reviewer just hasn't answered yet.
        await engine.PollOnceAsync(cts.Token);
        await using (IQuerySession query = store.QuerySession())
        {
            inspector.ReviewRerequests.Should().HaveCount(1, "one re-request per errored review, not per sweep");
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.ReviewPending);
        }
    }

    [Fact]
    public async Task A_repeatedly_erroring_review_parks_the_run_naming_the_errored_review()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        // Each re-request is answered by another errored review (a fresh review URL each
        // time). Budget of 2: two re-requests, then the third errored review parks.
        FakeInspector inspector = new();
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            inspector.Snapshot = FakeInspector.Quiet() with
            {
                ErroredCopilotReview = new ErroredReview(
                    "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-{attempt}"),
            };
            await engine.PollOnceAsync(cts.Token);
        }

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.ParkedReason.Should().Contain("copilot-pull-request-reviewer")
            .And.Contain("#pullrequestreview-3", "the park reason names the errored review the human should look at")
            .And.Contain("budget spent").And.Contain("h9k pr resolve");
        run.ReviewRerequestCount.Should().Be(2, "the budget bounds re-requests exactly like other closeout actions");
        inspector.ReviewRerequests.Should().HaveCount(2);

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
            TaskState.Done, "parking lives on the run; h9k pr resolve remains the human's retry lever");
    }

    [Fact]
    public async Task A_spent_reopen_budget_parks_an_errored_review_without_rerequesting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (_, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 2);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                ErroredCopilotReview = new ErroredReview(
                    "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-9"),
            },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.CloseoutParked, "re-requests draw on the same automatic budget the reopens already spent");
        inspector.ReviewRerequests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_errored_review_answered_by_a_successful_rereview_flows_through_thread_resolution()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                ErroredCopilotReview = new ErroredReview(
                    "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1"),
            },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        // The re-requested review succeeds and leaves real feedback: latestReviews no
        // longer matches as errored, unresolved threads appear.
        inspector.Snapshot = FakeInspector.Quiet() with { UnresolvedCopilotThreadCount = 2 };
        await engine.PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.Superseded, "the ReviewPending run is still watched, so the re-review dispatches normally");
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewFeedback);
    }

    [Fact]
    public async Task A_spent_automatic_budget_parks_the_closeout_instead_of_looping()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 2);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { FailingChecks = ["build"] },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.State.Should().Be(RunState.CloseoutParked);
            run.ParkedReason.Should().Contain("budget spent").And.Contain("h9k pr resolve");

            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
                TaskState.Done, "parking lives on the run; the task stays Done so h9k pr resolve still works");
        }

        // A parked run still gets merge detection — and only merge detection.
        await engine.PollOnceAsync(cts.Token);
        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(
                TaskState.Done, "no further automatic dispatch once parked");
        }

        inspector.Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddDays(1) };
        await engine.PollOnceAsync(cts.Token);
        await using (IQuerySession afterMerge = store.QuerySession())
        {
            (await afterMerge.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
                RunState.Completed, "a human merging a parked PR still completes the closeout");
        }
    }

    [Fact]
    public async Task A_closed_pull_request_fails_the_run_removes_the_worktree_and_keeps_the_branch()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, string originPath, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (_, Guid runId, Worktree worktree) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsClosed = true, ClosedAt = Now.AddHours(3) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.FailureReason.Should().Contain("closed without merge");

        Directory.Exists(worktree.Path).Should().BeFalse("a completed (closed) PR releases its worktree");
        TryGit(originPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().Be(0, "an unmerged branch still holds work and is never deleted");
    }

    [Fact]
    public async Task Pending_checks_defer_every_dispatch_decision()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                FailingChecks = ["build"], HasPendingChecks = true, UnresolvedCopilotThreadCount = 1,
            },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.AwaitingReview, "an incomplete CI picture defers action to the next sweep");
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Done);
    }

    [Fact]
    public async Task A_reopen_landing_mid_inspection_defers_the_sweep_instead_of_double_dispatching()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) =
            await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        // h9k pr resolve fires while the monitor's gh call is in flight — the exact
        // window the fence protects. Without it the monitor commits a second reopen.
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { FailingChecks = ["build"] },
            OnInspect = () => ReopenManuallyAsync(store, node, taskId, worktree.Branch, cts.Token),
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.AwaitingReview, "the deferred sweep leaves the run untouched — no observation, no retirement");
        TaskAggregate task = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        task.CloseoutAttempts.Should().Be(0, "only the human's reopen landed; the automatic one never committed");
        (await query.Events.FetchStreamAsync(taskId, token: cts.Token))
            .Count(e => e.Data is Hall9k.Domain.Features.Tasks.Events.TaskReopened)
            .Should().Be(1, "exactly one follow-up dispatches, not one per writer");
    }

    [Fact]
    public async Task A_merge_observed_while_a_reopen_landed_never_cleans_up_under_the_follow_up()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) =
            await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);

        // The merged path removes the worktree and deletes the branch — filesystem acts
        // no expectedVersion can roll back. A reopen mid-call means a follow-up agent
        // may already be working in that reused worktree; the sweep must defer.
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
            OnInspect = () => ReopenManuallyAsync(store, node, taskId, worktree.Branch, cts.Token),
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.AwaitingReview, "completion defers to the next sweep, which re-reads the reopened task");
        Directory.Exists(worktree.Path).Should().BeTrue("the follow-up workspace survives");
        TryGit(repoPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().Be(0, "the branch the follow-up resumes is untouched");
    }

    private static async Task ReopenManuallyAsync(
        DocumentStore store, NodeContext node, Guid taskId, string branch, CancellationToken cancellationToken)
    {
        await using IDocumentSession concurrent = store.LightweightSession();
        TaskAggregate task =
            (await concurrent.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!;
        concurrent.Events.Append(taskId, TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, branch,
            "Human asked first.", FollowUpKind.ReviewFeedback, automatic: false, Now, node.OwnerId));
        await concurrent.SaveChangesAsync(cancellationToken);
    }

    private async Task<(DocumentStore Store, NodeContext Node, GitWorktreeManager Worktrees, string OriginPath, string RepoPath)>
        SetUpAsync(CancellationToken cancellationToken)
    {
        DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, $"origin-{Guid.NewGuid():N}.git");
        string repoPath = Path.Combine(_root, $"repo-{Guid.NewGuid():N}");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# closeout test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");

        return (store, node, new GitWorktreeManager(NullLogger<GitWorktreeManager>.Instance), originPath, repoPath);
    }

    /// <summary>
    /// A task at the top of the closeout phase: done with a PR, its run AwaitingReview,
    /// the branch pushed, the worktree retained. priorAutomaticReopens seeds already-spent
    /// budget (each one is a full automatic reopen → claim → complete cycle on the stream).
    /// </summary>
    private static async Task<(Guid TaskId, Guid RunId, Worktree Worktree)> SeedAwaitingReviewAsync(
        DocumentStore store,
        NodeContext node,
        GitWorktreeManager worktrees,
        string repoPath,
        CancellationToken cancellationToken,
        int priorAutomaticReopens = 0)
    {
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid ownerId = node.OwnerId;
        Guid projectId = DomainId.New();

        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(repoPath, "main", taskId, DomainId.New(), "Close me out"), cancellationToken);
        File.WriteAllText(Path.Combine(worktree.Path, "WORK.md"), "agent output\n");
        Git(worktree.Path, "add -A");
        Git(worktree.Path, "-c user.name=Test -c user.email=t@t commit -qm work");
        Git(worktree.Path, $"push -q origin {worktree.Branch}");

        await using IDocumentSession session = store.LightweightSession();

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, "Close me out", ["merged"], TaskType.Chore, null, null, null, Now, ownerId),
            ownerId, Now);
        List<object> taskEvents = [.. lifecycle];
        Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
            TaskDecider.Claim(task, node.NodeId, ownerId, DomainId.New(), Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);
        Hall9k.Domain.Features.Tasks.Events.TaskCompleted completed =
            TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequestUrl, Now);
        task.Apply(completed);
        taskEvents.Add(completed);

        for (int i = 0; i < priorAutomaticReopens; i++)
        {
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                task, task.CurrentRunId!.Value, worktree.Branch,
                "CI checks failing.", FollowUpKind.FailingChecks, automatic: true, Now, ownerId);
            task.Apply(reopened);
            taskEvents.Add(reopened);
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed reclaimed =
                TaskDecider.Claim(task, node.NodeId, ownerId, DomainId.New(), Now);
            task.Apply(reclaimed);
            taskEvents.Add(reclaimed);
            Hall9k.Domain.Features.Tasks.Events.TaskCompleted recompleted =
                TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequestUrl, Now);
            task.Apply(recompleted);
            taskEvents.Add(recompleted);
        }

        // The run under watch is the task's current run — rewrite the last claim's run id.
        Guid lastClaimRunId = task.CurrentRunId!.Value;
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);

        session.Events.StartStream<RunAggregate>(lastClaimRunId,
            new RunDispatched(lastClaimRunId, taskId, node.NodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(lastClaimRunId, Now),
            new VerificationPassed(lastClaimRunId, Now),
            new PullRequestOpened(lastClaimRunId, PullRequestUrl, 7, Now));

        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"closeout-{taskId:N}", repoPath, null, "main", Now);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);

        await session.SaveChangesAsync(cancellationToken);
        return (taskId, lastClaimRunId, worktree);
    }

    private CloseoutEngine NewEngine(
        DocumentStore store, NodeContext node, IPullRequestInspector inspector, GitWorktreeManager worktrees) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), inspector, worktrees,
            Options.Create(new DaemonOptions { MaxAutomaticCloseoutRuns = 2 }),
            NullLogger<CloseoutEngine>.Instance);

    private static void Git(string workingDirectory, string arguments)
    {
        (int exitCode, string output) = TryGit(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    private static (int ExitCode, string Output) TryGit(string workingDirectory, string arguments)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
