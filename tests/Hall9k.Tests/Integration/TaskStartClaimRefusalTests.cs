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
using Hall9k.Domain.Features.Tasks.Projections;
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
/// h9k task start's entry-state refusals and success paths (task 8a56af78-h9k), pinned against a
/// real store because <see cref="TaskStartCommand.ClaimAndCutAsync"/> reads the dependency
/// snapshot for a Published task straight off Marten, exactly as <c>TaskWorkClaimRefusalTests</c>
/// does for h9k task work's identical shape. This never drives as far as the actual detached
/// process spawn (<see cref="HeadlessLaunch"/>) — that needs a real <c>claude</c> binary and is out
/// of reach here, the same way h9k task work's own <c>LaunchInteractiveClaudeAsync</c> is never
/// exercised by its own refusal tests either.
/// </summary>
// The success-path tests drive ClaimAndCutAsync all the way through, which rings the doorbell
// (Hall9k.Cli.Infrastructure.Doorbell) — see TaskWorkClaimRefusalTests's own class comment for why
// that needs HALL9K_CONNECTION_STRING pointed at the fixture for the duration of each such call,
// and why this joins the same collection.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TaskStartClaimRefusalTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
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

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Draft*Published or Queued*")
            .Where(exception => exception.Message.Contains("Publish it first"));
    }

    /// <summary>
    /// Unlike h9k task work, a persisted-Blocked task is still refused — the acknowledgment
    /// override only applies at the moment of assignment (the Published entry), not to a task
    /// that already sits Blocked from an earlier plain <c>h9k task assign</c>.
    /// </summary>
    [Fact]
    public async Task A_blocked_task_is_refused_even_with_acknowledgment()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        Guid taskId = DomainId.New();
        Guid blockerId = DomainId.New();
        Guid ownerId = DomainId.New();

        await using (IDocumentSession seed = store.LightweightSession())
        {
            TaskAdded added = TaskDecider.Add(
                taskId, DomainId.New(), "Waits on another task", ["it is done"], TaskType.Chore,
                null, null, null, Now, ownerId, blockedBy: [blockerId]);
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

            seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned);
            await seed.SaveChangesAsync(cts.Token);
        }

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: true, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Blocked*")
            .Where(exception => exception.Message.Contains("acknowledgment override only applies at the moment of assignment"));
    }

    [Fact]
    public async Task A_task_with_a_live_claim_is_refused()
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

        Func<Task> act = () => StartAsync(store, taskId, ownerId, acknowledgeUnmetDependencies: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Claimed*")
            .Where(exception => exception.Message.Contains("already has a live claim"));
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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
        using DocumentStore store = NewStore();
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

    private string CreateRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hall9k-start-claim-{Guid.NewGuid():N}");
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
            (Guid runId, _, _, _, _, _) = await TaskStartCommand.ClaimAndCutAsync(
                store, session, task, fence, context, DomainId.New(),
                SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build),
                acknowledgeUnmetDependencies, cancellationToken);
            return runId;
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
