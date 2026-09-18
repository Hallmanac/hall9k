using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Worktrees;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Bootstrap;
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
/// <para>
/// Releasing is itself the human's explicit act of returning the task to the machine, so by
/// default it also clears the task's interactive-mode flag exactly as h9k task handback does —
/// headless dispatch must not gate every phase boundary for a human who walked away (design
/// ruling R6, amended 2026-09-05). --keep-interactive is the stated exception, for an operator who
/// wants the next headless run to keep parking at each boundary for a recorded go.
/// </para>
/// <para>
/// --unassign takes the same untouched claim straight to Published instead of back to the queue,
/// in the single <see cref="Hall9k.Domain.Features.Tasks.Events.TaskInteractiveClaimUnassigned"/>
/// event rather than release's own <see cref="Hall9k.Domain.Features.Tasks.Events.TaskRequeued"/>
/// followed by a separate h9k task unassign — so the Queued/Blocked state the two-step path would
/// pass through is never written to the stream at all, and the dispatcher can never claim the
/// task in the gap between them (this task's own origin: the dispatcher claimed a released task
/// within seconds at ceiling 4, cd7e0202, 2026-09-04). Every other refusal above still applies
/// unchanged — --unassign only changes which event this decider produces once the claim is found
/// releasable.
/// </para>
/// <para>
/// A second, unrelated meaning lives here too (idea 202383dc, A3b, criterion 3): a task this node
/// still names itself the ledger holder of, but that is no longer <see cref="TaskState.Claimed"/>
/// at all — the window a Done or Blocked task with an open pull request deliberately survives
/// into, since the holder is released only at true completion, <c>h9k task abandon</c>, or lease
/// expiry, never at the earlier <c>TaskCompleted</c> that only opened the pull request. That is
/// the first way a holder moves without one of those three, and until this existed the only lever
/// on it was abandoning the task outright. This self-release never touches the interactive-claim
/// machinery above (there is none to touch — <see cref="Hall9k.Domain.Features.Tasks.TaskAggregate.IsInteractiveClaim"/>
/// is never set for it), and never reaches past this node's own holder: a task this node does not
/// hold refuses exactly as it always has, through the same decider guard every other refusal above
/// goes through.
/// </para>
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

        [CommandOption("--keep-interactive")]
        [Description("Preserve the task's interactive-mode flag across this release, instead of the default of clearing it — the next headless run still parks at each phase boundary for a recorded h9k review proceed")]
        public bool KeepInteractive { get; init; }

        [CommandOption("--unassign")]
        [Description("Take the claim straight to Published (unassigned) instead of back to the dispatch queue, in one atomic act — the dispatcher never sees this task claimable in between, closing the race a separate release then h9k task unassign cannot avoid")]
        public bool Unassign { get; init; }
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

        // The self-release lever criterion 3 calls for (idea 202383dc, A3b): a task the ledger
        // still names this node the holder of, but that is no longer TaskState.Claimed at all —
        // the window a Done or Blocked task with an open pull request deliberately survives into,
        // since the holder is released only at true completion, h9k task abandon, or lease
        // expiry, never at the earlier TaskCompleted. Claimed is excluded outright, headless and
        // interactive alike: that state is a live run, the worktree/commit guards below exist for
        // exactly that case, and releasing the ledger holder out from under a live run would race
        // it — an active headless claim keeps its own refusal, via the interactive-claim decider
        // below, exactly as before this existed.
        if (task.State != TaskState.Claimed && task.HolderNodeId == context.NodeId)
        {
            await ReleaseLedgerHolderOnlyAsync(session, store, context, task, taskId, fence, settings, cancellationToken);
            return ExitCodes.Ok;
        }

        // Mirrors TaskWorkCommand.ReenterAsync's own guard: once h9k task deliver (or handback)
        // hands the run to the standard pipeline, the task can still read Claimed+interactive
        // for the whole review loop, so the decider's own state check alone would let this
        // requeue a task whose delivered run is mid-gate or mid-review, double-booking the
        // worktree with a freshly dispatched headless agent (adversarial review, cycle 1).
        Guid? releasedRunId = task.State == TaskState.Claimed && task.IsInteractiveClaim
            ? task.CurrentRunId
            : null;
        bool supersedeRun = false;
        RunDetails? releasedRun = null;
        if (releasedRunId is { } currentRunId)
        {
            RunDetails? run = await session.LoadAsync<RunDetails>(currentRunId, cancellationToken);
            releasedRun = run;
            if (task.Type == TaskType.PrReview)
            {
                // A pr-review task's own Claimed+sentinel state is never a human's own interactive
                // claim (TaskWorkCommand and TaskStartCommand both refuse to create one) — it is
                // auto-pr-review's Now-speed deliberate claim (AutoPrReviewEngine.CreateOneAsync).
                // Checked ahead of the run-null branch below, not only the terminal/non-terminal
                // split that branch used to apply after it (independent pre-PR review, cycle 10,
                // adversarial lens): CreateOneAsync commits TaskClaimed and only afterward calls
                // LaunchAsync, which fetches the pull request, cuts the worktree, writes the prompt
                // and spawns before RunDispatched ever commits — so a run-null read here is far more
                // often "the launch is still in flight" than "the launch died", and the run-null
                // branch's own "releasing the claim" would dispatch a second run alongside the first
                // once the doorbell wakes the dispatcher: the exact double-dispatch this guard exists
                // to prevent. RunState.Unknown when there is no run record yet at all is what that
                // arm of PrReviewSentinelClaim.Refuse is for: the state genuinely is not recorded,
                // said out loud rather than guessed at either way (a died launch or one still
                // running) — h9k task abandon is still the honest way out of a truly dead one. A
                // live, parked, or terminal run all read as themselves the same way, since a
                // pr-review task is never delivered and so never takes the ordinary path below at
                // all (independent pre-PR review, cycles 7 and 8, adversarial lens).
                throw PrReviewSentinelClaim.Refuse(taskId, run?.State ?? RunState.Unknown, "release");
            }

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
                // give back (adversarial review, cycle 1). Unreachable for a pr-review task, which
                // the branch above already refuses before this one ever sees a null run.
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Task {taskId}'s run {currentRunId} has no record — its interactive claim never finished setting up (the process likely died while preparing the worktree). Releasing the claim; a partially-cut worktree or branch may be left on disk under this task's id and is safe to remove by hand.[/]");
            }
            else
            {
                supersedeRun = true;
                await ReleaseAttachedRunAsync(session, task, taskId, currentRunId, run, settings, cancellationToken);
            }
        }

        // --unassign appends the one TaskInteractiveClaimUnassigned event instead of
        // TaskRequeued: the task goes straight from Claimed to Published on this single append,
        // so the Queued/Blocked state a plain release then h9k task unassign would pass through
        // is never written to the stream at all — nothing for the dispatcher to ever see and
        // claim in between (this task's own acceptance criteria; cd7e0202, 2026-09-04).
        if (settings.Unassign)
        {
            session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.ReleaseInteractiveClaimUnassigned(
                task, DateTimeOffset.UtcNow, settings.KeepInteractive));
        }
        else
        {
            session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.ReleaseInteractiveClaim(
                task, DateTimeOffset.UtcNow, settings.KeepInteractive));
        }

        if (supersedeRun && releasedRunId is { } supersededRunId && releasedRun is { } supersededRun)
        {
            // Otherwise this run reads Running forever: it holds no TaskLease (an interactive
            // claim writes none) and its NodeId is the Guid.Empty sentinel, so neither
            // AdoptOrphansAsync's NodeId filter nor SweepExpiredLeasesAsync's lease scan will
            // ever retire it (adversarial review, cycle 1) — mirrors the lease-expiry requeue's
            // own retirement of the run it displaces (DispatchEngine.cs).
            DateTimeOffset supersededAt = DateTimeOffset.UtcNow;
            // Release is only for a claim nothing has been done in yet, but a start-it-mine
            // claim can reach here with tokens already spent (the commits-beyond-base check
            // above only catches committed work) — its own stream.jsonl is otherwise never read
            // back once this run is retired (conformance review, cycle 1, on h9k task start).
            HeadlessTokenRecovery.AppendIfRecorded(session, supersededRun, supersededAt);
            HeadlessTokenRecovery.AppendDelegatedPhaseTokens(session, supersededRun, supersededAt);
            session.Events.Append(supersededRunId, new RunSuperseded(supersededRunId, task.LeaseGeneration + 1, supersededAt));
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
        if (settings.Unassign)
        {
            // TaskInteractiveClaimUnassigned always lands Published, unconditionally — the same
            // "there is no unmet set left to matter to" reasoning
            // TaskAggregate.Apply(TaskUnassigned) already carries, since the task is leaving
            // assignment altogether rather than requeuing back into it.
            AnsiConsole.MarkupLine(
                $"[blue]Task {taskId} released and unassigned[/] — published again, and no node will claim it.");
            AnsiConsole.MarkupLine(
                $"[dim]To edit it:[/] h9k task draft {taskId} [dim]· to start it again:[/] h9k task assign {taskId}");
        }
        else
        {
            // Requeue (TaskAggregate.Apply(TaskRequeued)) clears the claim but never touches
            // _unmetDependencies, only Assign does — so a claim released from a deliberate
            // start-it-mine override (h9k task start --acknowledge-unmet-dependencies) still names
            // an open blocker here and lands Blocked, not Queued; the daemon will not claim it until
            // that blocker closes out (conformance review, cycle 4).
            int unmetDependencyCount = task.UnmetDependencies.Count;
            if (unmetDependencyCount == 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Task {taskId} released back to the queue — the daemon claims it as it would any other queued task.[/]");
            }
            else
            {
                string dependencyNoun = unmetDependencyCount == 1 ? "dependency" : "dependencies";
                AnsiConsole.MarkupLineInterpolated(
                    $"[dim]Task {taskId} released back, but {unmetDependencyCount} unmet {dependencyNoun} still name it Blocked — it will not dispatch until those close out.[/]");
            }
        }

        // The one fact that separates this invocation from --keep-interactive (adversarial
        // review, cycle 1): a default release also clears the task's interactive-mode flag, so
        // the next headless run no longer parks at the review engine's four phase boundaries —
        // silent otherwise, an operator only discovered it when the run reached its pull request
        // unannounced. task.InteractiveModeEnabled still reads the pre-release value here: the
        // aggregate in hand was loaded before ReleaseInteractiveClaim's event was appended above.
        if (task.InteractiveModeEnabled && settings.KeepInteractive)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId}'s interactive-mode flag survives this release (--keep-interactive) — a later headless run still parks at each phase boundary for a recorded h9k review proceed.[/]");
        }
        else if (task.InteractiveModeEnabled)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId}'s interactive-mode flag is cleared — a later headless run no longer parks at each phase boundary.[/]");
        }

        return ExitCodes.Ok;
    }

    private static async Task ReleaseAttachedRunAsync(
        IDocumentSession session, TaskAggregate task, Guid taskId, Guid currentRunId, RunDetails run, Settings settings,
        CancellationToken cancellationToken)
    {
        // Mirrors TaskHandbackCommand's own guard: an operator's own session, still attached
        // in another terminal, may be editing this exact worktree right now — requeuing it
        // out from under that session double-books the task the moment the daemon claims it
        // headlessly (adversarial review, cycle 1). Skipped when this invocation is that very
        // session releasing itself on the operator's own go
        // (InteractiveSessionLiveness.IsSelfInvocation's own doc has both signals).
        // WorkPromptBuilder.AppendSelfDeliveryRule is what tells a self-invoking session to stop
        // editing this worktree the instant this succeeds — the task requeues for dispatch
        // immediately (independent pre-PR review, conformance lens, cycle 1).
        if (!InteractiveSessionLiveness.IsSelfInvocation(run))
        {
            InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "release", settings.Force);
        }

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
            // still real work the operator did. A path under src/ or tests/ is strandable exactly
            // the way a committed change is (WorktreeGitStatus.SplitUntracked, shared with h9k
            // task deliver's own refusal and VerificationRunner's pre-gate check): release records
            // no RetryBranch, so a headless reclaim just cuts a second, run-suffixed worktree off
            // the base branch (this command's own doc comment above), orphaning that file in a
            // directory nothing points at — exactly the reason release refuses a modified file
            // above, and a warning here used to let it through anyway (out-of-scope conformance
            // finding on task a94dcd35, routed here). A path outside src/ or tests/ (a gate
            // byproduct under an un-gitignored path, say) stays advisory — refusing on every
            // untracked path would make the claim permanently unreleasable over its own build
            // output — so only that half is still warned by name, the same way h9k task deliver
            // and h9k task verify already report it, rather than requeuing in silence (adversarial
            // review, cycle 4).
            (IReadOnlyList<string> strandable, IReadOnlyList<string> byproduct) = WorktreeGitStatus.SplitUntracked(untracked);
            if (strandable.Count > 0)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s worktree has untracked file(s) under src/ or tests/: {string.Join(", ", strandable)} — "
                    + "release is only for a claim nothing has been done in yet, and h9k task deliver refuses the same "
                    + "files for the same reason. Commit or discard them first, then release, handback, or deliver as "
                    + "the work warrants.");
            }

            if (byproduct.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Task {taskId}'s worktree has untracked file(s) that will be left behind: {string.Join(", ", byproduct)} — release is only for a claim nothing has been done in yet; commit or discard them first if they matter.[/]");
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
        // (adversarial review, cycle 1, TaskReleaseCommand.cs:129). headReference is always
        // run.Branch by name, never the worktree's own HEAD: an operator who detached HEAD or
        // switched branches in the worktree (to compare something, say) leaves git status clean
        // while HEAD no longer points at the claim's branch, and counting HEAD there would read
        // the branch's real commits as zero and let this guard wave the orphaning through
        // (conformance review, cycle 3).
        // run.BaseBranchOr, not project.BaseBranch, for the reason TaskDeliverCommand's own
        // identical count states: a stacked child's branch would otherwise read its parent's
        // commits as this claim's own and refuse a release of a claim nothing was done in. The
        // recorded fork point rides along as a second candidate boundary, and the count is the
        // smaller of the two, for the same reason again: a force-pushed parent leaves this branch's
        // copies of its rewritten-away commits counted here (class sweep, conformance review
        // cycle 4).
        string baseBranch = run.BaseBranchOr(project.BaseBranch);
        string? forkPoint = run.StackedForkPoint(project.BaseBranch);
        int commits = worktreeExists
            ? await InteractiveWorktreeGit.CountBranchCommitsAsync(
                run.WorktreePath, baseBranch, cancellationToken, headReference: run.Branch,
                forkPointCommit: forkPoint)
            : await InteractiveWorktreeGit.CountBranchCommitsAsync(
                project.RepositoryPath, baseBranch, cancellationToken, headReference: run.Branch,
                forkPointCommit: forkPoint);
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
                $"Task {taskId}'s branch {run.Branch} holds {commits} commit(s) beyond {baseBranch} — "
                + "whether from this claim or one it resumed, the branch is not empty and release is only for "
                + "a claim nothing has been done in yet. " + recovery);
        }
    }

    /// <summary>
    /// The self-release path (idea 202383dc, A3b, criterion 3): this node gives back a ledger
    /// holder it still names itself for a task with no active claim of its own left to protect —
    /// the same conditional write <see cref="TaskAbandonCommand"/>'s own release uses, with a
    /// <see cref="Hall9k.Domain.Features.Tasks.Events.TaskHolderReleased"/> event on the task's
    /// own stream, but no <see cref="Hall9k.Domain.Features.Tasks.Events.TaskRequeued"/> or
    /// <see cref="Hall9k.Domain.Features.Tasks.Events.TaskInteractiveClaimUnassigned"/>: the
    /// task's own <see cref="TaskState"/> is untouched, since this never was an interactive claim
    /// to give back in the first place.
    /// </summary>
    private static async Task ReleaseLedgerHolderOnlyAsync(
        IDocumentSession session, Marten.DocumentStore store, BootstrapContext context, TaskAggregate task,
        Guid taskId, StreamState fence, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Unassign)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} with no active interactive claim to unassign — "
                + "this node still names itself its ledger holder from an earlier headless run. "
                + $"h9k task release {taskId} (without --unassign) gives that back.");
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, TaskDecider.ReleaseHolder(task, DateTimeOffset.UtcNow));
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
        await ReleaseLedgerHolderBestEffortAsync(store, context, task, cancellationToken);
        await MirrorTrackerReleaseBestEffortAsync(store, context, task, cancellationToken);

        AnsiConsole.MarkupLine(
            $"[dim]Task {taskId}'s ledger holder released[/] — this node no longer names itself the holder; the task's own state ({task.State.Value}) is unchanged.");
    }

    /// <summary>
    /// Release: the same conditional write a claim used (idea 202383dc, A3b), best effort — the
    /// domain-side release has already landed above by the time this runs; a write that cannot
    /// complete here is retried by whichever node's own dispatch sweep next finds this task's
    /// holder pointing at nothing that still needs it (<c>SweepPendingHolderReleasesAsync</c>),
    /// the identical shape <see cref="TaskAbandonCommand"/>'s own release helper uses.
    /// </summary>
    private static async Task ReleaseLedgerHolderBestEffortAsync(
        Marten.DocumentStore store, BootstrapContext context, TaskAggregate task, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
            if (project is null)
            {
                return;
            }

            ILedger ledger = new GitLedger(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitLedger>.Instance);
            string recordsRef = LedgerRefRegistry.Records.RefspecSource;
            string recordPath = LedgerRefRegistry.RecordPath(task.Id);
            LedgerFile record = await ledger.ReadAsync(project.RepositoryPath, recordsRef, recordPath, cancellationToken);
            if (!record.Exists)
            {
                // A fetch that itself failed is not a confirmed absence (LedgerFile.FetchFailed):
                // the domain-side release has already landed by the time this runs, so silently
                // returning here would drop the ledger's own release for good, with no pending row
                // for any later sweep to ever pick back up (mirrors TaskAbandonCommand's identical
                // guard).
                if (record.FetchFailed)
                {
                    await StorePendingReleaseAsync(
                        store, task, context,
                        "the ledger's own fetch failed before this node could tell whether a record exists "
                        + "here at all",
                        cancellationToken);
                }

                return;
            }

            (LedgerCommitter committer, LedgerSigningKey signingKey, _) =
                await TaskRecordPublication.ResolveIdentityAsync(session, context, cancellationToken);
            HolderReleaseResult result = await TaskLedgerHolder.TryReleaseAsync(
                ledger, project.RepositoryPath, task.Id, context.NodeId, committer, signingKey, cancellationToken);

            if (result.Verdict == HolderReleaseVerdict.Failed)
            {
                await StorePendingReleaseAsync(store, task, context, result.FailureReason ?? string.Empty, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StorePendingReleaseAsync(store, task, context, exception.Message, cancellationToken);
        }
    }

    private static async Task StorePendingReleaseAsync(
        Marten.DocumentStore store,
        TaskAggregate task,
        BootstrapContext context,
        string failureReason,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Store(new TaskHolderReleasePending
        {
            Id = TaskHolderReleasePending.KeyFor(task.Id, context.NodeId),
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            NodeId = context.NodeId,
            RecordedAt = DateTimeOffset.UtcNow,
            LastFailureReason = failureReason,
        });
        await session.SaveChangesAsync(cancellationToken);
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]Releasing task {task.Id}'s ledger holder failed; it is retried on this node's next dispatch sweep. ({failureReason})[/]");
    }

    /// <summary>
    /// The tracker-assignee twin of <see cref="ReleaseLedgerHolderBestEffortAsync"/> (criterion 5,
    /// idea 202383dc, A3b): this self-release gives the ledger holder back, so this install's own
    /// tracker identity is cleared off the linked item too, best effort — a named outcome short of
    /// a confirmed clear leaves a <see cref="TaskTrackerReleaseMirrorPending"/> row for
    /// <c>DispatchEngine</c>'s own <c>SweepPendingTrackerMirrorsAsync</c> to retry, the identical
    /// row every other holder-change mirror already retries through.
    /// </summary>
    private static async Task MirrorTrackerReleaseBestEffortAsync(
        Marten.DocumentStore store, BootstrapContext context, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (task.ExternalReference is null)
        {
            return;
        }

        try
        {
            await using IDocumentSession session = store.LightweightSession();
            ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
            if (project is null)
            {
                return;
            }

            TrackerAssignmentTake trackerAssignmentTake = new(new ProjectScopedGitHubRunner(store).Runner);
            TrackerRelease release = await trackerAssignmentTake.ReleaseAsync(
                store, ClaimGate.TrackerAssignee, task.ExternalReference, project.RepositoryPath, cancellationToken);
            if (!release.Succeeded)
            {
                await StorePendingTrackerReleaseMirrorAsync(
                    store, task, context, release.FailureReason ?? string.Empty, cancellationToken);
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Clearing task {task.Id}'s tracker assignee failed; it is retried on this node's next dispatch sweep. ({release.FailureReason})[/]");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StorePendingTrackerReleaseMirrorAsync(store, task, context, exception.Message, cancellationToken);
        }
    }

    private static async Task StorePendingTrackerReleaseMirrorAsync(
        Marten.DocumentStore store,
        TaskAggregate task,
        BootstrapContext context,
        string failureReason,
        CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        string pendingKey = TaskTrackerReleaseMirrorPending.KeyFor(task.Id, context.NodeId);
        TaskTrackerReleaseMirrorPending? existing = await session.LoadAsync<TaskTrackerReleaseMirrorPending>(pendingKey, cancellationToken);
        session.Store(new TaskTrackerReleaseMirrorPending
        {
            Id = pendingKey,
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            NodeId = context.NodeId,
            RecordedAt = DateTimeOffset.UtcNow,
            LastFailureReason = failureReason,
            AttemptCount = (existing?.AttemptCount ?? 0) + 1,
        });
        await session.SaveChangesAsync(cancellationToken);
    }
}
