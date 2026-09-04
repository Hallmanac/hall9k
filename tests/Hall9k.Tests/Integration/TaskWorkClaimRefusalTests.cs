using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

/// <summary>
/// h9k task work's entry-state refusals (task 688a1ccf-h9k), pinned against a real store because
/// <see cref="TaskWorkCommand.ClaimAndCutAsync"/> reads the dependency snapshot for a Published
/// task straight off Marten. Most cases here throw before the method ever loads
/// <c>TaskDetails</c>/<c>ProjectDetails</c> or touches the filesystem, so no project or worktree
/// setup is needed — only the task's own stream; the two exceptions
/// (<see cref="A_run_already_past_dispatched_or_running_refuses_reentry"/>, which needs a
/// <c>RunDetails</c> document, and <see cref="A_queued_task_assigned_to_the_operator_appends_exactly_one_event"/>,
/// which needs a real repository for <see cref="TaskWorkCommand.ClaimAndCutAsync"/>'s own worktree
/// cut) say so on themselves.
/// </summary>
// A_queued_task_assigned_to_the_operator_appends_exactly_one_event drives ClaimAndCutAsync all the
// way to its success path, which rings the doorbell (Hall9k.Cli.Infrastructure.Doorbell). That
// resolves its connection off the ambient HALL9K_CONNECTION_STRING rather than this fixture, so it
// is pointed at the fixture for the duration of that one call. That is process-wide state, same as
// DatabaseDoctorTests, so this joins the same collection to serialize against every other test that
// redirects it (independent pre-PR review, cycle 3).
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TaskWorkClaimRefusalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly List<string> _repositoryRoots = [];

    [Fact]
    public async Task A_draft_task_is_refused_and_told_to_publish_first()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Still being written", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*Published or Queued*")
            .Where(exception => exception.Message.Contains("Publish it first"));
    }

    /// <summary>
    /// The conversion this task (0ac72cb8-h9k) makes to an already-Blocked entry: no longer a
    /// hard <see cref="DomainConflictException"/> refusal — a <see cref="DomainBusinessRuleException"/>
    /// that names the open blocker and points at <c>--acknowledge-unmet-dependencies</c>, the same
    /// shape the atomic Published entry's own refusal already had. The blocker is seeded as its
    /// own stream (mirrors <see cref="A_published_task_with_an_open_dependency_is_refused_through_ClaimAndCutAsync"/>),
    /// because the Blocked entry now reads the same real <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskDependencyQuery"/>.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_is_refused_and_named_the_open_dependency()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage("*is Blocked*")
            .Where(exception => exception.Message.Contains("The blocker, still open")
                && exception.Message.Contains("--acknowledge-unmet-dependencies")
                // Already assigned, so pointing at h9k task assign — which refuses anything but a
                // Published task — would be advice this task cannot follow.
                && !exception.Message.Contains("h9k task assign"));
    }

    /// <summary>
    /// The platform advises rather than refuses (the idea's own ruling, fcaded0b): with the
    /// acknowledgment, the identical Blocked task above claims instead, the override recorded
    /// fresh (not carried forward — there is nothing to carry from yet).
    /// </summary>
    [Fact]
    public async Task A_blocked_task_with_acknowledgment_is_claimed_anyway()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
            await seed.SaveChangesAsync(cts.Token);
        }

        await WorkAsync(store, taskId, ownerId, cts.Token, acknowledgeUnmetDependencies: true);

        await using IQuerySession verify = store.QuerySession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue();

        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        TaskClaimed claimed = Assert.IsType<TaskClaimed>(stream[^1].Data);
        claimed.DependencyOverrideAcknowledged.Should().BeTrue();
        claimed.DependencyOverrideCarriedForward.Should().BeFalse("this is the first acknowledgment, not a carried-forward one");
    }

    /// <summary>
    /// The carry-forward this task adds (design ruling R7): once an earlier claim already
    /// acknowledged this exact blocker and gave the claim back (h9k task handback, landing Blocked
    /// again since the blocker is still on record unmet — <see cref="TaskAggregate.Apply(TaskHandedBack)"/>),
    /// a later reclaim of the same still-open blocker needs no flag and is recorded as relying on
    /// the earlier acknowledgment.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_already_acknowledged_by_a_handed_back_claim_does_not_need_the_flag_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, Now, ownerId);
            task.Apply(assigned);
            Guid firstRunId = DomainId.New();
            TaskClaimed firstClaim = TaskDecider.ClaimInteractively(
                task, ownerId, firstRunId, Now, dependencyOverrideAcknowledged: true);
            task.Apply(firstClaim);
            TaskHandedBack handedBack = TaskDecider.HandBack(
                task, firstRunId, "task/earlier-branch", "handing off", Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, firstClaim, handedBack);
            await seed.SaveChangesAsync(cts.Token);
        }

        // No acknowledgeUnmetDependencies flag this time — the earlier claim's own acknowledgment
        // is what covers the identical still-open blocker.
        await WorkAsync(store, taskId, ownerId, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        TaskClaimed secondClaim = Assert.IsType<TaskClaimed>(stream[^1].Data);
        secondClaim.DependencyOverrideAcknowledged.Should().BeTrue();
        secondClaim.DependencyOverrideCarriedForward.Should().BeTrue(
            "this claim relied on the earlier claim's own acknowledgment rather than asking again");
    }

    [Fact]
    public async Task A_task_claimed_by_a_node_is_refused_as_headless_work_already_running()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Already running headless", ["it is done"],
                    TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now);
            TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("claimed by a node running headless work already"));
    }

    [Fact]
    public async Task A_queued_task_assigned_to_a_different_owner_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid theirOwnerId = DomainId.New();
        Guid myOwnerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(taskId, DomainId.New(), "Someone else's queued work", ["it is done"],
                    TaskType.Chore, null, null, null, Now, theirOwnerId),
                theirOwnerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, myOwnerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage($"*assigned to {theirOwnerId}*")
            .Where(exception => exception.Message.Contains("an operator claims only their own owner's work"));
    }

    /// <summary>
    /// Mirrors <c>TaskHandbackCommand</c>'s own guard and <see cref="TaskWorkCommand.ExecuteAsync"/>'s
    /// pre-launch re-check: once <c>h9k task deliver</c> (or an earlier <c>h9k task handback</c>)
    /// hands a run to the standard pipeline, the task can still read Claimed+interactive for the
    /// whole review loop, so <see cref="TaskWorkCommand.ReenterAsync"/> is what actually stops a
    /// second session from rewriting a worktree the pipeline's own gates and review sessions now
    /// own. No worktree or task-stream setup is needed: <c>ReenterAsync</c> throws on the run's
    /// own <c>State</c> before it ever touches the filesystem or the task's stream.
    /// </summary>
    [Fact]
    public async Task A_run_already_past_dispatched_or_running_refuses_reentry()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid ownerId = DomainId.New();

        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            taskId, DomainId.New(), "Already handed to the standard pipeline", ["it is done"],
            TaskType.Chore, null, null, null, Now, ownerId));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, ownerId));
        task.Apply(TaskDecider.Assign(task, ownerId, [], Now, ownerId));
        task.Apply(TaskDecider.ClaimInteractively(task, ownerId, runId, Now));

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new RunDetails { Id = runId, TaskId = taskId, State = RunState.AwaitingReview });
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        Func<Task> act = () => TaskWorkCommand.ReenterAsync(session, task, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*already AwaitingReview*")
            .Where(exception => exception.Message.Contains("h9k task deliver (or handback)")
                && exception.Message.Contains($"h9k task show {taskId}"));
    }

    /// <summary>
    /// The re-entry acceptance criterion PLAN.md §16 #124 names (task 864c7f30-h9k): a live run
    /// carrying a previously recorded <see cref="RunDetails.InteractiveClaudeSessionId"/> hands it
    /// back as <c>ReenterAsync</c>'s own <c>PreviousClaudeSessionId</c>, which is what the launch
    /// above attempts <c>--resume</c> on before ever minting a fresh session. Nothing else on this
    /// branch pins that one field of the returned tuple — <see cref="TaskWorkResumeArgumentsTests"/>
    /// pins only the argument policy once a session id is already in hand, never the wiring that
    /// hands it to that policy in the first place (conformance review, cycle 1).
    /// </summary>
    [Fact]
    public async Task A_live_run_carrying_a_recorded_session_hands_it_back_as_the_one_to_resume()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid recordedClaudeSessionId = DomainId.New();
        string worktreePath = CreateEmptyDirectory();

        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            taskId, DomainId.New(), "Already claimed, re-entered from another terminal", ["it is done"],
            TaskType.Chore, null, null, null, Now, ownerId));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, ownerId));
        task.Apply(TaskDecider.Assign(task, ownerId, [], Now, ownerId));
        task.Apply(TaskDecider.ClaimInteractively(task, ownerId, runId, Now));

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new RunDetails
            {
                Id = runId,
                TaskId = taskId,
                State = RunState.Running,
                WorktreePath = worktreePath,
                Branch = "task/already-claimed",
                RunDirectory = worktreePath,
                InteractiveClaudeSessionId = recordedClaudeSessionId,
            });
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        (Guid resultRunId, string resultWorktreePath, _, _, bool resumesPreviousWork, _, Guid? previousClaudeSessionId) =
            await TaskWorkCommand.ReenterAsync(session, task, force: false, cts.Token);

        resultRunId.Should().Be(runId);
        resultWorktreePath.Should().Be(worktreePath);
        resumesPreviousWork.Should().BeTrue();
        previousClaudeSessionId.Should().Be(recordedClaudeSessionId);
    }

    /// <summary>
    /// The Queued entry's own success path through <see cref="TaskWorkCommand.ClaimAndCutAsync"/>
    /// is unchanged by the Published entry this branch adds (the <c>else if</c> restructure keeps
    /// it reachable exactly as before), but nothing pinned that claim used to append exactly one
    /// event — with no <see cref="TaskAssigned"/> beside it, since a Queued task is already
    /// assigned. Needs a real repository because <c>ClaimAndCutAsync</c>'s own worktree cut runs a
    /// real <c>GitWorktreeManager</c>, not an injectable fake.
    /// </summary>
    [Fact]
    public async Task A_queued_task_assigned_to_the_operator_appends_exactly_one_event()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(taskId, projectId, "Prove the Queued entry appends one event",
                    ["it is done"], TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cts.Token))!;
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cts.Token))!;
            BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

            // ClaimAndCutAsync's success path ends in Doorbell.RingAsync, which resolves its
            // connection off HALL9K_CONNECTION_STRING rather than this fixture (see the class-level
            // comment above), so it has to be pointed at the fixture for the one call that reaches it.
            string? previousConnectionString =
                Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
            try
            {
                await TaskWorkCommand.ClaimAndCutAsync(
                    store, session, task, fence, context, DomainId.New(),
                    SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
                    acknowledgeUnmetDependencies: false, cts.Token);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
            }
        }

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        // TaskSeed.EventCount (Add, Publish, Assign) is the seeded dispatch history — the
        // assignment that made the task Queued in the first place; the claim below must add
        // exactly one more event and no second TaskAssigned beside it (the shape the atomic
        // Published entry adds instead, which this Queued entry must not).
        stream.Should().HaveCount(TaskSeed.EventCount + 1, "the Queued entry appends only the claim");
        stream[^1].Data.Should().BeOfType<TaskClaimed>();

        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue();
        final.AssignedOwnerId.Should().Be(ownerId);
    }

    /// <summary>
    /// The atomic Published entry's own headline behavior (task 688a1ccf-h9k), driven through
    /// <see cref="TaskWorkCommand.ClaimAndCutAsync"/> itself rather than the pure
    /// <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/> helper both the
    /// conformance and adversarial review passes point at
    /// (<see cref="TaskWorkClaimConcurrencyTests"/> already proves the helper's own math and the
    /// race arbitration; nothing before this pinned the production wiring around it — the
    /// dependency load at <c>ClaimAndCutAsync</c>'s own Published branch, the
    /// <c>fence.Version + 2</c> fencing, and the two-event <c>Append</c> — against a real
    /// Published task). Mirrors <see cref="A_queued_task_assigned_to_the_operator_appends_exactly_one_event"/>'s
    /// shape but seeds Published only, with no prior assignment, so the claim itself must both
    /// assign and claim.
    /// </summary>
    [Fact]
    public async Task A_published_task_is_assigned_and_claimed_atomically_in_two_events()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });
            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Prove the Published entry appends assign and claim atomically",
                ["it is done"], TaskType.Chore, null, null, null, Now, ownerId);
            TaskAggregate task = new();
            task.Apply(added);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cts.Token))!;
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cts.Token))!;
            task.State.Should().Be(TaskState.Published, "the entry under test is the Published one, not Queued");
            BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

            // See the class-level comment on the Queued-entry twin above: ClaimAndCutAsync's
            // success path rings the doorbell, which resolves off HALL9K_CONNECTION_STRING
            // rather than this fixture.
            string? previousConnectionString =
                Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
            try
            {
                await TaskWorkCommand.ClaimAndCutAsync(
                    store, session, task, fence, context, DomainId.New(),
                    SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
                    acknowledgeUnmetDependencies: false, cts.Token);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
            }
        }

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        // Add + Publish seeded the stream (2 events); the atomic Published entry must add exactly
        // two more — TaskAssigned then TaskClaimed, the shape the Queued entry's own twin above
        // asserts must NOT appear beside its own single TaskClaimed.
        stream.Should().HaveCount(4, "the Published entry appends the assignment and the claim together");
        stream[^2].Data.Should().BeOfType<TaskAssigned>();
        stream[^1].Data.Should().BeOfType<TaskClaimed>();

        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue();
        final.AssignedOwnerId.Should().Be(ownerId);
    }

    /// <summary>
    /// The dependency refusal the Published entry's own branch introduces
    /// (<see cref="TaskWorkCommand.ClaimAndCutAsync"/> loads the dependency snapshot for a
    /// Published task before either event is built), driven through <c>ClaimAndCutAsync</c>
    /// itself rather than <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/>
    /// directly, so this also pins the real <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskDependencyQuery"/>
    /// read the earlier refusal tests in this class never exercise (they hand-build the
    /// <see cref="TaskDependency"/> list themselves).
    /// </summary>
    [Fact]
    public async Task A_published_task_with_an_open_dependency_is_refused_through_ClaimAndCutAsync()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();

        Guid projectId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            // ClaimAndCutAsync loads TaskDetails/ProjectDetails unconditionally, before the
            // dependency check fires (it lives inside PrepareInteractiveClaimFromPublished,
            // called later) — so a project has to exist here even though this test never reaches
            // the worktree cut that would actually need its repository.
            seed.Store(new ProjectDetails { Id = projectId, RepositoryPath = "/dev/null", BaseBranch = "main" });

            // A real stream, not a hand-built TaskDependency: ClaimAndCutAsync's Published branch
            // reads TaskDependencyQuery.LoadAsync straight off Marten's own TaskListItem
            // projection, so the blocker has to actually exist there.
            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task before an interactive claim",
                ["it is done"], TaskType.Chore, null, null, null, Now, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);
            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Draft, IsClosedOut: false,
                    CurrentRunState: null, PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s)*")
            .Where(exception => exception.Message.Contains("The blocker, still open")
                && exception.Message.Contains("h9k task assign"));
    }

    private string CreateEmptyDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-work-claim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _repositoryRoots.Add(root);
        return root;
    }

    private string CreateRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-work-claim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _repositoryRoots.Add(root);

        string originPath = Path.Combine(root, "origin.git");
        string seedPath = Path.Combine(root, "seed");
        Git(root, $"init --bare -b main \"{originPath}\"");
        Git(root, $"clone \"{originPath}\" \"{seedPath}\"");
        File.WriteAllText(Path.Combine(seedPath, "README.md"), "# seed\n");
        Git(seedPath, "add -A");
        Git(seedPath, "-c user.name=Test -c user.email=test@test commit -m init");
        Git(seedPath, "push origin main");

        string repositoryPath = Path.Combine(root, "repo");
        Git(root, $"clone \"{originPath}\" \"{repositoryPath}\"");
        return repositoryPath;
    }

    private static void Git(string workingDirectory, string arguments)
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
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    public void Dispose()
    {
        // git marks object/pack files read-only; best-effort cleanup either way.
        foreach (string root in _repositoryRoots)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task WorkAsync(
        DocumentStore store, Guid taskId, Guid ownerId, CancellationToken cancellationToken,
        bool acknowledgeUnmetDependencies = false)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

        // ClaimAndCutAsync's success path (a task that ends up claimed rather than refused) rings
        // the doorbell, which resolves off HALL9K_CONNECTION_STRING rather than this fixture (see
        // the class-level comment above) — pointed at the fixture for the duration of this call so
        // the acknowledged-claim tests, which do reach that path, do not need their own copy of
        // this dance.
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            await TaskWorkCommand.ClaimAndCutAsync(
                store, session, task, fence, context, DomainId.New(),
                SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
                acknowledgeUnmetDependencies, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
