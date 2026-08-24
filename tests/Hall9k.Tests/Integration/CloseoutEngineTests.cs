using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Marten.Linq.MatchesSql;
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
// The handoff that lands at true closeout is read from the run directory, so this class
// owns HALL9K_HOME for its duration (Decisions Log #36) and shares the serializing
// collection with every other test that does.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class CloseoutEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private const string PullRequestUrl = "https://github.com/x/y/pull/7";

    /// <summary>Where the seeded Jira connection's token lives; set and cleared by this class.</summary>
    private const string JiraTokenVariable = "HALL9K_TEST_CLOSEOUT_JIRA_TOKEN";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-closeout-{Guid.NewGuid():N}");

    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    private sealed class FakeInspector : IPullRequestInspector
    {
        public PullRequestSnapshot Snapshot { get; set; } = Quiet();

        /// <summary>Full (InspectAsync) reads — the watched path, which needs reviews and checks too.</summary>
        public int Inspections { get; private set; }

        /// <summary>
        /// State-only (InspectStateAsync) reads — the orphan sweep's own, cheaper path
        /// (Decisions Log #72's read count). Counted separately so a test can assert that an
        /// orphan candidate never falls back to the full, reviews-and-checks read.
        /// </summary>
        public int StateInspections { get; private set; }

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

        public async Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
        {
            StateInspections++;
            if (OnInspect is { } hook)
            {
                OnInspect = null;
                await hook();
            }

            return new PullRequestStateSnapshot(Snapshot.IsMerged, Snapshot.IsClosed, Snapshot.MergedAt, Snapshot.ClosedAt);
        }

        /// <summary>Logins the provider refuses, as GitHub refuses a non-collaborator.</summary>
        public HashSet<string> RefusedReviewers { get; } = [];

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken)
        {
            ReviewRerequests.Add(reviewer.Login);
            RerequestedBots.Add(reviewer.IsBot);
            return RefusedReviewers.Contains(reviewer.Login)
                ? Task.FromException(new InvalidOperationException(
                    $"gh api ... exited 1: Reviews may only be requested from collaborators ({reviewer.Login})."))
                : Task.CompletedTask;
        }

        /// <summary>Whether each re-request addressed a bot — the [bot]-suffix decision downstream.</summary>
        public List<bool> RerequestedBots { get; } = [];

        public static PullRequestSnapshot Quiet() => new(
            IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
            FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
            UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null);
    }

    /// <summary>
    /// The landing half of the capture-then-land split (Decisions Log #36): the handoff was
    /// written to the run directory at session end, and the event carrying it appends here,
    /// with RunCompleted, on the merge observation that unblocks the dependents who read it.
    /// </summary>
    [Fact]
    public async Task Merge_lands_the_handoff_captured_at_session_end()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (_, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);
        WriteHandoffArtifact(runId, "The column is named Canonical; the rename is deliberate.");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Completed);
        run.HandoffOutcome.Should().Be(HandoffOutcome.Captured);
        run.HandoffSummary.Should().Be("The column is named Canonical; the rename is deliberate.");
    }

    /// <summary>
    /// A run whose pull request never merges hands nothing down, because the only event that
    /// carries a handoff is appended on the merge path. The artifact still exists on disk —
    /// that is the point of splitting capture from landing.
    /// </summary>
    [Fact]
    public async Task A_pull_request_closed_unmerged_never_lands_its_handoff()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (_, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);
        WriteHandoffArtifact(runId, "Work that never landed on main.");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsClosed = true, ClosedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.HandoffOutcome.Should().Be(HandoffOutcome.Unknown, "nothing was handed down, so nothing was recorded");
        run.HandoffSummary.Should().BeNull();
        File.Exists(RunPaths.HandoffFile(RunPaths.GlobalDirectory(runId))).Should().BeTrue(
            "the capture happened at session end; only the landing is withheld");
    }

    /// <summary>
    /// The three artifact states are three observations, and each closes out honestly: an
    /// empty file means the session's result was read and carried no handoff, an absent file
    /// means there was no session-end capture at all (a park resolved by hand, a historical
    /// stream). Neither is an error, and neither is silently empty.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_with_no_usable_handoff_closes_out_with_the_absence_recorded(bool artifactExists)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(store, node, worktrees, repoPath, cts.Token);
        if (artifactExists)
        {
            WriteHandoffArtifact(runId, string.Empty);
        }

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Completed, "a run with no handoff closes out like any other");
        run.HandoffOutcome.Should().Be(
            artifactExists ? HandoffOutcome.NotAuthored : HandoffOutcome.NotCaptured,
            "the absence says which absence it is rather than collapsing both into one");
        run.HandoffSummary.Should().BeNull();
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Done);
    }

    /// <summary>
    /// The handoff a dependent inherits is the task's, not the completing run's. Decision #22
    /// makes review follow-ups automatic, so a merged pull request is normally the original
    /// run (retired with RunSuperseded) plus a follow-up that resolved the review threads and
    /// reached Completed — and the original is the run that wrote the feature. Both handoffs
    /// land, in dispatch order, so the description of the work leads and the thread resolution
    /// follows it.
    /// </summary>
    [Fact]
    public async Task A_superseded_run_hands_down_its_work_ahead_of_the_follow_up_that_merged()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1);
        Guid originalRunId = await OnlyOtherRunAsync(store, taskId, runId, cts.Token);

        WriteHandoffArtifact(originalRunId, "The column is named Canonical; the rename is deliberate.");
        WriteHandoffArtifact(runId, "Resolved three review threads; no behaviour changed.");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Completed);
        run.HandoffOutcome.Should().Be(HandoffOutcome.Captured);
        run.HandoffSummary.Should().Contain("The column is named Canonical")
            .And.Contain("Resolved three review threads",
                "the merge is the work of every run that carried the pull request");
        run.HandoffSummary!.IndexOf("The column is named Canonical", StringComparison.Ordinal)
            .Should().BeLessThan(
                run.HandoffSummary.IndexOf("Resolved three review threads", StringComparison.Ordinal),
                "dispatch order puts the run that built the thing first");
    }

    /// <summary>
    /// The sharp edge of the same rule: a follow-up that resolves review threads often has
    /// nothing of its own to say, and selecting the completing run alone would have handed the
    /// dependent a recorded absence while the description of the work sat unread in the
    /// superseded run's directory.
    /// </summary>
    [Fact]
    public async Task A_follow_up_with_nothing_to_say_still_hands_down_the_original_runs_handoff()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1);
        Guid originalRunId = await OnlyOtherRunAsync(store, taskId, runId, cts.Token);

        WriteHandoffArtifact(originalRunId, "The lifecycle split is why publish and assign are separate.");
        WriteHandoffArtifact(runId, string.Empty);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.HandoffOutcome.Should().Be(HandoffOutcome.Captured);
        run.HandoffSummary.Should().Be("The lifecycle split is why publish and assign are separate.",
            "one contributor renders as its own text, whichever run authored it");
    }

    /// <summary>The task's other run, which the seed leaves superseded on the same pull request.</summary>
    private static async Task<Guid> OnlyOtherRunAsync(
        DocumentStore store, Guid taskId, Guid watchedRunId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<RunDetails> runs = await query.Query<RunDetails>()
            .Where(run => run.TaskId == taskId)
            .ToListAsync(cancellationToken);
        return runs.Single(run => run.Id != watchedRunId).Id;
    }

    private static void WriteHandoffArtifact(Guid runId, string handoff)
    {
        string runDirectory = RunPaths.GlobalDirectory(runId);
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(RunPaths.HandoffFile(runDirectory), handoff);
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
            dependencyId, "Close me out", TaskState.Done, IsClosedOut: false, CurrentRunState: null,
            PullRequestUrl: null, []);
        Hall9k.Domain.Features.Tasks.Events.TaskPublished published =
            TaskDecider.Publish(dependent, new TaskDependencyGraph([blocker]), Now, ownerId);
        dependent.Apply(published);

        Hall9k.Domain.Features.Tasks.Events.TaskAssigned assigned =
            TaskDecider.Assign(dependent, ownerId, [blocker], Now, ownerId);

        session.Events.StartStream<TaskAggregate>(dependentId, added, published, assigned);
        await session.SaveChangesAsync(cancellationToken);
        return dependentId;
    }

    /// <summary>
    /// The orphan sweep (Decisions Log #72): a Delivered row whose run left the watch set by
    /// failing rather than merging — the pre-monitor-era shape (a crash, a stream from before
    /// the closeout monitor existed) that leaves a pull request nobody will ever watch again
    /// unless something looks a second time.
    /// </summary>
    [Fact]
    public async Task An_orphaned_failed_runs_actually_merged_pull_request_reaches_true_closeout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, string originPath, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await DrainPriorSweepStateAsync(store, node, cts.Token);

        (Guid taskId, Guid runId, Worktree worktree) =
            await SeedOrphanedFailedRunAsync(store, node, worktrees, repoPath, cts.Token);
        Guid dependentId = await SeedBlockedDependentAsync(store, node.OwnerId, taskId, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddDays(-3) },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult sweep = await engine.PollOnceAsync(cts.Token);
        sweep.Should().Be(new CloseoutSweepResult(RunsInspected: 1, MergesObserved: 1),
            "the orphan pass's own read counts toward the same tally a watched merge does");
        inspector.StateInspections.Should().Be(1, "one state-only API read for the one unwatched row");
        inspector.Inspections.Should().Be(0,
            "the orphan sweep never spends the reviews-and-checks read; it only asks for merge/close state");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Completed,
            "a merge GitHub reports closes the run out exactly as a watched merge does");
        run.PullRequestMergedAt.Should().Be(Now.AddDays(-3), "GitHub's own merge timestamp is recorded honestly");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Done);

        TaskListItem dependent = (await query.LoadAsync<TaskListItem>(dependentId, cts.Token))!;
        dependent.State.Should().Be(TaskState.Queued, "true closeout unblocks dependents exactly as a watched merge does");

        Directory.Exists(worktree.Path).Should().BeFalse("closeout completion removes the retained worktree");
        TryGit(repoPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the local branch is deleted, exactly as a watched merge's is");
        TryGit(originPath, $"rev-parse --verify refs/heads/{worktree.Branch}")
            .ExitCode.Should().NotBe(0, "the remote branch is deleted");
    }

    /// <summary>
    /// A killed run reaches the identical Delivered row a failed one does — TaskStatusComposer
    /// renders a Done task's current run as Delivered for either (Copilot review, PR 33): the
    /// candidate query must admit RunState.Killed alongside RunState.Failed, or a Done task
    /// whose run was killed rather than failed sits unwatched forever, contrary to the
    /// acceptance criterion this sweep exists to satisfy.
    /// </summary>
    [Fact]
    public async Task An_orphaned_killed_runs_actually_merged_pull_request_reaches_true_closeout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await DrainPriorSweepStateAsync(store, node, cts.Token);

        (Guid taskId, Guid runId, _) =
            await SeedOrphanedFailedRunAsync(store, node, worktrees, repoPath, cts.Token, killed: true);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddDays(-3) },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult sweep = await engine.PollOnceAsync(cts.Token);
        sweep.Should().Be(new CloseoutSweepResult(RunsInspected: 1, MergesObserved: 1),
            "a killed run is exactly as much an orphan candidate as a failed one");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Completed,
            "a merge GitHub reports closes out a killed run exactly as it does a failed one");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Done);
    }

    /// <summary>
    /// The other half of the never-guess rule (AGENTS.md): recording GitHub's own merge
    /// timestamp honestly is not the same as dating the platform's own completion record to
    /// it. A normally-watched merge is observed minutes after it happens, so the two times
    /// are indistinguishable; the orphan sweep can observe a merge that happened days ago, and
    /// that is exactly the case where reusing the historical timestamp would silently backdate
    /// a record of what this sweep just did.
    /// </summary>
    [Fact]
    public async Task The_orphan_sweeps_completion_is_dated_when_observed_not_backdated_to_the_merge()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (_, Guid runId, _) = await SeedOrphanedFailedRunAsync(store, node, worktrees, repoPath, cts.Token);

        DateTimeOffset historicalMerge = DateTimeOffset.UtcNow.AddDays(-5);
        DateTimeOffset beforeSweep = DateTimeOffset.UtcNow.AddSeconds(-1);
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = historicalMerge },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.PullRequestMergedAt.Should().Be(historicalMerge, "GitHub's own merge fact is recorded honestly, however old");
        run.FinishedAt.Should().BeAfter(beforeSweep,
            "RunCompleted is dated when this sweep observed the merge, never backdated to when GitHub says it merged");
    }

    /// <summary>
    /// A pull request GitHub still reports open is left exactly as the row already renders
    /// it: the run's own recorded failure reason, and the "nothing is watching this pull
    /// request any more" attention line it already produces. The orphan sweep exists to
    /// complete an incomplete record, not to invent one — there is no run left to dispatch a
    /// follow-up onto, and a still-open answer settles nothing yet.
    /// </summary>
    [Fact]
    public async Task An_orphaned_failed_runs_still_open_pull_request_is_left_exactly_as_it_was()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await DrainPriorSweepStateAsync(store, node, cts.Token);

        (_, Guid runId, _) = await SeedOrphanedFailedRunAsync(
            store, node, worktrees, repoPath, cts.Token, failureReason: "the daemon crashed mid-review");

        FakeInspector inspector = new() { Snapshot = FakeInspector.Quiet() };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult sweep = await engine.PollOnceAsync(cts.Token);
        sweep.Should().Be(new CloseoutSweepResult(RunsInspected: 1, MergesObserved: 0),
            "the read happened, but a still-open answer records nothing");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed, "nothing is invented for an answer that settles nothing");
        run.FailureReason.Should().Be("the daemon crashed mid-review",
            "the run's own recorded reason stands untouched — the row's existing needs-you rendering is correct already");
        run.PullRequestMergedAt.Should().BeNull();

        // This run stays Failed with an open PR by design — exactly what makes the assertion
        // above meaningful — so it would otherwise keep matching every later test's orphan
        // query on this shared node/database (see RetireWatchAsync's own note).
        await RetireWatchAsync(store, runId, cts.Token);
    }

    /// <summary>
    /// A pull request GitHub reports closed without a merge is recorded exactly as the
    /// watched path records it (adversarial pre-PR review, cycle 2): FailureReason becomes
    /// <see cref="RunDetails.PullRequestClosedWithoutMerge"/>, which is the fact
    /// <c>AttentionComposer.UnwatchedRemedy</c> reads to stop sending the human to
    /// <c>h9k pr resolve</c> for a pull request nobody can merge, and it is also the fact the
    /// orphan query's own exclusion filter watches for, so the row leaves the candidate set on
    /// its own — no manual retirement needed.
    /// </summary>
    [Fact]
    public async Task An_orphaned_failed_runs_pull_request_closed_without_merge_is_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await DrainPriorSweepStateAsync(store, node, cts.Token);

        (_, Guid runId, _) = await SeedOrphanedFailedRunAsync(
            store, node, worktrees, repoPath, cts.Token, failureReason: "the daemon crashed mid-review");

        FakeInspector inspector = new() { Snapshot = FakeInspector.Quiet() with { IsClosed = true, ClosedAt = Now } };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult sweep = await engine.PollOnceAsync(cts.Token);
        sweep.Should().Be(new CloseoutSweepResult(RunsInspected: 1, MergesObserved: 0),
            "a close is recorded, but it is not a merge");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.Failed);
        run.FailureReason.Should().Be(RunDetails.PullRequestClosedWithoutMerge,
            "the close overwrites the run's original failure reason with the fact that now decides its remedy");
        run.PullRequestMergedAt.Should().BeNull();

        CloseoutSweepResult secondSweep = await engine.PollOnceAsync(cts.Token);
        secondSweep.Should().Be(new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0),
            "a run recorded closed without merge leaves the orphan query's candidate set on its own");
        inspector.StateInspections.Should().Be(1,
            "the second sweep never re-reads a pull request whose fate is already recorded");
    }

    /// <summary>
    /// A Failed run the closeout monitor itself already watched close (the ordinary, watched
    /// path) has nothing left for the orphan sweep to learn — FailureReason already names it —
    /// so it is never re-inspected. Bounding the sweep to rows that could actually still be
    /// open or unknown is what keeps "one API read per unwatched row" honest.
    /// </summary>
    [Fact]
    public async Task A_run_already_recorded_closed_without_merge_is_never_reinspected()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await DrainPriorSweepStateAsync(store, node, cts.Token);

        await SeedOrphanedFailedRunAsync(
            store, node, worktrees, repoPath, cts.Token, recordAsClosedWithoutMerge: true);

        FakeInspector inspector = new();
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult sweep = await engine.PollOnceAsync(cts.Token);
        sweep.Should().Be(new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0));
        inspector.StateInspections.Should().Be(0, "the run already recorded why it will never merge; asking again spends a read to relearn it");
    }

    /// <summary>
    /// A superseded orphan retires the same way the watched path already does (adversarial
    /// pre-PR review, cycle 1): once <c>h9k pr resolve</c> hands the pull request to a
    /// successor run, the old Failed run's own predicates (Failed, PullRequestNumber set,
    /// not recorded closed) never stop matching the orphan query on their own — nothing
    /// about the run itself changes when a follow-up takes over. Left unretired it would pay
    /// a stream fetch and a full aggregate replay every sweep forever. Appending
    /// <see cref="RunSuperseded"/> the moment the mismatch is seen, exactly as
    /// <c>InspectAndActAsync</c> already does for the watched runs it retires, is what keeps
    /// the orphan set — like the watched one — bounded.
    /// </summary>
    [Fact]
    public async Task An_orphan_superseded_by_a_follow_up_run_retires_instead_of_being_reinspected_forever()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await DrainPriorSweepStateAsync(store, node, cts.Token);

        (Guid taskId, Guid runId, _) = await SeedOrphanedFailedRunAsync(store, node, worktrees, repoPath, cts.Token);

        // Model "h9k pr resolve" followed by the daemon claiming the follow-up: the task
        // stream moves CurrentRunId off the orphaned run without ever touching the orphaned
        // run's own stream — the exact gap the adversarial finding describes.
        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                task, runId, details.FollowUpBranch ?? "task/x", "Unresolved review comments.",
                FollowUpKind.ReviewFeedback, automatic: false, Now, node.OwnerId);
            task.Apply(reopened);
            session.Events.Append(taskId, reopened);

            Guid followUpRunId = DomainId.New();
            Hall9k.Domain.Features.Tasks.Events.TaskClaimed claimed =
                TaskDecider.Claim(task, node.NodeId, node.OwnerId, followUpRunId, Now);
            session.Events.Append(taskId, claimed);
            await session.SaveChangesAsync(cts.Token);
        }

        FakeInspector inspector = new();
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        CloseoutSweepResult firstSweep = await engine.PollOnceAsync(cts.Token);
        firstSweep.Should().Be(new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0),
            "a superseded orphan is skipped, not inspected — its pull request belongs to a successor now");
        inspector.StateInspections.Should().Be(0, "the successor owns this pull request; the old run's own state has nothing left to learn");

        await using IQuerySession query = store.QuerySession();
        RunDetails retired = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        retired.State.Should().Be(RunState.Superseded,
            "the orphan sweep retires a run whose task moved on, exactly as the watched path already does");

        // The retirement is what keeps the candidate set bounded: a second sweep does not
        // even find this row any more, because it no longer matches state = Failed.
        CloseoutSweepResult secondSweep = await engine.PollOnceAsync(cts.Token);
        secondSweep.Should().Be(new CloseoutSweepResult(RunsInspected: 0, MergesObserved: 0));
        inspector.StateInspections.Should().Be(0, "a superseded run has left the orphan query's candidate set entirely");
    }

    /// <summary>
    /// A task Done with a pull request whose run crashed before the closeout monitor ever
    /// watched it — the shape the orphan sweep exists for (Decisions Log #72):
    /// <see cref="PullRequestOpened"/> landed and the run then failed directly, the same way a
    /// pre-monitor-era stream or a daemon crash leaves one, rather than being retired by a
    /// follow-up or observed merged.
    /// </summary>
    private static async Task<(Guid TaskId, Guid RunId, Worktree Worktree)> SeedOrphanedFailedRunAsync(
        DocumentStore store,
        NodeContext node,
        GitWorktreeManager worktrees,
        string repoPath,
        CancellationToken cancellationToken,
        string failureReason = "the daemon crashed mid-review",
        bool recordAsClosedWithoutMerge = false,
        bool killed = false)
    {
        Guid taskId = DomainId.New();
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
                taskId, projectId, "Close me out", ["merged"], TaskType.Chore, null, null,
                null, Now, ownerId),
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

        Guid runId = task.CurrentRunId!.Value;
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now),
            new PullRequestOpened(runId, PullRequestUrl, 7, Now));

        if (recordAsClosedWithoutMerge)
        {
            session.Events.Append(runId, new PullRequestClosed(runId, Now, Now));
        }
        else if (killed)
        {
            session.Events.Append(runId, new RunKilled(runId, KillReason.HumanRequested, node.OwnerId, Now));
        }
        else
        {
            session.Events.Append(runId, new RunFailed(runId, failureReason, Now));
        }

        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"closeout-{taskId:N}", repoPath, null, "main", Now);
        session.Events.StartStream<Hall9k.Domain.Features.Project.ProjectAggregate>(registered.Id, registered);

        await session.SaveChangesAsync(cancellationToken);
        return (taskId, runId, worktree);
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
            Snapshot = FakeInspector.Quiet() with { UnresolvedReviewThreadCount = 2 },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.Superseded);
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewFeedback);
    }

    /// <summary>
    /// The reason the whole feature exists (Decisions Log #62, origin incident 2026-08-20): a
    /// human's unresolved thread was invisible while the inspector counted only Copilot's.
    /// Same dispatch, same budget, and the human count reaches the follow-up's prompt.
    /// </summary>
    [Fact]
    public async Task A_human_reviewers_unresolved_thread_dispatches_the_same_follow_up()
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
                UnresolvedReviewThreadCount = 1,
                UnresolvedHumanThreadCount = 1,
            },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.UnresolvedHumanReviewThreads.Should().Be(
            1, "who is waiting on an answer is part of the observation, not only how many threads there are");
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpKind.Should().Be(FollowUpKind.ReviewFeedback);
        task.FollowUpReason.Should().Contain("started by a human reviewer",
            "the prompt's care rules key off a person waiting");
    }

    /// <summary>
    /// The countersign is off unless someone said otherwise: nothing about a quiet pull
    /// request should spend review quota by default (Decisions Log #62).
    /// </summary>
    [Fact]
    public async Task A_quiet_pull_request_is_never_rerequested_by_default()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (_, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1, asFollowUp: true);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { Reviewers = [new PullRequestReviewer("teammate", ReviewerKind.Human)] },
        };
        await NewEngine(store, node, inspector, worktrees).PollOnceAsync(cts.Token);

        inspector.ReviewRerequests.Should().BeEmpty("the option is off until an owner or a project turns it on");
        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.ReviewRerequestsAfterFixes.Should().Be(0);
            run.State.Should().Be(RunState.AwaitingReview);
        }

        await RetireWatchAsync(store, runId, cts.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_opted_in_owner_or_project_rerequests_review_once_the_fixes_are_pushed(bool onTheOwner)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1, asFollowUp: true);
        await EnableReviewRerequestAsync(
            store,
            onTheOwner ? node.OwnerId : await ProjectIdAsync(store, taskId, cts.Token),
            onTheOwner,
            cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                Reviewers = [new PullRequestReviewer("copilot-pull-request-reviewer", ReviewerKind.Bot)],
            },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        inspector.ReviewRerequests.Should().Equal("copilot-pull-request-reviewer");
        inspector.RerequestedBots.Should().Equal([true],
            "the [bot]-suffix decision is the provider's actor type, not a guess from the login");

        await using (IQuerySession query = store.QuerySession())
        {
            RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
            run.ReviewRerequestsAfterFixes.Should().Be(1);
            run.State.Should().Be(RunState.AwaitingReview,
                "asking for a countersignature is a question, not a finding — the watch continues");
        }

        // The next sweep must not ask again: this run pushed its fixes once, so it
        // countersigns them once.
        await engine.PollOnceAsync(cts.Token);
        inspector.ReviewRerequests.Should().HaveCount(1);

        await RetireWatchAsync(store, runId, cts.Token);
        if (onTheOwner)
        {
            await ClearOwnerReviewRerequestAsync(store, node.OwnerId, cts.Token);
        }
    }

    /// <summary>
    /// A countersign asks the reviewers who have not seen the head. A reviewer whose latest
    /// review already sits on the pushed commit — a Copilot pass that recovered, a human who
    /// re-approved — has nothing left to countersign, and asking anyway would spend a pass,
    /// reset a standing verdict to pending, and buy a redundant bot pass whose new threads
    /// spend the OTHER budget (Decisions Log #62).
    /// </summary>
    [Fact]
    public async Task The_countersign_skips_reviewers_whose_latest_review_already_covers_the_head()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1, asFollowUp: true);
        await EnableReviewRerequestAsync(store, node.OwnerId, onTheOwner: true, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                HeadCommit = "cafe1",
                Reviewers =
                [
                    new PullRequestReviewer("copilot", ReviewerKind.Bot, LastReviewedCommit: "cafe1"),
                    new PullRequestReviewer("teammate", ReviewerKind.Human, LastReviewedCommit: "cafe1"),
                ],
            },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        inspector.ReviewRerequests.Should().BeEmpty("every reviewer has already read these commits");
        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ReviewRerequestsAfterFixes.Should().Be(
                0, "a pass nobody needed must not be charged against the cap");
        }

        // The teammate's review is now behind the head; only they are asked.
        inspector.Snapshot = inspector.Snapshot with
        {
            HeadCommit = "cafe2",
            Reviewers =
            [
                new PullRequestReviewer("copilot", ReviewerKind.Bot, LastReviewedCommit: "cafe2"),
                new PullRequestReviewer("teammate", ReviewerKind.Human, LastReviewedCommit: "cafe1"),
            ],
        };
        await engine.PollOnceAsync(cts.Token);

        inspector.ReviewRerequests.Should().Equal("teammate");
        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ReviewRerequestsAfterFixes.Should().Be(1);
        }

        await RetireWatchAsync(store, runId, cts.Token);
        await ClearOwnerReviewRerequestAsync(store, node.OwnerId, cts.Token);
    }

    /// <summary>
    /// One reviewer the provider refuses is that reviewer's answer, not the pass's. The pass
    /// is recorded before the requests go out and a refusal is stepped over, because the
    /// alternative — append only after all N succeeded — left a rejected reviewer throwing
    /// with earlier requests already landed and no pass recorded, so every sweep did it all
    /// again with the cap never binding (Decisions Log #62).
    /// </summary>
    [Fact]
    public async Task A_reviewer_the_provider_refuses_neither_stops_the_pass_nor_repeats_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1, asFollowUp: true);
        await EnableReviewRerequestAsync(store, node.OwnerId, onTheOwner: true, cts.Token);

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                Reviewers =
                [
                    new PullRequestReviewer("departed", ReviewerKind.Human),
                    new PullRequestReviewer("copilot", ReviewerKind.Bot),
                ],
            },
        };
        inspector.RefusedReviewers.Add("departed");

        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        inspector.ReviewRerequests.Should().Equal(
            ["departed", "copilot"], "the refusal is stepped over, not allowed to abort the pass");
        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ReviewRerequestsAfterFixes.Should().Be(
                1, "the pass is spent whether or not every provider accepted it");
        }

        await engine.PollOnceAsync(cts.Token);
        inspector.ReviewRerequests.Should().HaveCount(2, "a spent pass is not retried every sweep, forever");

        await RetireWatchAsync(store, runId, cts.Token);
        await ClearOwnerReviewRerequestAsync(store, node.OwnerId, cts.Token);
    }

    /// <summary>
    /// Two sweeps overlapping — the startup catch-up and the monitor's timer both reach this
    /// pull request — spend one pass between them, not two. The inspection is the slow call
    /// they overlap inside, so the second sweep is run from inside the first one's, which is
    /// exactly the window the fence has to cover.
    /// </summary>
    [Fact]
    public async Task Overlapping_sweeps_issue_one_countersign_between_them()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1, asFollowUp: true);
        await EnableReviewRerequestAsync(store, node.OwnerId, onTheOwner: true, cts.Token);

        PullRequestSnapshot quiet = FakeInspector.Quiet() with
        {
            Reviewers = [new PullRequestReviewer("copilot", ReviewerKind.Bot)],
        };
        FakeInspector second = new() { Snapshot = quiet };
        CloseoutEngine secondSweep = NewEngine(store, node, second, worktrees);
        FakeInspector first = new()
        {
            Snapshot = quiet,
            // The whole second sweep lands inside the first one's gh call and commits first.
            OnInspect = () => secondSweep.PollOnceAsync(cts.Token),
        };

        await NewEngine(store, node, first, worktrees).PollOnceAsync(cts.Token);

        second.ReviewRerequests.Should().Equal("copilot");
        first.ReviewRerequests.Should().BeEmpty(
            "the run had already spent its one pass by the time this sweep came back from gh");
        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.ReviewRerequestsAfterFixes.Should().Be(1);
        }

        await RetireWatchAsync(store, runId, cts.Token);
        await ClearOwnerReviewRerequestAsync(store, node.OwnerId, cts.Token);
    }

    /// <summary>
    /// The bound against the doom loop (Decisions Log #62): passes are counted across the
    /// task's runs, so a fresh follow-up run does not hand the loop a fresh budget.
    /// </summary>
    [Fact]
    public async Task The_rerequest_cap_is_summed_across_the_tasks_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token, priorAutomaticReopens: 1, asFollowUp: true);
        await EnableReviewRerequestAsync(store, node.OwnerId, onTheOwner: true, cts.Token);

        // A previous run of this task already spent the single allowed pass.
        await using (IDocumentSession spent = store.LightweightSession())
        {
            Guid earlier = (await spent.Query<RunDetails>()
                .Where(candidate => candidate.TaskId == taskId)
                .ToListAsync(cts.Token))
                .First(candidate => candidate.Id != runId).Id;
            spent.Events.Append(earlier, new ReviewRerequestedAfterFixes(earlier, ["copilot"], 1, Now));
            await spent.SaveChangesAsync(cts.Token);
        }

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { Reviewers = [new PullRequestReviewer("copilot", ReviewerKind.Bot)] },
        };
        await NewEngine(store, node, inspector, worktrees, maxReviewRerequests: 1).PollOnceAsync(cts.Token);

        inspector.ReviewRerequests.Should().BeEmpty(
            "at the cap the pull request settles on the internal review, the thread replies, and CI");
        await using (IQuerySession query = store.QuerySession())
        {
            (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.AwaitingReview);
        }

        await RetireWatchAsync(store, runId, cts.Token);
        await ClearOwnerReviewRerequestAsync(store, node.OwnerId, cts.Token);
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
            Snapshot = FakeInspector.Quiet() with { ErroredReview = errored },
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
                ErroredReview = new ErroredReview(
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
                ErroredReview = new ErroredReview(
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
                ErroredReview = new ErroredReview(
                    "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1"),
            },
        };
        CloseoutEngine engine = NewEngine(store, node, inspector, worktrees);
        await engine.PollOnceAsync(cts.Token);

        // The re-requested review succeeds and leaves real feedback: latestReviews no
        // longer matches as errored, unresolved threads appear.
        inspector.Snapshot = FakeInspector.Quiet() with { UnresolvedReviewThreadCount = 2 };
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

    /// <summary>
    /// Backlog 45: the cap counts repetition on the SAME obstruction, not the raw lap count.
    /// Two prior laps already spent against "the failing check(s) build" (the per-obstruction
    /// cap), but the lifetime ceiling is wide open — so a third lap on the identical check
    /// parks for the progress cap alone, never touching the lifetime budget's own message.
    /// </summary>
    [Fact]
    public async Task Repeating_the_same_failing_check_parks_at_the_per_obstruction_cap_with_lifetime_room_to_spare()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            priorAutomaticReopens: 2, priorObstructionKey: "FailingChecks:build");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { FailingChecks = ["build"] },
        };
        await NewEngine(
            store, node, inspector, worktrees,
            maxAutomaticCloseoutRuns: 10, maxCloseoutLapsPerObstruction: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.ParkedReason.Should().Contain("build")
            .And.Contain("survived 2 automatic lap(s)")
            .And.Contain("cap 2 per obstruction")
            .And.Contain("h9k pr resolve")
            .And.NotContain("lifetime automatic closeout budget spent",
                "the lifetime ceiling has plenty of room — this park is the progress cap's own");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Done);
    }

    /// <summary>
    /// A lap that clears its obstruction resets the progress counter (backlog 45): a
    /// different check failing is, mechanically, a different obstruction — even though the
    /// per-obstruction cap was already fully spent on "build", a fresh check gets its own
    /// first lap and dispatches normally.
    /// </summary>
    [Fact]
    public async Task A_different_failing_check_is_a_different_obstruction_and_dispatches_normally()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            priorAutomaticReopens: 2, priorObstructionKey: "FailingChecks:build");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { FailingChecks = ["lint"] },
        };
        await NewEngine(
            store, node, inspector, worktrees,
            maxAutomaticCloseoutRuns: 10, maxCloseoutLapsPerObstruction: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.Superseded, "a different check is a different obstruction — the lap dispatches, it does not park");
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpBranch.Should().Be(worktree.Branch);

        TaskAggregate aggregate = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        aggregate.LastAutomaticObstructionKey.Should().Be("FailingChecks:lint");
        aggregate.ConsecutiveObstructionLaps.Should().Be(
            1, "the obstruction changed, so this is its first lap regardless of how many the old one survived");
    }

    /// <summary>
    /// Backlog 45's origin incident (2026-08-22, PR 26): a human re-engaging with the pull
    /// request is proof the loop is not running away, so it grants one more automatic lap
    /// despite the per-obstruction cap already being spent — a newly opened human review
    /// thread is one of the three mechanical signals. The lap still counts toward the
    /// obstruction's own running total; the grant buys one exception, not a reset.
    /// </summary>
    [Fact]
    public async Task A_newly_opened_human_thread_grants_a_lap_despite_the_spent_progress_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            priorAutomaticReopens: 2, priorObstructionKey: "FailingChecks:build");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                FailingChecks = ["build"],
                UnresolvedReviewThreadCount = 1,
                UnresolvedHumanThreadCount = 1,
                UnresolvedReviewThreadIds = ["thread-new"],
                UnresolvedHumanThreadIds = ["thread-new"],
            },
        };
        await NewEngine(
            store, node, inspector, worktrees,
            maxAutomaticCloseoutRuns: 10, maxCloseoutLapsPerObstruction: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.Superseded, "the human-opened thread grants the lap instead of parking");
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpBranch.Should().Be(worktree.Branch);
        task.FollowUpReason.Should().Contain("human engaged")
            .And.Contain("new review thread(s) opened by a human");

        TaskAggregate aggregate = (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        aggregate.ConsecutiveObstructionLaps.Should().Be(
            3, "the grant buys this lap through the cap — it does not reset the obstruction's own count");
    }

    /// <summary>
    /// The lifetime ceiling is the absolute backstop (backlog 45): it is checked before any
    /// human-engagement grant is even consulted, so a human re-engaging cannot buy a lap past
    /// it. Only h9k pr resolve does.
    /// </summary>
    [Fact]
    public async Task A_human_engagement_never_bypasses_the_lifetime_ceiling()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            priorAutomaticReopens: 3, priorObstructionKey: "FailingChecks:build");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                FailingChecks = ["build"],
                UnresolvedReviewThreadCount = 1,
                UnresolvedHumanThreadCount = 1,
                UnresolvedReviewThreadIds = ["thread-new"],
                UnresolvedHumanThreadIds = ["thread-new"],
            },
        };
        await NewEngine(
            store, node, inspector, worktrees,
            maxAutomaticCloseoutRuns: 3, maxCloseoutLapsPerObstruction: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked, "the lifetime ceiling is spent, and nothing bypasses it");
        run.ParkedReason.Should().Contain("lifetime automatic closeout budget spent").And.Contain("h9k pr resolve");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Done);
    }

    /// <summary>
    /// A newly pending review request is one of the two mechanical human-engagement signals
    /// (backlog 45): a login neither the task nor this run already knew about grants the lap
    /// despite the spent progress cap, symmetrically with a newly opened human thread.
    /// </summary>
    [Fact]
    public async Task A_newly_pending_review_request_grants_a_lap_despite_the_spent_progress_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, Worktree worktree) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            priorAutomaticReopens: 2, priorObstructionKey: "FailingChecks:build");

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                FailingChecks = ["build"],
                PendingReviewRequestLogins = ["teammate"],
            },
        };
        await NewEngine(
            store, node, inspector, worktrees,
            maxAutomaticCloseoutRuns: 10, maxCloseoutLapsPerObstruction: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(
            RunState.Superseded, "the newly pending review request grants the lap instead of parking");
        TaskDetails task = (await query.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        task.State.Should().Be(TaskState.Queued);
        task.FollowUpBranch.Should().Be(worktree.Branch);
        task.FollowUpReason.Should().Contain("human engaged").And.Contain("a review re-request for teammate");
    }

    /// <summary>
    /// A review request the platform itself issued between two automatic decisions must not
    /// read back as a human's own engagement — that would let the monitor grant its own laps
    /// past the progress cap it exists to enforce (independent pre-PR review, 2026-08-23:
    /// KnownPendingReviewRequestLogins only travels forward on TaskReopened, so a re-request
    /// this run issued after its last automatic decision was invisible to the comparison
    /// until RunAggregate.RequestedReviewerLogins closed the gap).
    /// </summary>
    [Fact]
    public async Task The_platforms_own_review_rerequest_does_not_read_back_as_human_engagement()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            priorAutomaticReopens: 2, priorObstructionKey: "FailingChecks:build");

        // This run already asked "copilot-pull-request-reviewer" for a review (an errored-review
        // re-request between two automatic decisions) — recorded on the run's own stream, never
        // on the task's KnownPendingReviewRequestLogins.
        await using (IDocumentSession spent = store.LightweightSession())
        {
            spent.Events.Append(
                runId, new ReviewRerequested(runId, "copilot-pull-request-reviewer", $"{PullRequestUrl}#pullrequestreview-1", Now));
            await spent.SaveChangesAsync(cts.Token);
        }

        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with
            {
                FailingChecks = ["build"],
                PendingReviewRequestLogins = ["copilot-pull-request-reviewer"],
            },
        };
        await NewEngine(
            store, node, inspector, worktrees,
            maxAutomaticCloseoutRuns: 10, maxCloseoutLapsPerObstruction: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Should().Be(
            RunState.CloseoutParked, "the pending request is this run's own, so it must not grant a lap");
        run.ParkedReason.Should().NotContain("review re-request for copilot-pull-request-reviewer");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Done);
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
                FailingChecks = ["build"], HasPendingChecks = true, UnresolvedReviewThreadCount = 1,
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

    /// <summary>
    /// Closeout tells the card what happened (backlog 18). A comment and not a transition:
    /// which status a merge should move a card to is one team's workflow rather than a fact
    /// about software, so moving somebody's board on the platform's opinion is exactly the guess
    /// this repo refuses to make.
    /// </summary>
    [Fact]
    public async Task A_merge_comments_the_pull_request_on_the_card_the_task_carries()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await SeedJiraConnectionAsync(store, node, cts.Token);

        (Guid taskId, _, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            externalReference: new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));

        RecordingJira jira = new();
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees, jira: jira).PollOnceAsync(cts.Token);

        JiraRequest request = jira.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.ToString().Should().Be("https://hall9k.atlassian.net/rest/api/2/issue/PROJ-123/comment");
        request.JsonBody.Should().Contain(PullRequestUrl).And.Contain(taskId.ToString());
        request.JsonBody.Should().Contain("does not change the card",
            "a card that silently gains a comment and never moves reads like an integration that half worked");
    }

    [Fact]
    public async Task A_merge_with_no_jira_reference_says_nothing_to_anybody()
    {
        // A GitHub-adopted task gets nothing here on purpose: the pull request body already
        // mentions the issue, so GitHub cross-references the merge on its own timeline.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await SeedJiraConnectionAsync(store, node, cts.Token);

        await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            externalReference: new ExternalReference(WorkItemProvider.GitHub, "o/r#42"));

        RecordingJira jira = new();
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees, jira: jira).PollOnceAsync(cts.Token);

        jira.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// The merge is already recorded and the dependents already unblocked by the time the card is
    /// told, so a Jira outage costs the note and nothing else — and it is not retried, because a
    /// retry loop around an unwatched write is how one card ends up with four identical comments.
    /// </summary>
    [Fact]
    public async Task A_jira_that_refuses_the_comment_does_not_undo_the_closeout()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        (DocumentStore store, NodeContext node, GitWorktreeManager worktrees, _, string repoPath) =
            await SetUpAsync(cts.Token);
        using IDisposable storeLifetime = store;
        await SeedJiraConnectionAsync(store, node, cts.Token);

        (Guid taskId, Guid runId, _) = await SeedAwaitingReviewAsync(
            store, node, worktrees, repoPath, cts.Token,
            externalReference: new ExternalReference(WorkItemProvider.Jira, "PROJ-123"));

        RecordingJira jira = new(statusCode: 403, body: "{\"errorMessages\":[\"No permission\"]}");
        FakeInspector inspector = new()
        {
            Snapshot = FakeInspector.Quiet() with { IsMerged = true, MergedAt = Now.AddHours(2) },
        };
        await NewEngine(store, node, inspector, worktrees, jira: jira).PollOnceAsync(cts.Token);

        jira.Requests.Should().ContainSingle("the write is attempted once and never retried blind");
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Should().Be(RunState.Completed);
        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Should().Be(TaskState.Done);
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
        int priorAutomaticReopens = 0,
        bool asFollowUp = false,
        ExternalReference? externalReference = null,
        string? priorObstructionKey = null,
        string? priorObstructionSummary = null)
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
                taskId, projectId, "Close me out", ["merged"], TaskType.Chore, null, null,
                externalReference, Now, ownerId),
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

        // Each reopen retires the run that held the pull request and claims a follow-up onto
        // the same branch — the ordinary shape since decision #22, and the shape the composed
        // handoff is about, so the superseded runs get real streams rather than bare ids.
        List<Guid> superseded = [];
        for (int i = 0; i < priorAutomaticReopens; i++)
        {
            superseded.Add(task.CurrentRunId!.Value);
            Hall9k.Domain.Features.Tasks.Events.TaskReopened reopened = TaskDecider.Reopen(
                task, task.CurrentRunId!.Value, worktree.Branch,
                "CI checks failing.", FollowUpKind.FailingChecks, automatic: true, Now, ownerId,
                obstructionKey: priorObstructionKey,
                obstructionSummary: priorObstructionSummary ?? priorObstructionKey);
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

        for (int i = 0; i < superseded.Count; i++)
        {
            // Dispatched strictly before the run under watch, so composition order is the
            // dispatch order it claims to be rather than an accident of id generation.
            DateTimeOffset dispatchedAt = Now.AddMinutes(-10 * (superseded.Count - i));
            session.Events.StartStream<RunAggregate>(superseded[i],
                new RunDispatched(superseded[i], taskId, node.NodeId, ownerId, i + 1, DomainId.New(),
                    worktree.Path, worktree.Branch, ExecutorMode.Subscription, dispatchedAt),
                new AgentSessionCompleted(superseded[i], dispatchedAt),
                new VerificationPassed(superseded[i], dispatchedAt),
                new PullRequestOpened(superseded[i], PullRequestUrl, 7, dispatchedAt),
                new RunSuperseded(superseded[i], i + 2, dispatchedAt));
        }

        session.Events.StartStream<RunAggregate>(lastClaimRunId,
            new RunDispatched(lastClaimRunId, taskId, node.NodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                worktree.Path, worktree.Branch, ExecutorMode.Subscription, Now, IsFollowUp: asFollowUp),
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
        DocumentStore store,
        NodeContext node,
        IPullRequestInspector inspector,
        GitWorktreeManager worktrees,
        int maxReviewRerequests = 2,
        RecordingJira? jira = null,
        int maxAutomaticCloseoutRuns = 2,
        int maxCloseoutLapsPerObstruction = 2) =>
        new(store, node, new DaemonConnection(postgres.ConnectionString), inspector, worktrees,
            (jira ?? new RecordingJira()).Requester,
            Options.Create(new DaemonOptions
            {
                MaxAutomaticCloseoutRuns = maxAutomaticCloseoutRuns,
                MaxCloseoutLapsPerObstruction = maxCloseoutLapsPerObstruction,
                MaxReviewRerequestsAfterFixes = maxReviewRerequests,
            }),
            NullLogger<CloseoutEngine>.Instance);

    /// <summary>
    /// Leaves the watch set as the test found it. One node and one database back this whole
    /// class, and CloseoutEngine sweeps every watched run the node owns, so a run left
    /// AwaitingReview on a Done task is inspected by every later test's sweep — one test's
    /// leftovers become another's tally. Tests whose run legitimately stays watched retire it
    /// here, exactly as a real follow-up dispatch would.
    /// </summary>
    private static async Task RetireWatchAsync(DocumentStore store, Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunSuperseded(runId, 99, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Retires whatever is already sitting in the watch set or the orphan set for this node
    /// before a test seeds its own scenario, for the same reason <see cref="RetireWatchAsync"/>
    /// exists: this class's tests share one node and one database, so a leftover row from an
    /// earlier test that runs before it inflates <see cref="CloseoutSweepResult"/>'s exact
    /// count regardless of which test left it. Called by tests that assert that exact count,
    /// so their pass/fail cannot depend on execution order.
    /// </summary>
    private static async Task DrainPriorSweepStateAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> watched = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?)",
                RunState.AwaitingReview.Value, RunState.ReviewPending.Value, RunState.CloseoutParked.Value))
            .ToListAsync(cancellationToken);
        IReadOnlyList<RunDetails> orphaned = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql(
                "d.data ->> 'state' in (?, ?)", RunState.Failed.Value, RunState.Killed.Value))
            .Where(r => r.PullRequestNumber != null)
            .Where(r => r.FailureReason != RunDetails.PullRequestClosedWithoutMerge)
            .ToListAsync(cancellationToken);

        if (watched.Count == 0 && orphaned.Count == 0)
        {
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        foreach (Guid runId in watched.Concat(orphaned).Select(r => r.Id))
        {
            session.Events.Append(runId, new RunSuperseded(runId, 999, Now));
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Turns the countersign on at the level under test, leaving the others unset.</summary>
    private static async Task EnableReviewRerequestAsync(
        DocumentStore store, Guid streamId, bool onTheOwner, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        if (onTheOwner)
        {
            session.Events.Append(streamId, new OwnerSettingsChanged(
                streamId, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled), Now));
        }
        else
        {
            session.Events.Append(streamId, new ProjectSettingsChanged(
                streamId,
                Optional<IReadOnlyList<VerifyCommand>>.None,
                Optional<bool>.None,
                Optional<int>.None,
                Optional<IReadOnlyList<ContextLink>>.None,
                Now,
                Guid.Empty,
                ReviewRerequest: Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Enabled)));
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Puts the owner's preference back to unset. The owner is per machine, so it is shared by
    /// every test in this class: an opt-in left behind would silently opt the others in too.
    /// </summary>
    private static async Task ClearOwnerReviewRerequestAsync(
        DocumentStore store, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(ownerId, new OwnerSettingsChanged(
            ownerId, Optional<ReviewRerequestPolicy>.Of(ReviewRerequestPolicy.Unknown), Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The project the seeded task belongs to — the stream the project-level setting lands on.</summary>
    private static async Task<Guid> ProjectIdAsync(DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        return (await query.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken))!.ProjectId;
    }


    /// <summary>
    /// The Jira connector's network seam, recorded. It is registered on the engine even when a
    /// test seeds no Jira connection, which is itself the assertion in that case: a call that
    /// should never happen is one this fake would have kept.
    /// </summary>
    private sealed class RecordingJira(int statusCode = 201, string body = "{}")
    {
        public List<JiraRequest> Requests { get; } = [];

        public JiraRequester Requester => (request, _) =>
        {
            Requests.Add(request);
            return Task.FromResult(new JiraResponse(statusCode, body));
        };
    }

    /// <summary>
    /// A registered Jira connection, with its token in an environment variable so the test
    /// touches no keychain and writes no file. The credential still travels as a reference —
    /// the discipline is exercised rather than sidestepped.
    /// </summary>
    private static async Task SeedJiraConnectionAsync(
        DocumentStore store, NodeContext node, CancellationToken cancellationToken)
    {
        Environment.SetEnvironmentVariable(JiraTokenVariable, "a-token");
        await using IDocumentSession session = store.LightweightSession();

        // Once per database, not once per test: every test in this class shares the fixture's
        // Postgres, and a second registered Jira connection is a state the connector refuses on
        // purpose (nothing says which account a project uses) — which would make these tests
        // assert the refusal rather than the comment.
        if (await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken) is not null)
        {
            return;
        }

        Guid connectionId = DomainId.New();
        session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
            connectionId, node.OwnerId, WorkItemProvider.Jira, "brian@example.com",
            CredentialReference.EnvironmentVariable(JiraTokenVariable), Now,
            new Uri("https://hall9k.atlassian.net")));
        await session.SaveChangesAsync(cancellationToken);
    }

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
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        Environment.SetEnvironmentVariable(JiraTokenVariable, null);
        try
        {
            if (Directory.Exists(_home))
            {
                Directory.Delete(_home, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

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
