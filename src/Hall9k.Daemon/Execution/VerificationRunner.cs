using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Deterministic gates only (PLAN.md §6.5): the project's verify commands run sequentially
/// in the run's worktree; first failure stops the line. The reviewer agent is Slice 3.
/// </summary>
public sealed class VerificationRunner(
    IDocumentStore store,
    IOptions<DaemonOptions> options,
    ILogger<VerificationRunner> logger)
{
    public async Task<bool> VerifyAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await query.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);

        if (run is null || task is null)
        {
            logger.LogError("Cannot verify run {RunId}: run or task missing", runId);
            return false;
        }

        IReadOnlyList<VerifyCommand> gates = project?.VerifyCommands ?? [];
        if (gates.Count == 0)
        {
            await RecordPassAsync(runId, "No verification gates configured for this project.", cancellationToken);
            logger.LogInformation("Run {RunId} verification passed: no gates configured", runId);
            return true;
        }

        foreach (VerifyCommand gate in gates)
        {
            (bool passed, string summary) = await RunGateAsync(runId, run.WorktreePath, gate, cancellationToken);
            if (!passed)
            {
                await RecordFailureAsync(runId, taskId, gate.Name, summary, cancellationToken);
                logger.LogWarning("Run {RunId} verification failed at gate '{Gate}': {Summary}", runId, gate.Name, summary);
                return false;
            }

            logger.LogInformation("Run {RunId} gate '{Gate}' passed", runId, gate.Name);
        }

        await RecordPassAsync(runId, note: null, cancellationToken);
        logger.LogInformation("Run {RunId} verification passed ({Count} gate(s))", runId, gates.Count);
        return true;
    }

    private async Task<(bool Passed, string Summary)> RunGateAsync(
        Guid runId, string worktreePath, VerifyCommand gate, CancellationToken cancellationToken)
    {
        string logFile = Path.Combine(RunPaths.RunDirectory(runId), $"verify-{Sanitize(gate.Name)}.log");
        Directory.CreateDirectory(RunPaths.RunDirectory(runId));

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"({gate.Command}) > \"{logFile}\" 2>&1");

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, $"Gate '{gate.Name}' could not start: {exception.Message}");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.VerifyGateTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (false, $"Gate '{gate.Name}' exceeded the {options.Value.VerifyGateTimeout.TotalMinutes:0}-minute timeout.");
        }

        return process.ExitCode == 0
            ? (true, "ok")
            : (false, $"Gate '{gate.Name}' exited {process.ExitCode}. Output: {TailOf(logFile)}");
    }

    private async Task RecordPassAsync(Guid runId, string? note, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationPassed(runId, DateTimeOffset.UtcNow, note));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(
        Guid runId, Guid taskId, string failedGate, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationFailed(runId, [failedGate], now));
        session.Events.Append(runId, new Domain.Features.Run.Events.RunFailed(runId, reason, now));

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is not null && TaskDecider.CanFail(task))
        {
            session.Events.Append(taskId, TaskDecider.Fail(task, runId, $"Verification failed: {reason}", now));
        }

        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static string Sanitize(string name) =>
        new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    private static string TailOf(string logFile)
    {
        try
        {
            string content = File.Exists(logFile) ? File.ReadAllText(logFile).Trim() : string.Empty;
            return content.IsBlank() ? "(empty)" : content.Length <= 400 ? content : content[^400..];
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
    }
}
