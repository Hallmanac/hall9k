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
using Hall9k.Domain.Features.Tasks.Queries;
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

        // Minted once per invocation and carried through: the first claim records it as
        // RunDispatched.SessionId — the same "session id is the first spawned session's own
        // id" convention headless dispatch's RunLauncher follows — and every re-entry records
        // its own fresh session under InteractiveSessionStarted regardless.
        Guid claudeSessionId = DomainId.New();

        (Guid runId, string worktreePath, string branch, string runDirectory, bool resumesPreviousWork) = task.State == TaskState.Claimed && task.IsInteractiveClaim
            ? await ReenterAsync(session, task, cancellationToken)
            : await ClaimAndCutAsync(session, task, fence, context, claudeSessionId, cancellationToken);

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // The raw document rather than BlockerContextAssembler's own call: that type lives in
        // Hall9k.Daemon (it can spawn a synthesis session for a wide fan-in) and the CLI cannot
        // reference it. BlockerContextDocument and BlockerHandoffQuery are the shared renderer
        // and depth-one reader both surfaces already agree on — h9k task show pastes the same
        // unsynthesized document into its own "Starting context" screen (Decisions Log #36) —
        // so an operator's session gets the real context rather than none at all.
        string? blockerContext = await LoadBlockerContextAsync(session, taskDetails, cancellationToken);
        string prompt = WorkPromptBuilder.Build(taskDetails, project, branch, worktreePath, resumesPreviousWork, blockerContext);

        // The same settings file every headless spawn writes (ClaudeExecutor), so the one
        // platform-imposed override — no co-authored-by trailers (PLAN.md §6.6) — applies to
        // an operator's commits exactly as it does an agent's.
        string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(runDirectory);
        Directory.CreateDirectory(resolvedRunDirectory);
        string settingsFile = RunPaths.SettingsFile(resolvedRunDirectory);
        await File.WriteAllTextAsync(settingsFile, ClaudeSettingsFile.Content, cancellationToken);

        await using (IDocumentSession startSession = store.LightweightSession())
        {
            startSession.Events.Append(runId, new InteractiveSessionStarted(runId, claudeSessionId, DateTimeOffset.UtcNow));
            await startSession.SaveChangesAsync(cancellationToken);
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {worktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {branch}[/]");
        AnsiConsole.MarkupLine("[dim]Launching an interactive Claude Code session — exit it normally (Ctrl+D or /exit) to return here.[/]");

        int exitCode;
        try
        {
            exitCode = await LaunchInteractiveClaudeAsync(worktreePath, prompt, claudeSessionId, settingsFile, project.SkipPermissions, cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The claude binary is missing, or the worktree directory vanished between the
            // claim and the launch: the started/ended pair still has to close (the run stream
            // otherwise reads a session that never ended), and DomainConflictException is what
            // Program.cs maps to a readable message instead of a raw stack trace.
            await AppendSessionEndedAsync(store, runId, claudeSessionId, cancellationToken);
            throw new DomainConflictException(
                $"Could not launch the interactive Claude Code session for task {taskId}: {exception.Message} "
                + $"The claim is preserved — h9k task work {taskId} to try again, or h9k task release {taskId} to give it back.");
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C: the started/ended pair still has to close (PLAN.md #99, "paired every
            // launch") even though the command's own token is the one that just fired — a
            // cancelled token fails any further call made with it, so this cleanup runs
            // unconditionally rather than cooperatively (conformance review, cycle 1).
            await AppendSessionEndedAsync(store, runId, claudeSessionId, CancellationToken.None);
            throw;
        }

        await AppendSessionEndedAsync(store, runId, claudeSessionId, cancellationToken);

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

    private static async Task<string?> LoadBlockerContextAsync(
        IDocumentSession session, TaskDetails taskDetails, CancellationToken cancellationToken)
    {
        if (taskDetails.BlockedBy.Count == 0)
        {
            return null;
        }

        IReadOnlyList<BlockerHandoff> handoffs = await BlockerHandoffQuery.LoadAsync(
            session, taskDetails.BlockedBy, cancellationToken);
        if (BlockerContextDocument.Render(handoffs) is not { } context)
        {
            return null;
        }

        // Named rather than silently applied: whether this fan-in is wide enough to warrant
        // synthesis is the claiming node's own DaemonOptions setting, which the CLI cannot
        // read (Reference graph: Cli -> Domain + Connectors) — so the operator is told the
        // context is the raw, unsynthesized document rather than left to assume it matches
        // whatever a headless claim would have produced.
        AnsiConsole.MarkupLine(
            "[dim]Blocker context included verbatim (unsynthesized) — a headless claim may condense a wide fan-in first.[/]");
        return context;
    }

    private static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork)> ReenterAsync(
        IDocumentSession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {task.Id} reads as interactively claimed but carries no current run — this needs a human look.");
        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {task.Id} is claimed interactively but run {runId} has no record — this needs a human look.");

        // Mirrors TaskDeliverCommand's own guard: once h9k task deliver hands the run to the
        // daemon's pipeline (AgentSessionCompleted moves it to Verifying), the task can still
        // read Claimed+interactive for the whole review loop — closeout only moves the task
        // off Claimed once the pull request opens. Re-entering here anyway would rewrite the
        // worktree the pipeline's own gates and review sessions are reading, and would reset
        // the run's own State back to Running underneath whichever pipeline stage owns it now
        // (adversarial review, cycle 1).
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {task.Id} "
                + "to see where it stands.");
        }

        if (!Directory.Exists(run.WorktreePath))
        {
            throw new DomainConflictException(
                $"Task {task.Id}'s worktree {run.WorktreePath} no longer exists on disk. "
                + $"h9k task release {task.Id} to put it back in the queue, or investigate by hand.");
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]Re-entering task {task.Id}'s interactive claim.[/]");
        // Whatever the earlier session left — committed or not — is already sitting in this
        // worktree, so the prompt tells the fresh session to look for it exactly as a headless
        // retry's own resumed worktree does (conformance review, cycle 1).
        return (runId, run.WorktreePath, run.Branch, run.RunDirectory, ResumesPreviousWork: true);
    }

    private static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork)> ClaimAndCutAsync(
        IDocumentSession session, TaskAggregate task, StreamState fence, BootstrapContext context,
        Guid claudeSessionId, CancellationToken cancellationToken)
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

        // A reopened task carries its existing PR's branch and expects the follow-up prompt
        // RunLauncher.LaunchAsync builds for it (BuildFixChecks/BuildRebase/BuildFollowUp) —
        // that builder lives in Hall9k.Daemon, which the CLI cannot reference (Reference graph:
        // Cli -> Domain + Connectors), so an interactive claim here would cut a fresh branch off
        // the base and hand the operator a from-scratch prompt, stranding the reopen's review
        // feedback unanswered (conformance review, cycle 2). Refusing is the complete fix.
        if (taskDetails.FollowUpBranch.IsNotBlank())
        {
            throw new DomainConflictException(
                $"Task {task.Id} was reopened onto its existing pull request ({taskDetails.PullRequestUrl}) — "
                + "an interactive claim cannot build the follow-up prompt that branch needs. "
                + $"h9k pr resolve {task.Id} to dispatch a headless follow-up instead.");
        }

        Guid runId = DomainId.New();

        // Cut before committing the claim (mirrors RunLauncher: the worktree exists first, the
        // record follows). If this throws, the task stays Queued — nothing was appended yet.
        GitWorktreeManager worktrees = new(NullLogger<GitWorktreeManager>.Instance);
        (Worktree worktree, bool resumesPreviousWork) = await CheckoutFreshOrRetryAsync(
            worktrees, taskDetails, project, task.Id, runId, cancellationToken);

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
        // project/task role-resolution chain a headless build session runs through. SessionId
        // is claudeSessionId — the actual Claude session about to be spawned — the same
        // "SessionId names the first spawned session" convention RunLauncher's own RunDispatched
        // follows, rather than the run's own id, which no agent session ever runs under.
        session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
            runId, task.Id, Guid.Empty, context.OwnerId, claimed.LeaseGeneration, claudeSessionId,
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
        return (runId, worktree.Path, worktree.Branch, runDirectory, resumesPreviousWork);
    }

    /// <summary>
    /// Mirrors RunLauncher.CheckoutFreshOrRetryAsync exactly: a Queued task carrying a
    /// surviving branch (a prior <c>h9k task handback</c> or <c>h9k task retry</c>) resumes it
    /// instead of cutting a fresh branch off the base, which would otherwise silently strand
    /// whatever was already committed there under a worktree nothing points at any more
    /// (conformance review, cycle 1). When the branch is gone everywhere, this falls back to a
    /// fresh worktree exactly as the daemon's own path does.
    /// </summary>
    private static async Task<(Worktree Worktree, bool ResumesPreviousWork)> CheckoutFreshOrRetryAsync(
        IWorktreeManager worktrees, TaskDetails taskDetails, ProjectDetails project, Guid taskId, Guid runId,
        CancellationToken cancellationToken)
    {
        if (taskDetails.RetryBranch.IsNotBlank())
        {
            try
            {
                Worktree resumed = await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, taskDetails.RetryBranch, taskId, runId),
                    cancellationToken);
                return (resumed, true);
            }
            catch (WorktreeException exception)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Could not resume branch {taskDetails.RetryBranch} ({exception.Message}); starting clean from {project.BaseBranch}.[/]");
            }
        }

        Worktree fresh = await worktrees.CreateAsync(
            new WorktreeRequest(project.RepositoryPath, project.BaseBranch, taskId, runId, taskDetails.Objective),
            cancellationToken);
        return (fresh, false);
    }

    private static async Task AppendSessionEndedAsync(
        DocumentStore store, Guid runId, Guid claudeSessionId, CancellationToken cancellationToken)
    {
        await using IDocumentSession endSession = store.LightweightSession();
        // Attached to the operator's terminal, not driven headlessly through
        // --output-format stream-json — there is no result payload to read usage off, so
        // every field is honestly null (the nullable-Turns convention).
        endSession.Events.Append(runId, new InteractiveSessionEnded(
            runId, claudeSessionId, DateTimeOffset.UtcNow, Turns: null, InputTokens: null, OutputTokens: null, CostUsd: null));
        await endSession.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> LaunchInteractiveClaudeAsync(
        string worktreePath, string prompt, Guid sessionId, string settingsFile, bool skipPermissions, CancellationToken cancellationToken)
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
        process.StartInfo.ArgumentList.Add("--settings");
        process.StartInfo.ArgumentList.Add(settingsFile);
        if (skipPermissions)
        {
            process.StartInfo.ArgumentList.Add("--dangerously-skip-permissions");
        }

        // A positional argument, passed through ArgumentList rather than a shell string: no
        // shell escaping, so the prompt's own quotes and newlines travel to the child exactly
        // as written. Claude Code starts interactively (no -p) with this as the opening message.
        process.StartInfo.ArgumentList.Add(prompt);

        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The terminal's own Ctrl-C ordinarily reaches the child too (same foreground
            // process group), but kill it best-effort in case it is still alive — the CLI is
            // about to walk away from it either way, and an interactive claude process left
            // running detached from the terminal that spawned it is worse than a failed kill.
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

            throw;
        }

        return process.ExitCode;
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";
}
