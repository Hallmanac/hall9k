using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
/// paths call, with its working rules swapped for an attached operator). On a Published task
/// assigned to nobody, this command assigns it to the operator's own owner and claims it
/// interactively in the same atomic event append (task 688a1ccf-h9k, 2026-09-02): the task is
/// never observably Queued in between, so the dispatcher — woken within moments by the doorbell
/// notification a plain <c>h9k task assign</c> would have sent — can never win the race to it. A
/// Published task whose dependencies have not all closed out is refused, naming the open
/// blockers, the same bar dispatch itself holds an assignment to (<see cref="TaskDependency"/>).
/// The claim is held by the human, not a process: no <c>TaskLease</c> is written, so there is
/// nothing for a heartbeat to renew or an expiry sweep to reclaim, and closing the terminal is a
/// normal way to leave — the task stays Claimed, and re-running this command re-enters the same
/// worktree and branch (the two connectors below differ on what happens from there: a fresh
/// prompt by default, or the recorded conversation resumed under <c>--direct-launch</c>). An
/// interactive claim occupies zero concurrency slots: it never creates a node-owned run
/// (RunDispatched records NodeId as the sentinel <see cref="Guid.Empty"/>, which
/// <c>NodeLoad</c>'s ceiling measurement never counts), so it starts even when the daemon's
/// session ceiling is fully consumed and never competes with headless dispatch throughput.
/// <para>
/// <b>Default: the prompt-handoff connector (R4, idea fcaded0b's design rulings, Take the Wheel
/// epic 9272e514's slice 7).</b> This command claims the task and cuts the worktree exactly as
/// before, then prints the worktree path, the branch, and a starting prompt — it no longer
/// launches or waits on a Claude Code process itself. The operator pastes that prompt into a
/// Claude Code session started anywhere; the prompt carries the coordinates and tells the session
/// to self-register through <c>h9k task register-session</c> (replacing the launch-time pid
/// observation the direct-launch path below still performs), which is what lets the
/// double-booking and liveness guards (re-entry, verify, deliver, handback, release) recognise
/// it. A session that never registers gets the honest degradation every guard already had for a
/// task nobody ever recorded a session against — a silent no-op, not a false block. Re-entry here
/// always opens with a fresh prompt rather than resuming a prior conversation: the operator's own
/// pasted-in session is not one this command ever launched, so there is nothing of its own for
/// this command to reattach to.
/// </para>
/// <para>
/// <b><c>--direct-launch</c>: the prior behavior, kept for one release.</b> Launches a plain
/// interactive <c>claude</c> process attached to this terminal exactly as this command always
/// did, recording <see cref="Events.InteractiveSessionStarted"/> itself from
/// <c>Process.Start()</c>'s own pid the moment it is alive. Re-entering here resumes the most
/// recently recorded interactive session's own conversation (<c>claude --resume</c>), falling
/// back to a fresh session — said out loud, never silently — when the recorded one cannot be
/// resumed (PLAN.md §16 #124, a deliberate reversal of #103's original "always fresh" opening
/// move). The Windows script-shim refusal (<see cref="DetectWindowsScriptShim"/>) applies only
/// here: a pasted prompt travels through no argv, so the cmd.exe embedded-newline problem that
/// refusal exists for cannot occur on the default path above.
/// </para>
/// <para>
/// A task with unmet dependencies — Published and newly assigned in this same atomic entry, or
/// already sitting Blocked from an earlier <c>h9k task assign</c> or a handed-back/retried
/// deliberate claim — warns rather than refuses outright (task 0ac72cb8-h9k, design ruling R7):
/// the platform names every open blocker, and <c>--acknowledge-unmet-dependencies</c> is the
/// human's recorded override to claim it anyway, the same shape <c>h9k task start</c> already has
/// (task 8a56af78-h9k). An acknowledgment this task already carries from an earlier claim on the
/// same still-open blockers is honored without the flag needing to be passed again — the stream
/// shows whether a claim's own acknowledgment was fresh or carried forward
/// (<see cref="TaskClaimed.DependencyOverrideCarriedForward"/>).
/// </para>
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

        [CommandOption("--direct-launch")]
        [Description(
            "Launch a plain interactive Claude Code process attached to this terminal yourself, the way this "
            + "command always did, instead of printing a prompt to paste into a session you start on your own. "
            + "Kept for one release; the prompt-handoff default is the supported path going forward. Refused on "
            + "a machine where Claude Code resolves to a Windows script shim (.cmd/.bat/.ps1) — the opening "
            + "prompt cannot travel through cmd.exe's argv with its newlines intact.")]
        public bool DirectLaunch { get; init; }

        [CommandOption("--acknowledge-unmet-dependencies")]
        [Description(
            "Claim a task even though not every dependency has closed out yet — the platform names the open "
            + "blockers first; this is your recorded override to claim it anyway. Not needed when this task "
            + "already carries a covering acknowledgment from an earlier claim on the same still-open blockers.")]
        public bool AcknowledgeUnmetDependencies { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // Checked before anything is claimed, not inside the launch's own catch (adversarial
        // review, cycle 8), and only for --direct-launch: the prompt-handoff default below never
        // puts the prompt on an argv at all (the operator pastes it as a chat message into a
        // session they started themselves), so cmd.exe's embedded-newline problem — every
        // .cmd/.bat/.ps1 shim (the shape an npm-installed Claude Code takes on Windows) ultimately
        // runs through it, and it treats an embedded newline as a command separator, not literal
        // argument content — cannot occur there. There is no quoting fix for that (WindowsCommandLine's
        // own extra-quote wrapping only survives embedded quotes, not embedded newlines), so
        // --direct-launch is refused up front rather than left to strand a claim nobody can ever enter.
        if (settings.DirectLaunch && DetectWindowsScriptShim(ClaudeBinary()) is { } shimPath)
        {
            throw new DomainConflictException(
                $"Claude Code resolves to a script ({shimPath}) on this machine, which h9k task work --direct-launch "
                + "cannot launch: an interactive claim's opening prompt travels as a multi-line command-line "
                + "argument, and cmd.exe — which every .cmd/.bat/.ps1 shim runs through — cannot carry embedded "
                + "newlines in one. Drop --direct-launch to get the prompt-handoff default instead (paste the "
                + "printed prompt into a Claude Code session you start yourself — no argv involved), or install "
                + "Claude Code's native Windows build so `claude` resolves to an .exe.");
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

        // Minted once per invocation, whether or not it ends up used: a fresh claim records it
        // as RunDispatched.SessionId — the same "session id is the first spawned session's own
        // id" convention headless dispatch's RunLauncher follows — and a re-entry keeps it in
        // reserve as the fallback session id, used only if resuming the recorded conversation
        // (below) turns out not to be possible.
        Guid claudeSessionId = DomainId.New();

        // Every re-entry launches under the same name (task: every dispatched agent session
        // launches under a human-readable id-and-role name) — the interactive claim is one
        // named session across however many attach/detach cycles it takes, not a fresh identity
        // each time.
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.InteractiveClaim);

        (Guid runId, string worktreePath, string branch, string runDirectory, bool resumesPreviousWork, bool crossMachineNoticeShown, Guid? previousClaudeSessionId) = task.State == TaskState.Claimed && task.IsInteractiveClaim
            ? await ReenterAsync(session, task, settings.Force, cancellationToken)
            : await ClaimAndCutAsync(
                store, session, task, fence, context, claudeSessionId, sessionName,
                settings.AcknowledgeUnmetDependencies, cancellationToken);

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
            isInteractive: true, requiresSelfRegistration: !settings.DirectLaunch);

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

        return settings.DirectLaunch
            ? await LaunchDirectlyAsync(
                store, session, taskId, runId, worktreePath, branch, prompt, claudeSessionId, previousClaudeSessionId,
                settingsFile, project.SkipPermissions, sessionName, cancellationToken)
            : PrintPromptHandoff(worktreePath, branch, prompt, settingsFile);
    }

    /// <summary>
    /// The default connector (R4): the claim and the worktree cut already happened exactly as
    /// <c>--direct-launch</c>'s own path leaves them — this only differs in what happens with the
    /// result. No process is started and nothing about this run is recorded here; the operator's
    /// own pasted-in session records itself through <c>h9k task register-session</c> once it
    /// exists, which is the honest reason this prints rather than launches.
    /// </summary>
    private static int PrintPromptHandoff(string worktreePath, string branch, string prompt, string settingsFile)
    {
        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {worktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {branch}[/]");
        // settingsFile can trace back to a user-set HALL9K_HOME, so it is escaped explicitly before
        // landing in a plain (non-Interpolated) MarkupLine call — the message is built with
        // ordinary string concatenation across lines, which an interpolated-string literal cannot
        // be split across while still binding to the handler overload that would escape it itself.
        string escapedSettingsFile = Markup.Escape(settingsFile);
        AnsiConsole.MarkupLine(
            $"[dim]Settings file (recommended): {escapedSettingsFile} — pass --settings {escapedSettingsFile} to "
            + "your own claude invocation for this platform's required conventions (no co-authored-by trailers, "
            + "longer command-tool timeouts).[/]");
        AnsiConsole.MarkupLine(
            "[dim]Paste the prompt below into a Claude Code session started anywhere — its own terminal, this "
            + "one once you exit h9k, wherever suits you:[/]");
        AnsiConsole.WriteLine();
        // WriteLine, not MarkupLine: the prompt is the operator's to paste verbatim, and Spectre
        // would try to parse any [..] it happened to contain as markup instead of printing it.
        // ExternalText.ForTerminal first: the prompt embeds the task's own objective and agent
        // context, which since adoption (PLAN.md §3.1a) can be quoting an issue title or body
        // written by anyone who could file one — printed raw, a control or bidirectional-override
        // character in that text would be obeyed by this terminal rather than shown, exactly the
        // attack ExternalText's own doc comment names. Safe to run over the whole composed prompt,
        // platform-authored sections included: RelayedText.Printable keeps ordinary markdown
        // (newlines, tabs, backticks, headings) and drops only what a sink would obey instead of
        // display.
        AnsiConsole.WriteLine(ExternalText.ForTerminal(prompt));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// <c>--direct-launch</c>'s own path, kept for one release exactly as this command always
    /// behaved: launches a plain interactive <c>claude</c> process attached to this terminal,
    /// waits for it to exit, and prints the same deliver/verify/work/handback/release menu this
    /// command always has. Everything before the launch (the claim, the worktree cut, the prompt,
    /// the settings file, the pre-launch re-checks) is identical to the default path above; this
    /// method starts exactly where the two paths diverge.
    /// </summary>
    private static async Task<int> LaunchDirectlyAsync(
        DocumentStore store, IDocumentSession session, Guid taskId, Guid runId, string worktreePath, string branch,
        string prompt, Guid claudeSessionId, Guid? previousClaudeSessionId, string settingsFile, bool skipPermissions,
        string sessionName, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {worktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {branch}[/]");
        AnsiConsole.MarkupLine(previousClaudeSessionId is not null
            ? "[dim]Resuming the recorded interactive session — exit it normally (Ctrl+D or /exit) to return here.[/]"
            : "[dim]Launching an interactive Claude Code session — exit it normally (Ctrl+D or /exit) to return here.[/]");

        // A resume attaches to the same live conversation the operator already had; that
        // conversation's own prior turns already produced the objective, acceptance criteria,
        // context and working rules, so replaying the whole `prompt` document as a fresh user
        // turn on every re-attach would duplicate a multi-thousand-token message every time —
        // including the "review what's already in the worktree" guidance aimed at a fresh
        // session inheriting someone else's state, which is redundant against a conversation
        // that already knows that state (adversarial review, cycle 1, low). The fresh-session
        // fallback below still passes `prompt` itself — it genuinely is a new conversation with
        // nothing of its own to pick back up from.
        const string reentryPrompt =
            "You're back — an operator re-entered this task's interactive claim (`h9k task work`) "
            + "after leaving the terminal. This is the same conversation, not a new task: pick up "
            + "exactly where you left off.";

        // Local, not a private method: it closes over everything a single launch attempt needs
        // (worktree/prompt/settings from above, store/runId/sessionName for the two event
        // appends), so a resume attempt and its fresh-session fallback below are just two calls
        // to the same attempt rather than two hand-duplicated blocks.
        async Task<(int ExitCode, bool ResumeNotFound, (Guid SessionId, DateTimeOffset EndedAt)? PendingEndedSession)> AttemptAsync(
            Guid sessionIdToLaunch, bool resume, (Guid SessionId, DateTimeOffset EndedAt)? pendingEndedSession = null)
        {
            int attemptExitCode;
            bool sessionStartRecorded;
            bool resumeNotFound;
            DateTimeOffset exitedAt;
            try
            {
                // InteractiveSessionStarted appends only once the process is actually alive
                // (from inside LaunchInteractiveClaudeAsync's onStarted callback, with its real
                // pid) rather than pre-emptively here: recording it before the process exists
                // left ProcessId unobservable, so no other command could ever tell this worktree
                // had a live attached session (adversarial review, cycle 1) — and a launch that
                // never starts (the claude binary missing, the worktree vanishing) now never
                // appends a started event with nothing to pair it, instead of needing an ended
                // event to close a pairing that never really began. This holds for a resume
                // attempt too: a resume that turns out not to find a matching conversation still
                // really started a process with a real pid, so it is recorded and paired exactly
                // like any other attempt rather than left invisible.
                (attemptExitCode, sessionStartRecorded, resumeNotFound, exitedAt) = await LaunchInteractiveClaudeAsync(
                    worktreePath, resume ? reentryPrompt : prompt, sessionIdToLaunch, settingsFile, skipPermissions, runId,
                    sessionName, resume,
                    // CancellationToken.None: by the time this runs, process.Start() has already
                    // spawned a real, terminal-attached claude — a Ctrl-C landing in the window
                    // before this append completes must not turn into a lost append (adversarial
                    // review, cycle 3), the same reasoning AppendSessionEndedAsync's own call
                    // already applies. pendingEndedSession, when this is the fallback attempt
                    // following a not-found resume, rides in the same transaction as this
                    // attempt's own Started event (adversarial review, cycle 1: appending them
                    // separately left a window where ActiveSessions read empty — the failed
                    // resume's Ended already landed, this attempt's Started had not yet — and a
                    // second h9k task work from another terminal could pass
                    // EnsureNotAttachedElsewhere and launch a second claude into this worktree).
                    (processId, startedAt) => AppendSessionStartedAsync(
                        store, runId, sessionIdToLaunch, processId, startedAt, sessionName, pendingEndedSession, CancellationToken.None),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // The claude binary is missing, or the worktree directory vanished before the
                // process could even start: nothing was recorded, so the claim is preserved with
                // no run history to close out. A pendingEndedSession this attempt never got to
                // append stays unrecorded too — the same degraded-but-recoverable shape as
                // sessionStartRecorded staying false below: EnsureNotAttachedElsewhere checks the
                // recorded pid against the live process table, so a stale ActiveSessions entry
                // for the earlier, now-dead resume attempt does not block a later re-entry.
                throw new DomainConflictException(
                    $"Could not launch the interactive Claude Code session for task {taskId}: {exception.Message} "
                    + $"The claim is preserved — h9k task work {taskId} to try again, or h9k task release {taskId} to give it back.");
            }

            // Only when InteractiveSessionStarted actually landed (conformance review, cycle 4):
            // a transient database error inside LaunchInteractiveClaudeAsync's own onStarted
            // callback is swallowed there rather than propagated, so an ended event with no
            // started event to pair it would otherwise be recorded — a shape
            // InteractiveSessionStarted's own doc comment establishes only the other direction
            // (an unmatched started is normal) as expected. Always CancellationToken.None: while
            // the child was attached, Program.cs suppresses Ctrl-C entirely rather than
            // cancelling the shared token, so a press during the session leaves it uncancelled
            // by the time execution reaches here — but a press landing in the narrow window
            // after InteractiveChildGuard is disposed and before this line runs still escalates
            // and cancels it, and the interactive session's own exit is real regardless of that
            // race — it must never be lost to a token cancelled by a keystroke that arrived too
            // late to mean anything else (conformance review, cycle 1).
            //
            // A not-found resume is the one case this Ended is deferred rather than appended
            // here: the caller always retries a not-found resume with a fallback attempt, so
            // returning sessionIdToLaunch (paired with the resume attempt's own observed exit
            // time, not the fallback's later start time — independent pre-PR review, cycle 1)
            // lets that fallback bundle this attempt's Ended with its own Started in one
            // transaction instead of two, closing the window described above.
            if (sessionStartRecorded && resumeNotFound)
            {
                return (attemptExitCode, resumeNotFound, (sessionIdToLaunch, exitedAt));
            }

            // Re-read rather than assumed still Dispatched/Running: the widened self-invocation
            // exemption (Decisions Log #126) lets this very child call h9k task deliver or handback
            // on itself while still attached, which moves this run past Dispatched/Running under the
            // same runId deliver's own AgentSessionCompleted append does — and RunDetailsProjection's
            // EndSessions clears ActiveSessions unconditionally, with no role filter, so an
            // unconditional append here would wipe the review/fix sessions the daemon's pipeline has
            // already recorded on this exact stream by the time the child exits (independent pre-PR
            // review, adversarial lens, cycle 1). A run still sitting at Dispatched/Running was never
            // handed off, so this is the ordinary exit case exactly as before.
            if (sessionStartRecorded)
            {
                RunDetails? runAfterExit = await session.LoadAsync<RunDetails>(runId, CancellationToken.None);
                if (runAfterExit is not null
                    && (runAfterExit.State == RunState.Dispatched || runAfterExit.State == RunState.Running))
                {
                    await AppendSessionEndedAsync(store, runId, sessionIdToLaunch, CancellationToken.None);
                }
            }

            return (attemptExitCode, resumeNotFound, null);
        }

        // Only a re-entry ever carries a previously recorded session id (a fresh claim has
        // nothing to resume). Resuming is attempted first, on the operator's own recorded
        // conversation; the pre-minted claudeSessionId above is the fallback, used only if the
        // attempt below reports the recorded conversation could not be found — announced, never
        // silent, since silently swapping which conversation an operator is talking to would be
        // exactly the kind of unobserved-fact guess AGENTS.md rules out.
        (int exitCode, bool resumeNotFound, (Guid SessionId, DateTimeOffset EndedAt)? pendingEndedSession) = previousClaudeSessionId is { } previousSessionId
            ? await AttemptAsync(previousSessionId, resume: true)
            : await AttemptAsync(claudeSessionId, resume: false);
        if (resumeNotFound)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Task {taskId}'s recorded interactive session could not be resumed (no matching conversation found) — starting a fresh session instead.[/]");
            (exitCode, _, _) = await AttemptAsync(claudeSessionId, resume: false, pendingEndedSession);
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
            AnsiConsole.MarkupLineInterpolated($"[dim]  h9k task work {taskId}       re-enter (prints a fresh prompt; add --direct-launch to resume this conversation)[/]");
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

    /// <summary>Internal rather than private: <see cref="TaskStartCommand"/> shares this exact load (task 8a56af78-h9k).</summary>
    internal static async Task<string?> LoadBlockerContextAsync(
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

    internal static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork, bool CrossMachineNoticeShown, Guid? PreviousClaudeSessionId)> ReenterAsync(
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
        // worktree. Under --direct-launch, re-entry resumes that recorded conversation itself
        // (--resume) rather than handing a fresh session the same prompt to rediscover it
        // (Decisions Log #124); the prompt-handoff default (#126) never launches or resumes
        // anything itself, so this value simply goes unused on that path.
        // run.InteractiveClaudeSessionId is the most recently recorded InteractiveSessionStarted's
        // own ClaudeSessionId. When none has ever landed for this run, this falls back to
        // run.SessionId — the id RunDispatched recorded before any process ever started, the same
        // "session id is the first spawned session's own id" convention ClaimAndCutAsync follows —
        // because a first launch that spawned claude and then failed to record its own
        // InteractiveSessionStarted (a transient database error, swallowed in
        // LaunchInteractiveClaudeAsync's own onStarted catch) still spawned it under exactly that
        // id: the conversation is real and sitting on disk even though nothing durable ever named
        // it (adversarial review, cycle 2). A launch that never reached Process.Start() at all
        // carries the identical fallback value with nothing on disk to match it, and --resume
        // against it simply reports no matching conversation — the same announced, never-silent
        // fresh-session fallback below already handles either way.
        return (runId, run.WorktreePath, run.Branch, run.RunDirectory, ResumesPreviousWork: true, crossMachineNoticeShown, run.InteractiveClaudeSessionId ?? run.SessionId);
    }

    internal static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork, bool CrossMachineNoticeShown, Guid? PreviousClaudeSessionId)> ClaimAndCutAsync(
        DocumentStore store, IDocumentSession session, TaskAggregate task, StreamState fence, BootstrapContext context,
        Guid claudeSessionId, string sessionName, bool acknowledgeUnmetDependencies, CancellationToken cancellationToken)
    {
        // Published is the atomic entry (task 688a1ccf-h9k): the dependency snapshot is loaded
        // here, before any other check, because it decides whether this claim is even possible.
        // dependencies stays null for the ordinary Queued entry, which is what tells the append
        // step below whether an assignment travels in the same atomic batch as the claim.
        //
        // Blocked (task 0ac72cb8-h9k, "claiming or starting a task interactively across
        // dependency edges warns and asks instead of refuses"): the task was already assigned —
        // by an ordinary h9k task assign, or by an earlier deliberate claim that was handed back
        // or retried — so no assignment travels here either, but the still-open blockers are
        // loaded the same way so the warn-and-acknowledge path below can name them, the same bar
        // the just-assigned Published case holds an assignment to.
        IReadOnlyList<TaskDependency>? dependencies = null;
        IReadOnlyList<TaskDependency>? unmetAtEntry = null;
        if (task.State == TaskState.Published)
        {
            dependencies = await TaskDependencyQuery.LoadAsync(session, task.BlockedBy, cancellationToken);
        }
        else if (task.State != TaskState.Queued && task.State != TaskState.Blocked)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a Published, Queued, or Blocked task (or one you "
                + "already hold interactively) can be worked this way. " + task.State switch
                {
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
        else if (task.State == TaskState.Blocked)
        {
            unmetAtEntry = await TaskDependencyQuery.LoadAsync(session, task.UnmetDependencies, cancellationToken);
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
        // holding. unmetAtEntry is non-null only for the already-Blocked entry: no assignment
        // travels there either, but the warn-and-acknowledge shape is identical (task 0ac72cb8-h9k).
        TaskAssigned? assigned = null;
        TaskClaimed claimed;
        IReadOnlyList<TaskDependency> unmet = [];
        bool carriedForward = false;
        if (dependencies is not null)
        {
            (assigned, claimed, unmet) = PrepareInteractiveClaimFromPublished(
                task, context.OwnerId, dependencies, runId, claimedAt, acknowledgeUnmetDependencies);
        }
        else if (unmetAtEntry is not null)
        {
            (claimed, carriedForward) = PrepareInteractiveClaimFromBlocked(
                task, context.OwnerId, unmetAtEntry, runId, claimedAt, acknowledgeUnmetDependencies);
            unmet = unmetAtEntry;
        }
        else
        {
            claimed = TaskDecider.ClaimInteractively(task, context.OwnerId, runId, claimedAt);
        }

        if (unmet.Count > 0)
        {
            AnsiConsole.MarkupLine(carriedForward
                ? $"[yellow]Claiming task {task.Id} despite {unmet.Count} unmet dependenc"
                  + (unmet.Count == 1 ? "y" : "ies")
                  + " — already acknowledged by an earlier claim on this task, so nothing new was asked:[/]"
                : $"[yellow]Claiming task {task.Id} despite {unmet.Count} unmet dependenc"
                  + (unmet.Count == 1 ? "y" : "ies") + " (--acknowledge-unmet-dependencies):[/]");
            foreach (TaskDependency dependency in unmet)
            {
                // ExternalText.OneLineMarkup, not MarkupLineInterpolated's own hole-escaping alone
                // (mirrors h9k task start's own identical warning list, adversarial review, cycle 1
                // there): a dependency adopted with --from-issue carries an objective anyone who
                // can file an issue in that repo wrote.
                AnsiConsole.MarkupLine($"[yellow]  - {ExternalText.OneLineMarkup(dependency.Describe())}[/]");
            }
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
        // A fresh claim has no prior conversation to resume — PreviousClaudeSessionId is null
        // unconditionally here, never populated from claudeSessionId itself, which names the
        // session about to be launched for the first time, not one already recorded.
        return (runId, worktree.Path, worktree.Branch, runDirectory, resumesPreviousWork, CrossMachineNoticeShown: false, PreviousClaudeSessionId: null);
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
    /// A Published task whose dependencies have not all closed out warns and proceeds on
    /// acknowledgment rather than refusing outright (task 0ac72cb8-h9k, converting this atomic
    /// entry's own refuse-with-blockers-named exit the same way h9k task start's identical
    /// Published entry already converted, task 8a56af78-h9k): it refuses only when
    /// <paramref name="acknowledgeUnmetDependencies"/> is false, naming every open blocker in the
    /// refusal so the human can decide with the platform's own advice in hand; when true, the
    /// claim proceeds anyway and the override is recorded on the resulting
    /// <see cref="TaskClaimed.DependencyOverrideAcknowledged"/>. A fresh assignment always clears
    /// any acknowledgment this task carried from an earlier claim
    /// (<see cref="TaskAggregate.Apply(Events.TaskAssigned)"/>), so there is nothing to carry
    /// forward here — unlike <see cref="PrepareInteractiveClaimFromBlocked"/>, this entry only
    /// ever asks fresh.
    /// </para>
    /// </summary>
    internal static (TaskAssigned Assigned, TaskClaimed Claimed, IReadOnlyList<TaskDependency> UnmetDependencies) PrepareInteractiveClaimFromPublished(
        TaskAggregate task, Guid ownerId, IReadOnlyList<TaskDependency> dependencies, Guid runId, DateTimeOffset now,
        bool acknowledgeUnmetDependencies)
    {
        TaskAssigned assigned = TaskDecider.Assign(task, ownerId, dependencies, now, ownerId);
        IReadOnlyList<TaskDependency> unmet =
            [.. dependencies.Where(dependency => assigned.UnmetDependencies.Contains(dependency.Id))];

        if (unmet.Count > 0 && !acknowledgeUnmetDependencies)
        {
            // ExternalText.OneLine, not a raw interpolation (mirrors h9k task start's own
            // identical refusal): Program.cs prints this message with plain
            // Console.Error.WriteLineAsync, so there is no Spectre markup to escape, but a
            // control character or bidirectional override in an adopted dependency's objective
            // would otherwise reach the terminal exactly as raw.
            throw new DomainBusinessRuleException(
                $"Task {task.Id} depends on {unmet.Count} task(s) that have not closed out, the same bar "
                + "dispatch itself holds an assignment to: "
                + string.Join("; ", unmet.Select(dependency => ExternalText.OneLine(dependency.Describe()))) + ". "
                + "The platform advises rather than refuses here: "
                + $"h9k task work {task.Id} --acknowledge-unmet-dependencies to claim it anyway, once you have "
                + $"confirmed that is what you want. {DescribeUnmetDependencyAdvice(task.Id, unmet)} "
                + $"h9k task show {task.Id} for the full picture.");
        }

        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.ClaimInteractively(task, ownerId, runId, now, unmet.Count > 0);
        return (assigned, claimed, unmet);
    }

    /// <summary>
    /// The claim behind an already-Blocked entry (task 0ac72cb8-h9k): the task was already
    /// assigned — by an ordinary h9k task assign, or by an earlier deliberate claim that was
    /// handed back or retried — so no assignment travels here, unlike
    /// <see cref="PrepareInteractiveClaimFromPublished"/>'s just-assigned case; only
    /// <see cref="TaskDecider.ClaimInteractively"/> is ever appended. Warns and proceeds on
    /// acknowledgment exactly the same way: refuses, naming every open blocker, unless
    /// <paramref name="acknowledgeUnmetDependencies"/> is true or this task already carries a
    /// covering acknowledgment from an earlier claim on these same still-open blockers
    /// (<see cref="TaskAggregate.UnmetDependenciesAlreadyAcknowledged"/>) — design ruling R7: "an
    /// acknowledgment already given at claim time carries forward without re-asking". The returned
    /// <c>CarriedForward</c> flag is what the caller uses to record
    /// <see cref="TaskClaimed.DependencyOverrideCarriedForward"/> and to print an honest message:
    /// a carried-forward claim did not just ask the human anything.
    /// </summary>
    internal static (TaskClaimed Claimed, bool CarriedForward) PrepareInteractiveClaimFromBlocked(
        TaskAggregate task, Guid ownerId, IReadOnlyList<TaskDependency> unmetDependencies, Guid runId, DateTimeOffset now,
        bool acknowledgeUnmetDependencies)
    {
        bool carriedForward = !acknowledgeUnmetDependencies && task.UnmetDependenciesAlreadyAcknowledged;
        if (!acknowledgeUnmetDependencies && !carriedForward)
        {
            throw new DomainBusinessRuleException(
                $"Task {task.Id} is Blocked: it depends on {unmetDependencies.Count} task(s) that have not "
                + "closed out, the same bar dispatch itself holds an assignment to: "
                + string.Join("; ", unmetDependencies.Select(dependency => ExternalText.OneLine(dependency.Describe()))) + ". "
                + "The platform advises rather than refuses here: "
                + $"h9k task work {task.Id} --acknowledge-unmet-dependencies to claim it anyway, once you have "
                + $"confirmed that is what you want. "
                + $"{DescribeUnmetDependencyAdvice(task.Id, unmetDependencies, alreadyAssigned: true)} "
                + $"h9k task show {task.Id} for the full picture.");
        }

        TaskClaimed claimed = TaskDecider.ClaimInteractively(
            task, ownerId, runId, now, dependencyOverrideAcknowledged: true,
            dependencyOverrideCarriedForward: carriedForward);
        return (claimed, carriedForward);
    }

    /// <summary>
    /// The advice half of the unmet-dependency refusal above. A dependency that can still reach
    /// true closeout gets the ordinary "it queues itself" promise — the same one
    /// <see cref="TaskAssignCommand.AnnounceAsync"/> already makes on the identical fact pattern —
    /// but a dead one (<see cref="TaskDependency.IsDead"/>) never will, so making that promise for
    /// it tells the operator to wait on a merge that can never happen; <see cref="TaskDependency.DescribeDeath"/>
    /// already says the honest thing instead (independent pre-PR review, cycle 1). Internal, not
    /// private: <see cref="TaskStartCommand.PrepareDeliberateClaimFromPublished"/> reuses this
    /// exact fragment for the identical "h9k task assign to hold it Blocked" alternative in its own
    /// refusal, rather than re-deriving the dead-versus-live distinction a second time and letting
    /// the two drift (independent pre-PR review, cycle 1, adversarial lens).
    /// <paramref name="alreadyAssigned"/> is true only for <see cref="PrepareInteractiveClaimFromBlocked"/>'s
    /// own refusal (task 0ac72cb8-h9k): a task already sitting Blocked is already assigned, so
    /// pointing it at <c>h9k task assign</c> — which refuses anything but a Published task — would
    /// be advice that cannot be followed; that case drops the command and keeps only the promise
    /// (or the honest lack of one) behind it.
    /// </summary>
    internal static string DescribeUnmetDependencyAdvice(
        Guid taskId, IReadOnlyList<TaskDependency> unmet, bool alreadyAssigned = false)
    {
        IReadOnlyList<TaskDependency> dead = [.. unmet.Where(dependency => dependency.IsDead)];
        if (dead.Count == 0)
        {
            return alreadyAssigned
                ? "it queues itself the moment the last one's pull request merges, or"
                : $"h9k task assign {taskId} to hold it Blocked until they clear (it queues itself the moment "
                  + "the last one's pull request merges), or";
        }

        string deathAdvice = string.Join(" ", dead.Select(dependency => dependency.DescribeDeath() + "."));
        if (dead.Count == unmet.Count)
        {
            return deathAdvice;
        }

        string liveAdvice = alreadyAssigned
            ? "waiting will not clear that on its own, or"
            : $"h9k task assign {taskId} only holds it Blocked, and waiting will not clear that on its own, or";
        return deathAdvice + " The live ones can still close out on their own, but this task will not queue "
            + $"until the dead one is gone too — {liveAdvice}";
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
    /// fresh worktree exactly as the daemon's own path does. Internal rather than private:
    /// <see cref="TaskStartCommand"/>'s own claim shares this exact worktree-resume logic
    /// (task 8a56af78-h9k) rather than duplicating it.
    /// </summary>
    internal static async Task<(Worktree Worktree, bool ResumesPreviousWork)> CheckoutFreshOrRetryAsync(
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
        string sessionName, (Guid SessionId, DateTimeOffset EndedAt)? pendingEndedSession, CancellationToken cancellationToken)
    {
        await using IDocumentSession startSession = store.LightweightSession();
        if (pendingEndedSession is { } pending)
        {
            // The fallback attempt's own Started, appended below, lands in the same transaction
            // as the failed resume's Ended: RunDetailsProjection runs inline, so both apply to
            // the document atomically and no reader ever observes the gap in between where
            // ActiveSessions would otherwise read empty (adversarial review, cycle 1). EndedAt is
            // the resume attempt's own observed exit time, not DateTimeOffset.UtcNow read here at
            // the fallback's start — "now" is always later than the resume actually exited, which
            // would otherwise record the failed attempt as outliving the very session that
            // replaced it (independent pre-PR review, cycle 1).
            startSession.Events.Append(runId, new InteractiveSessionEnded(
                runId, pending.SessionId, pending.EndedAt, Turns: null, InputTokens: null, OutputTokens: null, CostUsd: null));
        }

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

    private static async Task<(int ExitCode, bool SessionStartRecorded, bool ResumeNotFound, DateTimeOffset ExitedAt)> LaunchInteractiveClaudeAsync(
        string worktreePath, string prompt, Guid sessionId, string settingsFile, bool skipPermissions, Guid runId,
        string sessionName, bool resume, Func<int, DateTimeOffset, Task> onStarted, CancellationToken cancellationToken)
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
        foreach (string argument in BuildInteractiveArguments(sessionId, resume, sessionName, settingsFile, skipPermissions, prompt))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        // Captured (not inherited) only on a resume attempt: a --resume naming a session with no
        // matching local conversation exits almost instantly with "No conversation found with
        // session ID: <id>" on stderr (verified empirically against the claude binary) — the one
        // signal this method has to tell a genuine resume failure from an ordinary session exit,
        // since an interactive session has no stream-json result payload to read the way a
        // headless run's ClaudeExecutor does. Read asynchronously from the moment the process
        // starts, not after it exits: a long, successful resumed session writing anything at all
        // to stderr over its lifetime must never fill the pipe and deadlock it — and, unlike an
        // earlier draft, must never be swallowed either (adversarial review, cycle 1, medium
        // finding): TeeAndDetectResumeNotFoundAsync below tees it live to Console.Error, the same
        // passthrough a fresh (non-resume) launch already gets from its own inherited handle,
        // except for the near-instant window a not-found failure's own text lives in.
        if (resume)
        {
            process.StartInfo.RedirectStandardError = true;
        }

        // Entered before Start() and held for the child's whole lifetime: Program.cs's global
        // Ctrl-C handler reads this to suppress its own escalate-to-terminate window while this
        // child is attached, since repeated Ctrl-C here is legitimate input to it — including
        // the double-tap that is Claude Code's own exit gesture — not an instruction to kill h9k
        // (adversarial review, cycle 4: a second press used to fall through to SIGINT's default
        // action and terminate h9k before AppendSessionEndedAsync ever ran).
        using IDisposable interactiveChildScope = InteractiveChildGuard.Enter();
        process.Start();
        // CancellationToken.None, not the caller's own token: a genuinely resumed session may run
        // for hours, and this read must survive to the process's own exit — via the stream closing
        // when the child's stderr handle closes — regardless of what happens to the outer token in
        // between (mirrors process.WaitForExitAsync's own CancellationToken.None retry below).
        Task<(bool NotFound, string? WithheldText)>? resumeNotFoundTask = resume
            ? TeeAndDetectResumeNotFoundAsync(process)
            : null;
        DateTimeOffset startedAt = InteractiveSessionLiveness.ReadStartedAt(process);
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

        // Awaited unconditionally, not only when the exit code says to look at it: the process
        // has already exited by this point, but `process` is disposed (the `using` above) the
        // moment this method returns, and a still-pending read against a disposed process's
        // stream would fault the task with nobody ever observing it (self-review finding: a
        // successful long-running resume — the ordinary, common case — never reached this line
        // at all in an earlier draft, leaving its own stderr read to race the dispose).
        //
        // Bounded, not indefinite: process.WaitForExitAsync above only guarantees the direct
        // child exited, not that its stderr pipe reached EOF, which needs every holder of the
        // write handle to close it — this repo's own WindowsStandardHandleInheritance doc
        // records exactly this shape once already ("the pipe never reached EOF because cmd.exe
        // was still holding it open"). A descendant that inherited claude's stderr and outlives
        // it (an MCP stdio server not dying with its parent is the ordinary case) would otherwise
        // hang this read forever, and with InteractiveChildGuard still attached at that point,
        // every further Ctrl-C is swallowed rather than reaching h9k. By this point
        // TeeAndDetectResumeNotFoundAsync has already teed everything a successful resume wrote
        // live, so what is bounded here is only the wait for its own task to finish observing
        // EOF — a wait of a few seconds past the child's own exit already covers the near-instant
        // not-found shape PLAN.md §16 #124 itself describes as "near-instantly"; past that, the
        // task is abandoned (still observed below, so its eventual fault against the disposed
        // process is never left unobserved) and this attempt is reported as an ordinary exit
        // (adversarial review, cycle 1).
        bool resumeNotFound = false;
        if (resumeNotFoundTask is not null)
        {
            // The same window TeeAndDetectResumeNotFoundAsync itself waits on, not an
            // independently-chosen duplicate of it — widening one without the other would grow
            // the detection window while this wait still abandons the task on the old schedule,
            // silently stopping the fallback from ever firing again (adversarial review, cycle 1,
            // low).
            Task firstToComplete = await Task.WhenAny(resumeNotFoundTask, Task.Delay(ResumeNotFoundDetectionWindow, CancellationToken.None));
            if (firstToComplete == resumeNotFoundTask)
            {
                // Always awaited, never short-circuited by the exit code: `&&`'s short-circuit
                // used to skip this await entirely whenever ExitCode was already 0, so a task that
                // faulted after that point (Console.Error's own pipe gone, e.g. `| head`) was never
                // observed and surfaced only as an unlogged TaskScheduler.UnobservedTaskException
                // (adversarial review, cycle 1, low).
                (bool markerMatched, string? withheldText) = await resumeNotFoundTask;
                // TeeAndDetectResumeNotFoundAsync already teed anything that was not the not-found
                // marker itself live as it arrived; ExitCode is the other half of the signal — the
                // marker text alone, with no matching nonzero exit, is not treated as a genuine
                // resume failure.
                resumeNotFound = process.ExitCode != 0 && markerMatched;
                if (markerMatched && !resumeNotFound && withheldText is not null)
                {
                    // The two signals disagreed: the marker matched but the exit code did not
                    // confirm it. The text was held back on the strength of the marker alone —
                    // flush it now rather than leaving the operator with neither the error text
                    // nor a fresh-session fallback (conformance review, cycle 1, low).
                    await Console.Error.WriteAsync(withheldText);
                }
            }
            else
            {
                _ = resumeNotFoundTask.ContinueWith(
                    static faulted => _ = faulted.Exception,
                    TaskScheduler.Default);
            }
        }

        return (process.ExitCode, sessionStartRecorded, resumeNotFound, ReadExitedAt(process));
    }

    private static readonly TimeSpan ResumeNotFoundDetectionWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts reading the resumed child's stderr immediately and tees it live to
    /// <see cref="Console.Error"/> — the same passthrough a fresh (non-resume) launch already gets
    /// from its own inherited handle — except for the first <see cref="ResumeNotFoundDetectionWindow"/>,
    /// whose text is held back instead of teed (adversarial review, cycle 1, medium finding: an
    /// earlier draft captured the whole session and only ever replayed it on a nonzero exit, so a
    /// successful resume's stderr never reached the terminal at all). That window is the only place
    /// the near-instant "No conversation found" failure (<see cref="IsResumeNotFoundError"/>) can
    /// appear, so nothing arriving after it is ever searched for the marker (adversarial review,
    /// cycle 1, low finding: a descendant that inherited the handle and echoed the same literal text
    /// hours into a genuinely resumed session must never be read as a resume failure). Held-back text
    /// is flushed the moment the window closes without that shape — whether by the window elapsing
    /// (an ordinary long-running resume) or by EOF arriving inside it with other, unrelated text — and
    /// everything after is teed as it arrives. Resolves to whether the window's own text was the
    /// not-found marker; the caller still gates that on the exit code too, since this method alone
    /// cannot see it — and when the marker matched, the withheld text rides back with it (rather
    /// than being discarded here), so the caller can still flush it to the terminal if the exit
    /// code goes on to disagree with the marker (conformance review, cycle 1, low: a genuine
    /// signal disagreement used to leave the operator with neither the error text nor a fallback,
    /// since the text was dropped here before the exit code was ever known).
    /// </summary>
    private static async Task<(bool NotFound, string? WithheldText)> TeeAndDetectResumeNotFoundAsync(Process process)
    {
        StringBuilder earlyBuffer = new();
        char[] buffer = new char[4096];
        using CancellationTokenSource windowCts = new(ResumeNotFoundDetectionWindow);
        try
        {
            while (true)
            {
                int read = await process.StandardError.ReadAsync(buffer, windowCts.Token);
                if (read == 0)
                {
                    // EOF inside the window: the near-instant shape a genuine not-found failure
                    // takes, and nothing further will ever arrive on this stream.
                    string text = earlyBuffer.ToString();
                    bool notFound = IsResumeNotFoundError(text);
                    if (!notFound && text.Length > 0)
                    {
                        await Console.Error.WriteAsync(text);
                    }

                    return (notFound, notFound ? text : null);
                }

                earlyBuffer.Append(buffer, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // The window elapsed with the process still writing — an ordinary long, successful
            // resume, a shape the not-found failure never takes (it is always near-instant).
            // Fall through: flush what the window caught, then keep teeing everything else live
            // until real EOF. The caller bounds how long it waits on this task exactly the way it
            // always bounded the old whole-session read.
        }

        await Console.Error.WriteAsync(earlyBuffer.ToString());
        while (true)
        {
            int read = await process.StandardError.ReadAsync(buffer, CancellationToken.None);
            if (read == 0)
            {
                return (false, null);
            }

            await Console.Error.WriteAsync(new string(buffer, 0, read));
        }
    }

    /// <summary>
    /// Internal for policy tests, mirroring <c>ClaudeExecutor.Arguments</c>'s own reasoning
    /// (Hall9k.Daemon): the flag set is the policy, worth asserting without spawning a process.
    /// A resume re-enters the recorded conversation (<c>--resume</c>); <c>--session-id</c> and
    /// <c>--model</c> are for a fresh session only and would conflict with it — a resumed session
    /// keeps the model it started with, exactly as <c>ClaudeExecutor</c>'s own headless resume
    /// branch (PLAN.md §16 #5) already does.
    /// </summary>
    internal static IEnumerable<string> BuildInteractiveArguments(
        Guid sessionId, bool resume, string sessionName, string settingsFile, bool skipPermissions, string prompt)
    {
        if (resume)
        {
            yield return "--resume";
            yield return sessionId.ToString();
        }
        else
        {
            yield return "--session-id";
            yield return sessionId.ToString();
            yield return "--model";
            yield return AgentModel.Fable.Value;
        }

        // Verified against `claude --help` and confirmed empirically (task: every dispatched
        // agent session launches under a human-readable id-and-role name): -n/--name is what
        // `~/.claude/sessions/<pid>.json` records as this session's name, which is what
        // `claude agents --json` and another session's cross-session mesh
        // (ListAgents/SendMessage) address it by. Set on both branches, resumed or fresh, so a
        // resumed session's name never reverts to whatever it was launched under originally.
        yield return "--name";
        yield return sessionName;
        yield return "--settings";
        yield return settingsFile;
        if (skipPermissions)
        {
            yield return "--dangerously-skip-permissions";
        }

        // A positional argument, passed through ArgumentList rather than a shell string: no
        // shell escaping, so the prompt's own quotes and newlines travel to the child exactly as
        // written. Claude Code starts interactively (no -p) with this as the opening message,
        // whether that message opens a fresh conversation or continues a resumed one.
        yield return prompt;
    }

    /// <summary>
    /// Internal for policy tests. The exact stderr text `claude --resume &lt;id&gt;` prints (and
    /// nothing else, verified empirically) when no local conversation matches the given session
    /// id — the signal <see cref="LaunchInteractiveClaudeAsync"/> uses to tell a genuine resume
    /// failure from an ordinary session exit.
    /// </summary>
    internal static bool IsResumeNotFoundError(string standardError) =>
        standardError.Contains("No conversation found with session ID", StringComparison.Ordinal);

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
    /// Called only after <c>process.WaitForExitAsync</c> has already completed, so unlike
    /// <see cref="InteractiveSessionLiveness.ReadStartedAt"/> there is no unobserved-guess risk in
    /// the fallback: the process has genuinely already exited by the time this runs, so
    /// <see cref="DateTimeOffset.UtcNow"/> read right here is itself an observation (of when this
    /// method witnessed the exit that already happened), not a plausible-looking stand-in for one
    /// that has not (independent pre-PR review, cycle 1: the deferred
    /// <see cref="Hall9k.Domain.Features.Run.Events.InteractiveSessionEnded"/> for a failed resume
    /// needs the resume's own exit time, not the later moment its fallback happens to start).
    /// </summary>
    private static DateTimeOffset ReadExitedAt(Process process)
    {
        try
        {
            return new DateTimeOffset(process.ExitTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
