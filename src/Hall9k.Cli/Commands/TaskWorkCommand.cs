using System.ComponentModel;
using System.Diagnostics;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Hall9k.Connectors.Worktrees;
using Hall9k.Domain.Features.Owner;
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
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// The operator's interactive claim (PLAN.md, an operator can work a task interactively): on a
/// Queued task, claims it exactly as headless dispatch would (same branch, same worktree, and the
/// prompt assembled through the identical code — <see cref="WorkPromptBuilder"/> is the code both
/// paths call, with its working rules swapped for an attached operator), then launches a regular
/// interactive Claude Code session attached to this terminal. On a Published task assigned to
/// nobody, this command assigns it to the operator's own owner and claims it interactively in the
/// same atomic event append (task 688a1ccf-h9k, 2026-09-02): the task is never observably Queued
/// in between, so the dispatcher — woken within moments by the doorbell notification a plain
/// <c>h9k task assign</c> would have sent — can never win the race to it. A Published task whose
/// dependencies have not all closed out is refused, naming the open blockers, the same bar
/// dispatch itself holds an assignment to (<see cref="TaskDependency"/>). The claim is
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

        [CommandOption("--force")]
        [Description("Re-enter even though the claim's interactive session was recorded on another machine this one cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // Checked before anything is claimed, not inside the launch's own catch (adversarial
        // review, cycle 8): the prompt travels as a single positional argument carrying the
        // whole multi-line working-rules document, and cmd.exe — which every .cmd/.bat/.ps1
        // shim (the shape an npm-installed Claude Code takes on Windows) ultimately runs
        // through — treats an embedded newline as a command separator, not literal argument
        // content. There is no quoting fix for that (WindowsCommandLine's own extra-quote
        // wrapping only survives embedded quotes, not embedded newlines), so this is refused
        // up front rather than left to strand a claim nobody can ever enter.
        if (DetectWindowsScriptShim(ClaudeBinary()) is { } shimPath)
        {
            throw new DomainConflictException(
                $"Claude Code resolves to a script ({shimPath}) on this machine, which h9k task work cannot "
                + "launch: an interactive claim's opening prompt travels as a multi-line command-line argument, "
                + "and cmd.exe — which every .cmd/.bat/.ps1 shim runs through — cannot carry embedded newlines "
                + "in one. Headless dispatch is unaffected (its prompt travels through a redirected file "
                + "instead): h9k task assign to dispatch this task headlessly, or install Claude Code's native "
                + "Windows build so `claude` resolves to an .exe.");
        }

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

        // Every re-entry launches under the same name (task: every dispatched agent session
        // launches under a human-readable id-and-role name) — the interactive claim is one
        // named session across however many attach/detach cycles it takes, not a fresh identity
        // each time.
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim);

        (Guid runId, string worktreePath, string branch, string runDirectory, bool resumesPreviousWork, bool crossMachineNoticeShown) = task.State == TaskState.Claimed && task.IsInteractiveClaim
            ? await ReenterAsync(session, task, settings.Force, cancellationToken)
            : await ClaimAndCutAsync(store, session, task, fence, context, claudeSessionId, sessionName, cancellationToken);

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
        string prompt = WorkPromptBuilder.Build(
            taskDetails, project, branch, worktreePath, resumesPreviousWork, blockerContext, taskDetails.RetryReason,
            isInteractive: true);

        // The same settings file every headless spawn writes (ClaudeExecutor), so the
        // platform-imposed overrides — no co-authored-by trailers (PLAN.md §6.6), and command-tool
        // timeout headroom sized for this platform's own gates (ClaudeSettingsFile) — apply to an
        // operator's interactive session exactly as they do a dispatched agent's.
        // The CLI cannot reference Hall9k.Daemon (Reference graph: Cli -> Domain + Connectors),
        // so there is no live VerifyGateTimeout to read here — DefaultCommandTimeout mirrors its
        // default, held to it by ClaudeSettingsFileTests. That the same 15 minutes is written down
        // in more than one place is a choice about which project owns the number, not a reference
        // the compiler forbids; ClaudeSettingsFile.DefaultCommandTimeout's own doc has the why.
        string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(runDirectory);
        Directory.CreateDirectory(resolvedRunDirectory);
        string settingsFile = RunPaths.SettingsFile(resolvedRunDirectory);
        string settingsContent = ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout);
        await File.WriteAllTextAsync(settingsFile, settingsContent, cancellationToken);

        // Re-checked immediately before launch, not only once inside ReenterAsync: everything
        // above (the worktree cut, the blocker-context load, the skill discovery
        // WorkPromptBuilder.Build runs, and the settings-file write) takes long enough for a
        // second `h9k task work` on the same task to pass ReenterAsync's own check — reading the
        // same RunDetails with ActiveSessions still empty — before this session is ever recorded,
        // launching two Claude processes into the same worktree (adversarial review, cycle 1).
        // Reloading RunDetails here (a lightweight session, so this hits the database rather than
        // an identity-map cache) narrows that window down to the launch itself.
        RunDetails currentRun = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId}'s run {runId} no longer has a record — h9k task release {taskId} to give the "
                + "claim back to the dispatch queue.");
        // Mirrors ReenterAsync's own guard: everything above (the worktree cut, the
        // blocker-context load, the skill discovery WorkPromptBuilder.Build runs, and the
        // settings-file write) takes long enough for a delivery, release, handback, or abandon
        // running concurrently in another terminal to land in between — each of which moves this
        // run past Dispatched/Running — and liveness alone does not catch that, since a run just
        // handed to the standard pipeline reads as having no live attached session either
        // (independent pre-PR review, cycle 7). Launching into that worktree anyway would double-book
        // it against whatever the other command already started, and InteractiveSessionStarted's
        // own projection would reset the run's State back to Running underneath the pipeline stage
        // that now owns it.
        if (currentRun.State != RunState.Dispatched && currentRun.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {currentRun.State.Value} — it was handed off to the "
                + "standard pipeline (delivered, handed back, or otherwise) while this session was preparing to "
                + $"launch. h9k task show {taskId} to see where it stands.");
        }

        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(currentRun, taskId, "work", settings.Force, quiet: crossMachineNoticeShown);

        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {worktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {branch}[/]");
        AnsiConsole.MarkupLine("[dim]Launching an interactive Claude Code session — exit it normally (Ctrl+D or /exit) to return here.[/]");

        // InteractiveSessionStarted appends only once the process is actually alive (from inside
        // LaunchInteractiveClaudeAsync's onStarted callback, with its real pid) rather than
        // pre-emptively here: recording it before the process exists left ProcessId unobservable,
        // so no other command could ever tell this worktree had a live attached session
        // (adversarial review, cycle 1) — and a launch that never starts (the claude binary
        // missing, the worktree vanishing) now never appends a started event with nothing to
        // pair it, instead of needing an ended event to close a pairing that never really began.
        int exitCode;
        bool sessionStartRecorded;
        try
        {
            (exitCode, sessionStartRecorded) = await LaunchInteractiveClaudeAsync(
                worktreePath, prompt, claudeSessionId, settingsFile, project.SkipPermissions, runId, sessionName,
                // CancellationToken.None: by the time this runs, process.Start() has already
                // spawned a real, terminal-attached claude — a Ctrl-C landing in the window before
                // this append completes must not turn into a lost append (adversarial review,
                // cycle 3), the same reasoning AppendSessionEndedAsync's own call already applies.
                (processId, startedAt) => AppendSessionStartedAsync(store, runId, claudeSessionId, processId, startedAt, sessionName, CancellationToken.None),
                cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The claude binary is missing, or the worktree directory vanished before the
            // process could even start: nothing was recorded, so the claim is preserved with
            // no run history to close out.
            throw new DomainConflictException(
                $"Could not launch the interactive Claude Code session for task {taskId}: {exception.Message} "
                + $"The claim is preserved — h9k task work {taskId} to try again, or h9k task release {taskId} to give it back.");
        }

        // Only when InteractiveSessionStarted actually landed (conformance review, cycle 4): a
        // transient database error inside LaunchInteractiveClaudeAsync's own onStarted callback
        // is swallowed there rather than propagated, so an ended event with no started event to
        // pair it would otherwise be recorded — a shape InteractiveSessionStarted's own doc
        // comment establishes only the other direction (an unmatched started is normal) as
        // expected. Always CancellationToken.None: while the child was attached, Program.cs
        // suppresses Ctrl-C entirely rather than cancelling the shared token, so a press during
        // the session leaves it uncancelled by the time execution reaches here — but a press
        // landing in the narrow window after InteractiveChildGuard is disposed and before this
        // line runs still escalates and cancels it, and the interactive session's own exit is
        // real regardless of that race — it must never be lost to a token cancelled by a
        // keystroke that arrived too late to mean anything else (conformance review, cycle 1).
        if (sessionStartRecorded)
        {
            await AppendSessionEndedAsync(store, runId, claudeSessionId, CancellationToken.None);
        }

        AnsiConsole.MarkupLineInterpolated(exitCode == 0
            ? (FormattableString)$"[dim]Session ended (exit {exitCode}).[/]"
            : $"[yellow]Session ended with exit code {exitCode}.[/]");

        // Re-read rather than assumed still true: another terminal may have delivered,
        // abandoned, handed back, or released this exact claim while the session the operator
        // was just attached to was running, and the levers below only make sense while the
        // claim is still sitting where this session left it (adversarial review, cycle 6).
        // CancellationToken.None: the interactive session already ended by the time execution
        // reaches here, so there is nothing left to cancel, and an ordinary Ctrl-C pressed as
        // input to the now-exited child (or Claude Code's own double-tap exit gesture) must
        // never turn a perfectly normal session end into a silent error exit that swallows the
        // deliver/verify/work/handback/release guidance below (independent pre-PR review,
        // cycle 7).
        TaskDetails? taskAfterSession = await session.LoadAsync<TaskDetails>(taskId, CancellationToken.None);
        bool stillClaimedHere = taskAfterSession is not null
            && taskAfterSession.State == TaskState.Claimed
            && taskAfterSession.IsInteractiveClaim
            && taskAfterSession.CurrentRunId == runId;
        if (stillClaimedHere)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Task {taskId} is still claimed —[/]");
            AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task deliver {taskId}    push and hand into the standard delivery pipeline[/]");
            AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task verify {taskId}     run the project's gates on demand[/]");
            AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task work {taskId}       resume this worktree with a fresh session[/]");
            AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task handback {taskId}   let a headless agent finish from here[/]");
            AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task release {taskId}    give it back to the dispatch queue[/]");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[dim]Task {taskId} is now {(taskAfterSession?.State.Value ?? "gone")} — its claim changed from another terminal while this session ran. h9k task show {taskId} to see where it stands.[/]");
        }

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

    internal static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork, bool CrossMachineNoticeShown)> ReenterAsync(
        IDocumentSession session, TaskAggregate task, bool force, CancellationToken cancellationToken)
    {
        Guid runId = task.CurrentRunId
            ?? throw new DomainConflictException(
                $"Task {task.Id} reads as interactively claimed but carries no current run — this needs a human look.");
        RunDetails run = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {task.Id} is claimed interactively but run {runId} has no record — the process likely died "
                + $"while preparing the worktree. h9k task release {task.Id} to give the claim back to the "
                + "dispatch queue.");

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

        // A second h9k task work on the same task is the same collision every other command in
        // this claim's surface already guards against: without this, a second terminal launches
        // a second claude into the same worktree, and RunDetailsProjection.StartSession's
        // single-slot ActiveSessions record overwrites the first session's liveness record with
        // the second's — so the first session becomes invisible to verify/deliver/handback too
        // (adversarial review, cycle 2).
        bool crossMachineNoticeShown = InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, task.Id, "work", force);

        AnsiConsole.MarkupLineInterpolated($"[dim]Re-entering task {task.Id}'s interactive claim.[/]");
        // Whatever the earlier session left — committed or not — is already sitting in this
        // worktree, so the prompt tells the fresh session to look for it exactly as a headless
        // retry's own resumed worktree does (conformance review, cycle 1).
        return (runId, run.WorktreePath, run.Branch, run.RunDirectory, ResumesPreviousWork: true, crossMachineNoticeShown);
    }

    internal static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork, bool CrossMachineNoticeShown)> ClaimAndCutAsync(
        DocumentStore store, IDocumentSession session, TaskAggregate task, StreamState fence, BootstrapContext context,
        Guid claudeSessionId, string sessionName, CancellationToken cancellationToken)
    {
        // Published is the atomic entry (task 688a1ccf-h9k): the dependency snapshot is loaded
        // here, before any other check, because it decides whether this claim is even possible —
        // a Published task whose dependencies have not all closed out cannot land Queued, and an
        // interactive claim needs Queued, not Blocked. dependencies stays null for the ordinary
        // Queued entry, which is what tells the append step below whether an assignment travels
        // in the same atomic batch as the claim.
        IReadOnlyList<TaskDependency>? dependencies = null;
        if (task.State == TaskState.Published)
        {
            dependencies = await TaskDependencyQuery.LoadAsync(session, task.BlockedBy, cancellationToken);
        }
        else if (task.State != TaskState.Queued)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a Published or Queued task (or one you already "
                + "hold interactively) can be worked this way. " + task.State switch
                {
                    var state when state == TaskState.Blocked =>
                        "It is assigned but waiting on a dependency; h9k task show names it.",
                    var state when state.IsPreDispatch =>
                        $"Publish it first: h9k task publish {task.Id}.",
                    var state when state == TaskState.Claimed =>
                        "It is claimed by a node running headless work already.",
                    _ => "Its story has already moved past dispatch.",
                });
        }
        else if (task.AssignedOwnerId != context.OwnerId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is assigned to {task.AssignedOwnerId} — an operator claims only their own owner's work.");
        }

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {task.Id}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {task.Id}'s project no longer exists.");

        // A pr-review task dispatches through a completely different path (RunLauncher.LaunchAsync's
        // own branch: a detached checkout of refs/pull/<n>/head, AgentRole.Review, the pr-review
        // prompt lens, PrReviewBaseRefName recorded on RunDispatched, UntrustedWorkingDirectory) —
        // none of which this command can build (that builder lives in Hall9k.Daemon, which the CLI
        // cannot reference). Claiming it here anyway would cut a fresh branch off the base and hand
        // the operator the ordinary build prompt, exactly the reopened-task failure the FollowUpBranch
        // refusal below exists to prevent, and h9k task deliver would then hand a run with no
        // PrReviewBaseRefName and no adversarial report into PrReviewEngine.DriveAsync (conformance
        // review, cycle 1).
        if (task.Type == TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a pr-review task — it has no diff of its own for an interactive session to "
                + "build; it dispatches headlessly against the pull request instead. h9k task show "
                + $"{task.Id} to see where it stands.");
        }

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
        DateTimeOffset claimedAt = DateTimeOffset.UtcNow;

        // Commit the claim before touching the filesystem — mirrors the daemon's own dispatch
        // order (DispatchEngine.TryClaimAsync commits TaskClaimed first; RunLauncher only then
        // cuts the worktree), not the reverse this used to do. A lost claim race is then caught
        // by the SaveChangesAsync below before any worktree or branch exists on disk, instead of
        // after — which used to leave one orphaned, referenced by nothing (adversarial review,
        // cycle 1).
        //
        // dependencies is non-null only for the Published entry (task 688a1ccf-h9k): the
        // assignment and the claim are computed together, and the assignment travels in the same
        // Append call as the claim — one expectedVersion covering both events, so the database
        // arbitrates a genuine collision (another operator, or another owner's h9k task assign)
        // to exactly one winner with nothing ever landing on a task this operator does not end up
        // holding.
        TaskAssigned? assigned = null;
        TaskClaimed claimed;
        if (dependencies is not null)
        {
            (assigned, claimed) = PrepareInteractiveClaimFromPublished(
                task, context.OwnerId, dependencies, runId, claimedAt);
        }
        else
        {
            claimed = TaskDecider.ClaimInteractively(task, context.OwnerId, runId, claimedAt);
        }

        long claimedVersion = fence.Version + (assigned is null ? 1 : 2);
        if (assigned is null)
        {
            session.Events.Append(task.Id, expectedVersion: claimedVersion, claimed);
        }
        else
        {
            session.Events.Append(task.Id, expectedVersion: claimedVersion, assigned, claimed);
        }

        // Deliberately no TaskLease: the claim is held by the human, not a process — no
        // liveness lease, no heartbeat reclaim (AGENTS.md).
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw assigned is null
                ? new DomainConflictException(
                    $"Task {task.Id} changed while claiming it — check h9k status and try again.")
                : await DescribeAssignAndClaimRaceLossAsync(store, task.Id, cancellationToken);
        }

        // Cut only after the claim is safely committed. A failure from here on has already
        // committed the claim, so leaving the task stuck Claimed with no run record would need
        // raw event-stream surgery to recover — h9k task release itself loads the very RunDetails
        // a failed worktree cut never wrote. Failing it honestly instead gives the operator the
        // ordinary Failed waypoint (retry, resolve, or abandon), exactly as
        // RunLauncher.RecordLaunchFailureAsync does for a headless launch failure (adversarial
        // review, cycle 1).
        Worktree worktree;
        bool resumesPreviousWork;
        string runDirectory;
        try
        {
            GitWorktreeManager worktrees = new(new ConsoleWorktreeLogger<GitWorktreeManager>());
            (worktree, resumesPreviousWork) = await CheckoutFreshOrRetryAsync(
                worktrees, taskDetails, project, task.Id, runId, cancellationToken);

            string? existingTaskDirectory = project.HomeDirectory.HasValue
                ? HomeEntryLookup.FindExisting(ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), task.Id)
                    ?? HomeEntryLookup.FindExisting(ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value), task.Id)
                : null;
            runDirectory = existingTaskDirectory is not null
                ? RunPaths.ResolveDirectoryUnderTaskDirectory(existingTaskDirectory, runId)
                : RunPaths.ResolveDirectory(project.HomeDirectory, TaskDocumentRenderer.DirectoryName(taskDetails), runId);

            // Fable is the human-interactive model tier (AgentModel's own doc comment, Decisions
            // Log #33) — a fixed platform choice for an operator-attended session, not the
            // project/task role-resolution chain a headless build session runs through. SessionId
            // is claudeSessionId — the actual Claude session about to be spawned — the same
            // "SessionId names the first spawned session" convention RunLauncher's own RunDispatched
            // follows, rather than the run's own id, which no agent session ever runs under.
            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, task.Id, Guid.Empty, context.OwnerId, claimed.LeaseGeneration, claudeSessionId,
                worktree.Path, worktree.Branch, ExecutorMode.Subscription, DateTimeOffset.UtcNow,
                IsFollowUp: false, Model: AgentModel.Fable, RunDirectory: runDirectory, SessionName: sessionName));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A Ctrl-C in this exact window used to leave the task stuck Claimed with no run
            // record — the raw-event-stream-surgery case the comment above describes — because
            // the filter this catch replaces let cancellation fall straight through uncaught.
            // CancellationToken.None here mirrors AppendSessionEndedAsync's own reasoning: the
            // token that just fired is the one this cleanup exists for (adversarial review,
            // cycle 2).
            await FailInteractiveClaimAsync(
                store, task.Id, claimedVersion, runId, "cancelled while preparing the worktree", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await FailInteractiveClaimAsync(store, task.Id, claimedVersion, runId, exception.Message, cancellationToken);
            throw new DomainConflictException(
                $"Task {task.Id} was claimed but could not be prepared for interactive work ({exception.Message}). "
                + $"It has been recorded Failed — h9k task retry {task.Id} to try again.");
        }

        await Doorbell.RingAsync($"task-claimed-interactively:{task.Id}", cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"[dim]Claimed task {task.Id} interactively.[/]");
        return (runId, worktree.Path, worktree.Branch, runDirectory, resumesPreviousWork, CrossMachineNoticeShown: false);
    }

    /// <summary>
    /// The atomic decision behind the Published entry (task 688a1ccf-h9k): assigns
    /// <paramref name="task"/> to the operator's own owner and claims it interactively as one
    /// unit, computed from an already-loaded dependency snapshot so this is pure — no session, no
    /// append — and independently testable. Mutates <paramref name="task"/> in place once the
    /// assignment is decided (the same "append, then <c>Apply</c> the local aggregate" convention
    /// <see cref="TaskPublishCommand"/> already uses for its own publish-then-assign composition),
    /// so <see cref="TaskDecider.ClaimInteractively"/>'s own guard (Queued, and this owner's own
    /// work) sees exactly the state the assignment just decided.
    /// <para>
    /// A Published task whose dependencies have not all closed out is refused outright, before
    /// either event is built: <see cref="TaskDecider.ClaimInteractively"/> needs Queued, not
    /// Blocked, and this command's whole point is claiming the task now — landing it Blocked and
    /// stopping there would silently turn a one-command interactive claim into a way to start
    /// (or half-start) work whose blockers are still open, exactly the outcome dispatch itself
    /// already refuses for a headless run.
    /// </para>
    /// </summary>
    internal static (TaskAssigned Assigned, TaskClaimed Claimed) PrepareInteractiveClaimFromPublished(
        TaskAggregate task, Guid ownerId, IReadOnlyList<TaskDependency> dependencies, Guid runId, DateTimeOffset now)
    {
        TaskAssigned assigned = TaskDecider.Assign(task, ownerId, dependencies, now, ownerId);
        if (assigned.UnmetDependencies.Count > 0)
        {
            IReadOnlyList<TaskDependency> unmet =
                [.. dependencies.Where(dependency => assigned.UnmetDependencies.Contains(dependency.Id))];
            throw new DomainBusinessRuleException(
                $"Task {task.Id} depends on {unmet.Count} task(s) that have not closed out, the same bar "
                + "dispatch itself holds an assignment to, so an interactive claim will not start it while "
                + "they are open: " + string.Join("; ", unmet.Select(dependency => dependency.Describe())) + ". "
                + $"{DescribeUnmetDependencyAdvice(task.Id, unmet)} h9k task show {task.Id} for the full picture.");
        }

        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, ownerId, runId, now);
        return (assigned, claimed);
    }

    /// <summary>
    /// The advice half of the unmet-dependency refusal above. A dependency that can still reach
    /// true closeout gets the ordinary "it queues itself" promise — the same one
    /// <see cref="TaskAssignCommand.AnnounceAsync"/> already makes on the identical fact pattern —
    /// but a dead one (<see cref="TaskDependency.IsDead"/>) never will, so making that promise for
    /// it tells the operator to wait on a merge that can never happen; <see cref="TaskDependency.DescribeDeath"/>
    /// already says the honest thing instead (independent pre-PR review, cycle 1).
    /// </summary>
    private static string DescribeUnmetDependencyAdvice(Guid taskId, IReadOnlyList<TaskDependency> unmet)
    {
        IReadOnlyList<TaskDependency> dead = [.. unmet.Where(dependency => dependency.IsDead)];
        if (dead.Count == 0)
        {
            return $"h9k task assign {taskId} to hold it Blocked until they clear (it queues itself the moment "
                + "the last one's pull request merges), or";
        }

        string deathAdvice = string.Join(" ", dead.Select(dependency => dependency.DescribeDeath() + "."));
        return dead.Count == unmet.Count
            ? deathAdvice
            : deathAdvice + " The live ones can still close out on their own, but this task will not queue "
              + $"until the dead one is gone too — h9k task assign {taskId} only holds it Blocked, and "
              + "waiting will not clear that on its own, or";
    }

    /// <summary>
    /// Reads what actually landed after this operator's atomic assign-and-claim lost the
    /// database's optimistic-concurrency race (task 688a1ccf-h9k) — the loser is told honestly who
    /// won rather than only that the task changed, since two operators (or an operator racing
    /// another owner's headless <c>h9k task assign</c>) racing the same Published task is exactly
    /// the collision this atomic path exists to arbitrate.
    /// </summary>
    internal static async Task<DomainConflictException> DescribeAssignAndClaimRaceLossAsync(
        DocumentStore store, Guid taskId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? current = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (current is null)
        {
            return new DomainConflictException(
                $"Task {taskId} changed while claiming it, and no longer has a record — check h9k status "
                + "to see where it stands.");
        }

        if (current.State != TaskState.Claimed)
        {
            // Something else committed first, but that write never claimed the task — a plain
            // h9k task assign that landed Queued or Blocked, most likely — so there is no
            // claimant to name. Saying so honestly beats asserting a claim that may never have
            // happened (AGENTS.md: never guess at unobserved facts).
            return new DomainConflictException(
                $"Task {taskId} changed while claiming it — it now reads {current.State.Value}, not Claimed. "
                + $"Most likely a benign concurrent append; h9k task work {taskId} to try again, or h9k status "
                + "to see where it stands.");
        }

        OwnerDetails? owner = current.AssignedOwnerId is { } ownerId
            ? await session.LoadAsync<OwnerDetails>(ownerId, cancellationToken)
            : null;
        string ownerName = owner?.Name ?? current.AssignedOwnerId?.ToString() ?? "an unknown owner";
        string winner = current.IsInteractiveClaim
            ? $"another operator claimed it interactively first, for {ownerName}"
            : $"a node claimed it for headless dispatch first, for {ownerName}";

        return new DomainConflictException(
            $"Task {taskId} was claimed by someone else first — {winner}. "
            + "h9k status to see where it stands.");
    }

    private static async Task FailInteractiveClaimAsync(
        DocumentStore store, Guid taskId, long claimedVersion, Guid runId, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        if (fence is null || fence.Version != claimedVersion)
        {
            // Something else already moved the task past the claim just made (a release, a
            // handback landing concurrently) — nothing here to fail.
            return;
        }

        TaskAggregate? current = await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken);
        if (current is null || !TaskDecider.CanFail(current))
        {
            return;
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1,
            TaskDecider.Fail(current, runId, $"Interactive claim setup failed: {reason}", DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
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
            new WorktreeRequest(
                project.RepositoryPath, project.BaseBranch, taskId, runId, taskDetails.Objective,
                project.BranchNameTemplate, taskDetails.ExternalReference),
            cancellationToken);
        return (fresh, false);
    }

    private static async Task AppendSessionStartedAsync(
        DocumentStore store, Guid runId, Guid claudeSessionId, int processId, DateTimeOffset startedAt,
        string sessionName, CancellationToken cancellationToken)
    {
        await using IDocumentSession startSession = store.LightweightSession();
        // MachineName is what lets InteractiveSessionLiveness tell "this session, checkable on
        // this machine's own process table" from "a pid recorded by some other machine sharing
        // the database" (adversarial review, cycle 2) — an interactive claim's RunDispatched
        // carries no usable node identity (NodeId is deliberately the Guid.Empty sentinel, so
        // NodeLoad's ceiling never counts it), so this event is the only place that identity is
        // ever recorded.
        startSession.Events.Append(runId, new InteractiveSessionStarted(
            runId, claudeSessionId, startedAt, processId, Environment.MachineName, sessionName));
        await startSession.SaveChangesAsync(cancellationToken);
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

    private static async Task<(int ExitCode, bool SessionStartRecorded)> LaunchInteractiveClaudeAsync(
        string worktreePath, string prompt, Guid sessionId, string settingsFile, bool skipPermissions, Guid runId,
        string sessionName, Func<int, DateTimeOffset, Task> onStarted, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ClaudeBinary(),
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
        };
        // Inherited by claude and every descendant it spawns, so a nested h9k task verify the
        // operator runs from inside this very session can recognise itself: it is the one case
        // InteractiveSessionLiveness's pid check cannot tell apart from a second terminal, since
        // that session is blocked waiting on the command it just started rather than racing it
        // (conformance review, cycle 2).
        process.StartInfo.EnvironmentVariables[InteractiveSessionLiveness.InteractiveRunEnvironmentVariable] = runId.ToString();
        process.StartInfo.ArgumentList.Add("--session-id");
        process.StartInfo.ArgumentList.Add(sessionId.ToString());
        // Verified against `claude --help` and confirmed empirically (task: every dispatched
        // agent session launches under a human-readable id-and-role name): -n/--name is what
        // `~/.claude/sessions/<pid>.json` records as this session's name, which is what
        // `claude agents --json` and another session's cross-session mesh
        // (ListAgents/SendMessage) address it by.
        process.StartInfo.ArgumentList.Add("--name");
        process.StartInfo.ArgumentList.Add(sessionName);
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

        // Entered before Start() and held for the child's whole lifetime: Program.cs's global
        // Ctrl-C handler reads this to suppress its own escalate-to-terminate window while this
        // child is attached, since repeated Ctrl-C here is legitimate input to it — including
        // the double-tap that is Claude Code's own exit gesture — not an instruction to kill h9k
        // (adversarial review, cycle 4: a second press used to fall through to SIGINT's default
        // action and terminate h9k before AppendSessionEndedAsync ever ran).
        using IDisposable interactiveChildScope = InteractiveChildGuard.Enter();
        process.Start();
        DateTimeOffset startedAt = ReadStartedAt(process);
        bool sessionStartRecorded = true;
        try
        {
            await onStarted(process.Id, startedAt);
        }
        catch (Exception exception)
        {
            // The child is already alive and attached to this terminal at this point — a failure
            // recording it (a transient database error; the caller's own CancellationToken.None
            // already rules out a cancelled-token race here) must never propagate out of this
            // method and orphan that live process, since nothing below would then wait for it to
            // exit or ever try the append again (adversarial review, cycle 3). Worst case with
            // this catch is an unrecorded session until the operator re-enters or ends it —
            // recoverable, and exactly the launch-never-started case's inverse this method's own
            // comment above already names as unhandled. sessionStartRecorded stays false so the
            // caller never appends an InteractiveSessionEnded with nothing to pair it (conformance
            // review, cycle 4).
            sessionStartRecorded = false;
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not record the interactive session start ({exception.Message}); continuing.[/]");
        }

        // The child shares h9k's foreground process group, so the terminal delivers Ctrl-C to it
        // directly and independently of the token below — the ordinary Claude Code keystroke for
        // "stop generating", not "quit". h9k's own token cancels on that same keystroke
        // (Program.cs's CancelKeyPress handler), but killing the tree in response used to tear
        // down a session the child chose to keep running (conformance + adversarial review,
        // cycle 1). Falling back to an unconditional wait leaves that choice with the child,
        // exactly as an unwrapped `claude` invocation would; every further Ctrl-C past this point
        // is suppressed by the guard above too, so it reaches only the child, never h9k's own
        // SIGINT default action.
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return (process.ExitCode, sessionStartRecorded);
    }

    private static string ClaudeBinary() =>
        Environment.GetEnvironmentVariable("HALL9K_CLAUDE_PATH") ?? "claude";

    private static readonly string[] WindowsScriptExtensions = [".cmd", ".bat", ".ps1"];

    /// <summary>
    /// Whether <paramref name="claudeBinary"/> resolves, on this machine, to a script this
    /// command cannot launch directly (see the caller's own comment). No-op off Windows, and
    /// no-op wherever the name resolves to a real executable: CreateProcess already appends
    /// only <c>.exe</c> when searching PATH for a bare name (DaemonEnvironment.ResolvesAsGiven's
    /// own doc comment names this same PATHEXT gap for the existence check <c>h9k doctor</c>
    /// runs; this is the launch-time counterpart), so a bare "claude" silently finds nothing on
    /// an npm install that only ships <c>claude.cmd</c> — the search below mirrors PATHEXT to
    /// catch that before Process.Start ever throws.
    /// </summary>
    private static string? DetectWindowsScriptShim(string claudeBinary)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (Path.IsPathRooted(claudeBinary))
        {
            return WindowsScriptExtensions.Contains(Path.GetExtension(claudeBinary), StringComparer.OrdinalIgnoreCase)
                && File.Exists(claudeBinary)
                ? claudeBinary
                : null;
        }

        string[] searchDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // An .exe alongside the shim wins — CreateProcess would find it too, so there is
        // nothing to refuse.
        if (searchDirectories.Any(directory => File.Exists(Path.Combine(directory, claudeBinary + ".exe"))))
        {
            return null;
        }

        return searchDirectories
            .SelectMany(directory => WindowsScriptExtensions.Select(extension => Path.Combine(directory, claudeBinary + extension)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Mirrors Hall9k.Daemon.ProcessManagement.ProcessManagerBase.ReadStartedAt exactly — not
    /// referenced, because the CLI cannot reference the daemon project (Reference graph:
    /// Cli -> Domain + Connectors). Reads the just-started process's own start time rather than
    /// stamping <see cref="DateTimeOffset.UtcNow"/>: stamping "now" risks a false match in
    /// InteractiveSessionLiveness.IsAlive if the child dies within milliseconds of Start() and
    /// the OS recycles its pid for an unrelated process inside the 2-second tolerance both
    /// checks use (adversarial review, cycle 4). DateTimeOffset.MinValue is recorded instead of
    /// a plausible-looking guess when the process's own start time cannot be read — AGENTS.md's
    /// "never guess at unobserved facts" — which guarantees no later liveness check ever matches
    /// a real process's start time against it.
    /// </summary>
    private static DateTimeOffset ReadStartedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
