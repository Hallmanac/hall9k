using Hall9k.Domain.Infrastructure.Storage;
using System.Diagnostics;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Deterministic gates only (PLAN.md §6.5): the project's verify commands run sequentially
/// in the run's worktree; first failure stops the line. Before any gate, a no-commit
/// check fails runs whose branch carries nothing — gates on an unmodified tree pass
/// vacuously and prove nothing (Research tasks exempt; their deliverable is the
/// transcript). The reviewer agent is Slice 3.
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

        // Fail fast on an agent that committed nothing, before any gate runs (origin
        // incident: task 08's agent completed all its work uncommitted; gates passed
        // vacuously on the unmodified tree and the failure surfaced two stages late as
        // "No commits between main and branch" at PR creation). Research tasks are
        // exempt — their deliverable is the transcript, not commits (the one TaskType
        // whose legitimate output is empty); every other type ships its work as commits.
        if (project is not null && task.Type != TaskType.Research)
        {
            int? commits = await CountBranchCommitsAsync(run.WorktreePath, project.BaseBranch, cancellationToken);
            if (commits == 0)
            {
                string reason =
                    $"Agent produced no commits: branch '{run.Branch}' holds nothing beyond " +
                    $"'{project.BaseBranch}'. The session ended without committing its work, so the " +
                    "gates were not run against the unmodified tree.";
                await FailBeforeGatesAsync(runId, taskId, reason, cancellationToken);
                logger.LogWarning("Run {RunId} failed before the gates: {Reason}", runId, reason);
                return false;
            }

            if (commits is null)
            {
                // Git is unobservable here (not a repo, unknown base ref); never guess —
                // proceed and let the gates surface whatever is actually broken.
                logger.LogWarning(
                    "Run {RunId}: could not count commits on branch {Branch} against {BaseBranch}; skipping the no-commit check",
                    runId, run.Branch, project.BaseBranch);
            }
        }

        IReadOnlyList<VerifyCommand> gates = project?.VerifyCommands ?? [];
        if (gates.Count == 0)
        {
            await RecordPassAsync(runId, "No verification gates configured for this project.", cancellationToken);
            logger.LogInformation("Run {RunId} verification passed: no gates configured", runId);
            return true;
        }

        // run.RunDirectory is whatever RunDispatched recorded once, at dispatch — stale for a
        // run whose task has since crossed the tasks/_archive/ boundary (backlog 51 cycle 6):
        // gates run well after the agent's own session ends, so the render sweep has had a
        // chance to move the task's directory by the time this reads it. Resolved once, here,
        // rather than at each gate: every reader of a recorded RunDirectory funnels through
        // RunPaths.ResolveCurrentDirectory (PLAN.md §16 #84).
        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);

        foreach (VerifyCommand gate in gates)
        {
            (bool passed, string summary, bool isInfrastructureFailure, string? excerpt) =
                await RunGateAsync(runDirectory, run.WorktreePath, gate, cancellationToken);
            if (passed)
            {
                logger.LogInformation("Run {RunId} gate '{Gate}' passed", runId, gate.Name);
                continue;
            }

            // A gate this run already retried once, in a prior daemon lifetime, never earns a
            // second one on adoption: the retry budget otherwise lives only in this method's
            // local state, so a daemon that died between the GateRetried commit below and this
            // gate's resolution would resume with no memory of the retry already spent
            // (backlog 53, Copilot review on PR #36 — RunSupervisor.AdoptOrphansAsync calls
            // VerifyAsync fresh). run.PendingGateRetry is the persisted record of that spend.
            if (isInfrastructureFailure && run.PendingGateRetry == gate.Name)
            {
                string adoptedReason =
                    $"Gate '{gate.Name}' failed again with an infrastructure-classified signature " +
                    $"after already spending its one retry before an earlier daemon restart. {summary}";
                await RecordFailureAsync(runId, taskId, gate.Name, adoptedReason, cancellationToken);
                logger.LogWarning(
                    "Run {RunId} verification failed at gate '{Gate}': its one retry was already spent before adoption",
                    runId, gate.Name);
                return false;
            }

            if (!isInfrastructureFailure)
            {
                await RecordFailureAsync(runId, taskId, gate.Name, summary, cancellationToken);
                logger.LogWarning("Run {RunId} verification failed at gate '{Gate}': {Summary}", runId, gate.Name, summary);
                return false;
            }

            // Infrastructure-classified: retry once, in place, before believing the agent's
            // work is broken (backlog 53's origin incident — the container, not the diff, was
            // what failed). Recorded on the stream so the record says the flake happened,
            // whichever way the retry goes, and never fails the run or spends any budget. The
            // cause carries the matching excerpt, not just the recorded summary's tail: a
            // large gate's summary is truncated to its last 400 characters and can push the
            // marker that actually triggered classification out of it (adversarial review).
            await RecordGateRetryAsync(runId, gate.Name, BuildRetryCause(summary, excerpt), cancellationToken);
            logger.LogWarning(
                "Run {RunId} gate '{Gate}' failed with an infrastructure-classified signature; retrying once: {Summary}",
                runId, gate.Name, summary);

            (bool retryPassed, string retrySummary, bool retryIsInfrastructureFailure, _) =
                await RunGateAsync(runDirectory, run.WorktreePath, gate, cancellationToken);
            if (retryPassed)
            {
                logger.LogInformation("Run {RunId} gate '{Gate}' passed on retry", runId, gate.Name);
                continue;
            }

            // A second consecutive infrastructure failure fails the run honestly with the
            // classification in the reason, so a genuinely broken environment surfaces instead
            // of looping. A retry that instead surfaces a real failure is recorded as exactly
            // that, unclassified — the retry earned it a second look, not a second pass.
            string reason = retryIsInfrastructureFailure
                ? $"Gate '{gate.Name}' failed twice in a row with an infrastructure-classified " +
                  $"signature (connection-class failure, not the agent's work). First attempt: " +
                  $"{summary} Retry attempt: {retrySummary}"
                : retrySummary;

            await RecordFailureAsync(runId, taskId, gate.Name, reason, cancellationToken);
            logger.LogWarning("Run {RunId} verification failed at gate '{Gate}' after retry: {Summary}", runId, gate.Name, reason);
            return false;
        }

        await RecordPassAsync(runId, note: null, cancellationToken);
        logger.LogInformation("Run {RunId} verification passed ({Count} gate(s))", runId, gates.Count);
        return true;
    }

    private async Task<(bool Passed, string Summary, bool IsInfrastructureFailure, string? InfrastructureExcerpt)> RunGateAsync(
        string runDirectory, string worktreePath, VerifyCommand gate, CancellationToken cancellationToken)
    {
        string logFile = Path.Combine(runDirectory, $"verify-{Sanitize(gate.Name)}.log");
        Directory.CreateDirectory(runDirectory);

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
            string startFailure = $"Gate '{gate.Name}' could not start: {exception.Message}";
            return (false, startFailure,
                GateInfrastructureFailureClassifier.IsInfrastructureFailure(startFailure),
                GateInfrastructureFailureClassifier.MatchingExcerpt(startFailure));
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
            string timeoutFailure = $"Gate '{gate.Name}' exceeded the {options.Value.VerifyGateTimeout.TotalMinutes:0}-minute timeout.";

            // A hang classifies on what the gate actually wrote before it was killed, not on
            // this synthetic message — the message never carries a marker, so a container that
            // never comes up (a startup hang, not a non-zero exit) would otherwise be silently
            // unclassifiable and blamed on the agent's work (adversarial review, cycle 2).
            string timeoutOutput = ReadFullOutput(logFile);
            bool timeoutIsInfrastructureFailure = GateInfrastructureFailureClassifier.IsInfrastructureFailure(timeoutOutput);
            return (false, timeoutFailure, timeoutIsInfrastructureFailure,
                GateInfrastructureFailureClassifier.MatchingExcerpt(timeoutOutput));
        }

        if (process.ExitCode == 0)
        {
            return (true, "ok", false, null);
        }

        // Classification reads the gate's whole output, never just the truncated tail kept
        // for the summary: a marker logged early in a large `dotnet test` run must not be
        // pushed out of a fixed-size window and go unclassified (adversarial review, cycle 1).
        string fullOutput = ReadFullOutput(logFile);
        bool isInfrastructureFailure = GateInfrastructureFailureClassifier.IsInfrastructureFailure(fullOutput);
        string summary = $"Gate '{gate.Name}' exited {process.ExitCode}. Output: {TailOf(fullOutput)}";
        return (false, summary, isInfrastructureFailure, GateInfrastructureFailureClassifier.MatchingExcerpt(fullOutput));
    }

    /// <summary>
    /// Commits the branch carries beyond the base (remote-tracking ref preferred, local
    /// base as the no-origin fallback — the log #4 convention). Null when git cannot
    /// answer: an unobservable count is never treated as zero.
    /// </summary>
    private static async Task<int?> CountBranchCommitsAsync(
        string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        foreach (string baseRef in new[] { $"origin/{baseBranch}", baseBranch })
        {
            (int exitCode, string output) = await RunGitAsync(
                worktreePath, ["rev-list", "--count", $"{baseRef}..HEAD"], cancellationToken);
            if (exitCode == 0 && int.TryParse(output.Trim(), out int count))
            {
                return count;
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string StandardOutput)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output);
    }

    private async Task FailBeforeGatesAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new Domain.Features.Run.Events.RunFailed(runId, reason, now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // that AllowsAsync never gets asked about (adversarial review, cycle 2).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && !current.Task.State.IsTerminal
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            // One transaction with the RunFailed append above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with it on purpose — see
            // RunSupervisor.AppendFencedTaskFailureAsync): a lost race here rolling back
            // the run's own failure fact too is a smaller cost than a reader observing the
            // run Failed while its task still reads Claimed.
            session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(current.Task, runId, reason, now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a pre-gate failure for run {RunId} — a newer claim committed first",
                taskId, runId);
        }
    }

    private async Task RecordPassAsync(Guid runId, string? note, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationPassed(runId, DateTimeOffset.UtcNow, note));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordGateRetryAsync(Guid runId, string gate, string cause, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new GateRetried(runId, gate, cause, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordFailureAsync(
        Guid runId, Guid taskId, string failedGate, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(runId, new VerificationFailed(runId, [failedGate], now));
        session.Events.Append(runId, new Domain.Features.Run.Events.RunFailed(runId, reason, now));

        // LoadFencedAsync's read must happen before the AllowsAsync identity check below —
        // not after — so a reclaim landing between the two is caught by AllowsAsync's fresh
        // read rather than baked into `current.Task` as an already-stale ownership fact
        // that AllowsAsync never gets asked about (adversarial review, cycle 2).
        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (fenced is { } current
            && TaskDecider.CanFail(current.Task)
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            // One transaction with the run-stream events above (Copilot review, PR #30's
            // expectedVersion fix, kept atomic with them on purpose — see
            // RunSupervisor.AppendFencedTaskFailureAsync): a lost race here rolling back
            // the run's own failure facts too is a smaller cost than a reader observing
            // the run Failed while its task still reads Claimed.
            session.Events.Append(
                taskId, expectedVersion: current.Version + 1,
                TaskDecider.Fail(current.Task, runId, $"Verification failed: {reason}", now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a verification failure for run {RunId} — a newer claim committed first",
                taskId, runId);
        }
    }

    private static string Sanitize(string name) =>
        new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    private static string ReadFullOutput(string logFile)
    {
        try
        {
            return File.Exists(logFile) ? File.ReadAllText(logFile).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
    }

    private static string TailOf(string content) =>
        content.IsBlank() ? "(empty)" : content.Length <= 400 ? content : content[^400..];

    /// <summary>
    /// The recorded summary plus the excerpt around the marker that actually classified the
    /// gate as infrastructure, so the durable <see cref="GateRetried"/> event still explains
    /// the classification even when that marker sits outside the summary's 400-character tail
    /// (adversarial review, PR #36's Copilot review).
    /// </summary>
    private static string BuildRetryCause(string summary, string? matchingExcerpt) =>
        matchingExcerpt is null ? summary : $"{summary} Matching signature: {matchingExcerpt}";
}
