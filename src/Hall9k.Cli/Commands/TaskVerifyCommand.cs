using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
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
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        if (untracked.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Untracked file(s) in the worktree (not counted against the check): {string.Join(", ", untracked)}[/]");
        }

        if (modified is { Count: > 0 })
        {
            string reason = $"The worktree has modified-but-uncommitted file(s): {string.Join(", ", modified)}.";
            await RecordFailureAsync(session, runId, [.. modified], reason, cancellationToken);
            AnsiConsole.MarkupLineInterpolated($"[red]Verification failed before any gate: {reason}[/]");
            return ExitCodes.Conflict;
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
            process.StartInfo.Arguments = $"/c {gate.Command}";
        }
        else
        {
            process.StartInfo.FileName = "/bin/sh";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(gate.Command);
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await standardOutput + await standardError;

        return process.ExitCode == 0
            ? (true, "ok")
            : (false, $"Gate '{gate.Name}' exited {process.ExitCode}. Output: {Tail(output)}");
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
        session.Events.Append(runId, new VerificationFailed(runId, failedGates, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }
}
