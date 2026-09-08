using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// The entry-state refusals and success paths of the three commands that take a claim by hand —
/// <c>h9k task start</c> (task 8a56af78-h9k), <c>h9k task work</c> (task 688a1ccf-h9k), and
/// <c>h9k task delegate</c> (task 15f889e3-h9k, design ruling R6, idea fcaded0b's design rulings,
/// Take the Wheel epic 9272e514's slice 10) — sharing one container. All three read the dependency
/// snapshot for a Published task straight off Marten, which is why each is pinned against a real
/// store rather than a fake; <c>h9k task delegate</c> additionally reads the interactive claim's
/// own run and worktree off both the store and a real git repository. The three were separate
/// classes for ten to thirteen tests each, with a byte-identical <c>Git</c>,
/// <c>CreateRepository</c>, and teardown copied into all three; folding them leaves one of each.
/// Where <c>h9k task start</c> and <c>h9k task work</c> both cover the same refusal, the two tests
/// keep their scenarios and take a <c>_by_task_start</c> / <c>_by_task_work</c> suffix, since one
/// class cannot hold two methods of one name.
/// <para>
/// None of the three drives as far as the actual detached process spawn
/// (<see cref="HeadlessLaunch"/>): that needs a real <c>claude</c> binary and is out of reach here,
/// so each stops at <c>ClaimAndCutAsync</c> / <c>PrepareAsync</c> rather than at
/// <c>RunDeliberateStartAsync</c> or <c>LaunchInteractiveClaudeAsync</c>.
/// </para>
/// </summary>
// The success-path tests drive ClaimAndCutAsync all the way through, which rings the doorbell
// (Hall9k.Cli.Infrastructure.Doorbell). That resolves its connection off the ambient
// HALL9K_CONNECTION_STRING rather than this fixture, so it is pointed at the fixture for the
// duration of each such call. That is process-wide state, same as DatabaseDoctorTests, so this
// joins the Hall9kHome collection to serialize against every other test that redirects it
// (independent pre-PR review, cycle 3). One test also asks OperatingSettingsResolver to resolve
// the node's own configured model default, which reads PlatformPaths.Home's config.json — this
// class redirects process-wide HALL9K_HOME too, for the same reason DispatchCeilingTests and
// VerificationRunnerTests do, so that read is isolated from whatever is actually installed on the
// machine running the suite rather than asserting against it by accident (adversarial review,
// cycle 4: a developer's own ~/.hall9k/config.json setting modelByRole.build made the test pass
// for the wrong reason, and would fail outright on a machine configuring a different model there).
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class ClaimRefusalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly List<string> _repositoryRoots = [];
    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-claim-refusal-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task A_draft_task_is_refused_and_told_to_publish_first_by_task_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Still being written", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*Published, Queued, or Blocked*")
            .Where(exception => exception.Message.Contains("Publish it first"));
    }

    /// <summary>
    /// The conversion this task (0ac72cb8-h9k) makes to an already-Blocked entry: no longer a
    /// hard refusal on Blocked alone — a <see cref="DomainBusinessRuleException"/> that names the
    /// open blocker and points at <c>--acknowledge-unmet-dependencies</c>, the same shape the
    /// atomic Published entry's own refusal already had, and the same conversion h9k task work
    /// made for its own identical Blocked entry.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_is_refused_and_named_the_open_dependency_by_task_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails { Id = projectId, RepositoryPath = "/dev/null", BaseBranch = "main" });

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task", ["it is done"], TaskType.Chore,
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

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage("*is Blocked*")
            .Where(exception => exception.Message.Contains("The blocker, still open")
                && exception.Message.Contains("--acknowledge-unmet-dependencies"));
    }

    /// <summary>
    /// The platform advises rather than refuses (the idea's own ruling, fcaded0b): with the
    /// acknowledgment, the identical Blocked task above claims instead, the override recorded
    /// fresh (not carried forward — there is nothing to carry from yet).
    /// </summary>
    [Fact]
    public async Task A_blocked_task_with_acknowledgment_is_started_anyway()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
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

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task", ["it is done"], TaskType.Chore,
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

        await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: true, cts.Token);

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
    /// The carry-forward this task adds (design ruling R7), on h9k task start's own Blocked entry
    /// exactly as h9k task work's identical entry already gets it: once an earlier deliberate claim
    /// already acknowledged this exact blocker and gave the claim back (h9k task handback, landing
    /// Blocked again since the blocker is still on record unmet), a later start of the same
    /// still-open blocker needs no flag and is recorded as relying on the earlier acknowledgment.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_already_acknowledged_by_a_handed_back_deliberate_claim_does_not_need_the_flag_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
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

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task", ["it is done"], TaskType.Chore,
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
            TaskClaimed firstClaim = TaskDecider.ClaimDeliberately(
                task, ownerId, firstRunId, Now, dependencyOverrideAcknowledged: true);
            task.Apply(firstClaim);
            TaskHandedBack handedBack = TaskDecider.HandBack(
                task, firstRunId, "task/earlier-branch", "handing off", Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, firstClaim, handedBack);
            await seed.SaveChangesAsync(cts.Token);
        }

        // No acknowledgeUnmetDependencies flag this time — the earlier claim's own acknowledgment
        // is what covers the identical still-open blocker.
        await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        TaskClaimed secondClaim = Assert.IsType<TaskClaimed>(stream[^1].Data);
        secondClaim.DependencyOverrideAcknowledged.Should().BeTrue();
        secondClaim.DependencyOverrideCarriedForward.Should().BeTrue(
            "this claim relied on the earlier claim's own acknowledgment rather than asking again");
    }

    /// <summary>
    /// The one way ClaimDeliberately's Blocked-accepting branch is reachable outside the atomic
    /// Published entry (task 45136b29, idea fcaded0b's R7 ruling): a claim once
    /// warned-and-acknowledged, handed back, and picked back up — the exact shape
    /// h9k task handback --now produces. The still-open blocker's acknowledgment carries forward
    /// (<see cref="TaskAggregate.AcknowledgedUnmetDependencyIds"/>) without the flag, unlike the
    /// sibling test above where nothing was ever acknowledged.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_with_a_carried_forward_acknowledgment_claims_without_the_flag()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();
        Guid firstRunId = DomainId.New();

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
                taskId, projectId, "Waits on another task, acknowledged once already",
                ["it is done"], TaskType.Chore, null, null, null, Now, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), Now, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, Now, ownerId);
            task.Apply(assigned);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, firstRunId, Now, dependencyOverrideAcknowledged: true);
            task.Apply(claimed);
            TaskHandedBack handedBack = TaskDecider.HandBack(
                task, firstRunId, "task/x", "Stepping away", Now, ownerId);

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, claimed, handedBack);
            await seed.SaveChangesAsync(cts.Token);
        }

        Guid runId = await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream[^1].Data.Should().BeOfType<TaskClaimed>()
            .Which.DependencyOverrideAcknowledged.Should().BeTrue(
                "the carried-forward acknowledgment from the earlier deliberate claim, never re-asked");

        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);

        RunDetails run = (await verify.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.NodeId.Should().Be(Guid.Empty, "the same ceiling-exempt sentinel every deliberate claim carries");
    }

    [Fact]
    public async Task A_task_with_a_live_claim_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("already has a live claim"));
    }

    [Fact]
    public async Task A_queued_task_assigned_to_a_different_owner_is_refused_by_task_start()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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

        Func<Task> act = () => StartAsync(store, taskId, myOwnerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage($"*assigned to {theirOwnerId}*")
            .Where(exception => exception.Message.Contains("a deliberate kick-off only starts your own owner's work"));
    }

    /// <summary>
    /// The Queued entry appends only the claim — no second <see cref="TaskAssigned"/>, since a
    /// Queued task is already assigned — and lands the run's own <c>RunDetails.NodeId</c> at the
    /// <see cref="Guid.Empty"/> sentinel, the ceiling-exemption mechanism itself (Decisions Log
    /// #103, <c>NodeLoad.LiveSlots</c>): a real node's ceiling never counts it.
    /// </summary>
    [Fact]
    public async Task A_queued_task_assigned_to_the_operator_appends_exactly_one_event_and_is_ceiling_exempt()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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

        Guid runId = await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream.Should().HaveCount(TaskSeed.EventCount + 1, "the Queued entry appends only the claim");
        stream[^1].Data.Should().BeOfType<TaskClaimed>()
            .Which.DependencyOverrideAcknowledged.Should().BeFalse("an already-Queued task has nothing to override");

        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue("the same ceiling-exempt sentinel h9k task work's own claim uses");
        final.AssignedOwnerId.Should().Be(ownerId);

        RunDetails run = (await verify.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.NodeId.Should().Be(Guid.Empty, "NodeLoad's ceiling measurement never counts this sentinel, for any node");
        run.SessionName.Should().Be(SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build));
    }

    /// <summary>
    /// The atomic Published entry: two events append together (Assigned then Claimed), exactly
    /// as h9k task work's own atomic entry does, when there is nothing to warn about.
    /// </summary>
    [Fact]
    public async Task A_published_task_with_no_open_dependencies_is_assigned_and_claimed_atomically_in_two_events()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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

        await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream.Should().HaveCount(4, "the Published entry appends the assignment and the claim together");
        stream[^2].Data.Should().BeOfType<TaskAssigned>();
        stream[^1].Data.Should().BeOfType<TaskClaimed>()
            .Which.DependencyOverrideAcknowledged.Should().BeFalse();

        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed);
        final.IsInteractiveClaim.Should().BeTrue();
    }

    [Fact]
    public async Task A_published_task_with_an_open_dependency_and_no_acknowledgment_is_refused_through_ClaimAndCutAsync()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails { Id = projectId, RepositoryPath = "/dev/null", BaseBranch = "main" });

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task before a deliberate kick-off",
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

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainBusinessRuleException>()
            .WithMessage("*depends on 1 task(s)*")
            .Where(exception => exception.Message.Contains("The blocker, still open")
                && exception.Message.Contains("--acknowledge-unmet-dependencies"));

        // The refusal is up front: nothing was decided.
        await using IQuerySession verify = store.QuerySession();
        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Published);
    }

    /// <summary>
    /// The headline behavior (AC3): the platform advises rather than refuses. With the
    /// acknowledgment flag, the same open dependency assigns and claims anyway, landing Claimed
    /// with the override recorded on the committed <see cref="TaskClaimed"/>.
    /// </summary>
    [Fact]
    public async Task A_published_task_with_an_open_dependency_and_acknowledgment_is_claimed_anyway()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
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

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "The human overrides an open dependency deliberately",
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

        await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: true, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        IReadOnlyList<IEvent> stream = await verify.Events.FetchStreamAsync(taskId, token: cts.Token);
        stream[^2].Data.Should().BeOfType<TaskAssigned>()
            .Which.UnmetDependencies.Should().ContainSingle().Which.Should().Be(blockerId);
        stream[^1].Data.Should().BeOfType<TaskClaimed>()
            .Which.DependencyOverrideAcknowledged.Should().BeTrue("the human's override is recorded on the claim");

        TaskAggregate final = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
        final.State.Should().Be(TaskState.Claimed, "the claim lands directly, never observably Blocked");
        final.IsInteractiveClaim.Should().BeTrue();

        TaskDetails details = (await verify.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.DependencyOverrideAcknowledged.Should().BeTrue();
    }

    /// <summary>
    /// The model chain reads the node's per-role and platform-default tiers through
    /// OperatingSettingsResolver — the same durable settings h9k config show renders — rather than
    /// bottoming out at AgentModel.PlatformFallback on the false premise that the CLI cannot reach
    /// them (independent pre-PR review, cycle 1, both lenses): a node with
    /// Hall9k__DefaultModel set resolves a start-it-mine session to that value, exactly as a
    /// dispatcher-launched build on the same node would.
    /// </summary>
    [Fact]
    public async Task A_queued_task_resolves_the_platform_default_model_from_the_environment()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();
        const string configuredDefaultModel = "claude-sonnet-5";

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
                TaskDecider.Add(taskId, projectId, "Prove the model chain reads the node's own default",
                    ["it is done"], TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        string? previousDefaultModel = Environment.GetEnvironmentVariable($"{OperatingSettingsResolver.EnvironmentPrefix}DefaultModel");
        Environment.SetEnvironmentVariable($"{OperatingSettingsResolver.EnvironmentPrefix}DefaultModel", configuredDefaultModel);
        Guid runId;
        try
        {
            runId = await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{OperatingSettingsResolver.EnvironmentPrefix}DefaultModel", previousDefaultModel);
        }

        await using IQuerySession verify = store.QuerySession();
        RunDetails run = (await verify.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.Model.Value.Should().Be(configuredDefaultModel,
            "the node's own configured default, not AgentModel.PlatformFallback, is what a dispatcher-launched build on this node would resolve to as well");
    }

    /// <summary>
    /// h9k task start is a third run-dispatch site alongside RunLauncher and TaskWorkCommand
    /// (PLAN.md #129), and used to be the one that never resolved the setting at all — a run it
    /// dispatched always recorded FullPipeline regardless of what the project or node set
    /// (independent pre-PR review, cycle 1, conformance lens). Pinning the project-level override
    /// reaching RunListItem.ReviewStageComposition (the field h9k task show's Stages column reads)
    /// through this exact code path is what closes that gap.
    /// </summary>
    [Fact]
    public async Task A_queued_task_resolves_the_projects_review_stage_composition()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
                ReviewStageComposition = "AdversarialOnly",
            });
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(taskId, projectId, "Prove h9k task start resolves the project's own composition",
                    ["it is done"], TaskType.Chore, null, null, null, Now, ownerId),
                ownerId, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        Guid runId = await StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await using IQuerySession verify = store.QuerySession();
        RunListItem run = (await verify.LoadAsync<RunListItem>(runId, cts.Token))!;
        run.ReviewStageComposition.Should().Be(ReviewStageComposition.AdversarialOnly,
            "the project's own override, not the FullPipeline default this dispatch site used to record regardless of what was set");
    }

    private string CreateRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-claim-refusal-repo-{Guid.NewGuid():N}");
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

        // Both pipes drained concurrently rather than one after the other — see
        // SeededGitOriginFixture.Git for the deadlock this shape avoids.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        string output = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {output}");
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
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

    private async Task<Guid> StartAsync(
        DocumentStore store, Guid taskId, Guid ownerId, bool acknowledgeUnmetDependencies, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

        // ClaimAndCutAsync's success path ends in Doorbell.RingAsync, which resolves its
        // connection off HALL9K_CONNECTION_STRING rather than this fixture (see the class-level
        // comment above), so it has to be pointed at the fixture for the one call that reaches it.
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            (Guid runId, _, _, _, _, _, _, _) = await TaskStartCommand.ClaimAndCutAsync(
                store, session, task, fence, context, DomainId.New(),
                SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build),
                acknowledgeUnmetDependencies, interactiveMode: false, trackerClaimGate: null,
                cancellationToken);
            return runId;
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }
    }


    // ── h9k task work ──
    private static readonly DateTimeOffset WorkClaimNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    [Fact]
    public async Task A_draft_task_is_refused_and_told_to_publish_first_by_task_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Still being written", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*Published, Queued, or Blocked*")
            .Where(exception => exception.Message.Contains("Publish it first"));
    }

    /// <summary>
    /// The conversion this task (0ac72cb8-h9k) makes to an already-Blocked entry: no longer a
    /// hard <see cref="DomainConflictException"/> refusal — a <see cref="DomainBusinessRuleException"/>
    /// that names the open blocker and points at <c>--acknowledge-unmet-dependencies</c>, the same
    /// shape the atomic Published entry's own refusal already had. The blocker is seeded as its
    /// own stream (mirrors <see cref="A_published_task_with_an_open_dependency_is_refused_through_ClaimAndCutAsync"/>),
    /// because the Blocked entry now reads the same real <see cref="Hall9k.Domain.Features.Tasks.Queries.TaskDependencyQuery"/>
    /// — and, like that sibling, a project has to exist too: <c>ClaimAndCutAsync</c> loads
    /// <c>TaskDetails</c>/<c>ProjectDetails</c> unconditionally before the Blocked branch's own
    /// refusal fires.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_is_refused_and_named_the_open_dependency_by_task_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid projectId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails { Id = projectId, RepositoryPath = "/dev/null", BaseBranch = "main" });

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), WorkClaimNow, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, WorkClaimNow, ownerId);

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
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
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

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), WorkClaimNow, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, WorkClaimNow, ownerId);

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
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
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

            seed.Events.StartStream<TaskAggregate>(blockerId, TaskDecider.Add(
                blockerId, DomainId.New(), "The blocker, still open", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, WorkClaimNow, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);

            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Queued, IsClosedOut: false, CurrentRunState: null,
                    PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), WorkClaimNow, ownerId);
            task.Apply(published);
            TaskAssigned assigned = TaskDecider.Assign(task, ownerId, blockers, WorkClaimNow, ownerId);
            task.Apply(assigned);
            Guid firstRunId = DomainId.New();
            TaskClaimed firstClaim = TaskDecider.ClaimInteractively(
                task, ownerId, firstRunId, WorkClaimNow, dependencyOverrideAcknowledged: true);
            task.Apply(firstClaim);
            TaskHandedBack handedBack = TaskDecider.HandBack(
                task, firstRunId, "task/earlier-branch", "handing off", WorkClaimNow, ownerId);

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
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Already running headless", ["it is done"],
                    TaskType.Chore, null, null, null, WorkClaimNow, ownerId),
                ownerId, WorkClaimNow);
            TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), WorkClaimNow);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("claimed by a node running headless work already"));
    }

    [Fact]
    public async Task A_queued_task_assigned_to_a_different_owner_is_refused_by_task_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid theirOwnerId = DomainId.New();
        Guid myOwnerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
                TaskDecider.Add(taskId, DomainId.New(), "Someone else's queued work", ["it is done"],
                    TaskType.Chore, null, null, null, WorkClaimNow, theirOwnerId),
                theirOwnerId, WorkClaimNow));
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
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid ownerId = DomainId.New();

        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            taskId, DomainId.New(), "Already handed to the standard pipeline", ["it is done"],
            TaskType.Chore, null, null, null, WorkClaimNow, ownerId));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, WorkClaimNow, ownerId));
        task.Apply(TaskDecider.Assign(task, ownerId, [], WorkClaimNow, ownerId));
        task.Apply(TaskDecider.ClaimInteractively(task, ownerId, runId, WorkClaimNow));

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
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        Guid ownerId = DomainId.New();
        Guid recordedClaudeSessionId = DomainId.New();
        string worktreePath = CreateEmptyDirectory();

        TaskAggregate task = new();
        task.Apply(TaskDecider.Add(
            taskId, DomainId.New(), "Already claimed, re-entered from another terminal", ["it is done"],
            TaskType.Chore, null, null, null, WorkClaimNow, ownerId));
        task.Apply(TaskDecider.Publish(task, TaskDependencyGraph.Empty, WorkClaimNow, ownerId));
        task.Apply(TaskDecider.Assign(task, ownerId, [], WorkClaimNow, ownerId));
        task.Apply(TaskDecider.ClaimInteractively(task, ownerId, runId, WorkClaimNow));

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
        DocumentStore store = postgres.Store;
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
                    ["it is done"], TaskType.Chore, null, null, null, WorkClaimNow, ownerId),
                ownerId, WorkClaimNow));
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
                    acknowledgeUnmetDependencies: false, trackerClaimGate: null, cts.Token);
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
    /// An interactive claim's node-level composition read used to go straight to the platform
    /// config file, ignoring the Hall9k__ReviewStageComposition environment variable
    /// OperatingSettingsResolver ranks above it (independent pre-PR review, cycle 1, adversarial
    /// lens) — the same env-over-file precedence h9k config show and a headless dispatch on this
    /// node already honor. Redirects HALL9K_HOME for the duration of this one test (unlike the rest
    /// of this class, which never touches the config file) so the file write below lands in a
    /// throwaway directory rather than whatever is actually installed on the machine running the
    /// suite.
    /// </summary>
    [Fact]
    public async Task A_queued_tasks_interactive_claim_resolves_the_composition_from_the_environment_over_the_file()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
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
                TaskDecider.Add(taskId, projectId, "Prove the interactive claim honors the environment over the file",
                    ["it is done"], TaskType.Chore, null, null, null, WorkClaimNow, ownerId),
                ownerId, WorkClaimNow));
            await seed.SaveChangesAsync(cts.Token);
        }

        string home = Path.Combine(Path.GetTempPath(), $"hall9k-work-claim-composition-home-{Guid.NewGuid():N}");
        string? previousHome = Environment.GetEnvironmentVariable("HALL9K_HOME");
        string? previousComposition =
            Environment.GetEnvironmentVariable($"{OperatingSettingsResolver.EnvironmentPrefix}ReviewStageComposition");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        try
        {
            // The file says None; the environment variable, which OperatingSettingsResolver ranks
            // above the file, says full-pipeline — the divergence the fix closes.
            await PlatformConfigFile.WriteOperatingSettingsAsync(s => s.ReviewStageComposition = "None", cts.Token);
            Environment.SetEnvironmentVariable(
                $"{OperatingSettingsResolver.EnvironmentPrefix}ReviewStageComposition", "full-pipeline");

            await using IDocumentSession session = store.LightweightSession();
            StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cts.Token))!;
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cts.Token))!;
            BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

            string? previousConnectionString =
                Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
            Guid runId;
            try
            {
                (runId, _, _, _, _, _, _) = await TaskWorkCommand.ClaimAndCutAsync(
                    store, session, task, fence, context, DomainId.New(),
                    SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
                    acknowledgeUnmetDependencies: false, trackerClaimGate: null, cts.Token);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
            }

            await using IQuerySession verify = store.QuerySession();
            RunListItem run = (await verify.LoadAsync<RunListItem>(runId, cts.Token))!;
            run.ReviewStageComposition.Should().Be(ReviewStageComposition.FullPipeline,
                "the environment variable OperatingSettingsResolver ranks above the file, not the file's own None");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HALL9K_HOME", previousHome);
            Environment.SetEnvironmentVariable(
                $"{OperatingSettingsResolver.EnvironmentPrefix}ReviewStageComposition", previousComposition);
            try
            {
                if (Directory.Exists(home))
                {
                    Directory.Delete(home, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The atomic Published entry's own headline behavior (task 688a1ccf-h9k), driven through
    /// <see cref="TaskWorkCommand.ClaimAndCutAsync"/> itself rather than the pure
    /// <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/> helper both the
    /// conformance and adversarial review passes point at
    /// (<see cref="ClaimAndLeaseArbitrationTests"/> already proves the helper's own math and the
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
        DocumentStore store = postgres.Store;
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
                ["it is done"], TaskType.Chore, null, null, null, WorkClaimNow, ownerId);
            TaskAggregate task = new();
            task.Apply(added);
            TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, WorkClaimNow, ownerId);

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
                    acknowledgeUnmetDependencies: false, trackerClaimGate: null, cts.Token);
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
        DocumentStore store = postgres.Store;
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
                null, null, null, WorkClaimNow, ownerId));

            TaskAdded added = TaskDecider.Add(
                taskId, projectId, "Waits on another task before an interactive claim",
                ["it is done"], TaskType.Chore, null, null, null, WorkClaimNow, ownerId, blockedBy: [blockerId]);
            TaskAggregate task = new();
            task.Apply(added);
            TaskDependency[] blockers =
            [
                new(blockerId, "The blocker, still open", TaskState.Draft, IsClosedOut: false,
                    CurrentRunState: null, PullRequestUrl: null, TaskType.Chore, []),
            ];
            TaskPublished published = TaskDecider.Publish(task, new TaskDependencyGraph(blockers), WorkClaimNow, ownerId);

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
                acknowledgeUnmetDependencies, trackerClaimGate: null, cancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }
    }

    // ── h9k task delegate ──
    private static readonly DateTimeOffset DelegateClaimNow = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Note = "Attempted: the retry loop. Deliberate: left the timeout at 30s. Latitude: none.";
    [Fact]
    public async Task A_draft_task_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<TaskAggregate>(taskId, TaskDecider.Add(
                taskId, DomainId.New(), "Still being written", ["it is done"], TaskType.Chore,
                null, null, null, DelegateClaimNow, ownerId));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*")
            .Where(exception => exception.Message.Contains("active interactive claim"));
    }

    /// <summary>
    /// An ordinary headless claim (h9k task assign, or the dispatcher's own claim) reads Claimed
    /// but carries a real node id rather than the interactive sentinel — the same discriminator
    /// <see cref="TaskAggregate.IsInteractiveClaim"/> is for.
    /// </summary>
    [Fact]
    public async Task A_headless_claim_is_refused_as_not_interactive()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Dispatched headlessly", ["it is done"],
                    TaskType.Chore, null, null, null, DelegateClaimNow, ownerId),
                ownerId, DelegateClaimNow);
            TaskClaimed claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), DelegateClaimNow);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("active interactive claim"));
    }

    [Fact]
    public async Task A_claim_belonging_to_a_different_owner_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid theirOwnerId = DomainId.New();
        Guid myOwnerId = DomainId.New();
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

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Someone else's interactive claim", ["it is done"],
                    TaskType.Chore, null, null, null, DelegateClaimNow, theirOwnerId),
                theirOwnerId, DelegateClaimNow);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, theirOwnerId, DomainId.New(), DelegateClaimNow, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, myOwnerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage($"*claimed by {theirOwnerId}*")
            .Where(exception => exception.Message.Contains("your own interactive claim"));
    }

    /// <summary>
    /// A pr-review task's own Claimed+sentinel state (AutoPrReviewEngine.CreateOneAsync's DelegateClaimNow
    /// speed) reads identically to a real interactive claim on IsInteractiveClaim's own
    /// Guid.Empty discriminator — mirrors TaskHandbackCommand's identical guard. Seeded with
    /// interactiveMode: false, exactly as CreateOneAsync's own claim leaves it (it never turns
    /// interactive mode on), so this test actually exercises the pr-review guard's ordering ahead
    /// of the interactive-mode-off check rather than the interactive-mode-off check itself
    /// (adversarial review, cycle 1: interactiveMode: true here made this test pass regardless of
    /// which guard the command reached first).
    /// </summary>
    [Fact]
    public async Task A_pr_review_sentinel_claim_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Reviewing a pull request", ["it is done"],
                    TaskType.PrReview, null, null, null, DelegateClaimNow, ownerId),
                ownerId, DelegateClaimNow);
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, DomainId.New(), DelegateClaimNow, dependencyOverrideAcknowledged: false, interactiveMode: false);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*pr-review task*")
            .Where(exception => exception.Message.Contains("not an interactive claim to delegate"));
    }

    [Fact]
    public async Task A_claim_whose_run_has_no_record_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "The process died mid-claim", ["it is done"],
                    TaskType.Chore, null, null, null, DelegateClaimNow, ownerId),
                ownerId, DelegateClaimNow);
            // No RunDispatched stream ever started for this run id — mirrors a process that died
            // between the TaskClaimed append and the worktree cut.
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, DomainId.New(), DelegateClaimNow, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*has no record*")
            .Where(exception => exception.Message.Contains("h9k task release"));
    }

    /// <summary>
    /// Orchestrator ruling on cycle 2's own medium finding: the refusal must not send the operator
    /// to a command that cannot actually fix the problem it names. h9k task work re-enters this
    /// exact claim (<see cref="Hall9k.Cli.Commands.TaskWorkCommand.ReenterAsync"/>) without
    /// appending any event, so it can never turn <see cref="TaskAggregate.InteractiveModeEnabled"/>
    /// back on — only a fresh <c>TaskClaimed</c> does that (<c>TaskRevised.ClearInteractiveMode</c>'s
    /// own doc). The refusal instead has to name the release-then-reclaim path (only possible on an
    /// untouched claim) and h9k task handback as the honest alternative once the branch holds work.
    /// </summary>
    [Fact]
    public async Task A_claim_with_interactive_mode_cleared_is_refused_without_naming_task_work_as_the_fix()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await ClaimInteractivelyAsync(store, taskId, projectId, ownerId, repositoryPath, cts.Token);

        await using (IDocumentSession clear = store.LightweightSession())
        {
            StreamState fence = (await clear.Events.FetchStreamStateAsync(taskId, cts.Token))!;
            TaskAggregate task = (await clear.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cts.Token))!;
            TaskRevised revised = TaskDecider.Revise(
                task, default, default, default, default, default, default, DelegateClaimNow, ownerId,
                clearInteractiveMode: true);
            clear.Events.Append(taskId, expectedVersion: fence.Version + 1, revised);
            await clear.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*claim turned interactive mode off*")
            .Where(exception =>
                !exception.Message.Contains("re-enters it interactively, which turns the flag back on")
                && exception.Message.Contains("h9k task release")
                && exception.Message.Contains("h9k task handback"));
    }

    [Fact]
    public async Task A_claim_already_handed_to_the_standard_pipeline_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();
        Guid runId;

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Already delivered", ["it is done"],
                    TaskType.Chore, null, null, null, DelegateClaimNow, ownerId),
                ownerId, DelegateClaimNow);
            runId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, runId, DelegateClaimNow, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, ownerId, LeaseGeneration: 1, SessionId: DomainId.New(),
                WorktreePath: Path.Combine(repositoryPath, "wt"), Branch: "task/x",
                ExecutorMode.Subscription, DelegateClaimNow));
            // Mirrors h9k task deliver's own append: the run's own state moves to Verifying, past
            // the Dispatched/Running window this command may still act in.
            seed.Events.Append(runId, new AgentSessionCompleted(runId, DelegateClaimNow, DomainId.New()));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*already*")
            .Where(exception => exception.Message.Contains("h9k task deliver"));
    }

    /// <summary>
    /// The double-booking guard <see cref="InteractiveSessionLiveness.EnsureNotAttachedElsewhere"/>
    /// already carries its own generic tests; this is the one integration point proving
    /// <see cref="TaskDelegateCommand.PrepareAsync"/> actually calls it before dispatching a
    /// contractor into a worktree an operator is still attached to.
    /// </summary>
    [Fact]
    public async Task A_claim_with_a_still_attached_interactive_session_is_refused()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();
        Guid runId;
        using Process current = Process.GetCurrentProcess();
        DateTimeOffset startedAt = new(current.StartTime.ToUniversalTime(), TimeSpan.Zero);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Store(new ProjectDetails
            {
                Id = projectId,
                RepositoryPath = repositoryPath,
                BaseBranch = "main",
                BranchNameTemplate = BranchNameTemplate.Default,
            });

            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, projectId, "Still attached in another terminal", ["it is done"],
                    TaskType.Chore, null, null, null, DelegateClaimNow, ownerId),
                ownerId, DelegateClaimNow);
            runId = DomainId.New();
            TaskClaimed claimed = TaskDecider.ClaimDeliberately(
                task, ownerId, runId, DelegateClaimNow, dependencyOverrideAcknowledged: false, interactiveMode: true);

            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, ownerId, LeaseGeneration: 1, SessionId: DomainId.New(),
                WorktreePath: Path.Combine(repositoryPath, "wt"), Branch: "task/x",
                ExecutorMode.Subscription, DelegateClaimNow));
            seed.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), startedAt, current.Id, Environment.MachineName, "abc12345-interactive-claim"));
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => PrepareAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*still attached*");
    }

    [Fact]
    public async Task A_fresh_interactive_claim_on_a_virgin_branch_does_not_resume_previous_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        Guid runId = await ClaimInteractivelyAsync(store, taskId, projectId, ownerId, repositoryPath, cts.Token);

        TaskDelegateCommand.DelegationPlan plan = await PrepareAsync(store, taskId, ownerId, cts.Token);

        plan.RunId.Should().Be(runId, "delegation reuses the interactive claim's own run rather than minting a new one");
        plan.ResumesPreviousWork.Should().BeFalse("nothing has been committed on this branch yet");
        plan.SessionName.Should().EndWith("-build");
        plan.Prompt.Should().Contain("A human delegated this phase to you");
        plan.Prompt.Should().Contain("Nothing has been committed on this branch yet");
        plan.Prompt.Should().Contain(Note);
        plan.OwnerId.Should().Be(ownerId);
    }

    [Fact]
    public async Task A_claim_whose_branch_already_carries_commits_resumes_previous_work()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        DocumentStore store = postgres.Store;
        Guid taskId = DomainId.New();
        Guid ownerId = await ResolveOwnerAsync(store, cts.Token);
        Guid projectId = DomainId.New();
        string repositoryPath = CreateRepository();

        await ClaimInteractivelyAsync(store, taskId, projectId, ownerId, repositoryPath, cts.Token);

        await using (IQuerySession verify = store.QuerySession())
        {
            TaskAggregate task = (await verify.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            RunDetails run = (await verify.LoadAsync<RunDetails>(task.CurrentRunId!.Value, cts.Token))!;
            File.WriteAllText(Path.Combine(run.WorktreePath, "committed.txt"), "already here\n");
            Git(run.WorktreePath, "add -A");
            Git(run.WorktreePath, "-c user.name=Test -c user.email=test@test commit -m \"prior work\"");
        }

        TaskDelegateCommand.DelegationPlan plan = await PrepareAsync(store, taskId, ownerId, cts.Token);

        plan.ResumesPreviousWork.Should().BeTrue("the branch already carries a commit ahead of base");
        plan.Prompt.Should().Contain("This worktree already holds work");
    }

    /// <summary>
    /// <see cref="TaskDelegateCommand.PrepareAsync"/> resolves the calling owner itself, through
    /// <see cref="NodeBootstrap.EnsureAsync"/>, which is idempotent per store but not per test: a
    /// <see cref="PostgresFixture"/> database is shared across every <c>[Fact]</c> in this class,
    /// so whichever test runs first mints the one owner every later test's own bootstrap call
    /// reads back. Every test below whose claim must actually pass PrepareAsync's own "is this
    /// your claim" check resolves that real owner here first, rather than a random id of its own
    /// that would only coincidentally match.
    /// </summary>
    private static async Task<Guid> ResolveOwnerAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return context.OwnerId;
    }

    private async Task<Guid> ClaimInteractivelyAsync(
        DocumentStore store, Guid taskId, Guid projectId, Guid ownerId, string repositoryPath, CancellationToken cancellationToken)
    {
        await using IDocumentSession seed = store.LightweightSession();
        seed.Store(new ProjectDetails
        {
            Id = projectId,
            RepositoryPath = repositoryPath,
            BaseBranch = "main",
            BranchNameTemplate = BranchNameTemplate.Default,
        });
        seed.Events.StartStream<TaskAggregate>(taskId, TaskSeed.Dispatchable(
            TaskDecider.Add(taskId, projectId, "Interactively claimed for delegation", ["it is done"],
                TaskType.Chore, null, null, null, DelegateClaimNow, ownerId),
            ownerId, DelegateClaimNow));
        await seed.SaveChangesAsync(cancellationToken);

        StreamState fence = (await seed.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await seed.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

        // ClaimAndCutAsync's success path ends in Doorbell.RingAsync, which resolves its
        // connection off HALL9K_CONNECTION_STRING rather than this fixture, so it has to be
        // pointed at the fixture for the one call that reaches it (mirrors ClaimRefusalTests).
        string? previousConnectionString =
            Environment.GetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, postgres.ConnectionString);
        try
        {
            (Guid runId, _, _, _, _, _, _, _) = await TaskStartCommand.ClaimAndCutAsync(
                store, seed, task, fence, context, DomainId.New(),
                SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim),
                acknowledgeUnmetDependencies: false, interactiveMode: true, trackerClaimGate: null,
                cancellationToken);
            return runId;
        }
        finally
        {
            Environment.SetEnvironmentVariable(Hall9kDatabase.EnvironmentVariableName, previousConnectionString);
        }
    }

    private static async Task<TaskDelegateCommand.DelegationPlan> PrepareAsync(
        DocumentStore store, Guid taskId, Guid ownerId, CancellationToken cancellationToken)
    {
        // ownerId is unused here — TaskDelegateCommand.PrepareAsync resolves the calling owner
        // itself, through NodeBootstrap.EnsureAsync — but every call site names the owner the
        // fixture claimed as, so the test reads honestly even though this helper does not pass it on.
        await using IDocumentSession session = store.LightweightSession();
        return await TaskDelegateCommand.PrepareAsync(session, taskId, Note, force: false, cancellationToken);
    }
}
