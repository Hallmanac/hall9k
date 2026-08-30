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

        [CommandOption("--force")]
        [Description("Release even though the claim's interactive session was recorded on another machine this one cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }
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
        bool supersedeRun = false;
        if (releasedRunId is { } currentRunId)
        {
            RunDetails? run = await session.LoadAsync<RunDetails>(currentRunId, cancellationToken);
            if (run is null)
            {
                // ClaimAndCutAsync commits TaskClaimed, then cuts the worktree, and only then
                // commits RunDispatched — a process death in that window (the operator's terminal
                // closing, a killed process) leaves the task Claimed with a CurrentRunId that
                // resolves to nothing. An interactive claim writes no TaskLease by design, so
                // there is no expiry sweep to reclaim it the way a headless claim's would, and
                // every other lever (work, handback, deliver, verify) refuses a run with no
                // record — each one's own no-record message names h9k task release by id as the
                // way out (TaskWorkCommand.cs, TaskDeliverCommand.cs, TaskHandbackCommand.cs,
                // TaskVerifyCommand.cs — adversarial review, cycle 2). Nothing has run yet at
                // this point in ClaimAndCutAsync — RunDispatched is the last thing it commits —
                // so there is nothing to check and no run to supersede, only the claim itself to
                // give back (adversarial review, cycle 1).
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Task {taskId}'s run {currentRunId} has no record — its interactive claim never finished setting up (the process likely died while preparing the worktree). Releasing the claim; a partially-cut worktree or branch may be left on disk under this task's id and is safe to remove by hand.[/]");
            }
            else
            {
                supersedeRun = true;
                await ReleaseAttachedRunAsync(session, task, taskId, currentRunId, run, settings, cancellationToken);
            }
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.ReleaseInteractiveClaim(
            task, DateTimeOffset.UtcNow));

        if (supersedeRun && releasedRunId is { } supersededRunId)
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

    private static async Task ReleaseAttachedRunAsync(
        IDocumentSession session, TaskAggregate task, Guid taskId, Guid currentRunId, RunDetails run, Settings settings,
        CancellationToken cancellationToken)
    {
        // Mirrors TaskHandbackCommand's own guard: an operator's own session, still attached
        // in another terminal, may be editing this exact worktree right now — requeuing it
        // out from under that session double-books the task the moment the daemon claims it
        // headlessly (adversarial review, cycle 1).
        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "release", settings.Force);

        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {currentRunId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver (or handback) and is now in the standard pipeline. h9k task show {taskId} "
                + "to see where it stands.");
        }

        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // h9k task work directs an operator here by name for exactly this case (TaskWorkCommand's
        // ReenterAsync: "worktree ... no longer exists on disk ... h9k task release <id>"), which
        // means the two checks below cannot read run.WorktreePath — it is gone. Reading the
        // worktree's own git status was never possible to skip honestly there (adversarial
        // review, cycle 1): the commits-beyond-base check falls back to asking the repository
        // itself, by branch name, instead of silently waving committed work through.
        bool worktreeExists = Directory.Exists(run.WorktreePath);

        // Release is only for a claim nothing has been done in yet (this command's own doc
        // comment): mirrors TaskHandbackCommand/TaskDeliverCommand's own uncommitted-files
        // refusal, naming the files, rather than requeuing over edits nothing will ever point
        // at again — the commits-beyond-base check below catches committed work, but a claim
        // holding modified-but-uncommitted files was passing it silently, orphaning the
        // operator's edits in a worktree the next headless claim's own second, run-suffixed
        // worktree leaves nothing pointing at (adversarial review, cycle 1).
        if (!worktreeExists)
        {
            // Nothing to read: the directory itself is gone, so any uncommitted edits it held
            // are already lost along with it. Warning about them would be pointless; only
            // committed work (on the branch, in the repository) can still be recovered, and the
            // commits-beyond-base check below is what looks for that.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Task {taskId}'s worktree {run.WorktreePath} no longer exists on disk; any uncommitted edits it held are already gone.[/]");
        }
        else
        {
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
        }

        // Release is for an untouched claim only (AGENTS.md's own command surface says so):
        // Requeue records no RetryBranch, so a headless reclaim of a branch that already
        // exists just cuts a second, run-suffixed one off the base
        // (GitWorktreeManager.ResolveBranchNameAsync) rather than resuming it, orphaning
        // every commit the operator made with nothing left pointing at them
        // (adversarial review, cycle 1). When the worktree itself is gone, the branch the
        // operator committed to still lives in the repository (worktrees share refs), so the
        // check reads it there by name instead of failing along with the missing directory
        // (adversarial review, cycle 1, TaskReleaseCommand.cs:129).
        int commits = worktreeExists
            ? await InteractiveWorktreeGit.CountBranchCommitsAsync(run.WorktreePath, project.BaseBranch, cancellationToken)
            : await InteractiveWorktreeGit.CountBranchCommitsAsync(project.RepositoryPath, project.BaseBranch, cancellationToken, headReference: run.Branch);
        if (commits < 0)
        {
            // Never guessed at as empty (InteractiveWorktreeGit's own contract, mirrored by
            // TaskDeliverCommand and TaskVerifyCommand's own unreadable-git cases): neither
            // origin/<base>..HEAD nor <base>..HEAD resolved, so the check is honestly skipped
            // rather than silently letting the orphaning this guard exists to prevent through
            // (adversarial review, cycle 2).
            string readFrom = worktreeExists ? run.WorktreePath : project.RepositoryPath;
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read {(worktreeExists ? "the worktree's" : "the repository's")} git status at {readFrom}; skipping the commits-beyond-base check.[/]");
        }
        else if (commits > 0)
        {
            // h9k task deliver needs the worktree itself (it pushes from run.WorktreePath) — naming
            // it here on the worktree-gone path would send the operator to a command that fails
            // with a bare "Push failed: " rather than a real error. Only handback, which tolerates
            // a missing worktree (its uncommitted check degrades to skip, and CheckoutExistingAsync
            // re-adds it), is offered there (adversarial review, cycle 2).
            string recovery = worktreeExists
                ? $"h9k task handback {taskId} to hand the committed work to a headless agent, or "
                  + $"h9k task deliver {taskId} to submit it yourself."
                : $"h9k task handback {taskId} to hand the committed work to a headless agent, which will "
                  + "re-create the worktree from the branch.";
            throw new DomainConflictException(
                $"Task {taskId}'s branch {run.Branch} holds {commits} commit(s) beyond {project.BaseBranch} — "
                + "whether from this claim or one it resumed, the branch is not empty and release is only for "
                + "a claim nothing has been done in yet. " + recovery);
        }
    }
}
