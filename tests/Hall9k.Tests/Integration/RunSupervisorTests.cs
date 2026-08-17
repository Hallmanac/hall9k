using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using FluentAssertions;
using Hall9k.Daemon;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hall9k.Tests.Integration;

// Both classes redirect the process-wide HALL9K_HOME; sharing a collection serializes
// them so one test's home is never yanked out from under the other's tail loop.
[Collection("Hall9kHome")]
public sealed class RunSupervisorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private const string ResultLine =
        """{"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":1200,"output_tokens":300},"total_cost_usd":0.0123}""";

    private readonly string _home = SetTempHome();

    private static string SetTempHome()
    {
        string home = Path.Combine(Path.GetTempPath(), $"hall9k-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HALL9K_HOME", home);
        return home;
    }

    [Fact]
    public async Task Fake_agent_stream_is_tailed_to_completion_with_tokens_recorded()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        int processId = SpawnFakeAgent(runId,
            $"sleep 0.3; echo '{{\"type\":\"assistant\"}}'; sleep 0.3; echo '{ResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        supervisor.StartMonitoring(runId, taskId, processId, startedAt, cts.Token);
        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);

        details.InputTokens.Should().Be(1200);
        details.OutputTokens.Should().Be(300);
        details.CostUsd.Should().Be(0.0123m);

        await using IQuerySession query = store.QuerySession();
        var activity = await query.LoadAsync<Hall9k.Domain.Features.Run.Documents.RunActivity>(runId, cts.Token);
        activity!.StreamBytesRead.Should().BeGreaterThan(0, "the tail cursor persists progress");
    }

    [Fact]
    public async Task Daemon_restart_mid_run_adopts_the_orphan_and_completes_it()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        // Agent outlives the "first daemon": takes ~4s, first monitor is killed after ~1s.
        int processId = SpawnFakeAgent(runId,
            $"echo '{{\"type\":\"assistant\"}}'; sleep 4; echo '{ResultLine}'");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        using (CancellationTokenSource firstDaemon = new())
        {
            RunSupervisor doomed = NewSupervisor(store, node);
            doomed.StartMonitoring(runId, taskId, processId, startedAt, firstDaemon.Token);
            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            firstDaemon.Cancel();
        }

        // The "restarted daemon": adoption finds the live process and resumes tailing.
        RunSupervisor restarted = NewSupervisor(store, node);
        await restarted.AdoptOrphansAsync(cts.Token);
        restarted.ActiveCount.Should().Be(1, "the live orphan must be adopted, not killed (log #7)");

        RunDetails details = await WaitForStateAsync(store, runId, "Verifying", cts.Token);
        details.InputTokens.Should().Be(1200);
    }

    [Fact]
    public async Task Agent_dying_without_a_result_fails_run_and_task_and_releases_the_lease()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using DocumentStore store = NewStore();
        (NodeContext node, Guid taskId, Guid runId) = await SeedClaimedTaskAsync(store, cts.Token);

        int processId = SpawnFakeAgent(runId, "echo '{\"type\":\"assistant\"}'; exit 1");
        DateTimeOffset startedAt = await RecordProcessStartedAsync(store, runId, processId, cts.Token);

        RunSupervisor supervisor = NewSupervisor(store, node);
        supervisor.StartMonitoring(runId, taskId, processId, startedAt, cts.Token);

        RunDetails details = await WaitForStateAsync(store, runId, "Failed", cts.Token);
        details.FailureReason.Should().Contain("without a result");

        await using IQuerySession query = store.QuerySession();
        TaskListItem task = (await query.LoadAsync<TaskListItem>(taskId, cts.Token))!;
        task.State.Value.Should().Be("Failed");
        (await query.LoadAsync<TaskLease>(taskId, cts.Token)).Should().BeNull("failure releases the lease");
    }

    private DocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(postgres.ConnectionString);
        opts.ConfigureHall9k(AutoCreate.All);
    });

    private async Task<(NodeContext Node, Guid TaskId, Guid RunId)> SeedClaimedTaskAsync(
        DocumentStore store, CancellationToken cancellationToken)
    {
        NodeContext node = new();
        await node.InitializeAsync(store, cancellationToken);

        Guid taskId = DomainId.New();
        Guid runId = DomainId.New();
        await using IDocumentSession session = store.LightweightSession();

        TaskAggregate task = new();
        var added = TaskDecider.Add(taskId, DomainId.New(), "Executor test task", ["it completes"],
            TaskType.Chore, null, null, null, Now, node.OwnerId);
        task.Apply(added);
        var claimed = TaskDecider.Claim(task, node.NodeId, node.OwnerId, runId, Now);
        session.Events.StartStream<TaskAggregate>(taskId, added, claimed);
        session.Store(new TaskLease { Id = taskId, NodeId = node.NodeId, LeaseGeneration = 1, HeartbeatAt = Now });

        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, taskId, node.NodeId, node.OwnerId, 1, DomainId.New(),
            "/tmp/wt-test", "task/test", ExecutorMode.Subscription, Now));
        await session.SaveChangesAsync(cancellationToken);

        return (node, taskId, runId);
    }

    private int SpawnFakeAgent(Guid runId, string script)
    {
        Directory.CreateDirectory(RunPaths.RunDirectory(runId));
        Process process = new();
        process.StartInfo = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"({script}) > \"{RunPaths.StreamFile(runId)}\" 2> \"{RunPaths.StandardErrorFile(runId)}\"");
        process.Start();
        return process.Id;
    }

    private static async Task<DateTimeOffset> RecordProcessStartedAsync(
        DocumentStore store, Guid runId, int processId, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt;
        try
        {
            using Process process = Process.GetProcessById(processId);
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            // Very fast scripts can exit before we look them up; a nominal start time still
            // exercises the result-on-disk paths.
            startedAt = DateTimeOffset.UtcNow;
        }

        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunProcessStarted(runId, processId, startedAt));
        await session.SaveChangesAsync(cancellationToken);
        return startedAt;
    }

    private static async Task<RunDetails> WaitForStateAsync(
        DocumentStore store, Guid runId, string state, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using IQuerySession query = store.QuerySession();
            RunDetails? details = await query.LoadAsync<RunDetails>(runId, cancellationToken);
            if (details?.State.Value == state)
            {
                return details;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Run {runId} never reached state {state}.");
    }

    private static RunSupervisor NewSupervisor(DocumentStore store, NodeContext node) =>
        new(store, node, new UnixProcessManager(),
            new VerificationRunner(store, Options.Create(new DaemonOptions()), NullLogger<VerificationRunner>.Instance),
            new PullRequestOpener(store,
                new Hall9k.Daemon.Worktrees.GitWorktreeManager(NullLogger<Hall9k.Daemon.Worktrees.GitWorktreeManager>.Instance),
                NullLogger<PullRequestOpener>.Instance),
            NullLogger<RunSupervisor>.Instance);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HALL9K_HOME", null);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
