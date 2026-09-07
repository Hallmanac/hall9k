using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Tests.Fakes;
using JasperFx;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// A stacked child standing on a pull request another install owns (task: a stacked child can stand
/// on a pull request another install owns), against a real store and a real repository with the
/// provider seam faked. The reviewer's node never holds the parent's run, so everything the child
/// knows about its parent comes from one read of that pull request on the closeout watcher's
/// cadence — which makes three things load-bearing, each a one-directional hazard.
/// <list type="bullet">
/// <item>A child whose parent has not been observed OPEN must not dispatch: there is no branch on
/// origin to cut from, so it would land on the project's base carrying none of the parent's
/// work.</item>
/// <item>A merge observed on GitHub must produce exactly the retarget-and-replay a local parent's
/// closeout event produces — the same operation, a different trigger.</item>
/// <item>A parent that closed unmerged must park rather than keep the child chasing a branch
/// nothing further arrives on.</item>
/// </list>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class RemoteStackedParentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Hands each test a parent pull request number of its own. The whole class shares one Postgres
    /// database AND one owner — <c>NodeBootstrap.EnsureAsync</c> returns the owner an earlier test
    /// created rather than minting a fresh one — so every test's child is visible to every other
    /// test's sweep. That is the shape the sweep is scoped for in production (one owner, several
    /// stacked children), so it is left alone here and the assertions are made per child instead of
    /// on the sweep's own totals.
    /// </summary>
    private static int _parentNumbers = 1000;

    private const int ChildNumber = 8;

    private const string ChildPullRequest = "https://github.com/x/y/pull/8";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-remote-stacked-{Guid.NewGuid():N}");

    // ---------------------------------------------------------------------------------------
    // Before the child dispatches: the pull request's own state is the whole of the hold.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The gap this whole feature closes: a reviewer's node never sees the parent reach Delivered
    /// locally, so without a read of the pull request itself the child waits forever.
    /// </summary>
    [Fact]
    public async Task An_open_parent_pull_request_releases_the_blocked_child_to_the_dispatcher()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token);

        FakeRemoteParentReader reader = new(Open(fixture, fixture.ParentHeadCommit));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        reader.Reads.Should().Contain(fixture.ParentNumber);

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.State.Should().Be(TaskState.Queued,
            "an open pull request is the remote parent's Delivered, and Delivered is what releases a child");
        child.RemoteStackedParentHeadBranch.Should().Be(fixture.ParentBranch);
        child.RemoteStackedParentState.Should().Be(RemoteParentState.Open);

        TaskDetails details = (await query.LoadAsync<TaskDetails>(fixture.ChildTaskId, cts.Token))!;
        details.State.Should().Be(TaskState.Queued, "the projection and the aggregate read one rule");
        details.RemoteStackedParentHeadBranch.Should().Be(fixture.ParentBranch);
    }

    /// <summary>
    /// A second look that says the same thing appends nothing: the stream is a record of
    /// observations, and re-recording an unchanged one every few minutes would bury the moment the
    /// parent actually moved.
    /// </summary>
    [Fact]
    public async Task An_unchanged_parent_records_nothing_on_the_second_look()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token);

        FakeRemoteParentReader reader = new(Open(fixture, fixture.ParentHeadCommit));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        reader.Reads.Should().HaveCountGreaterThanOrEqualTo(2,
            "the child is still watched — it is Queued, not dispatched, so it is looked at twice");
        await using IQuerySession query = fixture.Store.QuerySession();
        (await ObservationsOnAsync(query, fixture.ChildTaskId, cts.Token)).Should().Be(1,
            "the second look said nothing new, so nothing was appended");
    }

    [Theory]
    [InlineData("ClosedUnmerged")]
    [InlineData("Absent")]
    public async Task A_parent_that_is_closed_or_absent_holds_the_child(string state)
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token);

        FakeRemoteParentReader reader = new(new RemoteParentRead(
            state, string.Empty, string.Empty, string.Empty, string.Empty, null, $"observed {state}"));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.State.Should().Be(TaskState.Blocked, "there is no open pull request to cut a branch from");
        child.AwaitsRemoteStackedParent.Should().BeTrue();
    }

    /// <summary>
    /// Only the closed-unmerged case asks for a human — slice two's dead-parent rule, one pull
    /// request over. A number declared before the teammate opened the pull request is ordinary
    /// waiting and must not cry wolf.
    /// </summary>
    [Fact]
    public async Task A_parent_that_closed_unmerged_hands_the_undispatched_child_to_a_human()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token);

        FakeRemoteParentReader closed = new(new RemoteParentRead(
            RemoteParentState.ClosedUnmerged, string.Empty, string.Empty, string.Empty, string.Empty, null,
            "closed without merging"));
        await NewSweep(fixture, closed).SweepOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskListItem row = (await query.LoadAsync<TaskListItem>(fixture.ChildTaskId, cts.Token))!;
        row.BlockingHoldReason.Should().Contain("closed without merging",
            "this is what makes h9k status read the row as NeedsHuman");
        row.RemoteStackedParentHoldReason.Should().Contain($"#{fixture.ParentNumber}");
    }

    /// <summary>
    /// The same hold, one sweep later, in the window the queue actually keeps a child in: released
    /// to Queued by an open parent and still waiting for dispatch capacity when that parent closed.
    /// Nothing has cut a branch yet, so the release is taken back — otherwise the dispatcher's own
    /// Queued query hands this row out and the run lands on the project's base with none of the
    /// parent's work on it (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_parent_that_closes_after_the_child_was_queued_takes_the_release_back()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token);

        FakeRemoteParentReader open = new(Open(fixture, fixture.ParentHeadCommit));
        await NewSweep(fixture, open).SweepOnceAsync(cts.Token);

        await using (IQuerySession queued = fixture.Store.QuerySession())
        {
            (await queued.LoadAsync<TaskListItem>(fixture.ChildTaskId, cts.Token))!
                .State.Should().Be(TaskState.Queued);
        }

        FakeRemoteParentReader closed = new(new RemoteParentRead(
            RemoteParentState.ClosedUnmerged, string.Empty, string.Empty, string.Empty, string.Empty, null,
            "closed without merging"));
        await NewSweep(fixture, closed).SweepOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.State.Should().Be(TaskState.Blocked, "there is no longer a pull request to cut a branch from");

        TaskListItem row = (await query.LoadAsync<TaskListItem>(fixture.ChildTaskId, cts.Token))!;
        row.State.Should().Be(TaskState.Blocked, "and this is the row the dispatcher asks for Queued work");
        row.BlockingHoldReason.Should().Contain("closed without merging");
    }

    /// <summary>
    /// A look that FAILED is not an observation. Recording one would put a guess in an audit field,
    /// and on an Open it would dispatch a child onto a branch nobody confirmed exists.
    /// </summary>
    [Fact]
    public async Task A_failed_look_records_nothing_and_leaves_the_hold_standing()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token);

        FakeRemoteParentReader unreadable = new(RemoteParentRead.Unobserved("gh exited 1: network unreachable"));
        RemoteParentSweepResult sweep = await NewSweep(fixture, unreadable).SweepOnceAsync(cts.Token);

        // Counted with the failures the next sweep retries rather than as a look: nothing was read,
        // and a summary reporting a clean look for an expired credential tells an operator the
        // opposite of what happened (independent pre-PR review, cycle 1, conformance lens). Asserted
        // on the totals, unusually for this class, because this reader answers Unobserved for EVERY
        // child in the shared database — so both numbers hold whichever other tests have run.
        sweep.ChildrenLooked.Should().Be(0);
        sweep.Failures.Should().BeGreaterThan(0);

        await using IQuerySession query = fixture.Store.QuerySession();
        (await ObservationsOnAsync(query, fixture.ChildTaskId, cts.Token)).Should().Be(0);
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.State.Should().Be(TaskState.Blocked);
        child.RemoteStackedParentState.Should().Be(RemoteParentState.Unknown);
        child.RemoteStackedParentObservedAt.Should().BeNull("nothing was observed, so nothing is claimed");
    }

    // ---------------------------------------------------------------------------------------
    // After the child is delivered: the same retarget and replay a local parent's closeout runs.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The retarget-and-replay lap, driven by a merge observed on GitHub rather than by a local
    /// closeout event. The parent's branch is deliberately left on origin here, which is the
    /// ordinary race — a teammate's own closeout deletes it moments later.
    /// </summary>
    [Fact]
    public async Task A_parent_merge_observed_on_github_retargets_the_child_and_dispatches_a_replay()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token, delivered: true);
        MergeTheParentOnOrigin(fixture);

        FakeRemoteParentReader reader = new(new RemoteParentRead(
            RemoteParentState.Merged, fixture.ParentBranch, fixture.ParentHeadCommit, "main",
            $"https://github.com/x/y/pull/{fixture.ParentNumber}", null, "merged"));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        FakeStackedChildInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().Equal(["main"], "the child's pull request moves onto the project's base");

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.LastStackedRetargetSucceeded.Should().BeTrue();
        run.StackedOnBranch.Should().BeNull("a successful retarget clears the recorded base back to the project's");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.FollowUpKind.Should().Be(FollowUpKind.StackReplay);
        child.StackReplayUpstreamCommit.Should().Be(fixture.ParentHeadCommit,
            "the boundary is the parent's own head — the commit the replay drops the parent's work at");
        child.StackReplaysDispatched.Should().Be(1);
    }

    /// <summary>
    /// A parent that merged into somebody else's branch rather than the project's own — it was
    /// itself stacked. Retargeting onto main from there would drop work that branch does not have,
    /// so the child parks with the situation named rather than being moved mechanically.
    /// </summary>
    [Fact]
    public async Task A_parent_that_merged_somewhere_other_than_the_projects_base_parks_the_child()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token, delivered: true);

        FakeRemoteParentReader reader = new(new RemoteParentRead(
            RemoteParentState.Merged, fixture.ParentBranch, fixture.ParentHeadCommit, "feature/grandparent",
            $"https://github.com/x/y/pull/{fixture.ParentNumber}", null, "merged into feature/grandparent"));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        FakeStackedChildInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty("nothing is moved mechanically onto a base missing the grandparent");
        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.ParkedReason.Should().Contain("feature/grandparent");
    }

    [Fact]
    public async Task A_parent_that_closes_unmerged_parks_the_delivered_child()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token, delivered: true);

        FakeRemoteParentReader reader = new(new RemoteParentRead(
            RemoteParentState.ClosedUnmerged, fixture.ParentBranch, fixture.ParentHeadCommit, "main",
            $"https://github.com/x/y/pull/{fixture.ParentNumber}", null, "closed without merging"));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        FakeStackedChildInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty();
        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked, "matching slice two's dead-parent rule");
        run.ParkedReason.Should().Contain($"#{fixture.ParentNumber}");
        run.ParkedReason.Should().Contain("closed without merging");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.StackReplaysDispatched.Should().Be(0, "a dead parent is not replayed onto");
    }

    /// <summary>
    /// A force-push seen on GitHub while the child is Delivered. The pull request never changed
    /// state — only its head moved — so the replay is triggered by the branch, not by the state,
    /// and lands on the parent's new head rather than on the project's base.
    /// </summary>
    [Fact]
    public async Task A_parent_force_push_seen_on_github_dispatches_a_replay_onto_the_new_head()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token, delivered: true);
        string newHead = ForcePushTheParent(fixture);

        FakeRemoteParentReader reader = new(Open(fixture, newHead));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        FakeStackedChildInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty("the base stays the parent's branch; only the replay is owed");

        await using IQuerySession query = fixture.Store.QuerySession();
        (await ObservationsOnAsync(query, fixture.ChildTaskId, cts.Token)).Should().Be(2,
            "the head commit moved with the state unchanged, and that move is the whole signal — the "
            + "seed's own Open observation is the first");
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.FollowUpKind.Should().Be(FollowUpKind.StackReplay);
        child.StackReplayOntoCommit.Should().Be(newHead);
        child.StackReplayUpstreamCommit.Should().Be(fixture.ParentHeadCommit,
            "the boundary is this run's own recorded fork point — the parent's head is no longer on this line");
        child.StackReplaysDispatched.Should().Be(1);

        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.Superseded);
    }

    /// <summary>
    /// The same rebase budget slice one bounds a local parent's churn with, and the same park past
    /// the cap: a parent that keeps moving faster than the child can follow is a human's problem,
    /// not an unbounded dispatch loop's.
    /// </summary>
    [Fact]
    public async Task A_force_push_past_the_rebase_budget_parks_the_child()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token, delivered: true, priorStackReplays: 2);
        string newHead = ForcePushTheParent(fixture);

        FakeRemoteParentReader reader = new(Open(fixture, newHead));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        FakeStackedChildInspector inspector = new();
        await NewEngine(fixture, inspector, maxStackReplayRuns: 2).PollOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.ParkedReason.Should().Contain("rebase budget is spent");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.StackReplaysDispatched.Should().Be(2, "the park spends nothing further");
    }

    /// <summary>
    /// A child whose own run has closed out is past the point where an observation changes
    /// anything, so it costs no provider call at all. The sweep is a poll on a cadence; leaving
    /// finished stacks in it would spend a call per child forever.
    /// </summary>
    [Fact]
    public async Task A_child_whose_run_has_completed_is_not_looked_at_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        RemoteFixture fixture = await SeedAsync(cts.Token, delivered: true);
        await using (IDocumentSession session = fixture.Store.LightweightSession())
        {
            session.Events.Append(fixture.ChildRunId, new PullRequestMerged(fixture.ChildRunId, Now, Now));
            session.Events.Append(fixture.ChildRunId, new RunCompleted(fixture.ChildRunId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeRemoteParentReader reader = new(Open(fixture, fixture.ParentHeadCommit));
        await NewSweep(fixture, reader).SweepOnceAsync(cts.Token);

        reader.Reads.Should().NotContain(fixture.ParentNumber,
            "nothing further arrives on a stack whose child has already merged");
    }

    // ---------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------

    private static RemoteParentRead Open(RemoteFixture fixture, string headCommit) => new(
        RemoteParentState.Open, fixture.ParentBranch, headCommit, "main",
        $"https://github.com/x/y/pull/{fixture.ParentNumber}", null, "open");

    /// <summary>
    /// How many observations this child's own stream carries — the per-child answer the sweep's own
    /// totals cannot give, since every test's child is in every test's sweep (see
    /// <see cref="_parentNumbers"/>).
    /// </summary>
    private static async Task<int> ObservationsOnAsync(
        IQuerySession query, Guid taskId, CancellationToken cancellationToken) =>
        (await query.Events.FetchStreamAsync(taskId, token: cancellationToken))
            .Count(e => e.Data is RemoteStackedParentObserved);

    private sealed record RemoteFixture(
        DocumentStore Store,
        NodeContext Node,
        GitWorktreeManager Worktrees,
        string RepoPath,
        Guid ProjectId,
        int ParentNumber,
        string ParentBranch,
        string ParentWorktreePath,
        string ParentHeadCommit,
        string BaseCommit,
        Guid ChildTaskId,
        Guid ChildRunId,
        string ChildBranch);

    private RemoteStackedParentSweep NewSweep(RemoteFixture fixture, IRemoteParentReader reader) =>
        new(fixture.Store, fixture.Node, reader, NullLogger<RemoteStackedParentSweep>.Instance);

    private CloseoutEngine NewEngine(
        RemoteFixture fixture, IPullRequestInspector inspector, int maxStackReplayRuns = 3) =>
        new(fixture.Store, fixture.Node, new DaemonConnection(postgres.ConnectionString), inspector,
            fixture.Worktrees,
            new StackedParentWatch(fixture.Worktrees, NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner,
            FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions { MaxStackReplayRuns = maxStackReplayRuns }),
            NullLogger<CloseoutEngine>.Instance);

    /// <summary>
    /// The teammate's merge as a rebase merge lands it: their commit is replayed onto main under a
    /// NEW sha, which is exactly why the child's replay has to drop its own copy of the parent's
    /// work rather than rebase plainly onto the base.
    /// </summary>
    private static void MergeTheParentOnOrigin(RemoteFixture fixture)
    {
        Git(fixture.RepoPath, "checkout -q main");
        Git(fixture.RepoPath, $"-c user.name=Test -c user.email=t@t cherry-pick {fixture.ParentHeadCommit}");
        Git(fixture.RepoPath, "push -q origin main");
    }

    private static string ForcePushTheParent(RemoteFixture fixture)
    {
        string parentWorktree = fixture.ParentWorktreePath;
        Git(parentWorktree, $"reset --hard {fixture.BaseCommit}");
        File.WriteAllText(Path.Combine(parentWorktree, "PARENT.md"), "teammate slice, review fixes folded in\n");
        Git(parentWorktree, "add -A");
        Git(parentWorktree, "-c user.name=Test -c user.email=t@t commit -qm \"teammate slice\"");
        Git(parentWorktree, $"push -q --force origin {fixture.ParentBranch}");
        return Git(parentWorktree, "rev-parse HEAD").Trim();
    }

    /// <summary>
    /// A repository holding a teammate's branch — which is all a reviewer's node ever sees of a
    /// remote parent: there is no parent TASK here, no parent run, and nothing in the store that
    /// knows the branch belongs to a pull request. The child is declared stacked on the number
    /// alone, exactly as a human would declare it.
    /// </summary>
    private async Task<RemoteFixture> SeedAsync(
        CancellationToken cancellationToken, bool delivered = false, int priorStackReplays = 0)
    {
        DocumentStore store = DocumentStore.For(opts =>
        {
            opts.Connection(postgres.ConnectionString);
            opts.ConfigureHall9k(AutoCreate.All);
        });
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);

        Directory.CreateDirectory(_root);
        string originPath = Path.Combine(_root, $"origin-{Guid.NewGuid():N}.git");
        string repoPath = Path.Combine(_root, $"repo-{Guid.NewGuid():N}");
        Git(_root, $"init --bare -b main \"{originPath}\"");
        Git(_root, $"clone \"{originPath}\" \"{repoPath}\"");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# remote stacked test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");
        string baseCommit = Git(repoPath, "rev-parse HEAD").Trim();

        Guid projectId = DomainId.New();
        Guid ownerId = node.OwnerId;
        Guid childTaskId = DomainId.New();
        int parentNumber = Interlocked.Increment(ref _parentNumbers);

        // The teammate's branch, pushed by their own install. Cut through the worktree manager only
        // because that is the shortest way to a real branch with a worktree to force-push from.
        Worktree parent = await worktrees.CreateAsync(
            new WorktreeRequest(
                repoPath, "main", DomainId.New(), DomainId.New(), "Teammate slice",
                BranchNameTemplate.Default, ExternalReference: null),
            cancellationToken);
        File.WriteAllText(Path.Combine(parent.Path, "PARENT.md"), "teammate slice\n");
        Git(parent.Path, "add -A");
        Git(parent.Path, "-c user.name=Test -c user.email=t@t commit -qm \"teammate slice\"");
        Git(parent.Path, $"push -q origin {parent.Branch}");
        string parentHeadCommit = Git(parent.Path, "rev-parse HEAD").Trim();

        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"remote-stacked-{childTaskId:N}", repoPath, null, "main", Now);

        await using IDocumentSession session = store.LightweightSession();
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        // The child, declared stacked on the pull request number and nothing else — no blocked-by,
        // because there is no local task to name.
        (TaskAggregate child, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                childTaskId, projectId, "Playwright coverage for the teammate's slice", ["it works"],
                TaskType.Feature, null, null, null, Now, ownerId,
                stackedOnPullRequestNumber: parentNumber),
            ownerId, Now);
        List<object> childEvents = [.. lifecycle];

        string childBranch = string.Empty;
        Guid childRunId = Guid.Empty;
        if (delivered)
        {
            // The observation that released it, then the branch it cut, then delivery. Recorded in
            // that order because that is the order it happens in: nothing dispatches before the
            // sweep has seen the pull request open.
            RemoteStackedParentObserved observed = new(
                childTaskId, parentNumber, RemoteParentState.Open, parent.Branch, parentHeadCommit, "main",
                $"https://github.com/x/y/pull/{parentNumber}", null, "open", Now);
            child.Apply(observed);
            childEvents.Add(observed);

            Worktree childWorktree = await worktrees.CreateAsync(
                new WorktreeRequest(
                    repoPath, parent.Branch, childTaskId, DomainId.New(), "Child slice",
                    BranchNameTemplate.Default, ExternalReference: null),
                cancellationToken);
            File.WriteAllText(Path.Combine(childWorktree.Path, "CHILD.md"), "reviewer's tests\n");
            Git(childWorktree.Path, "add -A");
            Git(childWorktree.Path, "-c user.name=Test -c user.email=t@t commit -qm \"reviewer's tests\"");
            Git(childWorktree.Path, $"push -q origin {childWorktree.Branch}");
            childBranch = childWorktree.Branch;

            TaskClaimed claimed = TaskDecider.Claim(child, node.NodeId, ownerId, DomainId.New(), Now);
            child.Apply(claimed);
            childEvents.Add(claimed);
            TaskCompleted completed = TaskDecider.Complete(child, child.CurrentRunId!.Value, ChildPullRequest, Now);
            child.Apply(completed);
            childEvents.Add(completed);

            List<Guid> superseded = [];
            for (int i = 0; i < priorStackReplays; i++)
            {
                superseded.Add(child.CurrentRunId!.Value);
                TaskReopened reopened = TaskDecider.Reopen(
                    child, child.CurrentRunId!.Value, childBranch, "the parent moved", FollowUpKind.StackReplay,
                    automatic: true, Now, ownerId,
                    stackReplayUpstreamCommit: $"deadbee{i}", stackReplayOntoCommit: $"cafe111{i}");
                child.Apply(reopened);
                childEvents.Add(reopened);
                TaskClaimed reclaimed = TaskDecider.Claim(child, node.NodeId, ownerId, DomainId.New(), Now);
                child.Apply(reclaimed);
                childEvents.Add(reclaimed);
                TaskCompleted recompleted = TaskDecider.Complete(
                    child, child.CurrentRunId!.Value, ChildPullRequest, Now);
                child.Apply(recompleted);
                childEvents.Add(recompleted);
            }

            childRunId = child.CurrentRunId!.Value;
            foreach (Guid retired in superseded)
            {
                session.Events.StartStream<RunAggregate>(retired,
                    new RunDispatched(retired, childTaskId, node.NodeId, ownerId, 1, DomainId.New(),
                        Path.GetTempPath(), childBranch, ExecutorMode.Subscription, Now.AddMinutes(-10),
                        BaseBranch: parent.Branch, BaseCommit: parentHeadCommit),
                    new AgentSessionCompleted(retired, Now.AddMinutes(-10)),
                    new VerificationPassed(retired, Now.AddMinutes(-10)),
                    new PullRequestOpened(retired, ChildPullRequest, ChildNumber, Now.AddMinutes(-10)),
                    new RunSuperseded(retired, 2, Now.AddMinutes(-10)));
            }

            // The child's own recorded fork point IS the parent's head at the cut — what
            // GitWorktreeManager.CreateAsync observes and RunDispatched freezes, and what a replay
            // needs as its boundary once the parent's head is no longer on this branch's line.
            session.Events.StartStream<RunAggregate>(childRunId,
                new RunDispatched(childRunId, childTaskId, node.NodeId, ownerId, child.LeaseGeneration,
                    DomainId.New(), Path.GetTempPath(), childBranch, ExecutorMode.Subscription, Now,
                    BaseBranch: parent.Branch, BaseCommit: parentHeadCommit),
                new AgentSessionCompleted(childRunId, Now),
                new VerificationPassed(childRunId, Now),
                new PullRequestOpened(childRunId, ChildPullRequest, ChildNumber, Now));
        }

        session.Events.StartStream<TaskAggregate>(childTaskId, [.. childEvents]);
        await session.SaveChangesAsync(cancellationToken);

        return new RemoteFixture(
            store, node, worktrees, repoPath, projectId, parentNumber, parent.Branch, parent.Path,
            parentHeadCommit, baseCommit, childTaskId, childRunId, childBranch);
    }

    private sealed class FakeRemoteParentReader(RemoteParentRead read) : IRemoteParentReader
    {
        public List<int> Reads { get; } = [];

        public Task<RemoteParentRead> ReadAsync(
            string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken)
        {
            Reads.Add(pullRequestNumber);
            return Task.FromResult(read);
        }
    }

    /// <summary>
    /// Answers for the CHILD's own pull request, which is the only one this seam is ever asked
    /// about: the parent is read through <see cref="IRemoteParentReader"/> instead, which is the
    /// whole point of the two being separate seams.
    /// </summary>
    private sealed class FakeStackedChildInspector : IPullRequestInspector
    {
        public List<string> Retargets { get; } = [];

        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestSnapshot(
                IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
                FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
                UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
                CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0));

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestStateSnapshot(false, false, null, null));

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("a stacked child is never at the merge bar"));

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken)
        {
            Retargets.Add(baseBranch);
            return Task.CompletedTask;
        }
    }

    private static string Git(string workingDirectory, string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"git {arguments} failed in {workingDirectory}: {error}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows can hold a git pack file open past the process's own exit, and git marks
            // objects read-only, which surfaces as either of these. A leftover temp directory is
            // not worth failing a green run over — and swallowing it here must never swallow a real
            // assertion failure, which is why only these two are caught.
        }
    }
}
