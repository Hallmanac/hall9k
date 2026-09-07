using System.Diagnostics;
using FluentAssertions;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
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
/// The closeout half of the stacked edge (task: a stacked pull-request edge exists as an explicit
/// opt-in dependency), against a real store and a real repository with the provider seam faked.
/// Three things are load-bearing and each is a one-directional hazard.
/// <list type="bullet">
/// <item>A child whose parent merged must be retargeted BEFORE anything replays it, or the pull
/// request is left aimed at a deleted branch with nothing coming to fix it.</item>
/// <item>The replay's upstream boundary must be the parent's own head, computed from git rather
/// than remembered: too low replays the parent's commits as duplicates, too high drops the child's
/// own work.</item>
/// <item>A child still stacked must never reach the merge bar, and must never spend the child's
/// review budget on its parent's activity.</item>
/// </list>
/// </summary>
[Trait("Category", "RequiresDocker")]
public sealed class StackedCloseoutTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private const string ChildPullRequest = "https://github.com/x/y/pull/8";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hall9k-stacked-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_child_whose_parent_still_has_its_branch_is_left_alone()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty("the parent has not merged");
        await using IQuerySession query = fixture.Store.QuerySession();
        TaskDetails child = (await query.LoadAsync<TaskDetails>(fixture.ChildTaskId, cts.Token))!;
        child.State.Should().Be(TaskState.Done, "no follow-up is owed while the parent's branch stands still");
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.AwaitingReview);
        run.StackedOnBranch.Should().Be(fixture.ParentBranch);
    }

    /// <summary>
    /// A stacked child is not at the merge bar, however green it reads: merging it would land its
    /// commits on its parent's branch, which is not a merge into anything the project ships from.
    /// </summary>
    [Fact]
    public async Task A_pre_approved_child_is_never_auto_merged_while_it_is_still_stacked()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token, childPreApproved: true);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.MergeAttempts.Should().Be(0,
            "the bar machinery treats an un-retargeted stacked pull request as not at the bar");
    }

    /// <summary>
    /// The retarget-and-replay lap. The parent's branch is deliberately left on origin here, which
    /// is the ordinary race — the parent's own closeout deletes it moments later, and the sibling
    /// test below covers that.
    /// </summary>
    [Fact]
    public async Task A_merged_parent_retargets_the_child_and_dispatches_a_mechanical_replay()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        await MergeTheParentAsync(fixture, cts.Token);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().Equal(["main"], "the child's pull request moves onto the project's base");

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.LastStackedRetargetSucceeded.Should().BeTrue();
        run.StackedOnBranch.Should().BeNull("a successful retarget clears the recorded base back to the project's");
        run.State.Should().Be(RunState.Superseded, "the reopen hands the pull request to the replay run");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.State.Should().Be(TaskState.Queued, "the replay flows through the standard dispatch pipeline");
        child.FollowUpKind.Should().Be(FollowUpKind.StackReplay);
        child.FollowUpBranch.Should().Be(fixture.ChildBranch);
        child.StackReplayUpstreamCommit.Should().Be(fixture.ParentHeadCommit,
            "the boundary is the parent's own head — the commit the replay drops the parent's work at");
        child.StackReplayOntoCommit.Should().NotBeNullOrEmpty(
            "the replay lands on the base branch's own observed tip, not on a ref that may have moved");
        child.StackReplaysDispatched.Should().Be(1);
        child.CloseoutAttempts.Should().Be(0,
            "a replay answers the parent moving, not an obstruction of the child's own");

        // The reopen's own audit field describes the obstruction it actually recorded. A kind with
        // no arm of its own in DescribeObstruction fell through to "the same 1 unresolved review
        // thread(s)" over an identity that is a commit — a guess written into an audit field
        // (conformance review, cycle 4).
        IReadOnlyList<IEvent> childEvents = await query.Events.FetchStreamAsync(fixture.ChildTaskId, token: cts.Token);
        TaskReopened reopen = childEvents.Select(recorded => recorded.Data).OfType<TaskReopened>().Last();
        reopen.ObstructionSummary.Should()
            .Contain("the parent branch having moved past what this branch was built on")
            .And.Contain(fixture.ParentHeadCommit,
                "the obstruction's mechanical identity IS the boundary commit, so its description names that "
                + "rather than a review-thread count it has nothing to do with");
    }

    /// <summary>
    /// The parent's own closeout deletes its branch everywhere, so by the time the child's sweep
    /// runs there may be no <c>origin/&lt;parent&gt;</c> left. The pull request's own head ref is
    /// what GitHub keeps forever, and it is what this fallback reads — without it the child would
    /// be permanently unobservable and never retarget at all.
    /// </summary>
    [Fact]
    public async Task A_merged_parent_whose_branch_is_already_gone_is_still_observed_through_its_pull_request_head()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        await MergeTheParentAsync(fixture, cts.Token);

        // The parent's closeout, in the order it really happens: the merge lands on the base, then
        // the branch goes. refs/pull/<n>/head is faked the way GitHub keeps it — a real remote
        // exposes it automatically, and a bare test origin has to be told.
        Git(fixture.OriginPath, $"update-ref refs/pull/{FakeStackedInspector.ParentNumber}/head {fixture.ParentHeadCommit}");
        Git(fixture.OriginPath, $"update-ref -d refs/heads/{fixture.ParentBranch}");
        Git(fixture.RepoPath, "fetch --prune -q origin");

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().Equal(["main"]);
        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.StackReplayUpstreamCommit.Should().Be(fixture.ParentHeadCommit,
            "the pull request's own head ref names the same boundary the deleted branch did");
    }

    /// <summary>
    /// The retarget comes first and nothing replays without it: replaying onto the project's base
    /// while the pull request is still aimed at a deleted branch would leave a pull request nobody
    /// can merge and no dispatch coming to fix it.
    /// </summary>
    [Fact]
    public async Task A_failed_retarget_dispatches_no_replay_and_records_why()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        await MergeTheParentAsync(fixture, cts.Token);

        FakeStackedInspector inspector = new() { RetargetFailureMessage = "gh: HTTP 403" };
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.LastStackedRetargetSucceeded.Should().BeFalse();
        run.LastStackedRetargetDetail.Should().Contain("HTTP 403");
        run.StackedOnBranch.Should().Be(fixture.ParentBranch, "the pull request is still aimed at the parent");
        run.State.Should().Be(RunState.AwaitingReview, "nothing superseded this run, so the next sweep retries");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.State.Should().Be(TaskState.Done);
        child.StackReplaysDispatched.Should().Be(0, "a failed retarget spends no budget either");
    }

    /// <summary>
    /// The parent force-push arm (the sixth acceptance criterion): the base stays where it is and
    /// only the replay is owed, onto the parent's new head — the same mechanical operation with a
    /// different <c>--onto</c>, which is why it shares one follow-up kind and one budget.
    /// </summary>
    [Fact]
    public async Task A_force_pushed_parent_dispatches_the_same_replay_without_retargeting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        string rewrittenParentHead = ForcePushTheParent(fixture);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty("the parent has not merged, so the child's base is unchanged");

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.FollowUpKind.Should().Be(FollowUpKind.StackReplay);
        child.StackReplaysDispatched.Should().Be(1);

        // The whole point of recording the fork point rather than computing it. A force-push
        // rewrites the parent's commit, so `git merge-base` between the child and the parent's NEW
        // head collapses to the BASE commit — and a replay from there re-applies the child's copy
        // of the parent's old commit against its new one, which conflicts on the parent's own
        // content (verified in a scratch repository while this was built). The boundary has to be
        // the parent head the child was actually built on, which only the record still knows.
        child.StackReplayUpstreamCommit.Should().Be(fixture.ParentHeadCommit,
            "the boundary is the parent head this branch was built on, from the run's own record");
        child.StackReplayUpstreamCommit.Should().NotBe(fixture.BaseCommit,
            "the merge base of the child and the rewritten parent IS the base commit — the wrong answer");
        child.StackReplayOntoCommit.Should().Be(rewrittenParentHead,
            "the replay lands on the parent's freshly observed new head, as a commit rather than a ref");
    }

    /// <summary>
    /// The recorded fork point is trusted only once git confirms the branch actually contains it
    /// (adversarial review, cycle 4). For a replay run that field is a dispatch-time PREDICTION:
    /// <c>RunLauncher</c> records the commit the replay was told to land on before the session has
    /// performed the rebase, so a replay that ends without landing — its own prompt sanctions
    /// <c>git rebase --abort</c> on a conflict it cannot honestly resolve, and the no-op push then
    /// succeeds — leaves a fork point the branch never reached. Replaying from it would tell the
    /// next mechanical session that the parent's own commits are this task's work, which is the
    /// exact duplication the boundary exists to prevent, in a session no reviewer reads.
    /// </summary>
    [Fact]
    public async Task A_recorded_fork_point_the_branch_never_landed_on_is_unobservable_rather_than_replayed()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        string rewrittenParentHead = ForcePushTheParent(fixture);

        // The shape a replay that never landed leaves behind: the run's recorded fork point names
        // the parent's new head, which the child's branch does not contain at all.
        RunDetails predicted = new()
        {
            Id = fixture.ChildRunId,
            TaskId = fixture.ChildTaskId,
            Branch = fixture.ChildBranch,
            BaseBranch = fixture.ParentBranch,
            BaseCommit = rewrittenParentHead,
        };

        await using IQuerySession query = fixture.Store.QuerySession();
        ProjectDetails project = (await query.LoadAsync<ProjectDetails>(fixture.ProjectId, cts.Token))!;
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;

        StackedParentObservation observation = await new StackedParentWatch(
                fixture.Worktrees, NullLogger<StackedParentWatch>.Instance)
            .ObserveAsync(query, project, child, predicted, cts.Token);

        observation.Verdict.Should().Be(StackedParentVerdict.Unobservable,
            "a boundary the branch never landed on is not an observation, and replaying from it would "
            + "carry the parent's own work as this task's");
        observation.Detail.Should().Contain("does not contain the fork point");
        observation.BoundaryCommit.Should().BeEmpty("nothing was observed, so nothing is claimed");
    }

    /// <summary>
    /// The sibling of the check above: a fork point the branch really does hold is still trusted,
    /// so the containment check refuses a bad record without refusing every record.
    /// </summary>
    [Fact]
    public async Task A_recorded_fork_point_the_branch_does_hold_is_still_the_replays_boundary()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        string rewrittenParentHead = ForcePushTheParent(fixture);

        await using IQuerySession query = fixture.Store.QuerySession();
        ProjectDetails project = (await query.LoadAsync<ProjectDetails>(fixture.ProjectId, cts.Token))!;
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;

        StackedParentObservation observation = await new StackedParentWatch(
                fixture.Worktrees, NullLogger<StackedParentWatch>.Instance)
            .ObserveAsync(query, project, child, run, cts.Token);

        observation.Verdict.Should().Be(StackedParentVerdict.ParentMoved);
        observation.BoundaryCommit.Should().Be(fixture.ParentHeadCommit);
        observation.OntoCommit.Should().Be(rewrittenParentHead);
    }

    /// <summary>
    /// The rebase budget's cap: a parent that keeps moving faster than the child can follow is not
    /// converging on its own, so the child parks for a human rather than replaying forever.
    /// </summary>
    [Fact]
    public async Task A_child_past_its_rebase_budget_parks_without_retargeting_or_replaying()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token, priorStackReplays: 2);
        await MergeTheParentAsync(fixture, cts.Token);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector, maxStackReplayRuns: 2).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty(
            "parking with the base untouched leaves a coherent stack for the human to finish by hand");

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.ParkedReason.Should().Contain("rebase budget is spent")
            .And.Contain("2/2")
            .And.Contain("h9k pr resolve");
    }

    /// <summary>
    /// A parent that merged somewhere other than the project's base — a mid-stack parent, itself
    /// stacked, merged while still aimed at ITS parent's branch. The child's correct base is one
    /// level up, and mechanically moving it onto the project's base would take it off a branch
    /// holding the grandparent's work and replay its commits without it, in a session no reviewer
    /// reads. This platform's own merge bar never merges an un-retargeted stacked pull request, so
    /// nothing automatic produced this state and nothing automatic answers it: the child parks with
    /// its stack intact (independent pre-PR review, 2026-09-07, adversarial lens; ordering across
    /// three or more levels is out of this slice by design, docs/scope.md).
    /// </summary>
    [Fact]
    public async Task A_parent_that_merged_into_its_own_parents_branch_parks_the_child_untouched()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token, parentBaseBranch: "task/grandparent-slice");
        await MergeTheParentAsync(fixture, cts.Token);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty(
            "there is no base this child can be moved onto mechanically without dropping work");

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.StackedOnBranch.Should().Be(fixture.ParentBranch,
            "nothing was moved, so the record still names the stack the human inherits");
        run.ParkedReason.Should().Contain("task/grandparent-slice")
            .And.Contain("h9k pr resolve");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.StackReplaysDispatched.Should().Be(0, "no replay was dispatched, so no budget was spent");
    }

    /// <summary>
    /// The boundary is what git can see NOW, not only what dispatch recorded. A parent that
    /// advanced past the child's cut point, and a child that has since been brought onto that new
    /// head, leaves the recorded fork point naming a commit that is no longer the highest parent
    /// commit on the child's line — and a replay from there re-applies the parent's later commit
    /// onto the base, which is the duplication the boundary exists to prevent (independent pre-PR
    /// review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_parent_that_advanced_before_merging_replays_from_its_own_head_not_the_stale_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);

        // The parent adds a second commit and pushes; the child is brought onto it, which is the
        // state that makes the recorded fork point stale rather than merely old.
        string advancedParentHead = AdvanceTheParent(fixture);
        RebaseChildOntoParent(fixture, advancedParentHead);

        // Both parent commits land on the base, as a rebase merge would put them there.
        Git(fixture.RepoPath, "checkout -q main");
        Git(fixture.RepoPath, $"-c user.name=Test -c user.email=t@t cherry-pick {fixture.ParentHeadCommit}");
        Git(fixture.RepoPath, $"-c user.name=Test -c user.email=t@t cherry-pick {advancedParentHead}");
        Git(fixture.RepoPath, "push -q origin main");
        await using (IDocumentSession session = fixture.Store.LightweightSession())
        {
            session.Events.Append(fixture.ParentRunId, new PullRequestMerged(fixture.ParentRunId, Now, Now));
            session.Events.Append(fixture.ParentRunId, new RunCompleted(fixture.ParentRunId, Now));
            await session.SaveChangesAsync(cts.Token);
        }

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.StackReplayUpstreamCommit.Should().Be(advancedParentHead,
            "the parent's head is still contained in the child's branch, so it is the boundary — observed "
            + "directly rather than taken from a record that predates the parent's second commit");
        child.StackReplayUpstreamCommit.Should().NotBe(fixture.ParentHeadCommit,
            "replaying from the stale record would carry the parent's second commit onto the base as well");
    }

    /// <summary>
    /// Nothing moves the pull request's base unless the replay is actually going to be dispatched.
    /// The lifetime automatic-closeout ceiling still applies to a stacked child (it is the runaway
    /// backstop only h9k pr resolve lifts), and a park landing AFTER the retarget would leave a
    /// human a pull request aimed at the project's base still carrying the parent's duplicated
    /// commits, with no dispatch coming (independent pre-PR review, cycle 1, adversarial lens).
    /// </summary>
    [Fact]
    public async Task A_child_past_its_lifetime_ceiling_parks_without_retargeting()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);
        await MergeTheParentAsync(fixture, cts.Token);

        FakeStackedInspector inspector = new();
        await NewEngine(fixture, inspector, maxAutomaticCloseoutRuns: 0).PollOnceAsync(cts.Token);

        inspector.Retargets.Should().BeEmpty(
            "the park verdict is asked before the provider write, so the base is never moved for a replay "
            + "that is not going to be dispatched");

        await using IQuerySession query = fixture.Store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(fixture.ChildRunId, cts.Token))!;
        run.State.Should().Be(RunState.CloseoutParked);
        run.ParkedReason.Should().Contain("lifetime automatic closeout budget spent");
        run.StackedOnBranch.Should().Be(fixture.ParentBranch, "the stack is left coherent for a human");

        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.StackReplaysDispatched.Should().Be(0, "nothing was dispatched, so nothing was spent");
    }

    /// <summary>
    /// A stacked child with an ordinary obstruction of its own is unaffected: the stacked check is a
    /// no-op unless the parent actually moved, so failing checks still take the ordinary lap.
    /// </summary>
    [Fact]
    public async Task A_stacked_child_with_failing_checks_still_takes_its_ordinary_lap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        StackedFixture fixture = await SeedAsync(cts.Token);

        FakeStackedInspector inspector = new()
        {
            Snapshot = FakeStackedInspector.Quiet() with { FailingChecks = ["build (windows-latest)"] },
        };
        await NewEngine(fixture, inspector).PollOnceAsync(cts.Token);

        await using IQuerySession query = fixture.Store.QuerySession();
        TaskAggregate child =
            (await query.Events.AggregateStreamAsync<TaskAggregate>(fixture.ChildTaskId, token: cts.Token))!;
        child.FollowUpKind.Should().Be(FollowUpKind.FailingChecks);
        child.CloseoutAttempts.Should().Be(1, "this obstruction IS the child's own");
        child.StackReplaysDispatched.Should().Be(0);
    }

    /// <summary>Everything one stacked pair needs: a real repository, both branches pushed, both tasks seeded.</summary>
    private sealed record StackedFixture(
        DocumentStore Store,
        NodeContext Node,
        GitWorktreeManager Worktrees,
        string OriginPath,
        string RepoPath,
        Guid ProjectId,
        Guid ParentTaskId,
        Guid ParentRunId,
        string ParentBranch,
        string ParentWorktreePath,
        string ParentHeadCommit,
        string BaseCommit,
        Guid ChildTaskId,
        Guid ChildRunId,
        string ChildBranch,
        string ChildWorktreePath);

    private CloseoutEngine NewEngine(
        StackedFixture fixture, FakeStackedInspector inspector, int maxStackReplayRuns = 12,
        int maxAutomaticCloseoutRuns = 6) =>
        new(fixture.Store, fixture.Node, new DaemonConnection(postgres.ConnectionString), inspector,
            fixture.Worktrees,
            new StackedParentWatch(fixture.Worktrees, NullLogger<StackedParentWatch>.Instance),
            RecordingProcessRunner.Succeeding(string.Empty).Runner,
            FakeJiraRequester.NeverInvoked(),
            Options.Create(new DaemonOptions
            {
                MaxStackReplayRuns = maxStackReplayRuns,
                // Zero in one test, which is how the ordering of the retarget against the park is
                // proved: the ceiling is a knob, and a spent one has to stop the sweep before the
                // provider write rather than after it.
                MaxAutomaticCloseoutRuns = maxAutomaticCloseoutRuns,
            }),
            NullLogger<CloseoutEngine>.Instance);

    /// <summary>
    /// The parent's merge, as a rebase merge lands it: its commit is replayed onto the base under a
    /// NEW sha, which is exactly why the child's replay has to drop the parent's own copies rather
    /// than rebase plainly onto the base.
    /// </summary>
    private async Task MergeTheParentAsync(StackedFixture fixture, CancellationToken cancellationToken)
    {
        Git(fixture.RepoPath, "checkout -q main");
        Git(fixture.RepoPath, $"-c user.name=Test -c user.email=t@t cherry-pick {fixture.ParentHeadCommit}");
        Git(fixture.RepoPath, "push -q origin main");

        await using IDocumentSession session = fixture.Store.LightweightSession();
        session.Events.Append(fixture.ParentRunId, new PullRequestMerged(fixture.ParentRunId, Now, Now));
        session.Events.Append(fixture.ParentRunId, new RunCompleted(fixture.ParentRunId, Now));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The parent adds a second commit on top of the one the child was cut from and pushes it —
    /// an ordinary follow-up landing new work, no rewrite, so the child's own copy of the parent's
    /// first commit is untouched. The counterpart to <see cref="ForcePushTheParent"/>, and the
    /// reason ParentMoved's own account never claims a force-push it did not observe: an appended
    /// commit reaches that verdict identically. Done in the parent run's own retained worktree,
    /// which is where the real thing happens too and, incidentally, the only place git will check
    /// that branch out (it is already checked out there).
    /// </summary>
    private static string AdvanceTheParent(StackedFixture fixture)
    {
        string parentWorktree = fixture.ParentWorktreePath;
        File.WriteAllText(Path.Combine(parentWorktree, "PARENT-MORE.md"), "parent slice, second commit\n");
        Git(parentWorktree, "add -A");
        Git(parentWorktree, "-c user.name=Test -c user.email=t@t commit -qm \"parent slice, continued\"");
        Git(parentWorktree, $"push -q origin {fixture.ParentBranch}");
        return Git(parentWorktree, "rev-parse HEAD").Trim();
    }

    /// <summary>
    /// The child brought onto its parent's new head — which is what makes its dispatch-time fork
    /// point stale: the branch now contains a parent commit later than the one recorded.
    /// </summary>
    private static void RebaseChildOntoParent(StackedFixture fixture, string parentHead)
    {
        string childWorktree = fixture.ChildWorktreePath;
        Git(childWorktree, "fetch -q origin");
        Git(childWorktree, $"rebase -q {parentHead}");
        Git(childWorktree, $"push -q --force origin {fixture.ChildBranch}");
    }

    private static string ForcePushTheParent(StackedFixture fixture)
    {
        string parentWorktree = fixture.ParentWorktreePath;
        Git(parentWorktree, $"reset --hard {fixture.BaseCommit}");
        File.WriteAllText(Path.Combine(parentWorktree, "PARENT.md"), "parent slice, with review fixes folded in\n");
        Git(parentWorktree, "add -A");
        Git(parentWorktree, "-c user.name=Test -c user.email=t@t commit -qm \"parent slice\"");
        Git(parentWorktree, $"push -q --force origin {fixture.ParentBranch}");
        return Git(parentWorktree, "rev-parse HEAD").Trim();
    }

    /// <summary>
    /// <paramref name="parentBaseBranch"/> is what the PARENT's own run records as its base, blank
    /// (the project's own) for every test but the mid-stack one: a parent that is itself stacked
    /// merges into its own parent's branch rather than into the project's base, and the child's
    /// retarget cannot be aimed at the project's base from there.
    /// </summary>
    private async Task<StackedFixture> SeedAsync(
        CancellationToken cancellationToken, bool childPreApproved = false, int priorStackReplays = 0,
        string parentBaseBranch = "")
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
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# stacked test\n");
        Git(repoPath, "add -A");
        Git(repoPath, "-c user.name=Test -c user.email=t@t commit -qm init");
        Git(repoPath, "push -q origin main");
        string baseCommit = Git(repoPath, "rev-parse HEAD").Trim();

        Guid projectId = DomainId.New();
        Guid ownerId = node.OwnerId;
        Guid parentTaskId = DomainId.New();
        Guid childTaskId = DomainId.New();

        // The parent branch, cut from main and pushed — Delivered.
        Worktree parent = await worktrees.CreateAsync(
            new WorktreeRequest(
                repoPath, "main", parentTaskId, DomainId.New(), "Parent slice",
                BranchNameTemplate.Default, ExternalReference: null),
            cancellationToken);
        File.WriteAllText(Path.Combine(parent.Path, "PARENT.md"), "parent slice\n");
        Git(parent.Path, "add -A");
        Git(parent.Path, "-c user.name=Test -c user.email=t@t commit -qm \"parent slice\"");
        Git(parent.Path, $"push -q origin {parent.Branch}");
        string parentHeadCommit = Git(parent.Path, "rev-parse HEAD").Trim();

        // The child branch, cut from the PARENT's branch rather than main — which is the whole
        // point: its own commit sits on top of the parent's.
        Worktree child = await worktrees.CreateAsync(
            new WorktreeRequest(
                repoPath, parent.Branch, childTaskId, DomainId.New(), "Child slice",
                BranchNameTemplate.Default, ExternalReference: null),
            cancellationToken);
        File.WriteAllText(Path.Combine(child.Path, "CHILD.md"), "child slice\n");
        Git(child.Path, "add -A");
        Git(child.Path, "-c user.name=Test -c user.email=t@t commit -qm \"child slice\"");
        Git(child.Path, $"push -q origin {child.Branch}");

        await using IDocumentSession session = store.LightweightSession();

        Guid parentRunId = SeedDeliveredTask(
            session, node, projectId, parentTaskId, "Parent slice", parent.Branch,
            "https://github.com/x/y/pull/7", FakeStackedInspector.ParentNumber, stackedOn: null,
            preApproved: false, baseBranch: parentBaseBranch, baseCommit: baseCommit, priorStackReplays: 0);

        // The child's own recorded fork point IS the parent's head at the cut — which is what
        // GitWorktreeManager.CreateAsync observes and RunDispatched freezes, and what the replay
        // needs as its boundary. Seeded rather than left blank precisely because a blank one is
        // (correctly) unobservable, and this fixture is about the observable case.
        Guid childRunId = SeedDeliveredTask(
            session, node, projectId, childTaskId, "Child slice", child.Branch,
            ChildPullRequest, FakeStackedInspector.ChildNumber, stackedOn: parentTaskId,
            preApproved: childPreApproved, baseBranch: parent.Branch, baseCommit: parentHeadCommit,
            priorStackReplays: priorStackReplays);

        var registered = Hall9k.Domain.Features.Project.Handlers.ProjectDecider.Register(
            projectId, ownerId, DomainId.New(), $"stacked-{childTaskId:N}", repoPath, null, "main", Now);
        session.Events.StartStream<ProjectAggregate>(registered.Id, registered);

        await session.SaveChangesAsync(cancellationToken);

        return new StackedFixture(
            store, node, worktrees, originPath, repoPath, projectId,
            parentTaskId, parentRunId, parent.Branch, parent.Path, parentHeadCommit, baseCommit,
            childTaskId, childRunId, child.Branch, child.Path);
    }

    /// <summary>
    /// One task at the top of the closeout phase: added, published, assigned, claimed, completed
    /// onto a pull request, with its run AwaitingReview and its recorded base branch set.
    /// <paramref name="priorStackReplays"/> seeds already-spent rebase budget as real reopen →
    /// claim → complete cycles, the same way the ordinary closeout seed spends the lifetime one.
    /// </summary>
    private static Guid SeedDeliveredTask(
        IDocumentSession session,
        NodeContext node,
        Guid projectId,
        Guid taskId,
        string objective,
        string branch,
        string pullRequestUrl,
        int pullRequestNumber,
        Guid? stackedOn,
        bool preApproved,
        string baseBranch,
        string baseCommit,
        int priorStackReplays)
    {
        Guid ownerId = node.OwnerId;
        // A stacked child's parent is also a blocked-by edge (the invariant the decider enforces),
        // and it is already Delivered here, so the assignment lands the child Queued.
        TaskDependencyGraph graph = stackedOn is { } parentId
            ? new([new TaskDependency(
                parentId, "Parent slice", TaskState.Done, IsClosedOut: false, RunState.AwaitingReview,
                "https://github.com/x/y/pull/7", TaskType.Feature, [])])
            : TaskDependencyGraph.Empty;

        (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(
                taskId, projectId, objective, ["it works"], TaskType.Feature, null, null, null, Now, ownerId,
                blockedBy: stackedOn is { } id ? [id] : [], stackedOnTaskId: stackedOn),
            ownerId, Now, graph);
        List<object> taskEvents = [.. lifecycle];

        if (preApproved)
        {
            TaskPreApprovedSet set = TaskDecider.SetPreApproved(task, true, Now, ownerId, taskClosedOut: false);
            task.Apply(set);
            taskEvents.Add(set);
        }

        TaskClaimed claimed = TaskDecider.Claim(task, node.NodeId, ownerId, DomainId.New(), Now);
        task.Apply(claimed);
        taskEvents.Add(claimed);
        TaskCompleted completed = TaskDecider.Complete(task, task.CurrentRunId!.Value, pullRequestUrl, Now);
        task.Apply(completed);
        taskEvents.Add(completed);

        List<Guid> superseded = [];
        for (int i = 0; i < priorStackReplays; i++)
        {
            superseded.Add(task.CurrentRunId!.Value);
            TaskReopened reopened = TaskDecider.Reopen(
                task, task.CurrentRunId!.Value, branch, "the parent moved", FollowUpKind.StackReplay,
                automatic: true, Now, ownerId,
                stackReplayUpstreamCommit: $"deadbee{i}", stackReplayOntoCommit: $"cafe111{i}");
            task.Apply(reopened);
            taskEvents.Add(reopened);
            TaskClaimed reclaimed = TaskDecider.Claim(task, node.NodeId, ownerId, DomainId.New(), Now);
            task.Apply(reclaimed);
            taskEvents.Add(reclaimed);
            TaskCompleted recompleted = TaskDecider.Complete(task, task.CurrentRunId!.Value, pullRequestUrl, Now);
            task.Apply(recompleted);
            taskEvents.Add(recompleted);
        }

        Guid runId = task.CurrentRunId!.Value;
        session.Events.StartStream<TaskAggregate>(taskId, [.. taskEvents]);

        for (int i = 0; i < superseded.Count; i++)
        {
            DateTimeOffset dispatchedAt = Now.AddMinutes(-10 * (superseded.Count - i));
            session.Events.StartStream<RunAggregate>(superseded[i],
                new RunDispatched(superseded[i], taskId, node.NodeId, ownerId, i + 1, DomainId.New(),
                    Path.GetTempPath(), branch, ExecutorMode.Subscription, dispatchedAt, BaseBranch: baseBranch, BaseCommit: baseCommit),
                new AgentSessionCompleted(superseded[i], dispatchedAt),
                new VerificationPassed(superseded[i], dispatchedAt),
                new PullRequestOpened(superseded[i], pullRequestUrl, pullRequestNumber, dispatchedAt),
                new RunSuperseded(superseded[i], i + 2, dispatchedAt));
        }

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, node.NodeId, ownerId, task.LeaseGeneration, DomainId.New(),
                Path.GetTempPath(), branch, ExecutorMode.Subscription, Now, BaseBranch: baseBranch, BaseCommit: baseCommit),
            new AgentSessionCompleted(runId, Now),
            new VerificationPassed(runId, Now),
            new PullRequestOpened(runId, pullRequestUrl, pullRequestNumber, Now));

        return runId;
    }

    /// <summary>
    /// Answers for the CHILD's pull request only — the parent's own run is never watched by these
    /// sweeps (its merge is seeded directly), so a call naming any other number is a bug in the
    /// test rather than something to answer quietly.
    /// </summary>
    private sealed class FakeStackedInspector : IPullRequestInspector
    {
        internal const int ParentNumber = 7;
        internal const int ChildNumber = 8;

        public PullRequestSnapshot Snapshot { get; set; } = Quiet();

        public List<string> Retargets { get; } = [];

        public string? RetargetFailureMessage { get; set; }

        public int MergeAttempts { get; private set; }

        public Task<PullRequestSnapshot> InspectAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber,
            CancellationToken cancellationToken) => Task.FromResult(Snapshot);

        public Task<PullRequestStateSnapshot> InspectStateAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PullRequestStateSnapshot(
                IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null));

        public Task RerequestReviewAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task MergeAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
            CancellationToken cancellationToken)
        {
            MergeAttempts++;
            return Task.CompletedTask;
        }

        public Task RetargetAsync(
            string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
            CancellationToken cancellationToken)
        {
            if (RetargetFailureMessage is { } message)
            {
                return Task.FromException(new InvalidOperationException(message));
            }

            Retargets.Add(baseBranch);
            return Task.CompletedTask;
        }

        public static PullRequestSnapshot Quiet() => new(
            IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
            FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
            UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
            CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0);
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
