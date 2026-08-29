using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Run the project's build and test gates on demand against an interactive claim's worktree, so
/// an operator can check their work before delivering it — without waiting for h9k task deliver
/// to hand off to the daemon's own pipeline. Records the outcome as the same
/// VerificationPassed/VerificationFailed gate events a headless run's own gates record, on this
/// run's stream, so h9k task show reads one history regardless of who ran the gate. Deliberately
/// simpler than the daemon's own VerificationRunner: every gate always runs at full scope, once,
/// with no infrastructure-failure retry and no dotnet-test scoping — an operator watching the
/// output can see and re-run a flake themselves, and h9k task deliver's own hand-off pays for the
/// full machinery's retry and scoping regardless, so nothing here needs to duplicate it.
/// </summary>
public sealed class TaskVerifyCommand : Hall9kAsyncCommand<TaskVerifyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        TaskDetails task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim verifies this way.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException($"Task {taskId} is claimed interactively but run {runId} has no record.");

        // An operator's own session, still attached in another terminal, is editing and possibly
        // rebuilding this same worktree right now — running gates here would collide with it in
        // shared obj/bin output exactly as the daemon's own gates and review sessions would
        // (adversarial review, cycle 1). Skipped when this invocation is that very session asking
        // for itself (the environment variable h9k task work's own launch set): it is blocked
        // waiting on this command to finish rather than racing it, so there is nothing to collide
        // with (conformance review, cycle 2).
        if (Environment.GetEnvironmentVariable(InteractiveSessionLiveness.InteractiveRunEnvironmentVariable) != runId.ToString())
        {
            InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "verify");
        }

        // Mirrors TaskWorkCommand.ReenterAsync's own guard: once h9k task deliver or handback
        // hands the run to the standard pipeline, the task can still read Claimed+interactive
        // for the whole review loop, but the worktree now belongs to the daemon's own gates and
        // review sessions — running dotnet build/test here would collide with them (adversarial
        // review, cycle 1).
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {taskId} "
                + "to see where it stands.");
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // Unlike h9k task deliver's own refusal, uncommitted files here are only reported, never
        // a hard failure: an operator mid-edit is the ordinary state of an interactive claim, not
        // an anomaly, and dotnet build/test read the working tree regardless of git status — the
        // headless pre-gate check this used to mirror exists because a dispatched session's
        // process dies at its final message and stranded files never ship, which does not apply
        // to an operator who is present and still working. h9k task deliver's own uncommitted-file
        // refusal already covers the case that matters: nothing ships with files left behind
        // (conformance review, cycle 4 — the prior hard failure made the self-invocation exemption
        // above unreachable for its own stated purpose).
        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        if (untracked.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Untracked file(s) in the worktree (not counted against the check): {string.Join(", ", untracked)}[/]");
        }

        if (modified is null)
        {
            // Never guessed at as clean (InteractiveWorktreeGit's own contract): git could not
            // be asked, so the check is honestly skipped rather than silently passed.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
        }
        else if (modified.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Modified-but-uncommitted file(s) in the worktree (gates run against them anyway): {string.Join(", ", modified)}[/]");
        }

        if (project.VerifyCommands.Count == 0)
        {
            string? headSha = await InteractiveWorktreeGit.GetHeadShaAsync(run.WorktreePath, cancellationToken);
            await RecordPassAsync(session, runId, "No verification gates configured for this project.", headSha, cancellationToken);
            AnsiConsole.MarkupLine("[green]No verification gates configured for this project — nothing to run.[/]");
            return ExitCodes.Ok;
        }

        foreach (VerifyCommand gate in project.VerifyCommands)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Running gate '{gate.Name}'...[/]");
            (bool passed, string summary) = await RunGateAsync(run.WorktreePath, gate, cancellationToken);
            if (passed)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Gate '{gate.Name}' passed.[/]");
                continue;
            }

            await RecordFailureAsync(session, runId, [gate.Name], summary, cancellationToken);
            AnsiConsole.MarkupLineInterpolated($"[red]Gate '{gate.Name}' failed:[/]");
            AnsiConsole.WriteLine(summary);
            return ExitCodes.Conflict;
        }

        string? passHeadSha = await InteractiveWorktreeGit.GetHeadShaAsync(run.WorktreePath, cancellationToken);
        await RecordPassAsync(session, runId, $"h9k task verify: {project.VerifyCommands.Count} gate(s) ran full scope.", passHeadSha, cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"[green]Verification passed ({project.VerifyCommands.Count} gate(s)).[/]");
        return ExitCodes.Ok;
    }

    private static async Task<(bool Passed, string Summary)> RunGateAsync(
        string worktreePath, VerifyCommand gate, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            WorkingDirectory = worktreePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.FileName = "cmd.exe";
            // WindowsCommandLine.WrapForCmdExe's own doc comment records why: cmd.exe's /c
            // parsing does not follow the CommandLineToArgvW convention ArgumentList assumes, so
            // a gate command carrying its own embedded quotes gets mangled unless it is wrapped
            // in one extra pair and set as the raw Arguments string exactly as
            // VerificationRunner.RunGateAsync already does for the identical cmd.exe path
            // (adversarial review, cycle 1).
            process.StartInfo.Arguments = WindowsCommandLine.WrapForCmdExe(gate.Command);
        }
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(gate.Command);
        }

        // Streamed to the console line by line as it arrives, and buffered in parallel for the
        // failure summary's own tail — buffering to completion and printing nothing until the
        // gate finishes left an operator watching a silent terminal for however long `dotnet
        // test` takes, with no way to tell "nothing is happening yet" from "it hung", and no
        // output at all on a passing run (conformance review, cycle 1: this type's own doc
        // comment promises "an operator watching the output can see and re-run a flake
        // themselves", which buffering to completion cannot deliver). stdout and stderr each
        // fire on their own thread-pool thread, so both the console write and the shared
        // builder are guarded by one lock.
        StringBuilder output = new();
        object outputLock = new();
        void OnOutputReceived(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (outputLock)
            {
                Console.WriteLine(e.Data);
                output.AppendLine(e.Data);
            }
        }

        process.OutputDataReceived += OnOutputReceived;
        process.ErrorDataReceived += OnOutputReceived;

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            // The worktree can vanish between h9k task work claiming it and this gate running
            // (deleted by hand, or pruned) — TaskWorkCommand.ReenterAsync guards this exact state
            // explicitly, and this is the one command in the interactive surface that would
            // otherwise crash on it with a raw stack trace instead of the domain-shaped failure
            // every other gate outcome here already returns (adversarial review, cycle 8).
            return (false, $"Gate '{gate.Name}' could not start: {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Mirrors VerificationRunner.RunGateAsync's own kill-on-cancel and timeout: an operator's
        // own Ctrl-C, or a gate that simply hangs, must not leave it writing into the claim's
        // worktree after this command has already walked away from it, and must not block the
        // command indefinitely either (adversarial review, cycle 1). 15 minutes mirrors
        // DaemonOptions.VerifyGateTimeout's own default — the CLI cannot reference that type
        // (Reference graph: Cli -> Domain + Connectors), so there is no per-project override here.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between the check and the kill — nothing left to do.
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (false, $"Gate '{gate.Name}' exceeded its 15-minute timeout.");
        }

        string tail;
        lock (outputLock)
        {
            tail = Tail(output.ToString());
        }

        return process.ExitCode == 0
            ? (true, "ok")
            : (false, $"Gate '{gate.Name}' exited {process.ExitCode}. Output: {tail}");
    }

    private static string Tail(string content) =>
        content.Length <= 4000 ? content : content[^4000..];

    private static async Task RecordPassAsync(
        IDocumentSession session, Guid runId, string? note, string? headSha, CancellationToken cancellationToken)
    {
        session.Events.Append(runId, new VerificationPassed(runId, DateTimeOffset.UtcNow, note, RanFullScope: true, headSha));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task RecordFailureAsync(
        IDocumentSession session, Guid runId, IReadOnlyList<string> failedGates, string reason, CancellationToken cancellationToken)
    {
        session.Events.Append(runId, new VerificationFailed(runId, failedGates, DateTimeOffset.UtcNow, Note: reason));
        await session.SaveChangesAsync(cancellationToken);
    }
}
