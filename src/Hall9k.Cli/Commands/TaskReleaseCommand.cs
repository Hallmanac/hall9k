using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Give an untouched interactive claim (h9k task work) back to the dispatch queue. Refused on a
/// task a node holds — that is running headless work with its own levers (let it finish, or
/// h9k task abandon) — and refused on a claim that already holds commits beyond the base branch:
/// Requeue clears the claim without recording a resume branch (TaskAggregate.Apply(TaskRequeued)),
/// so committed work would be silently orphaned in a worktree nothing points at, and the next
/// headless claim would redo the objective from scratch in a second, differently-named worktree
/// (adversarial review, cycle 1). h9k task handback is the lever for committed work; release is
/// only for a claim nothing has been done in yet. The worktree and branch are left on disk exactly
/// as they stood; nothing resumes them automatically.
/// </summary>
public sealed class TaskReleaseCommand : Hall9kAsyncCommand<TaskReleaseCommand.Settings>
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

        // Mirrors TaskWorkCommand.ReenterAsync's own guard: once h9k task deliver (or handback)
        // hands the run to the standard pipeline, the task can still read Claimed+interactive
        // for the whole review loop, so the decider's own state check alone would let this
        // requeue a task whose delivered run is mid-gate or mid-review, double-booking the
        // worktree with a freshly dispatched headless agent (adversarial review, cycle 1).
        Guid? releasedRunId = task.State == TaskState.Claimed && task.IsInteractiveClaim
            ? task.CurrentRunId
            : null;
        if (releasedRunId is { } currentRunId)
        {
            RunDetails run = await session.LoadAsync<RunDetails>(currentRunId, cancellationToken)
                ?? throw new DomainConflictException($"Task {taskId} is claimed interactively but run {currentRunId} has no record.");

            // Mirrors TaskHandbackCommand's own guard: an operator's own session, still attached
            // in another terminal, may be editing this exact worktree right now — requeuing it
            // out from under that session double-books the task the moment the daemon claims it
            // headlessly (adversarial review, cycle 1).
            InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "release");

            if (run.State != RunState.Dispatched && run.State != RunState.Running)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s run {currentRunId} is already {run.State.Value} — it was handed off with "
                    + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {taskId} "
                    + "to see where it stands.");
            }

            // Release is only for a claim nothing has been done in yet (this command's own doc
            // comment): mirrors TaskHandbackCommand/TaskDeliverCommand's own uncommitted-files
            // refusal, naming the files, rather than requeuing over edits nothing will ever point
            // at again — the commits-beyond-base check below catches committed work, but a claim
            // holding modified-but-uncommitted files was passing it silently, orphaning the
            // operator's edits in a worktree the next headless claim's own second, run-suffixed
            // worktree leaves nothing pointing at (adversarial review, cycle 1).
            (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
                await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
            if (modified is null)
            {
                // Never guessed at as clean (InteractiveWorktreeGit's own contract): git could
                // not be asked, so the operator is told the check was skipped rather than release
                // silently proceeding over a tree nobody actually looked at.
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
            }
            else if (modified.Count > 0)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s worktree has uncommitted file(s): {string.Join(", ", modified)} — "
                    + "release is only for a claim nothing has been done in yet, and h9k task handback and "
                    + "h9k task deliver both refuse the same uncommitted files for the same reason. Commit or "
                    + "discard them first, then release, handback, or deliver as the work warrants.");
            }

            // Untracked files (new, never git add-ed) pass the modified-files check above but are
            // still real work the operator did: a blanket refusal would be wrong (a gate
            // byproduct under an un-gitignored path would make the claim permanently
            // unreleasable), but silently dropping the list is the one option that loses it — the
            // operator is warned by name instead, the same way h9k task deliver and h9k task
            // verify already report untracked files, rather than requeuing in silence (adversarial
            // review, cycle 4).
            if (untracked.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Task {taskId}'s worktree has untracked file(s) that will be left behind: {string.Join(", ", untracked)} — release is only for a claim nothing has been done in yet; commit or discard them first if they matter.[/]");
            }

            // Release is for an untouched claim only (AGENTS.md's own command surface says so):
            // Requeue records no RetryBranch, so a headless reclaim of a branch that already
            // exists just cuts a second, run-suffixed one off the base
            // (GitWorktreeManager.ResolveBranchNameAsync) rather than resuming it, orphaning
            // every commit the operator made with nothing left pointing at them
            // (adversarial review, cycle 1).
            ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
                ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");
            int commits = await InteractiveWorktreeGit.CountBranchCommitsAsync(run.WorktreePath, project.BaseBranch, cancellationToken);
            if (commits < 0)
            {
                // Never guessed at as empty (InteractiveWorktreeGit's own contract, mirrored by
                // TaskDeliverCommand and TaskVerifyCommand's own unreadable-git cases): neither
                // origin/<base>..HEAD nor <base>..HEAD resolved, so the check is honestly skipped
                // rather than silently letting the orphaning this guard exists to prevent through
                // (adversarial review, cycle 2).
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the commits-beyond-base check.[/]");
            }
            else if (commits > 0)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s branch {run.Branch} holds {commits} commit(s) beyond {project.BaseBranch} — "
                    + "release is only for a claim nothing has been done in yet. "
                    + $"h9k task handback {taskId} to hand the committed work to a headless agent, or "
                    + $"h9k task deliver {taskId} to submit it yourself.");
            }
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.ReleaseInteractiveClaim(
            task, DateTimeOffset.UtcNow));

        if (releasedRunId is { } supersededRunId)
        {
            // Otherwise this run reads Running forever: it holds no TaskLease (an interactive
            // claim writes none) and its NodeId is the Guid.Empty sentinel, so neither
            // AdoptOrphansAsync's NodeId filter nor SweepExpiredLeasesAsync's lease scan will
            // ever retire it (adversarial review, cycle 1) — mirrors the lease-expiry requeue's
            // own retirement of the run it displaces (DispatchEngine.cs).
            session.Events.Append(supersededRunId, new RunSuperseded(supersededRunId, task.LeaseGeneration + 1, DateTimeOffset.UtcNow));
        }
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while releasing it — check h9k status and try again.");
        }

        await Doorbell.RingAsync($"task-released:{taskId}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} released back to the queue — the daemon claims it as it would any other queued task.[/]");
        return ExitCodes.Ok;
    }
}
