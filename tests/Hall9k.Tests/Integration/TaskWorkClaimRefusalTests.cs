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

    [Fact]
    public async Task A_blocked_task_is_refused_and_told_a_dependency_is_open()
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

        Func<Task> act = () => WorkAsync(store, taskId, ownerId, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*is Blocked*")
            .Where(exception => exception.Message.Contains("waiting on a dependency"));
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
                    SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim), cts.Token);
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

    private static async Task WorkAsync(DocumentStore store, Guid taskId, Guid ownerId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState fence = (await session.Events.FetchStreamStateAsync(taskId, cancellationToken))!;
        TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken))!;
        BootstrapContext context = new(ownerId, DomainId.New(), DomainId.New());

        await TaskWorkCommand.ClaimAndCutAsync(
            store, session, task, fence, context, DomainId.New(),
            SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim), cancellationToken);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });
}
