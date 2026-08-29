using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The operator's interactive claim (PLAN.md, an operator can work a task interactively): on a
/// Queued task, claims it exactly as headless dispatch would (same branch, same worktree, same
/// prompt and packet context — <see cref="WorkPromptBuilder"/> is the code both paths call), then
/// launches a regular interactive Claude Code session attached to this terminal. The claim is
/// held by the human, not a process: no <c>TaskLease</c> is written, so there is nothing for a
/// heartbeat to renew or an expiry sweep to reclaim, and closing the terminal is a normal way to
/// leave — the task stays Claimed and re-running this command re-enters the same worktree and
/// branch with a fresh session. An interactive claim occupies zero concurrency slots: it never
/// creates a node-owned run (RunDispatched records NodeId as the sentinel <see cref="Guid.Empty"/>,
/// which <c>NodeLoad</c>'s ceiling measurement never counts), so it starts even when the daemon's
/// session ceiling is fully consumed and never competes with headless dispatch throughput.
/// </summary>
public sealed class TaskWorkCommand : Hall9kAsyncCommand<TaskWorkCommand.Settings>
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
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        (Guid runId, string worktreePath, string branch) = task.State == TaskState.Claimed && task.IsInteractiveClaim
            ? await ReenterAsync(session, task, cancellationToken)
            : await ClaimAndCutAsync(session, task, fence, context, cancellationToken);

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // No blocker context: BlockerContextAssembler lives in Hall9k.Daemon (it can spawn a
        // synthesis session) and the CLI cannot reference it. An operator sitting at the
        // keyboard can read a blocker's handoff themselves with h9k task show; the same prompt
        // and packet context otherwise reaches this session verbatim, through the shared
        // WorkPromptBuilder headless dispatch calls too.
        string prompt = WorkPromptBuilder.Build(taskDetails, project, branch, worktreePath, resumesPreviousWork: false, blockerContext: null);

        Guid claudeSessionId = DomainId.New();
        await using (IDocumentSession startSession = store.LightweightSession())
        {
            startSession.Events.Append(runId, new InteractiveSessionStarted(runId, claudeSessionId, DateTimeOffset.UtcNow));
            await startSession.SaveChangesAsync(cancellationToken);
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {worktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {branch}[/]");
        AnsiConsole.MarkupLine("[dim]Launching an interactive Claude Code session — exit it normally (Ctrl+D or /exit) to return here.[/]");

        int exitCode = await LaunchInteractiveClaudeAsync(worktreePath, prompt, claudeSessionId, project.SkipPermissions, cancellationToken);

        await using (IDocumentSession endSession = store.LightweightSession())
        {
            // Attached to the operator's terminal, not driven headlessly through
            // --output-format stream-json — there is no result payload to read usage off, so
            // every field is honestly null (the nullable-Turns convention).
            endSession.Events.Append(runId, new InteractiveSessionEnded(
                runId, claudeSessionId, DateTimeOffset.UtcNow, Turns: null, InputTokens: null, OutputTokens: null, CostUsd: null));
            await endSession.SaveChangesAsync(cancellationToken);
        }

        AnsiConsole.MarkupLineInterpolated(exitCode == 0
            ? (FormattableString)$"[dim]Session ended (exit {exitCode}). Task {taskId} is still claimed —[/]"
            : $"[yellow]Session ended with exit code {exitCode}. Task {taskId} is still claimed —[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task deliver {taskId}    push and hand into the standard delivery pipeline[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task verify {taskId}     run the project's gates on demand[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task work {taskId}       resume this worktree with a fresh session[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task handback {taskId}   let a headless agent finish from here[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task release {taskId}    give it back to the dispatch queue[/]");
        return ExitCodes.Ok;
    }

    private static async Task<(Guid RunId, string WorktreePath, string Branch)> ReenterAsync(
        IDocumentSession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {task.Id} reads as interactively claimed but carries no current run — this needs a human look.");
        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {task.Id} is claimed interactively but run {runId} has no record — this needs a human look.");

        if (!Directory.Exists(run.WorktreePath))
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s worktree {run.WorktreePath} no longer exists on disk. "
                + $"h9k task release {task.Id} to put it back in the queue, or investigate by hand.");
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]Re-entering task {task.Id}'s interactive claim.[/]");
        return (runId, run.WorktreePath, run.Branch);
    }

    private static async Task<(Guid RunId, string WorktreePath, string Branch)> ClaimAndCutAsync(
        IDocumentSession session, TaskAggregate task, StreamState fence, BootstrapContext context, CancellationToken cancellationToken)
    {
        if (task.State != TaskState.Queued)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a Queued task (or one you already hold "
                + "interactively) can be worked this way. " + task.State switch
                {
                    var state when state == TaskState.Blocked =>
                        "It is assigned but waiting on a dependency; h9k task show names it.",
                    var state when state.IsPreDispatch =>
                        $"Assign it first: h9k task assign {task.Id}.",
                    var state when state == TaskState.Claimed =>
                        "It is claimed by a node running headless work already.",
                    _ => "Its story has already moved past dispatch.",
                });
        }

        if (task.AssignedOwnerId != context.OwnerId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is assigned to {task.AssignedOwnerId} — an operator claims only their own owner's work.");
        }

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {task.Id}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {task.Id}'s project no longer exists.");

        Guid runId = DomainId.New();

        // Cut before committing the claim (mirrors RunLauncher: the worktree exists first, the
        // record follows). If this throws, the task stays Queued — nothing was appended yet.
        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        Worktree worktree = await worktrees.CreateAsync(
            new WorktreeRequest(project.RepositoryPath, project.BaseBranch, task.Id, runId, taskDetails.Objective),
            cancellationToken);

        string? existingTaskDirectory = project.HomeDirectory.HasValue
            ? HomeEntryLookup.FindExisting(ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), task.Id)
                ?? HomeEntryLookup.FindExisting(ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value), task.Id)
            : null;
        string runDirectory = existingTaskDirectory is not null
            ? RunPaths.ResolveDirectoryUnderTaskDirectory(existingTaskDirectory, runId)
            : RunPaths.ResolveDirectory(project.HomeDirectory, TaskDocumentRenderer.DirectoryName(taskDetails), runId);

        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, context.OwnerId, runId, DateTimeOffset.UtcNow);
        session.Events.Append(task.Id, expectedVersion: fence.Version + 1, claimed);
        // Deliberately no TaskLease: the claim is held by the human, not a process — no
        // liveness lease, no heartbeat reclaim (AGENTS.md).

        // Fable is the human-interactive model tier (AgentModel's own doc comment, Decisions
        // Log #33) — a fixed platform choice for an operator-attended session, not the
        // project/task role-resolution chain a headless build session runs through.
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, task.Id, Guid.Empty, context.OwnerId, claimed.LeaseGeneration, runId,
            worktree.Path, worktree.Branch, ExecutorMode.Subscription, DateTimeOffset.UtcNow,
            IsFollowUp: false, Model: AgentModel.Fable, RunDirectory: runDirectory));

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {task.Id} changed while claiming it — check h9k status and try again.");
        }

        await Doorbell.RingAsync($"task-claimed-interactively:{task.Id}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"[dim]Claimed task {task.Id} interactively.[/]");
        return (runId, worktree.Path, worktree.Branch);
    }

    private static async Task<int> LaunchInteractiveClaudeAsync(
        string worktreePath, string prompt, Guid sessionId, bool skipPermissions, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ClaudeBinary(),
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("--session-id");
        process.StartInfo.ArgumentList.Add(sessionId.ToString());
        process.StartInfo.ArgumentList.Add("--model");
        process.StartInfo.ArgumentList.Add(AgentModel.Fable.Value);
        if (skipPermissions)
        {
            process.StartInfo.ArgumentList.Add("--dangerously-skip-permissions");
        }

        // A positional argument, passed through ArgumentList rather than a shell string: no
        // shell escaping, so the prompt's own quotes and newlines travel to the child exactly
        // as written. Claude Code starts interactively (no -p) with this as the opening message.
        process.StartInfo.ArgumentList.Add(prompt);

        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";
}
