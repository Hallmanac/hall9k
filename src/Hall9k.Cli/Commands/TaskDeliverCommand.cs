using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Deliver an interactive claim: refuse on uncommitted files (naming them — the operator is
/// present to commit), push the branch, then hand into the standard delivery pipeline. Delivery
/// means handed back (RULED): appending AgentSessionCompleted is the same event a headless run's
/// own agent session completing appends, moving this run to Verifying exactly as that run would
/// be — the daemon's RunSupervisor.ResumeStrandedPipelinesAsync notices a Verifying run with no
/// monitor on its very next sweep and runs the real gates, the independent review loop, and
/// PullRequestOpener, all through the identical code a headless run's own pipeline uses. From
/// here on the run is indistinguishable from a headless one: interactive participation in review
/// rounds is a later enhancement, and the review loop's own parks already provide the human hook
/// if something needs attention.
/// </summary>
public sealed class TaskDeliverCommand : Hall9kAsyncCommand<TaskDeliverCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--handoff <TEXT>")]
        [Description("What this run hands down to a dependent task or a resuming session (Decisions Log #36). Omit to be prompted on an interactive terminal, or to leave it unauthored on a non-interactive one.")]
        public string? Handoff { get; init; }

        [CommandOption("--force")]
        [Description("Deliver even though the claim's interactive session was recorded on another machine this one cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }
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
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim delivers this way.");
        }

        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId} is claimed interactively but run {runId} has no record — the process likely died "
                + $"while preparing the worktree. h9k task release {taskId} to give the claim back to the "
                + "dispatch queue.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // An operator's own session, still attached in another terminal, may still be editing
        // this worktree — pushing and handing it into the standard pipeline out from under it
        // risks delivering a tree mid-edit (adversarial review, cycle 1).
        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "deliver", settings.Force);

        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Run {runId} is already {run.State.Value} — task {taskId} was delivered already. h9k task show {taskId} to see where it stands.");
        }

        (IReadOnlyList<string>? modified, IReadOnlyList<string> untracked) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        if (modified is null)
        {
            // Never guessed at as clean (InteractiveWorktreeGit's own contract): git could not
            // be asked, so the operator is told the check was skipped rather than delivery
            // silently proceeding over a tree nobody actually looked at.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the uncommitted-files check.[/]");
        }
        else if (modified.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Task {taskId}'s worktree has uncommitted file(s); commit or discard them first:[/]");
            foreach (string file in modified)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]  {file}[/]");
            }

            return ExitCodes.Conflict;
        }

        if (untracked.Count > 0)
        {
            // Split the same way VerificationRunner does, with the same shared classification, so
            // this check says what will actually happen once the pipeline picks the run up. An
            // untracked file under src/ or tests/ is exactly as fatal to the pending run as a
            // modified-but-uncommitted one: VerificationRunner fails the run over it before any
            // gate runs, so pushing and handing off anyway would only spend a push, a run, and a
            // retry to learn what this check already knows (independent pre-PR review, both
            // lenses, cycle 1: this command used to warn and proceed into a guaranteed pre-gate
            // failure). Refused here, the same as the modified-files case above.
            (IReadOnlyList<string> strandable, IReadOnlyList<string> byproduct) = WorktreeGitStatus.SplitUntracked(untracked);

            if (strandable.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]Task {taskId}'s worktree has untracked file(s) under src/ or tests/; delivery refuses to push over them. Commit them first:[/]");
                foreach (string file in strandable)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]  {file}[/]");
                }

                return ExitCodes.Conflict;
            }

            if (byproduct.Count > 0)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]Untracked file(s) in the worktree (not blocking delivery): {string.Join(", ", byproduct)}[/]");
            }
        }

        // Refused here rather than left to the commits-beyond-base count below: that count reads
        // run.Branch by name (via origin/refs, shared across the worktree), so an operator who
        // left the worktree checked out somewhere else — detached at a commit that happens to
        // build, or on another branch — would sail past it with git status clean and the count
        // correct, only for every downstream reader (VerificationRunner's gates, the review
        // packet's diff, PullRequestOpener's --head) to read the worktree's actual HEAD instead
        // of the branch just published, gating and reviewing one tree while opening a pull
        // request over another (adversarial review, cycle 4).
        string? currentBranch = await InteractiveWorktreeGit.GetCurrentBranchAsync(run.WorktreePath, cancellationToken);
        if (currentBranch is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's current branch at {run.WorktreePath}; skipping the branch-checkout check.[/]");
        }
        else if (currentBranch != run.Branch)
        {
            string where = currentBranch.Length == 0 ? "a detached commit" : $"'{currentBranch}'";
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Task {taskId}'s worktree is checked out to {where}, not its claim branch '{run.Branch}' — check out '{run.Branch}' before delivering.[/]");
            return ExitCodes.Conflict;
        }

        // headReference is run.Branch by name rather than the worktree's own HEAD: an operator
        // who left the worktree with a different branch or a detached HEAD checked out (git
        // status clean either way) would otherwise have this count the wrong ref, report "N
        // commits to deliver" while pushing a branch that holds none, and hand PullRequestOpener
        // an empty branch to fail on (conformance review, cycle 3).
        int commits = await InteractiveWorktreeGit.CountBranchCommitsAsync(run.WorktreePath, project.BaseBranch, cancellationToken, headReference: run.Branch);
        if (commits < 0)
        {
            // Never guessed at as "holds commits" (InteractiveWorktreeGit's own contract,
            // mirrored by TaskReleaseCommand's identical check): the operator is told the check
            // was skipped rather than delivery proceeding in silence over a branch nobody
            // actually confirmed holds anything (adversarial review, cycle 4).
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's git status at {run.WorktreePath}; skipping the commits-beyond-base check.[/]");
        }
        else if (commits == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Branch '{run.Branch}' holds no commits beyond its base branch — nothing to deliver.[/]");
            return ExitCodes.Conflict;
        }

        (bool pushed, string pushError) = await InteractiveWorktreeGit.PushAsync(run.WorktreePath, run.Branch, cancellationToken);
        if (!pushed)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Push failed: {pushError}[/]");
            return ExitCodes.Error;
        }

        // Mirrors RunSupervisor.CaptureHandoffAsync, called in the same place relative to
        // AgentSessionCompleted: without this, an interactively delivered task hands nothing
        // down to a dependent — CloseoutEngine.ComposeHandoffAsync reads this same file off
        // disk at true closeout, agnostic of whether the run behind it was headless or
        // interactive, so writing it here is all that is missing (conformance review, cycle 1).
        // CancellationToken.None from here on: the push above already landed, so the branch is
        // published regardless of what happens next, and PromptForHandoff blocks on
        // Console.ReadKey, which observes no token — an operator who presses Ctrl-C while it
        // waits hits Program.cs's non-attached escalation branch, which cancels the shared
        // token without ever unblocking the prompt. Passing that same token to the writes and
        // appends below would let a keystroke meant to interrupt a stuck prompt instead poison
        // every step after the operator finishes answering it, aborting a delivery that already
        // pushed with the branch stranded and no AgentSessionCompleted ever appended
        // (independent pre-PR review, cycle 7).
        string handoff = settings.Handoff ?? PromptForHandoff();
        await WriteHandoffAsync(run.RunDirectory, handoff, CancellationToken.None);

        // Re-checked immediately before the append that hands this run to the daemon's
        // pipeline, not only once above: PromptForHandoff blocks on operator input with no
        // timeout, and everything from the push through that prompt leaves a window in which
        // another terminal can abandon, release, or hand back this very claim — each of which
        // appends RunSuperseded. Appending AgentSessionCompleted unconditionally would then
        // resurrect that superseded run (RunDetailsProjection sets State back to Verifying
        // unconditionally), sending it into RunSupervisor.ResumeStrandedPipelinesAsync and on to
        // PullRequestOpener for a task that no longer reads Claimed (adversarial review, cycle
        // 6). Reloading here (a lightweight session, so this hits the database rather than an
        // identity-map cache) narrows the window down to the append itself, mirroring
        // TaskWorkCommand's own pre-launch re-check.
        TaskDetails taskBeforeAppend = await session.LoadAsync<TaskDetails>(taskId, CancellationToken.None)
            ?? throw new DomainConflictException($"Task {taskId} no longer exists — the claim was lost while delivering.");
        if (taskBeforeAppend.State != TaskState.Claimed || !taskBeforeAppend.IsInteractiveClaim || taskBeforeAppend.CurrentRunId != runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {taskBeforeAppend.State.Value} — its interactive claim changed while delivering "
                + "(released, abandoned, or handed back from another terminal). "
                + $"Branch {run.Branch} was already pushed; h9k task show {taskId} to see where it stands.");
        }

        RunDetails runBeforeAppend = await session.LoadAsync<RunDetails>(runId, CancellationToken.None)
            ?? throw new DomainConflictException($"Task {taskId}'s run {runId} no longer has a record while delivering.");
        if (runBeforeAppend.State != RunState.Dispatched && runBeforeAppend.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Run {runId} is already {runBeforeAppend.State.Value} — task {taskId}'s claim changed while "
                + $"delivering. Branch {run.Branch} was already pushed; h9k task show {taskId} to see where it stands.");
        }

        // Re-checked here too, not only at the top of this command: the window between that
        // first check and this append (the git checks, the push, and PromptForHandoff blocking
        // on operator input with no timeout) is long enough for a second terminal's h9k task
        // work to pass its own pre-launch guard and launch a live Claude into this worktree —
        // that guard only catches a delivery, release, or handback that beat it there, not one
        // that starts after it already passed. Appending AgentSessionCompleted unconditionally
        // from here would then hand this run to RunSupervisor.ResumeStrandedPipelinesAsync
        // (gates, review sessions) while that second session is still editing the same tree
        // (independent pre-PR review, cycle 8).
        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(runBeforeAppend, taskId, "deliver", settings.Force);

        // The delivering node's own id, not the sentinel the claim was dispatched under: from
        // here the run travels the identical daemon-driven pipeline a headless run's own
        // AgentSessionCompleted hands into (gates, review, fix sessions), and NodeLoad's own
        // ceiling measurement counts strictly by NodeId — an interactive claim's Guid.Empty
        // sentinel left in place past this point would make the whole pipeline invisible to
        // every node's session ceiling forever (conformance review, cycle 1).
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, CancellationToken.None);
        session.Events.Append(runId, new AgentSessionCompleted(runId, DateTimeOffset.UtcNow, context.NodeId));
        await session.SaveChangesAsync(CancellationToken.None);

        await Doorbell.RingAsync($"task-delivered:{taskId}", CancellationToken.None);
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Branch {run.Branch} pushed. Task {taskId} handed into the standard delivery pipeline — h9k task show {taskId} to watch it.[/]");
        return ExitCodes.Ok;
    }

    private static string PromptForHandoff()
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return string.Empty;
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>(
                "[dim]Handoff for a dependent task or a resuming session (blank to leave unauthored):[/]")
                .AllowEmpty());
    }

    private static async Task WriteHandoffAsync(string runDirectory, string handoff, CancellationToken cancellationToken)
    {
        try
        {
            string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(runDirectory);
            Directory.CreateDirectory(resolvedRunDirectory);
            await File.WriteAllTextAsync(RunPaths.HandoffFile(resolvedRunDirectory), handoff, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Mirrors RunSupervisor.CaptureHandoffAsync: the branch is already pushed, so the
            // delivery itself succeeded — losing the artifact must not abort it. Closeout then
            // reads an absent file and records NotCaptured, which is exactly what happened.
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not write the handoff artifact ({exception.Message}); delivery proceeds without it.[/]");
        }
    }
}
