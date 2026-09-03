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
using Hall9k.Domain.Infrastructure.Persistence;
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
/// A deliberate human kick-off (task 8a56af78-h9k, "Take the Wheel" epic 9272e514, start-it-mine
/// mode): dispatches a Published or Queued task on the spot, headless, instead of waiting for the
/// dispatcher's own ceiling and ordering to reach it. On a Published task assigned to nobody, this
/// assigns it to the operator's own owner and claims it in the same atomic event append
/// <c>h9k task work</c>'s own Published entry already uses (task 688a1ccf-h9k) — the task is never
/// observably Queued in between. Unmet dependencies do not refuse outright the way
/// <c>h9k task work</c>'s claim does: the platform advises, naming every open blocker, and
/// <c>--acknowledge-unmet-dependencies</c> is the human's recorded override to start anyway (the
/// idea's own ruling, fcaded0b: "the platform advises rather than refuses"). Draft, an
/// already-Blocked task, a task already carrying a live claim (interactive, headless, or another
/// deliberate start), and every terminal state refuse — there is nothing here to start.
/// <para>
/// Ceiling-exempt on #103's own reasoning: the claim carries the sentinel <see cref="Guid.Empty"/>
/// node id an operator's own interactive claim already uses, so <c>NodeLoad</c>'s ceiling
/// measurement never counts it (a deliberate human act is outside the automation's budget) — the
/// same mechanism, not a new one, because the two claims are the same kind of fact: a human, not a
/// machine, took responsibility. What differs is only how the session runs: <c>h9k task work</c>
/// launches attached to this terminal and blocks until it exits; this command launches the agent
/// headless and detached — under the slice-1 <c>&lt;task-shortid&gt;-build</c> name (task
/// 68a953b1-h9k), addressable on the session mesh (<c>claude agents --json</c>,
/// <c>ListAgents</c>/<c>SendMessage</c>) — and returns as soon as the process is confirmed alive,
/// without waiting for it to finish. The worktree cut, the prompt (the ordinary headless build
/// lens, not the attended one), and the claim/dependency machinery are shared with
/// <see cref="TaskWorkCommand"/> wherever the shape is identical; only the launch itself differs.
/// </para>
/// </summary>
public sealed class TaskStartCommand : Hall9kAsyncCommand<TaskStartCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Task id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;

        [CommandOption("--acknowledge-unmet-dependencies")]
        [Description(
            "Start a Published task even though not every dependency has closed out yet — the platform names "
            + "the open blockers first; this is your recorded override to start anyway.")]
        public bool AcknowledgeUnmetDependencies { get; init; }
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

        // Minted once per invocation, exactly as h9k task work's own claim does: the first
        // (only, for this command — there is no re-entry) session records it as
        // RunDispatched.SessionId.
        Guid claudeSessionId = DomainId.New();

        // The build role, not h9k task work's interactive-claim role (task: every dispatched
        // agent session launches under a human-readable id-and-role name): this session is a
        // spawned, unattended agent, exactly the shape SessionRoleName.Build already names — the
        // sentinel NodeId is what says "a human, not a machine" claimed it, not the session's own
        // role.
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Build);

        (Guid runId, string worktreePath, string branch, string runDirectory, bool resumesPreviousWork, AgentModel model) =
            await ClaimAndCutAsync(
                store, session, task, fence, context, claudeSessionId, sessionName,
                settings.AcknowledgeUnmetDependencies, cancellationToken);

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {taskId}'s project no longer exists.");

        string? blockerContext = await TaskWorkCommand.LoadBlockerContextAsync(session, taskDetails, cancellationToken);
        // isInteractive: false — the ordinary headless build prompt (checkpoint commits, the
        // self-review phase, the end-of-session recompose, the handoff rules): this session is
        // unattended exactly like a dispatcher-launched build, not an operator's own attended one.
        // isDeliberateHeadlessStart: true — but unlike a dispatcher-launched build, nothing on this
        // node watches this run (RunSupervisor never adopts the sentinel Guid.Empty NodeId), so the
        // prompt tells the agent delivery is its own to trigger by hand rather than claiming the
        // platform verifies and opens the PR after it finishes.
        string prompt = WorkPromptBuilder.Build(
            taskDetails, project, branch, worktreePath, resumesPreviousWork, blockerContext, taskDetails.RetryReason,
            isInteractive: false, isDeliberateHeadlessStart: true);

        string resolvedRunDirectory = RunPaths.ResolveCurrentDirectory(runDirectory);
        Directory.CreateDirectory(resolvedRunDirectory);
        string promptFile = RunPaths.PromptFile(resolvedRunDirectory);
        string streamFile = RunPaths.StreamFile(resolvedRunDirectory);
        string standardErrorFile = RunPaths.StandardErrorFile(resolvedRunDirectory);
        await File.WriteAllTextAsync(promptFile, prompt, cancellationToken);

        // Same platform-imposed overrides every other spawn writes (ClaudeSettingsFile): no
        // co-authored-by trailers, and command-tool timeout headroom. The CLI cannot reference
        // Hall9k.Daemon (Reference graph: Cli -> Domain + Connectors), so there is no live
        // VerifyGateTimeout to read here — DefaultCommandTimeout mirrors its default, held to it
        // by ClaudeSettingsFileTests, exactly as h9k task work's own settings file already does.
        string settingsFile = RunPaths.SettingsFile(resolvedRunDirectory);
        string settingsContent = ClaudeSettingsFile.Build(ClaudeSettingsFile.DefaultCommandTimeout);
        await File.WriteAllTextAsync(settingsFile, settingsContent, cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[dim]Worktree: {worktreePath}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Branch: {branch}[/]");
        AnsiConsole.MarkupLineInterpolated($"[dim]Session: {sessionName}[/]");

        // Re-checked immediately before launch, mirroring h9k task work's own identical guard
        // (adversarial review, cycle 1, on that command): the worktree cut, the prompt build, and
        // the settings-file write above all take long enough for a concurrent h9k task work on
        // this exact task to run ReenterAsync in between — this run reads Dispatched with no
        // session recorded yet, so its own liveness check would pass and it would launch an
        // attached session into the very worktree this command is about to spawn a headless one
        // into. Reloading RunDetails here (a lightweight session, so this hits the database
        // rather than an identity-map cache) narrows that window down to the launch itself.
        RunDetails currentRun = await session.LoadAsync<RunDetails>(runId, cancellationToken)
            ?? throw new DomainConflictException(
                $"Task {taskId}'s run {runId} no longer has a record — h9k task release {taskId} to give the "
                + "claim back to the dispatch queue.");
        if (currentRun.State != RunState.Dispatched && currentRun.State != RunState.Running)
        {
            throw new DomainConflictException(
                $"Task {taskId}'s run {runId} is already {currentRun.State.Value} — another command moved it "
                + $"while this one was preparing to launch. h9k task show {taskId} to see where it stands.");
        }

        InteractiveSessionLiveness.EnsureNotAttachedElsewhere(currentRun, taskId, "start");

        int processId;
        DateTimeOffset startedAt;
        try
        {
            (processId, startedAt) = HeadlessLaunch.SpawnDetached(
                worktreePath, claudeSessionId, sessionName, model, promptFile, streamFile, standardErrorFile,
                settingsFile, project.SkipPermissions);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Mirrors h9k task work's own identical launch-failure handling: the claim and
            // worktree are already committed by this point, so nothing here needs raw
            // event-stream surgery to recover — the ordinary levers just point at it honestly.
            throw new DomainConflictException(
                $"Task {taskId} was claimed and its worktree prepared, but the headless session could not be "
                + $"launched ({exception.Message}). The claim is preserved — h9k task work {taskId} to attach "
                + $"and continue by hand, or h9k task release {taskId} to give it back to the dispatch queue.");
        }

        await using (IDocumentSession startSession = store.LightweightSession())
        {
            // Mirrors h9k task work's own InteractiveSessionStarted append exactly (same event,
            // same fields): MachineName is what lets another machine sharing this database tell
            // "checkable from here" from "recorded somewhere else" (InteractiveSessionLiveness),
            // since this claim's RunDispatched carries the same Guid.Empty sentinel an operator's
            // own claim does and so names no node of its own.
            startSession.Events.Append(runId, new InteractiveSessionStarted(
                runId, claudeSessionId, startedAt, processId, Environment.MachineName, sessionName));
            await startSession.SaveChangesAsync(cancellationToken);
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[dim]Task {taskId} is dispatched, headless, as {sessionName} (pid {processId}) — reachable on the session mesh (claude agents --json, or SendMessage). h9k task show {taskId} to watch it, or once it finishes: h9k task deliver, h9k task verify, h9k task work (to attach), h9k task handback, or h9k task release.[/]");

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The claim itself, pure enough to test against the failure states without touching the
    /// filesystem: Published loads the dependency snapshot and warns-then-optionally-overrides
    /// through <see cref="PrepareDeliberateClaimFromPublished"/>; Queued claims directly, exactly
    /// as <see cref="TaskWorkCommand.ClaimAndCutAsync"/>'s own Queued branch does (dependencies are
    /// empty by construction on an already-Queued task, so there is nothing to warn about).
    /// Everything else refuses — there is no re-entry branch here, unlike h9k task work: a
    /// deliberate kick-off only ever starts a fresh claim.
    /// </summary>
    internal static async Task<(Guid RunId, string WorktreePath, string Branch, string RunDirectory, bool ResumesPreviousWork, AgentModel Model)> ClaimAndCutAsync(
        DocumentStore store, IDocumentSession session, TaskAggregate task, StreamState fence, BootstrapContext context,
        Guid claudeSessionId, string sessionName, bool acknowledgeUnmetDependencies, CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDependency>? dependencies = null;
        if (task.State == TaskState.Published)
        {
            dependencies = await TaskDependencyQuery.LoadAsync(session, task.BlockedBy, cancellationToken);
        }
        else if (task.State != TaskState.Queued)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is {task.State.Value} — only a Published or Queued task can be started this "
                + "way. " + task.State switch
                {
                    var state when state == TaskState.Blocked =>
                        "It is already assigned and waiting on a dependency; h9k task show names it — the "
                        + "acknowledgment override only applies at the moment of assignment, not to a task "
                        + "already sitting Blocked.",
                    var state when state.IsPreDispatch =>
                        $"Publish it first: h9k task publish {task.Id}.",
                    var state when state == TaskState.Claimed =>
                        $"It already has a live claim — h9k task show {task.Id} names who holds it.",
                    _ => "Its story has already moved past dispatch.",
                });
        }
        else if (task.AssignedOwnerId != context.OwnerId)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is assigned to {task.AssignedOwnerId} — a deliberate kick-off only starts your "
                + "own owner's work.");
        }

        TaskDetails taskDetails = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {task.Id}.");
        ProjectDetails project = await session.LoadAsync<ProjectDetails>(taskDetails.ProjectId, cancellationToken)
            ?? throw new DomainNotFoundException($"Task {task.Id}'s project no longer exists.");

        // The full resolution chain (Decisions Log #33): the CLI cannot reach a live daemon's
        // in-memory DaemonOptions (Reference graph: Cli -> Domain + Connectors), but the node's
        // per-role and platform-default tiers are durable settings, not daemon state — they live
        // in the platform config file and environment, read through the same
        // OperatingSettingsResolver h9k config show already renders them with, so a start-it-mine
        // session resolves to exactly the model a dispatcher-launched build on this node would.
        // Checked before anything is claimed: a broken model refuses up front rather than after
        // the claim and worktree cut are already committed.
        OperatingSettingsReport operatingSettings = await OperatingSettingsResolver.ResolveAsync(cancellationToken);
        string? buildRoleDefault = operatingSettings.ModelByRole
            .First(role => role.Role == nameof(RoleModelSettings.Build)).Model.Value;
        AgentModel model = AgentModel.Resolve(
            taskOverride: taskDetails.Model, roleDefault: buildRoleDefault, projectDefault: project.Model,
            platformDefault: operatingSettings.DefaultModel.Value);
        if (!model.IsWellFormed)
        {
            throw new DomainConflictException(
                $"Task {task.Id} resolved to an unusable model ('{model.Value}') — fix it with "
                + $"h9k task revise {task.Id} --model or h9k project set --model before starting it this way.");
        }

        // A pr-review task dispatches through a completely different path (a detached checkout of
        // the pull request's own head, the pr-review prompt lens, no branch of its own) that this
        // command cannot build — mirrors h9k task work's own identical refusal exactly.
        if (task.Type == TaskType.PrReview)
        {
            throw new DomainConflictException(
                $"Task {task.Id} is a pr-review task — it has no diff of its own for a build session to work "
                + "against; it dispatches headlessly against the pull request instead. h9k task show "
                + $"{task.Id} to see where it stands.");
        }

        // A reopened task carries its existing pull request's branch and expects the daemon's own
        // follow-up prompt (BuildFixChecks/BuildRebase/BuildFollowUp), which lives in Hall9k.Daemon
        // and this command cannot build — mirrors h9k task work's own identical refusal.
        if (taskDetails.FollowUpBranch.IsNotBlank())
        {
            throw new DomainConflictException(
                $"Task {task.Id} was reopened onto its existing pull request ({taskDetails.PullRequestUrl}) — a "
                + "deliberate kick-off cannot build the follow-up prompt that branch needs. "
                + $"h9k pr resolve {task.Id} to dispatch a headless follow-up instead.");
        }

        Guid runId = DomainId.New();
        DateTimeOffset claimedAt = DateTimeOffset.UtcNow;

        TaskAssigned? assigned = null;
        TaskClaimed claimed;
        if (dependencies is not null)
        {
            IReadOnlyList<TaskDependency> unmet;
            (assigned, claimed, unmet) = PrepareDeliberateClaimFromPublished(
                task, context.OwnerId, dependencies, runId, claimedAt, acknowledgeUnmetDependencies);
            if (unmet.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Starting task {task.Id} despite {unmet.Count} unmet dependenc"
                    + (unmet.Count == 1 ? "y" : "ies") + " (--acknowledge-unmet-dependencies):[/]");
                foreach (TaskDependency dependency in unmet)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]  - {dependency.Describe()}[/]");
                }
            }
        }
        else
        {
            claimed = TaskDecider.ClaimDeliberately(
                task, context.OwnerId, runId, claimedAt, dependencyOverrideAcknowledged: false);
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

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException($"Task {task.Id} changed while claiming it — check h9k status and try again.");
        }

        Worktree worktree;
        bool resumesPreviousWork;
        string runDirectory;
        try
        {
            GitWorktreeManager worktrees = new(new ConsoleWorktreeLogger<GitWorktreeManager>());
            (worktree, resumesPreviousWork) = await TaskWorkCommand.CheckoutFreshOrRetryAsync(
                worktrees, taskDetails, project, task.Id, runId, cancellationToken);

            string? existingTaskDirectory = project.HomeDirectory.HasValue
                ? HomeEntryLookup.FindExisting(ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), task.Id)
                    ?? HomeEntryLookup.FindExisting(ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value), task.Id)
                : null;
            runDirectory = existingTaskDirectory is not null
                ? RunPaths.ResolveDirectoryUnderTaskDirectory(existingTaskDirectory, runId)
                : RunPaths.ResolveDirectory(project.HomeDirectory, TaskDocumentRenderer.DirectoryName(taskDetails), runId);

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, task.Id, Guid.Empty, context.OwnerId, claimed.LeaseGeneration, claudeSessionId,
                worktree.Path, worktree.Branch, ExecutorMode.Subscription, DateTimeOffset.UtcNow,
                IsFollowUp: false, Model: model, RunDirectory: runDirectory, SessionName: sessionName));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailDeliberateClaimAsync(
                store, task.Id, claimedVersion, runId, "cancelled while preparing the worktree", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await FailDeliberateClaimAsync(store, task.Id, claimedVersion, runId, exception.Message, cancellationToken);
            throw new DomainConflictException(
                $"Task {task.Id} was claimed but could not be prepared for a headless start ({exception.Message}). "
                + $"It has been recorded Failed — h9k task retry {task.Id} to try again.");
        }

        await Hall9k.Cli.Infrastructure.Doorbell.RingAsync($"task-claimed-deliberately:{task.Id}", cancellationToken);
        return (runId, worktree.Path, worktree.Branch, runDirectory, resumesPreviousWork, model);
    }

    /// <summary>
    /// The atomic decision behind the Published entry: assigns <paramref name="task"/> to
    /// <paramref name="ownerId"/> and claims it deliberately as one unit, pure so it is
    /// independently testable (mirrors <see cref="TaskWorkCommand.PrepareInteractiveClaimFromPublished"/>'s
    /// own shape exactly). The one behavior that differs from that sibling: an unmet dependency
    /// does not refuse outright here — it refuses only when
    /// <paramref name="acknowledgeUnmetDependencies"/> is false, naming every open blocker in the
    /// refusal so the human can decide with the platform's own advice in hand; when true, the
    /// claim proceeds anyway and the override is recorded on the resulting
    /// <see cref="TaskClaimed.DependencyOverrideAcknowledged"/> (the idea's own ruling: "the
    /// platform advises rather than refuses... let it be the human's call").
    /// </summary>
    internal static (TaskAssigned Assigned, TaskClaimed Claimed, IReadOnlyList<TaskDependency> UnmetDependencies) PrepareDeliberateClaimFromPublished(
        TaskAggregate task, Guid ownerId, IReadOnlyList<TaskDependency> dependencies, Guid runId, DateTimeOffset now,
        bool acknowledgeUnmetDependencies)
    {
        TaskAssigned assigned = TaskDecider.Assign(task, ownerId, dependencies, now, ownerId);
        IReadOnlyList<TaskDependency> unmet =
            [.. dependencies.Where(dependency => assigned.UnmetDependencies.Contains(dependency.Id))];

        if (unmet.Count > 0 && !acknowledgeUnmetDependencies)
        {
            throw new DomainBusinessRuleException(
                $"Task {task.Id} depends on {unmet.Count} task(s) that have not closed out: "
                + string.Join("; ", unmet.Select(dependency => dependency.Describe())) + ". "
                + "The platform advises rather than refuses here: "
                + $"h9k task start {task.Id} --acknowledge-unmet-dependencies to start it anyway, once you have "
                + $"confirmed that is what you want, or h9k task assign {task.Id} to hold it Blocked until they "
                + $"clear. h9k task show {task.Id} for the full picture.");
        }

        task.Apply(assigned);
        TaskClaimed claimed = TaskDecider.ClaimDeliberately(task, ownerId, runId, now, unmet.Count > 0);
        return (assigned, claimed, unmet);
    }

    private static async Task FailDeliberateClaimAsync(
        DocumentStore store, Guid taskId, long claimedVersion, Guid runId, string reason, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        if (fence is null || fence.Version != claimedVersion)
        {
            return;
        }

        TaskAggregate? current = await session.Events.AggregateStreamAsync<TaskAggregate>(
            taskId, version: fence.Version, token: cancellationToken);
        if (current is null || !TaskDecider.CanFail(current))
        {
            return;
        }

        session.Events.Append(taskId, expectedVersion: fence.Version + 1,
            TaskDecider.Fail(current, runId, $"Deliberate headless start setup failed: {reason}", DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(cancellationToken);
    }
}
