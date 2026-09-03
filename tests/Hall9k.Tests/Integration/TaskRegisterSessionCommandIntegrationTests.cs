using System.Diagnostics;
using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Daemon;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Tests.Fakes;
using JasperFx;
using Marten;
using Xunit;

namespace Hall9k.Tests.Integration;

/// <summary>
/// <see cref="TaskRegisterSessionCommand.RegisterAsync"/> against a real store — the store round
/// trip (the Claimed+interactive and run-state guards, the double-booking guard, and the append
/// itself) that <see cref="Hall9k.Tests.Cli.TaskRegisterSessionCommandTests"/>'s own doc comment
/// calls this command's integration-tier concern (independent pre-PR review, cycle 1, both
/// lenses, medium: nothing before this exercised this command's actual domain behavior — the
/// state guards, the run-state guard, the double-booking check added by this same fix, or the
/// shape of the appended event). These mutate CLAUDE_PID, an environment variable, so — per
/// <see cref="Hall9k.Tests.Domain.HomeEnvironmentIsolationTests"/>'s own blanket rule over every
/// <c>Environment.SetEnvironmentVariable</c>/<c>GetEnvironmentVariable</c> caller — this joins the
/// <c>Hall9kHome</c> collection so it never races a different collection's own env-var test.
/// </summary>
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
public sealed class TaskRegisterSessionCommandIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Refuses_when_the_task_carries_no_active_interactive_claim()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cts.Token);

        Guid taskId = DomainId.New();
        await using (IDocumentSession seed = store.LightweightSession())
        {
            (TaskAggregate task, object[] lifecycle) = TaskSeed.Start(
                TaskDecider.Add(taskId, DomainId.New(), "Never claimed interactively", ["done"], TaskType.Chore,
                    null, null, null, Now, node.OwnerId),
                node.OwnerId, Now);
            seed.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle]);
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*only a task with an active interactive claim*");
    }

    [Fact]
    public async Task Refuses_against_a_stale_current_run_id_with_no_run_record()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        // Deliberately no RunAggregate stream started for the claimed run: this reproduces a
        // claim whose run record never landed (the process died while cutting the worktree).
        (Guid taskId, _, _) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;
        details.CurrentRunId.Should().NotBeNull();

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*has no record*");
    }

    [Fact]
    public async Task Refuses_once_the_run_has_already_moved_to_the_standard_pipeline()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, Now));
            seed.Events.Append(runId, new AgentSessionCompleted(runId, Now, node.NodeId));
            await seed.SaveChangesAsync(cts.Token);
        }

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*h9k task deliver (or handback)*");
    }

    [Fact]
    public async Task Registers_the_first_session_and_appends_InteractiveSessionStarted_on_the_runs_own_stream()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, Now));
            await seed.SaveChangesAsync(cts.Token);
        }

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, Environment.ProcessId.ToString()));

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        (Guid registeredRunId, int processId) = await TaskRegisterSessionCommand.RegisterAsync(
            session, details, force: false, cts.Token);
        await session.SaveChangesAsync(cts.Token);

        registeredRunId.Should().Be(runId);
        processId.Should().Be(Environment.ProcessId);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ActiveSessions.Should().ContainSingle(active => active.Role == AgentRole.Interactive
            && active.ProcessId == Environment.ProcessId);
    }

    /// <summary>
    /// The double-booking guard this fix adds (independent pre-PR review, both lenses, cycle 1,
    /// medium): a second registration attempt, from a session CLAUDE_PID identifies as a
    /// different, still-live process than the one already recorded, must be refused rather than
    /// silently overwriting the first session's own liveness record — the exact failure
    /// <c>TaskWorkCommand.ReenterAsync</c>'s own comment names for the parallel <c>h9k task work</c>
    /// door onto this same collision.
    /// </summary>
    [Fact]
    public async Task Refuses_a_second_registration_while_the_first_sessions_process_is_still_alive()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        using Process thisProcess = Process.GetCurrentProcess();
        DateTimeOffset firstSessionStartedAt = InteractiveSessionLiveness.ReadStartedAt(thisProcess);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, Now));
            // The "first session" is this very test process — guaranteed alive for the whole
            // test, with its own genuinely-readable start time, exactly as a real self-registered
            // session's own record would read.
            seed.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), firstSessionStartedAt, Environment.ProcessId, Environment.MachineName,
                "register-session-first"));
            await seed.SaveChangesAsync(cts.Token);
        }

        // The "second session" claims a different CLAUDE_PID than the one already recorded — a
        // second terminal, or a stale prompt pasted again — so IsSelfInvocation must not match it.
        int otherPid = Environment.ProcessId == 1 ? 2 : 1;
        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, otherPid.ToString()));

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().ThrowAsync<DomainConflictException>()
            .WithMessage("*still attached in another terminal*");

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.ActiveSessions.Should().ContainSingle(active => active.ProcessId == Environment.ProcessId,
            "the refused second registration must never overwrite the first session's own record");
    }

    /// <summary>
    /// The same session re-registering (a retry, or the operator re-pasting the identical
    /// starting prompt into the same still-running session) matches on CLAUDE_PID and must not be
    /// refused — the exemption <see cref="InteractiveSessionLiveness.IsSelfInvocation"/> exists
    /// for, exercised here through the real double-booking guard rather than in isolation.
    /// </summary>
    [Fact]
    public async Task Allows_the_same_session_to_register_again()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId, NodeContext node) = await SeedClaimedInteractiveTaskAsync(store, cts.Token);

        using Process thisProcess = Process.GetCurrentProcess();
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(thisProcess);

        await using (IDocumentSession seed = store.LightweightSession())
        {
            seed.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, Guid.Empty, node.OwnerId, 1, DomainId.New(), "/tmp/register-session-worktree",
                "task/register-session-branch", ExecutorMode.Subscription, Now));
            seed.Events.Append(runId, new InteractiveSessionStarted(
                runId, DomainId.New(), startedAt, Environment.ProcessId, Environment.MachineName,
                "register-session-first"));
            await seed.SaveChangesAsync(cts.Token);
        }

        using EnvironmentVariableScope scope = EnvironmentVariableScope.Set(
            (InteractiveSessionLiveness.ClaudeCodePidEnvironmentVariable, Environment.ProcessId.ToString()));

        await using IDocumentSession session = store.LightweightSession();
        TaskDetails details = (await session.LoadAsync<TaskDetails>(taskId, cts.Token))!;

        Func<Task> act = () => TaskRegisterSessionCommand.RegisterAsync(session, details, force: false, cts.Token);

        await act.Should().NotThrowAsync();
    }

    private static async Task<(Guid TaskId, Guid RunId, NodeContext Node)> SeedClaimedInteractiveTaskAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = await NodeBootstrapSeed.NewNodeAsync(store, cancellationToken);
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession seed = store.LightweightSession();
        TaskAggregate task = new();
        TaskAdded added = TaskDecider.Add(taskId, DomainId.New(), "Register a session against an interactive claim",
            ["done"], TaskType.Chore, null, null, null, Now, node.OwnerId);
        task.Apply(added);
        TaskPublished published = TaskDecider.Publish(task, TaskDependencyGraph.Empty, Now, node.OwnerId);
        task.Apply(published);
        TaskAssigned assigned = TaskDecider.Assign(task, node.OwnerId, [], Now, node.OwnerId);
        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, node.OwnerId, runId, Now);
        seed.Events.StartStream<TaskAggregate>(taskId, added, published, assigned, claimed);
        await seed.SaveChangesAsync(cancellationToken);

        return (taskId, runId, node);
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    /// <summary>Saves and restores the named environment variables around a test, isolating it from every other.</summary>
    [Collection("Hall9kHome")]
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _saved;

        private EnvironmentVariableScope((string Name, string? Previous)[] saved) => _saved = saved;

        public static EnvironmentVariableScope Set(params (string Name, string? Value)[] values)
        {
            (string Name, string? Previous)[] saved =
                [.. values.Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))];
            foreach ((string name, string? value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            return new EnvironmentVariableScope(saved);
        }

        public void Dispose()
        {
            foreach ((string name, string? previous) in _saved)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }
}
