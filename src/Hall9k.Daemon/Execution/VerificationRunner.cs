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
/// in the run's worktree; first failure stops the line. Before any gate, two pre-gate checks
/// fail a run honestly rather than let the gates run against a tree the pull request will
/// never actually carry: a no-commit check for a branch that carries nothing at all (Research
/// tasks exempt; their deliverable is the transcript), and an uncommitted-files check
/// (backlog 57, not exempt for any task type) for a session that ended with
/// modified-but-uncommitted files still sitting in the worktree — a failure that names every
/// file left behind so a human or a retry session finds the work instead of losing it. The
/// reviewer agent is Slice 3.
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

        // Fail fast on an agent that left work behind uncommitted, before any gate runs
        // against a tree the pull request will never actually carry (origin incident:
        // task 08's agent completed all its work uncommitted; gates passed vacuously on
        // the unmodified tree and the failure surfaced two stages late as "No commits
        // between main and branch" at PR creation). Two distinct shapes of that same
        // failure, checked separately because they need different words (backlog 57):
        // zero commits at all, and — the shape the zero-commit check alone always missed —
        // some commits landed but the session still ended with modified-but-uncommitted
        // files sitting in the worktree (origin incidents, both 2026-08-26: the PR #53
        // follow-up's cycle-3 fix round left eight files uncommitted, caught only by the
        // next review pass; task df277369 failed twice backgrounding its own test suite and
        // ending the session before it finished). Either way the reason names the files, so
        // a human or a retry session finds the finished work instead of rediscovering it.
        if (project is not null)
        {
            (IReadOnlyList<string>? uncommittedFiles, IReadOnlyList<string> untrackedFiles) =
                await ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);

            if (uncommittedFiles is null)
            {
                // Git is unobservable here (not a repo, permission denied, `git` missing from
                // PATH); never guess — proceed and let the gates surface whatever is actually
                // broken, but say so, the same as the no-commit check's own unobservable case
                // below: an unlogged skip here would leave an operator with no record of why a
                // session's stranded work went uncaught.
                logger.LogWarning(
                    "Run {RunId}: could not read the worktree's status at {WorktreePath}; skipping the uncommitted-files check",
                    runId, run.WorktreePath);
            }

            if (untrackedFiles.Count > 0)
            {
                // A softer signal than a failure (conformance review): an untracked file is
                // usually a gate byproduct the project's .gitignore has not caught up with, but
                // it can just as easily be a brand-new source file the session forgot to `git
                // add` — the prompt rule promises the platform names "anything left modified but
                // uncommitted", so silence here would be a real gap. This never fails the run;
                // failing it would strand a retry in the same unrecoverable loop the -uno
                // exclusion above exists to avoid.
                logger.LogWarning(
                    "Run {RunId}: the worktree at {WorktreePath} has untracked file(s) not counted against the " +
                    "uncommitted-files check: {Files}",
                    runId, run.WorktreePath, SummarizeFiles(untrackedFiles));
            }

            // Research tasks are exempt from the no-commit check — their deliverable is the
            // transcript, not commits (the one TaskType whose legitimate output is empty);
            // every other type ships its work as commits. The uncommitted-files check right
            // below is not exempt: a research task that left modified files behind still
            // stranded work, whatever its deliverable is.
            if (task.Type != TaskType.Research)
            {
                int? commits = await CountBranchCommitsAsync(run.WorktreePath, project.BaseBranch, cancellationToken);
                if (commits == 0)
                {
                    string reason = uncommittedFiles is { Count: > 0 }
                        ? $"Agent produced no commits: branch '{run.Branch}' holds nothing beyond " +
                          $"'{project.BaseBranch}'. The session ended with modified-but-uncommitted files " +
                          $"still sitting in the worktree instead of being committed: " +
                          $"{SummarizeFiles(uncommittedFiles)}."
                        : $"Agent produced no commits: branch '{run.Branch}' holds nothing beyond " +
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

            if (uncommittedFiles is { Count: > 0 })
            {
                string reason =
                    "The session ended with modified-but-uncommitted files still sitting in the worktree: " +
                    $"{SummarizeFiles(uncommittedFiles)}. Finished work left uncommitted never reaches " +
                    "the pull request, so the gates were not run until it is committed.";
                await FailBeforeGatesAsync(runId, taskId, reason, cancellationToken);
                logger.LogWarning("Run {RunId} failed before the gates: {Reason}", runId, reason);
                return false;
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

        string innerCommand = $"({gate.Command}) > \"{logFile}\" 2>&1";

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows())
        {
            // The raw Arguments string, never ArgumentList (see WindowsCommandLine): a
            // project's verify command is entirely capable of carrying its own embedded
            // quotes (this repo's own CI filter, `--filter "Category!=RequiresDocker"`,
            // is exactly that shape), and ArgumentList would C-runtime-escape them in a
            // way cmd.exe's own /c parsing does not undo.
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = WindowsCommandLine.WrapForCmdExe(innerCommand);
        }
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(innerCommand);
        }

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

    /// <summary>
    /// Every tracked file the worktree holds modified or staged at session end (backlog 57),
    /// plus, separately, every untracked one — `git status --porcelain -z`, NUL-separated
    /// rather than the default newline-and-quote form so a path holding a space or a non-ASCII
    /// character (`core.quotePath`'s octal-escaping) comes back verbatim instead of quoted
    /// (conformance review finding). A rename or copy entry emits the new path first and the
    /// old path as a second NUL-terminated field with no ` -&gt; ` marker; the old path is
    /// consumed and discarded, since the new path is the one that still exists. Untracked
    /// files are reported separately rather than folded into the failing list: a gate's own
    /// build or test output (a coverage report, `TestResults/`, a lint cache) the project's
    /// `.gitignore` does not yet name is not stranded agent work, and failing a run on it is a
    /// defect a retry can never clear, since the next session's gates regenerate the same file
    /// (adversarial review, independent pre-PR review cycle 1). The modified-list slot is null
    /// when git cannot answer, the same "never guess" convention
    /// <see cref="CountBranchCommitsAsync"/> already follows: an unobservable worktree is never
    /// reported as clean.
    /// </summary>
    private static async Task<(IReadOnlyList<string>? Modified, IReadOnlyList<string> Untracked)>
        ListUncommittedFilesAsync(string worktreePath, CancellationToken cancellationToken)
    {
        (int exitCode, string output) =
            await RunGitAsync(worktreePath, ["status", "--porcelain", "-z"], cancellationToken);
        if (exitCode != 0)
        {
            return (null, []);
        }

        List<string> modified = [];
        List<string> untracked = [];
        string[] entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            char indexStatus = entry[0];
            char worktreeStatus = entry[1];
            string path = entry[3..];

            if (indexStatus is 'R' or 'C' || worktreeStatus is 'R' or 'C')
            {
                // The old path is the next NUL-terminated field; it no longer exists, so it is
                // consumed here and never added to either list.
                i++;
            }

            if (indexStatus == '?' && worktreeStatus == '?')
            {
                untracked.Add(path);
            }
            else
            {
                modified.Add(path);
            }
        }

        return (modified, untracked);
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
    /// A file list for a one-line failure reason (backlog 57): capped the same way
    /// <see cref="TailOf"/> caps gate output, so a wide-rewrite session's file list cannot
    /// blow out the attention pane's one-line cause (<c>AttentionComposer.FailureCause</c>,
    /// `h9k status`) into dozens of wrapped terminal lines (conformance review finding).
    /// </summary>
    private const int MaxListedFiles = 20;

    private static string SummarizeFiles(IReadOnlyList<string> files) =>
        files.Count <= MaxListedFiles
            ? string.Join(", ", files)
            : $"{string.Join(", ", files.Take(MaxListedFiles))}, and {files.Count - MaxListedFiles} more";

    /// <summary>
    /// The recorded summary plus the excerpt around the marker that actually classified the
    /// gate as infrastructure, so the durable <see cref="GateRetried"/> event still explains
    /// the classification even when that marker sits outside the summary's 400-character tail
    /// (adversarial review, PR #36's Copilot review).
    /// </summary>
    private static string BuildRetryCause(string summary, string? matchingExcerpt) =>
        matchingExcerpt is null ? summary : $"{summary} Matching signature: {matchingExcerpt}";
}
