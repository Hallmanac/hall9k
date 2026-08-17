using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Handlers;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

public sealed class VerificationRunnerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _home = SetTempHome();
    private readonly string _worktree = Path.Combine(Path.GetTempPath(), $"hall9k-vt-{Guid.NewGuid():N}");

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-vhome-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task All_gates_passing_records_verification_passed_with_logs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("hello", "echo hello-from-gate"), new VerifyCommand("truth", "true")], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Verifying", "VerificationPassed does not transition; the PR step does");
        run.FailedGates.Should().BeEmpty();

        File.ReadAllText(Path.Combine(RunPaths.RunDirectory(runId), "verify-hello.log"))
            .Should().Contain("hello-from-gate");
    }

    [Fact]
    public async Task Failing_gate_fails_run_and_task_and_names_the_gate()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("ok", "true"), new VerifyCommand("boom", "echo exploding; exit 3"), new VerifyCommand("never", "true")], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailedGates.Should().ContainSingle().Which.Should().Be("boom");
        run.FailureReason.Should().Contain("exploding");

        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("verification failure releases the lease");

        File.Exists(Path.Combine(RunPaths.RunDirectory(runId), "verify-never.log"))
            .Should().BeFalse("gates after the failure never run");
    }

    [Fact]
    public async Task No_gates_passes_with_an_explicit_note()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store, gates: [], cts.Token);

        await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        var events = await query.Events.FetchStreamAsync(runId, token: cts.Token);
        VerificationPassed passed = events.Select(e => e.Data).OfType<VerificationPassed>().Single();
        passed.Note.Should().Contain("No verification gates configured");
    }

    [Fact]
    public async Task Overrunning_gate_times_out_as_a_failure_not_a_hang()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("slow", "sleep 30")], cts.Token);

        VerificationRunner runner = new(
            store,
            Options.Create(new DaemonOptions { VerifyGateTimeout = TimeSpan.FromSeconds(1) }),
            NullLogger<VerificationRunner>.Instance);

        await runner.VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("timeout");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static VerificationRunner NewRunner(DocumentStore store) =>
        new(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance);

    private async Task<(Guid TaskId, Guid RunId)> SeedAsync(
        DocumentStore store, IReadOnlyList<VerifyCommand> gates, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_worktree);
        Guid ownerId = DomainId.New();
        Guid connectionId = DomainId.New();
        Guid projectId = DomainId.New();
        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();

        await using IDocumentSession session = store.LightweightSession();

        ProjectAggregate project = new();
        ProjectRegistered registered = ProjectDecider.Register(
            projectId, ownerId, connectionId, $"verify-{projectId:N}", _worktree, null, "main", Now);
        project.Apply(registered);
        session.Events.StartStream<ProjectAggregate>(projectId, registered, ProjectDecider.ChangeSettings(
            project,
            verifyCommands: Optional<IReadOnlyList<VerifyCommand>>.Of(gates),
            skipPermissions: Optional<bool>.None,
            maxParallelAgents: Optional<int>.None,
            contextLinks: Optional<IReadOnlyList<ContextLink>>.None,
            Now, ownerId));

        TaskAggregate task = new();
        var added = TaskDecider.Add(taskId, projectId, "Verify me", ["gates run"], TaskType.Chore,
            null, null, null, Now, ownerId);
        task.Apply(added);
        var claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, added, claimed);
        session.Store(new TaskLease { Id = taskId, NodeId = claimed.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId,
            new RunDispatched(runId, taskId, claimed.NodeId, ownerId, 1, DomainId.New(),
                _worktree, "task/verify", ExecutorMode.Subscription, Now),
            new AgentSessionCompleted(runId, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (taskId, runId);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        foreach (string dir in new[] { _home, _worktree })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
