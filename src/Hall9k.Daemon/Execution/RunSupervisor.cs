using Hall9k.Domain.Infrastructure.Storage;
using System.Collections.Concurrent;
using System.Text;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Owns every live run: tails its stream file (cursor persisted in RunActivity, so a
/// restarted daemon resumes where it left off), detects the terminal result event, and
/// adopts orphans at startup — reattach before declaring anything dead (log #7).
/// </summary>
public sealed class RunSupervisor(
    IDocumentStore store,
    NodeContext node,
    IProcessManager processManager,
    VerificationRunner verification,
    PullRequestOpener pullRequests,
    ILogger<RunSupervisor> logger)
{
    private static readonly TimeSpan TailInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeadProcessGrace = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, Task> _monitors = new();

    public int ActiveCount => _monitors.Count;

    public void StartMonitoring(Guid runId, Guid taskId, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken)
    {
        _monitors.TryAdd(runId, Task.Run(
            () => MonitorAsync(runId, taskId, processId, processStartedAt, cancellationToken),
            cancellationToken));
    }

    /// <summary>
    /// Startup adoption: every non-terminal run recorded for this node either gets its
    /// monitor back (process alive, or result already on disk) or is failed honestly.
    /// </summary>
    public async Task AdoptOrphansAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> candidates = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' in (?, ?, ?)", RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value))
            .ToListAsync(cancellationToken);

        foreach (RunDetails run in candidates)
        {
            if (run.State == RunState.Verifying)
            {
                // Daemon died mid-verification: the agent's work is done, just re-verify.
                logger.LogInformation("Adopting run {RunId} stranded in Verifying — re-running gates", run.Id);
                if (await verification.VerifyAsync(run.Id, run.TaskId, cancellationToken))
                {
                    await pullRequests.OpenAsync(run.Id, run.TaskId, cancellationToken);
                }

                continue;
            }

            if (run.ProcessId is null || run.ProcessStartedAt is null)
            {
                await FailRunAsync(run.Id, run.TaskId, "Dispatched but never started before the daemon stopped.", cancellationToken);
                continue;
            }

            bool alive = processManager.IsAlive(run.ProcessId.Value, run.ProcessStartedAt.Value);
            bool resultOnDisk = await ResultAlreadyWrittenAsync(run.Id, cancellationToken);
            if (alive || resultOnDisk)
            {
                logger.LogInformation(
                    "Adopting run {RunId} (pid {ProcessId}, alive: {Alive}, result on disk: {Result})",
                    run.Id, run.ProcessId, alive, resultOnDisk);
                StartMonitoring(run.Id, run.TaskId, run.ProcessId.Value, run.ProcessStartedAt.Value, cancellationToken);
            }
            else
            {
                await FailRunAsync(run.Id, run.TaskId, "Agent process died without a result while the daemon was down.", cancellationToken);
            }
        }
    }

    private async Task MonitorAsync(Guid runId, Guid taskId, int processId, DateTimeOffset processStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            string streamFile = RunPaths.StreamFile(runId);
            long cursor = await LoadCursorAsync(runId, cancellationToken);
            DateTimeOffset? deadSince = null;
            StringBuilder partialLine = new();

            while (!cancellationToken.IsCancellationRequested)
            {
                (long newCursor, bool sawResult, AgentResult? result) =
                    await ReadNewLinesAsync(streamFile, cursor, partialLine, cancellationToken);

                if (newCursor > cursor)
                {
                    cursor = newCursor;
                    await SaveActivityAsync(runId, cursor, cancellationToken);
                }

                if (sawResult)
                {
                    await CompleteRunAsync(runId, taskId, result!, cancellationToken);
                    return;
                }

                if (!processManager.IsAlive(processId, processStartedAt))
                {
                    // Give buffered output a moment to land before declaring death.
                    deadSince ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - deadSince > DeadProcessGrace)
                    {
                        await FailRunAsync(runId, taskId, ReadStandardErrorTail(runId), cancellationToken);
                        return;
                    }
                }
                else
                {
                    deadSince = null;
                }

                await Task.Delay(TailInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Daemon shutdown: the agent keeps running; adoption picks this run back up.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Monitor for run {RunId} crashed", runId);
        }
        finally
        {
            _monitors.TryRemove(runId, out _);
        }
    }

    private static async Task<(long Cursor, bool SawResult, AgentResult? Result)> ReadNewLinesAsync(
        string streamFile, long cursor, StringBuilder partialLine, CancellationToken cancellationToken)
    {
        if (!File.Exists(streamFile))
        {
            return (cursor, false, null);
        }

        await using FileStream stream = new(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= cursor)
        {
            return (cursor, false, null);
        }

        stream.Seek(cursor, SeekOrigin.Begin);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        char[] buffer = new char[8192];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    string line = partialLine.ToString();
                    partialLine.Clear();
                    if (StreamJsonParser.TryParseResult(line, out AgentResult result))
                    {
                        return (stream.Position, true, result);
                    }
                }
                else
                {
                    partialLine.Append(buffer[i]);
                }
            }
        }

        return (stream.Position, false, null);
    }

    private async Task CompleteRunAsync(Guid runId, Guid taskId, AgentResult result, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();

        session.Events.Append(runId, new AgentSessionCompleted(runId, now));
        session.Events.Append(runId, new TokensRecorded(runId, result.InputTokens, result.OutputTokens, result.CostUsd, now));

        if (result.IsError)
        {
            session.Events.Append(runId, new RunFailed(runId, "Agent reported an error result.", now));
            await FailTaskInSessionAsync(session, runId, taskId, "Agent reported an error result.", now, cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Run {RunId} agent session completed ({Input}in/{Output}out tokens, error: {IsError})",
            runId, result.InputTokens, result.OutputTokens, result.IsError);

        if (!result.IsError && await verification.VerifyAsync(runId, taskId, cancellationToken))
        {
            await pullRequests.OpenAsync(runId, taskId, cancellationToken);
        }
    }

    private async Task FailRunAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new RunFailed(runId, reason, now));
        await FailTaskInSessionAsync(session, runId, taskId, reason, now, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Run {RunId} failed: {Reason}", runId, reason);
    }

    private static async Task FailTaskInSessionAsync(
        IDocumentSession session, Guid runId, Guid taskId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not null && !task.State.IsTerminal)
        {
            session.Events.Append(taskId, TaskDecider.Fail(task, runId, reason, now));
        }

        session.Delete<TaskLease>(taskId);
    }

    private async Task<long> LoadCursorAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunActivity? activity = await query.LoadAsync<RunActivity>(runId, cancellationToken);
        return activity?.StreamBytesRead ?? 0;
    }

    private async Task SaveActivityAsync(Guid runId, long cursor, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new RunActivity
        {
            Id = runId,
            LastActivityAt = DateTimeOffset.UtcNow,
            StreamBytesRead = cursor,
        });
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ResultAlreadyWrittenAsync(Guid runId, CancellationToken cancellationToken)
    {
        string streamFile = RunPaths.StreamFile(runId);
        if (!File.Exists(streamFile))
        {
            return false;
        }

        using StreamReader reader = new(new FileStream(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (StreamJsonParser.TryParseResult(line, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadStandardErrorTail(Guid runId)
    {
        try
        {
            string stderrFile = RunPaths.StandardErrorFile(runId);
            if (File.Exists(stderrFile))
            {
                string content = File.ReadAllText(stderrFile).Trim();
                if (content.IsNotBlank())
                {
                    return $"Agent process died without a result. stderr: {Truncate(content, 500)}";
                }
            }
        }
        catch (IOException)
        {
        }

        return "Agent process died without a result.";
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[^max..];
}
