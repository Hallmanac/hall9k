using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
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
/// Delegating the build while staying at the wheel (task 15f889e3-h9k, design ruling R6, idea
/// fcaded0b's design rulings, Take the Wheel epic 9272e514's slice 10): distinct from
/// <see cref="TaskHandbackCommand"/> on purpose. A handback ends interactive mode and returns the
/// task to the machine; this dispatches a contractor build agent for one phase while the task
/// stays the operator's own, still in interactive mode, so slice 8's boundary parks and slice 9's
/// milestone reporting still apply to whatever it does next. The verb distinction is the point:
/// blurring the two would put "who is in charge afterward" in doubt.
/// <para>
/// Only ever runs against the operator's own live interactive claim (<c>h9k task work</c>) —
/// Claimed, <see cref="TaskAggregate.IsInteractiveClaim"/>, assigned to this owner, its run still
/// Dispatched or Running, with no other session presently attached. Everything else refuses:
/// there is no fresh claim here the way <see cref="TaskStartCommand"/> makes one, because this
/// never claims anything — the run, the worktree, and the branch are all the interactive claim's
/// own, reused exactly as they stand, whether or not any work has been committed there yet.
/// </para>
/// <para>
/// The contractor is dispatched exactly the way <c>h9k task start</c> dispatches its own headless
/// session — <see cref="HeadlessLaunch.SpawnDetached"/>, the ceiling-exempt mechanism itself,
/// since this run's <c>RunDispatched</c> already carries the sentinel <see cref="Guid.Empty"/>
/// node id the interactive claim minted — under the slice-1 <c>&lt;task-shortid&gt;-build</c> name
/// (<see cref="SessionRoleName.Build"/>), addressable on the session mesh the moment it starts.
/// <see cref="InteractiveSessionStarted"/> is the same event both an attached re-entry and a
/// headless dispatch already append to track a run's own liveness, reused again here rather than
/// a new mechanism of its own — the contractor is simply this run's next recorded session.
/// </para>
/// <para>
/// <see cref="RunPhaseDelegated"/> records the operator's own handoff note on the run stream —
/// the blocker-handoff mold (what was attempted, what is deliberate versus abandoned, what
/// latitude is granted) — and the identical text lands in the contractor's starting prompt
/// (<see cref="WorkPromptBuilder.Build"/>'s <c>isDelegatedContractor</c> branch), whose own default
/// posture toward whatever the branch already holds is conservative: respect it, and discard only
/// what the note explicitly grants latitude to discard.
/// </para>
/// </summary>
public sealed class TaskDelegateCommand : Hall9kAsyncCommand<TaskDelegateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--note <TEXT>")]
        [Description(
            "The handoff the contractor starts from, in the blocker-handoff mold: what was attempted, what is "
            + "deliberate versus abandoned, what latitude is granted. Recorded on the run stream and placed "
            + "verbatim in the contractor's starting prompt. Its default posture toward whatever the branch "
            + "already holds is conservative — discard or rewrite latitude exists only when this note grants "
            + "it explicitly.")]
        public string Note { get; init; } = string.Empty;

        [CommandOption("--force")]
        [Description(
            "Delegate even though the claim's interactive session was recorded on another machine this one "
            + "cannot check — attests you confirmed by hand that it has exited")]
        public bool Force { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        Validate(settings);

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        Guid taskId = await TaskIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        DelegationPlan plan = await PrepareAsync(session, taskId, settings.Note, settings.Force, cancellationToken);

        string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(plan.RunDirectory);
        Directory.CreateDirectory(resolvedRunDirectory);

        // Session-scoped, not the run-level PromptFile/StreamFile/SettingsFile h9k task start
        // writes: a run can be delegated more than once (RunDetails.PhaseDelegations exists
        // precisely to record "delegate, re-enter, delegate again"), and the run-level files are
        // truncate-on-open — a second delegation's spawn would otherwise destroy the first
        // contractor's transcript, handoff, and token usage before anything ever read them back
        // (independent pre-PR review, cycle 1, both lenses). Keyed on plan.SessionFileKey, not
        // plan.SessionName: the mesh name is documented as identical across every delegation on
        // this run (AGENTS.md, "its slice-1 <task-shortid>-build name"), so it is SessionFileKey —
        // unique per delegation — that RunPaths.SessionStreamFile and its siblings actually key
        // on; HeadlessTokenRecovery.AppendDelegatedPhaseTokens and LogsCommand both read
        // PhaseDelegation.SessionFileKey back later for the identical reason.
        string promptFile = RunPaths.SessionPromptFile(resolvedRunDirectory, plan.SessionFileKey);
        string streamFile = RunPaths.SessionStreamFile(resolvedRunDirectory, plan.SessionFileKey);
        string standardErrorFile = RunPaths.SessionStandardErrorFile(resolvedRunDirectory, plan.SessionFileKey);
        await File.WriteAllTextAsync(promptFile, plan.Prompt, cancellationToken);

        // The same platform-imposed overrides every other spawn writes (ClaudeSettingsFile): no
        // co-authored-by trailers, and command-tool timeout headroom — mirrors h9k task start's
        // own settings file exactly. Session-scoped for the identical truncation reason as the
        // stream/prompt files above.
        string settingsFile = RunPaths.SessionSettingsFile(resolvedRunDirectory, plan.SessionFileKey);
        string settingsContent = ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout);
        await File.WriteAllTextAsync(settingsFile, settingsContent, cancellationToken);

        // Fetched here, before the RunDetails reload below, not immediately before the append —
        // mirrors h9k task register-session's own identical fence, and its own comment explains
        // why: fetching this late would only fence the append against a version a concurrent
        // append has already moved past. Without it, two concurrent delegates on this same claim
        // both read the identical RunDetails state and both pass the checks below, both spawn a
        // contractor into this same worktree, and whichever commits its own
        // InteractiveSessionStarted last silently overwrites the other's single-slot
        // ActiveSessions entry — the exact double-booking corruption PR #237's review flagged for
        // this append (independent pre-PR review, follow-up cycle 1).
        StreamState? fence = await session.Events.FetchStreamStateAsync(plan.RunId, cancellationToken);

        // Re-checked immediately before launch, mirroring h9k task start's own identical guard: the
        // git read, the model resolution, the prompt build, and the settings-file write above all
        // take long enough for a concurrent command against this same claim to move the run in
        // between (a delivery, another delegation, a re-entry).
        RunDetails currentRun = await session.LoadAsync<RunDetails>(plan.RunId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId}'s run {plan.RunId} no longer has a record — h9k task release {taskId} to give "
                + "the claim back to the dispatch queue.");
        if (currentRun.State != RunState.Dispatched && currentRun.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {plan.RunId} is already {currentRun.State.Value} — another command moved "
                + $"it while this one was preparing to dispatch the contractor. h9k task show {taskId} to see "
                + "where it stands.");
        }

        // The pathological remainder fence's own nullability leaves open, mirroring h9k task
        // register-session's own identical guard: RunDetails exists, so the stream had events at
        // the instant that load ran, yet the fence fetched a moment earlier read no stream at all.
        // Inline projections make this unreachable in practice — kept as a named, honest refusal
        // rather than a null-forgiving operator on fence.Version below (AGENTS.md: never guess at
        // unobserved facts).
        if (fence is null)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {plan.RunId} lost its run stream while delegating — h9k task show "
                + $"{taskId} to see where it stands.");
        }

        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(
            currentRun, taskId, "delegate", settings.Force, quiet: plan.CrossMachineNoticeShown);

        int processId;
        DateTimeOffset startedAt;
        try
        {
            (processId, startedAt) = HeadlessLaunch.SpawnDetached(
                plan.WorktreePath, plan.ClaudeSessionId, plan.SessionName, plan.Model, promptFile, streamFile,
                standardErrorFile, settingsFile, plan.SkipPermissions);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Nothing about the claim or the worktree changed by this point — unlike h9k task
            // start's identical catch, this never claimed or cut anything, so there is nothing to
            // recover beyond trying again.
            throw new DomainConflictException(
                $"Task {taskId}'s worktree is ready, but the contractor session could not be launched "
                + $"({exception.Message}). Your interactive claim is untouched — h9k task work {taskId} to "
                + $"attach and continue by hand, or h9k task delegate {taskId} again once the problem is fixed.");
        }

        await using (IDocumentSession delegateSession = store.LightweightSession())
        {
            // CancellationToken.None, not the shared cancellationToken (mirrors h9k task start's
            // own identical append): by this point HeadlessLaunch.SpawnDetached has already spawned
            // a real, detached claude process — a Ctrl-C landing in the window before this append
            // completes must not turn into a lost append.
            //
            // expectedVersion: fence.Version + 2 fences both events in this one append against the
            // stream state read above — Marten's own expectedVersion is the stream's maximum
            // version *after* the append, so two events appended together need +2, the same
            // convention h9k task work (TaskWorkCommand.cs) and h9k task handback
            // (TaskHandbackCommand.cs) already follow for their own two-event appends; h9k task
            // register-session's fence.Version + 1 is not a mirror to follow here, since that
            // command appends only one event. It cannot stop a race that has already spawned two
            // contractor processes into the same worktree by the time either append runs — the
            // launch above already happened — but it stops the second append from silently winning
            // and overwriting the first contractor's ActiveSessions record, surfacing the collision
            // loudly instead (caught below) so the operator learns a second, untracked process is
            // out there rather than losing track of it entirely.
            delegateSession.Events.Append(plan.RunId, expectedVersion: fence.Version + 2, new RunPhaseDelegated(
                plan.RunId, settings.Note, DateTimeOffset.UtcNow, plan.OwnerId, plan.SessionName,
                plan.SessionFileKey, plan.Model), new InteractiveSessionStarted(
                plan.RunId, plan.ClaudeSessionId, startedAt, processId, Environment.MachineName, plan.SessionName));
            try
            {
                await delegateSession.SaveChangesAsync(CancellationToken.None);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                throw new DomainConflictException(
                    $"Task {taskId}'s run {plan.RunId} changed while delegating — most likely this same claim "
                    + $"was delegated more than once at once, or another command (deliver, handback, release) "
                    + $"moved it in between. The contractor process this call started (pid {processId}) is "
                    + $"still running but was not recorded — h9k task show {taskId} to see which session the "
                    + "platform did record, and stop the unrecorded one by hand.");
            }
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {plan.WorktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {plan.Branch}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Session: {plan.SessionName}[/]");
        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} is delegated for this phase, headless, as {plan.SessionName} (pid {processId}) — reachable on the session mesh (claude agents --json, or SendMessage). The task stays claimed interactively; nothing about the claim or the interactive-mode flag changed. h9k logs {taskId} to read the contractor's report once it lands, then h9k task work {taskId} to re-enter this same worktree yourself and finish the build by hand, or h9k task delegate again for another phase.[/]");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// Checked before the store ever opens (the <see cref="TaskWriteJiraCommand"/> convention: a
    /// cheap local mistake costs nothing, rather than paying for a task resolution and a node
    /// bootstrap only to fail on a missing note afterward).
    /// </summary>
    internal static void Validate(Settings settings)
    {
        if (settings.Note.IsBlank())
        {
            throw new DomainValidationException(
                "--note carries the handoff the contractor starts from — what was attempted, what is "
                + "deliberate versus abandoned, what latitude is granted. A contractor dispatched with nothing "
                + "to go on is the failure mode this flag exists to prevent.");
        }
    }

    /// <summary>
    /// Everything that can be decided and built without actually spawning a process — the state
    /// refusals, the double-booking guard, the git read behind <see cref="DelegationPlan.ResumesPreviousWork"/>,
    /// the model resolution, and the composed starting prompt — pulled out of
    /// <see cref="ExecuteAsync"/> so it is independently testable against a real store and a real
    /// git repository without needing a <c>claude</c> binary to actually launch, the same shape
    /// <see cref="TaskStartCommand.ClaimAndCutAsync"/> is for its own sibling
    /// <see cref="TaskStartCommand.RunDeliberateStartAsync"/>. Unlike that one, this never claims
    /// or cuts anything: the run, the worktree, and the branch are all the interactive claim's own,
    /// read here exactly as they stand.
    /// </summary>
    internal static async Task<DelegationPlan> PrepareAsync(
        IDocumentSession session, Guid taskId, string note, bool force, CancellationToken cancellationToken)
    {
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        if (task.State != TaskState.Claimed || !task.IsInteractiveClaim || task.CurrentRunId is not { } runId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is {task.State.Value} — only a task with an active interactive claim "
                + "(h9k task work) can delegate this way.");
        }

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);

        // A pr-review task's own Claimed+sentinel state is never a human's own interactive claim
        // (mirrors TaskHandbackCommand's identical guard, and for the identical reason —
        // AutoPrReviewEngine.CreateOneAsync's Now-speed claim leaves InteractiveModeEnabled false
        // and reads identically to one on IsInteractiveClaim's own Guid.Empty discriminator).
        // Checked ahead of every other guard below, including the interactive-mode-off check right
        // after it — a Now-speed claim is never interactive to begin with, so that check would
        // otherwise fire first and misdiagnose this exact state as "the claim turned interactive
        // mode off", naming h9k task handback --now and h9k task release/h9k task work as remedies
        // that both refuse a pr-review task outright, the identical dead-end-hop defect the
        // interactive-mode-off check's own cycle 2 ruling exists to prevent (adversarial review,
        // cycle 1). Same ordering TaskHandbackCommand already settled on for its identical guard.
        if (task.Type == TaskType.PrReview)
        {
            throw PrReviewSentinelClaim.Refuse(taskId, run?.State ?? RunState.Unknown, "delegate");
        }

        // IsInteractiveClaim alone reads true for a h9k task handback --now claim too — that
        // command deliberately turns interactive mode off (design ruling R2) while still minting
        // its claim on the same Guid.Empty sentinel this command's own guard above keys on, so a
        // claim it left behind is not actually an operator sitting on this task interactively —
        // there is no boundary park left for a delegated contractor's report to arrive at, and the
        // success message below promises one regardless (conformance review, cycle 1).
        if (!task.InteractiveModeEnabled)
        {
            // h9k task work is never the fix here (orchestrator ruling, cycle 2): re-entering an
            // already-live claim (TaskWorkCommand.ReenterAsync) appends no event at all, and
            // TaskRevised.ClearInteractiveMode's own contract says the flag comes back on only
            // from a fresh TaskClaimed carrying InteractiveMode: true — never from re-entry. The
            // one way back to a fresh claim is giving this one back first, and h9k task release
            // only accepts a claim nothing has been done in yet (TaskReleaseCommand's own doc);
            // once the branch holds work, h9k task handback is the only door left, and it hands
            // the task to headless dispatch rather than restoring interactive mode.
            throw new DomainConflictException(
                $"Task {taskId}'s claim turned interactive mode off (h9k task handback --now, or "
                + $"h9k task revise {taskId} --clear-interactive-mode) — only a task still in "
                + $"interactive mode can delegate this way. h9k task work {taskId} re-enters the same "
                + "claim without changing that flag, so it will not help here. If nothing has been "
                + $"committed or edited in this claim yet, h9k task release {taskId} gives it back and "
                + $"h9k task work {taskId} then claims it fresh, which does turn interactive mode back "
                + $"on. Once the branch holds work, h9k task handback {taskId} is the only way off this "
                + "claim, and it stays headless from there.");
        }

        // Saved immediately, unlike every other caller of NodeBootstrap.EnsureAsync, which lets a
        // fresh Owner/Node/Connection registration ride along with whatever business event that
        // caller appends to this same session right after: PrepareAsync appends nothing of its own
        // — the actual RunPhaseDelegated/InteractiveSessionStarted pair lands later, in
        // ExecuteAsync's own separate session, once the contractor has actually spawned — so a
        // first-ever bootstrap here would otherwise be silently discarded the moment this session
        // is disposed unsaved, and the very next EnsureAsync call would mint a second, different
        // owner instead of reading this one back.
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (task.AssignedOwnerId != context.OwnerId)
        {
            throw new DomainConflictException(
                $"Task {taskId} is claimed by {task.AssignedOwnerId} — you can only delegate your own "
                + "interactive claim.");
        }

        if (run is null)
        {
            throw new DomainConflictException(
                $"Task {taskId} is claimed interactively but run {runId} has no record — the process likely "
                + $"died while preparing the worktree. h9k task release {taskId} to give the claim back to "
                + "the dispatch queue.");
        }

        // Once h9k task deliver hands the run to the standard pipeline, the task can still read
        // Claimed+interactive for the whole review loop (mirrors TaskHandbackCommand's identical
        // guard) — a delegated contractor must never be dispatched into a worktree the daemon's own
        // gates or review sessions are already reading.
        if (run.State != RunState.Dispatched && run.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {run.State.Value} — it was handed off with "
                + $"h9k task deliver and is now in the standard pipeline. h9k task show {taskId} to see where "
                + "it stands.");
        }

        // Checked before any of the work below, not only right before launch: a live attached
        // session — the operator's own, still editing — must never race a contractor dispatched
        // into the identical worktree (mirrors h9k task handback's own double-booking guard).
        // Deliberately no InteractiveSessionLiveness.IsSelfInvocation exemption the way deliver,
        // handback, and release each carry one: this command is the orchestrator window's own act
        // (AGENTS.md, "the orchestrator window"), typed by the human, never something a build
        // session dispatches against its own claim — there is no legitimate self-invoking caller
        // for a command whose whole point is spawning a second, competing session into a worktree
        // the caller is already running in. Its return value is threaded through to
        // ExecuteAsync's own re-check of this identical guard right before launch, mirroring
        // h9k task work's own quiet/CrossMachineNoticeShown pairing, so the cross-machine
        // --force notice never prints twice for the same delegation (adversarial review, cycle 1).
        bool crossMachineNoticeShown = InteractiveSessionLiveness.EnsureNotAttachedElsewhere(run, taskId, "delegate", force);

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        // An operator who left the worktree checked out on a different branch or detached (git
        // status can read perfectly clean either way) must not have a contractor dispatched into
        // it: the contractor's commits would land on whatever ref is actually checked out, never
        // the claim's own branch, and everything downstream (h9k task deliver's push, the
        // eventual pull request) still names run.Branch regardless (mirrors h9k task deliver's
        // identical guard, TaskDeliverCommand.cs). Unreadable (null) warns and proceeds rather
        // than refusing on a fact this command could not observe.
        string? currentBranch = await InteractiveWorktreeGit.GetCurrentBranchAsync(run.WorktreePath, cancellationToken);
        if (currentBranch is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Could not read the worktree's current branch at {run.WorktreePath}; skipping the branch-checkout check.[/]");
        }
        else if (currentBranch != run.Branch)
        {
            string where = currentBranch.Length == 0 ? "a detached commit" : $"'{currentBranch}'";
            throw new DomainConflictException(
                $"Task {taskId}'s worktree is checked out to {where}, not its claim branch '{run.Branch}' — "
                + $"check out '{run.Branch}' before delegating.");
        }

        // Whether the branch is virgin or already carries work (design ruling R6: "sets
        // ResumesPreviousWork whenever the branch is not virgin"), read off the same worktree the
        // interactive claim already cut — there is no fresh checkout here to resume into, unlike
        // h9k task start's own ResumesPreviousWork, so this counts real commits rather than
        // inferring the fact from which branch of the claim mechanism ran. headReference is
        // run.Branch by name, not the worktree's default HEAD (independent pre-PR review, cycle
        // 1, both lenses): the branch-checkout guard above already refuses an operator who left
        // the worktree elsewhere, but the count itself still has to name the claim's own branch
        // rather than trust whatever happens to be checked out, the same reasoning
        // TaskDeliverCommand.cs and TaskReleaseCommand.cs already give this identical call.
        // Uncommitted work counts too — a commit count alone says nothing about an operator's own
        // edits sitting uncommitted in the tree (the exact gap the delegated-contractor prompt's
        // "clean worktree" claim fell into). Neither an unreadable commit count (the -1 sentinel)
        // nor an unreadable working-tree status is folded into "clean" (InteractiveWorktreeGit's
        // own contract, "never guessed at as clean"): both fold into "assume this worktree
        // already holds work" instead, the conservative direction design ruling R6 calls for —
        // the only user-visible effect is which of two true sentences the contractor's prompt
        // states, never a decision that discards anything.
        int commitsAheadOfBase = await InteractiveWorktreeGit.CountBranchCommitsAsync(
            run.WorktreePath, project.BaseBranch, cancellationToken, headReference: run.Branch);
        (IReadOnlyList<string>? modifiedFiles, IReadOnlyList<string> untrackedFiles) =
            await InteractiveWorktreeGit.ListUncommittedFilesAsync(run.WorktreePath, cancellationToken);
        bool resumesPreviousWork = commitsAheadOfBase != 0
            || modifiedFiles is null || modifiedFiles.Count > 0 || untrackedFiles.Count > 0;

        // The contractor's own recompose boundary (adversarial review, cycle 1,
        // TaskDelegateCommand.cs:367): whatever this branch already holds — including commits the
        // operator authored on this same interactive claim before this delegation — is not the
        // contractor's history to rewrite, so its own checkpoint/recompose protocol resets to this
        // exact commit rather than the branch's fork point against the base branch. Null when git
        // itself could not be read, the same unreadable-git case CountBranchCommitsAsync's own -1
        // sentinel and ListUncommittedFilesAsync's own null both fold into "assume work exists"
        // for the identical reason: WorkPromptBuilder treats a null here as "no safe boundary",
        // never a guessed one.
        string? delegationBaseCommit = await InteractiveWorktreeGit.GetHeadShaAsync(run.WorktreePath, cancellationToken);

        AgentModel model = await TaskStartCommand.ResolveBuildModelAsync(taskDetails, project, cancellationToken);

        // Minted once for this dispatch — the first (only, for this delegation) session records it
        // as this InteractiveSessionStarted's own ClaudeSessionId, exactly as a fresh h9k task start
        // claim's first session does.
        Guid claudeSessionId = DomainId.New();

        // The build role (task: every dispatched agent session launches under a human-readable
        // id-and-role name): this session is a spawned, unattended contractor, the identical shape
        // h9k task start's own session already answers to — not h9k task work's own
        // interactive-claim role, since the human is not attached to this process. Documented as
        // this run's mesh-visible name (AGENTS.md, "its slice-1 <task-shortid>-build name") and
        // deliberately identical across every delegation on this run — it is sessionFileKey below,
        // not this, that has to be unique per delegation.
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build);

        // Unique per delegation (independent pre-PR review, cycle 1, both lenses): sessionName
        // above is deliberately identical across every delegation on this run, so it cannot also
        // be what RunPaths.Session*File keys its files on — a second delegation would then
        // truncate the first contractor's own transcript, handoff, and recorded token usage the
        // moment HeadlessLaunch.SpawnDetached opened its files for writing. claudeSessionId is
        // already minted fresh per delegation just above, so its own short id makes a stable,
        // collision-free suffix without needing to count this run's prior delegations.
        string sessionFileKey = $"{sessionName}-{DomainId.Short(claudeSessionId)}";

        string? blockerContext = await TaskWorkCommand.LoadBlockerContextAsync(session, taskDetails, cancellationToken);
        string prompt = WorkPromptBuilder.Build(
            taskDetails, project, run.Branch, run.WorktreePath, resumesPreviousWork, blockerContext,
            resumeReason: null, isInteractive: false, isHandback: false, isDeliberateHeadlessStart: true,
            requiresSelfRegistration: false, isDelegatedContractor: true, delegationNote: note,
            delegationBaseCommit: delegationBaseCommit);

        return new DelegationPlan(
            runId, run.WorktreePath, run.Branch, run.RunDirectory, resumesPreviousWork, model, prompt,
            claudeSessionId, sessionName, sessionFileKey, context.OwnerId, project.SkipPermissions,
            crossMachineNoticeShown);
    }

    /// <summary>Everything <see cref="ExecuteAsync"/> needs to actually spawn the contractor, decided once by <see cref="PrepareAsync"/>.</summary>
    internal sealed record DelegationPlan(
        Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork,
        AgentModel Model, string Prompt, Guid ClaudeSessionId, string SessionName, string SessionFileKey,
        Guid OwnerId, bool SkipPermissions, bool CrossMachineNoticeShown);
}
