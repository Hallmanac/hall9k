using Hall9k.Domain.Infrastructure.Storage;
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

using Hall9k.Tests.Fakes;

namespace Hall9k.Tests.Integration;

// Both classes redirect the process-wide HALL9K_HOME; sharing a collection serializes
// them so one test's home is never yanked out from under the other's tail loop.
[Collection("Hall9kHome")]
[Trait("Category", "RequiresDocker")]
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

    [Fact]
    public async Task Zero_commits_on_the_branch_fails_fast_before_any_gate_runs()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: false, cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("never", "echo should-not-run")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeFalse("gates on an unmodified tree pass vacuously and prove nothing");
        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed");
        run.FailureReason.Should().Contain("produced no commits");
        run.FailedGates.Should().BeEmpty("no gate failed — no gate ever ran");

        (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("the failure releases the lease");
        File.Exists(Path.Combine(RunPaths.RunDirectory(runId), "verify-never.log"))
            .Should().BeFalse("the failure lands before the gates, not after");
    }

    [Fact]
    public async Task A_branch_with_commits_clears_the_no_commit_check_and_runs_its_gates()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: true, cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store, [new VerifyCommand("truth", "true")], cts.Token);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue();
        await using IQuerySession query = store.QuerySession();
        (await query.LoadAsync<RunDetails>(runId, cts.Token))!.State.Value.Should().Be("Verifying");
    }

    [Fact]
    public async Task A_research_task_may_legitimately_end_with_zero_commits()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        await InitGitWorktreeAsync(withTaskCommit: false, cts.Token);
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("truth", "true")], cts.Token, TaskType.Research);

        bool passed = await NewRunner(store).VerifyAsync(runId, taskId, cts.Token);

        passed.Should().BeTrue("a research task's deliverable is its transcript, not commits");
    }

    /// <summary>
    /// The generation fence (backlog 39): a requeue-and-reclaim moved the task on to
    /// generation 2 while this run — still generation 1 — was mid-gate. The run's own
    /// failure is still recorded honestly, but it must not fail the task the live
    /// generation is working, nor take that generation's lease with it.
    /// </summary>
    [Fact]
    public async Task A_stale_generations_gate_failure_does_not_touch_the_live_generations_task_or_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (Guid taskId, Guid runId) = await SeedAsync(store,
            [new VerifyCommand("boom", "echo exploding; exit 3")], cts.Token);

        await using (IDocumentSession session = store.LightweightSession())
        {
            TaskAggregate task = (await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cts.Token))!;
            Guid ownerId = task.AssignedOwnerId!.Value;
            Guid nodeId = DomainId.New();
            var requeued = TaskDecider.Requeue(task, RequeueReason.LeaseExpired, Now);
            task.Apply(requeued);
            var reclaimed = TaskDecider.Claim(task, nodeId, ownerId, DomainId.New(), Now);
            session.Events.Append(taskId, requeued, reclaimed);
            session.Store(new TaskLease { Id = taskId, NodeId = nodeId, LeaseGeneration = 2, HeartbeatAt = Now });
            await session.SaveChangesAsync(cts.Token);
        }

        ListLogger<VerificationRunner> logger = new();
        VerificationRunner runner = new(store, Options.Create(new DaemonOptions()), logger);
        await runner.VerifyAsync(runId, taskId, cts.Token);

        await using IQuerySession query = store.QuerySession();
        RunDetails run = (await query.LoadAsync<RunDetails>(runId, cts.Token))!;
        run.State.Value.Should().Be("Failed", "the run's own failure is an honest fact regardless of generation");

        TaskListItem task2 = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task2.State.Value.Should().Be("Claimed", "the live generation's claim survives the stale run's gate failure");
        task2.LeaseGeneration.Should().Be(2);
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().NotBeNull(
            "the stale run's failure must not release the live generation's lease");

        logger.Lines.Should().Contain(line =>
            line.Contains("run at generation 1") && line.Contains("at generation 2 - rejected"));
    }

    /// <summary>
    /// Turns the seeded worktree into a real repo: base branch `main`, task branch
    /// checked out — with or without a commit of its own past the base.
    /// </summary>
    private async Task InitGitWorktreeAsync(bool withTaskCommit, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_worktree);
        string script =
            "git init -q -b main && " +
            "git -c user.email=t@t -c user.name=t commit --allow-empty -m init -q && " +
            "git checkout -q -b task/verify" +
            (withTaskCommit ? " && git -c user.email=t@t -c user.name=t commit --allow-empty -m work -q" : "");

        using System.Diagnostics.Process process = new();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = _worktree,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        process.ExitCode.Should().Be(0, "the test repo must seed cleanly");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private static VerificationRunner NewRunner(DocumentStore store) =>
        new(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance);

    private async Task<(Guid TaskId, Guid RunId)> SeedAsync(
        DocumentStore store, IReadOnlyList<VerifyCommand> gates, CancellationToken cancellationToken,
        TaskType? taskType = null)
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
        (task, object[] lifecycle) = TaskSeed.Start(
            TaskDecider.Add(taskId, projectId, "Verify me", ["gates run"], taskType ?? TaskType.Chore,
                null, null, null, Now, ownerId),
            ownerId, Now);
        var claimed = TaskDecider.Claim(task, DomainId.New(), ownerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, [.. lifecycle, claimed]);
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
