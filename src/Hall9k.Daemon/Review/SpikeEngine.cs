using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Idea.Rendering;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Review;

/// <summary>
/// The spike task type's own driver (task: a spike is a run, not a walk): one build session
/// (dispatched by RunLauncher like any ordinary task), then exactly one review cycle and at most
/// one fix lap, fixed by the type rather than any review-cycle config — never ReviewEngine's own
/// cycle/track/fix-loop state machine, and never PullRequestOpener: a spike never opens a pull
/// request, draft or otherwise, and there is no merge for closeout to ever watch for.
/// <para>
/// Deliberately simpler than <see cref="PrReviewEngine"/>: the whole review-then-maybe-fix-then-
/// review sequence runs synchronously inside one call to <see cref="ReviewAsync"/>, entered
/// exactly once, right after the primary build session's own completion — there is no
/// daemon-restart resumption of a review cycle already in flight the way PrReviewEngine's own
/// conformance lens has, because a spike's whole review cycle is short and bounded by
/// construction (at most two review passes and one fix lap) and adding that hardening here was
/// judged out of proportion to what a spike actually needs. A daemon restart mid-cycle leaves the
/// run in whatever state it was in when the process died; the next adoption sweep's own generic
/// stranded-run handling picks it up like any other stalled run.
/// </para>
/// </summary>
public sealed class SpikeEngine(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IWorktreeManager worktrees,
    NodeContext node,
    IOptions<DaemonOptions> options,
    ILogger<SpikeEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    private static readonly string[] BudgetWatchedStates = [RunState.Dispatched.Value, RunState.Running.Value];

    /// <summary>
    /// Polled by <c>SpikeBudgetWatchLoop</c> on the ordinary sweep cadence, mirroring
    /// <c>RunSupervisor.StopRunsSupersededByTakeoverAsync</c>'s own shape: this node's own live
    /// build sessions for a spike with a stated wall-clock budget (PLAN.md §16 PLACEHOLDER-1d81543a:
    /// the budget bounds the build session only), terminated the moment they cross it rather than
    /// left to run unbounded.
    /// Scoped to <see cref="RunState.Dispatched"/>/<see cref="RunState.Running"/> only — once a
    /// spike's build session ends, its own review cycle runs on the review model, outside this
    /// budget entirely, and is never a candidate here.
    /// </summary>
    public async Task EndRunsOverWallClockBudgetAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<RunDetails> candidates = await query.Query<RunDetails>()
            .Where(run => run.NodeId == nodeId)
            .Where(run => run.MatchesSql("d.data ->> 'state' in (?, ?)", BudgetWatchedStates[0], BudgetWatchedStates[1]))
            .ToListAsync(cancellationToken);

        foreach (RunDetails run in candidates)
        {
            if (run.ActiveSessions.Count == 0)
            {
                continue;
            }

            TaskDetails? task = await query.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            if (task is not { Type: var type } || type != TaskType.Spike
                || task.Constraints?.MaxWallClock is not { } maxWallClock)
            {
                continue;
            }

            TimeSpan elapsed = DateTimeOffset.UtcNow - run.DispatchedAt;
            if (elapsed < maxWallClock)
            {
                continue;
            }

            await KillOverBudgetRunAsync(
                run, run.TaskId,
                $"the build session's own wall-clock budget ({maxWallClock}) was crossed after {elapsed:g}",
                cancellationToken);
        }
    }

    /// <summary>
    /// The token half of the same budget watch (task: TaskConstraints gains its first consumer) —
    /// polled by <c>SpikeBudgetWatchLoop</c> alongside <see cref="EndRunsOverWallClockBudgetAsync"/>.
    /// Unlike the wall-clock half, nothing on <see cref="RunDetails"/> itself carries a live spend
    /// figure: <c>RunDetails.InputTokens</c> and its siblings only ever accumulate once
    /// <c>TokensRecorded</c> lands at session end, which is exactly the moment this watch exists to
    /// act before. <see cref="StreamTailReader.ReadLiveTokenSpendAsync"/> reads the session's own
    /// still-growing stream file instead, summing every turn's own billed usage — the one place a
    /// budget crossed mid-session is actually observable before the session decides to stop on its
    /// own.
    /// </summary>
    public async Task EndRunsOverTokenBudgetAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        await using IQuerySession query = store.QuerySession();
        IReadOnlyList<RunDetails> candidates = await query.Query<RunDetails>()
            .Where(run => run.NodeId == nodeId)
            .Where(run => run.MatchesSql("d.data ->> 'state' in (?, ?)", BudgetWatchedStates[0], BudgetWatchedStates[1]))
            .ToListAsync(cancellationToken);

        foreach (RunDetails run in candidates)
        {
            if (run.ActiveSessions.Count == 0)
            {
                continue;
            }

            TaskDetails? task = await query.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
            if (task is not { Type: var type } || type != TaskType.Spike
                || task.Constraints?.MaxTokens is not { } maxTokens)
            {
                continue;
            }

            string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
            string streamFile = RunPaths.StreamFile(runDirectory);
            long spent = await StreamTailReader.ReadLiveTokenSpendAsync(streamFile, cancellationToken);
            if (spent < maxTokens)
            {
                continue;
            }

            await KillOverBudgetRunAsync(
                run, run.TaskId,
                $"the build session's own live token spend ({spent}) crossed its own stated budget "
                + $"({maxTokens}) before it finished",
                cancellationToken);
        }
    }

    /// <summary>
    /// The shared tail of both budget watches above: terminate whatever process is still running,
    /// record the kill, and hand off to <see cref="EndOverBudgetAsync"/> for the actual verdict.
    /// Best-effort against a run that ended some other way in the same instant — the fenced append
    /// below simply loses that race and does nothing further, exactly as <see cref="FailAsync"/>'s
    /// own identical guard does.
    /// </summary>
    private async Task KillOverBudgetRunAsync(
        RunDetails run, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        foreach (ActiveSession activeSession in run.ActiveSessions)
        {
            if (activeSession.StartedAt is { } startedAt)
            {
                processManager.TerminateTree(activeSession.ProcessId, startedAt);
            }
        }

        await using (IDocumentSession killSession = store.LightweightSession())
        {
            // Read fresh, immediately before deciding to write: FetchStreamStateAsync's own
            // version-conflict guard below only catches an event landing AFTER this read, never
            // one that already landed before it — a run this watch queried as still live (its own
            // candidates query, above) can have completed naturally in the gap between that query
            // and this kill, and a stale RunDetails read here would still see it as non-terminal
            // and append RunKilled on top of whatever terminal event the natural completion
            // already wrote (the identical shape FinalizeAsync's own terminal guard exists to
            // close, independent pre-PR review, cycle 1, adversarial lens — class sweep on that
            // same finding).
            RunDetails? currentRun = await killSession.LoadAsync<RunDetails>(run.Id, cancellationToken);
            if (currentRun is { State.IsTerminal: true })
            {
                logger.LogInformation(
                    "Run {RunId}: already {State} by the time a budget kill was about to be recorded - not recording a kill over it",
                    run.Id, currentRun.State.Value);
                return;
            }

            StreamState? runFence = await killSession.Events.FetchStreamStateAsync(run.Id, cancellationToken);
            if (runFence is null)
            {
                return;
            }

            killSession.Events.Append(
                run.Id, expectedVersion: runFence.Version + 1,
                new RunKilled(run.Id, KillReason.BudgetExceeded, KilledByOwnerId: null, DateTimeOffset.UtcNow));
            try
            {
                await killSession.SaveChangesAsync(cancellationToken);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                logger.LogInformation(
                    "Run {RunId}: lost the race recording a budget kill — already ended some other way",
                    run.Id);
                return;
            }
        }

        logger.LogInformation(
            "Run {RunId} task {TaskId}: spike build session ended — {Reason}", run.Id, taskId, reason);
        await EndOverBudgetAsync(run.Id, taskId, reason, cancellationToken);
    }

    /// <summary>
    /// Writes the primary build session's own result to disk as the spike's findings document, at
    /// the fixed path <see cref="RunPaths.SpikeFindingsFile"/> — the session is asked to end its
    /// final message with exactly this text (<see cref="AgentPromptBuilder.SpikeFindingsMarker"/>),
    /// so its whole summary IS the findings document, mirroring
    /// <see cref="PrReviewEngine.RecordPrimarySessionResultAsync"/>'s identical capture for its own
    /// primary session. Idempotent: a resumed call finds the file already there and does nothing.
    /// </summary>
    public async Task RecordFindingsAsync(string runDirectory, string summary, CancellationToken cancellationToken)
    {
        string path = RunPaths.SpikeFindingsFile(runDirectory);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(runDirectory);
            await File.WriteAllTextAsync(path, summary, cancellationToken);
        }
    }

    public async Task ReviewAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await DriveAsync(runId, taskId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Spike loop crashed for run {RunId}", runId);
            await FailAsync(runId, taskId, $"Spike loop failed: {exception.Message}", cancellationToken);
        }
    }

    /// <summary>
    /// Ends a spike's build session for crossing its own stated token or wall-clock budget (task:
    /// TaskConstraints gains its first consumer) — called by <c>SpikeBudgetWatchLoop</c> once it
    /// terminates the process, never by the ordinary completion path above. Finalizes the run with
    /// a budget-exhausted verdict directly, using whatever findings the session had already
    /// written, and never as Failed.
    /// </summary>
    public async Task EndOverBudgetAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null ? null : await query.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (run is null || task is null || project is null)
        {
            logger.LogError("Cannot end over-budget spike run {RunId}: run, task, or project missing", runId);
            return;
        }

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        string findingsPath = RunPaths.SpikeFindingsFile(runDirectory);
        await EnsureFindingsRecordedAsync(runDirectory, cancellationToken);
        await FinalizeAsync(
            runId, taskId, run, task, project, SpikeVerdict.BudgetExhausted, reason, findingsPath, cancellationToken);
    }

    private async Task DriveAsync(Guid runId, Guid taskId, CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        RunDetails? run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        TaskDetails? task = run is null ? null : await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null ? null : await query.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (run is null || task is null || project is null)
        {
            logger.LogError("Cannot drive spike run {RunId}: run, task, or project missing", runId);
            return;
        }

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        await EnsureFindingsRecordedAsync(runDirectory, cancellationToken);
        string findingsPath = RunPaths.SpikeFindingsFile(runDirectory);

        // Reactive token-budget check (PLAN.md §16 PLACEHOLDER-1d81543a: the budget bounds the
        // build session only) — a backstop behind EndRunsOverTokenBudgetAsync's own proactive
        // watch, for the session that finished naturally (or between two polls) with a spend that
        // had already crossed the limit by then: the build session's own spend is already on the
        // run stream by the time this is ever called (RunSupervisor records it before reaching
        // this branch), so a session whose spend already crossed the stated limit is recorded
        // budget-exhausted here rather than spending a review cycle judging findings a budget
        // already cut short.
        if (task.Constraints?.MaxTokens is { } maxTokens)
        {
            long spent = run.InputTokens + run.CacheReadInputTokens + run.CacheCreationInputTokens + run.OutputTokens;
            if (spent >= maxTokens)
            {
                await FinalizeAsync(
                    runId, taskId, run, task, project, SpikeVerdict.BudgetExhausted,
                    $"the build session's own token spend ({spent}) had already met or crossed the "
                    + $"stated budget ({maxTokens}) by the time it ended",
                    findingsPath, cancellationToken);
                return;
            }
        }

        string findingsText = File.Exists(findingsPath)
            ? await File.ReadAllTextAsync(findingsPath, cancellationToken)
            : "(no findings recorded)";
        string baseBranch = run.BaseBranchOr(project.BaseBranch);

        (SpikeVerdict Verdict, string Reason)? firstPass = await RunReviewPassAsync(
            runId, taskId, run, task, project, runDirectory, findingsText, baseBranch, pass: 1, isFixLap: false,
            cancellationToken);
        if (firstPass is not { } first)
        {
            return;
        }

        if (first.Verdict == SpikeVerdict.Met)
        {
            await FinalizeAsync(runId, taskId, run, task, project, SpikeVerdict.Met, first.Reason, findingsPath, cancellationToken);
            return;
        }

        // Exactly one fix lap, fixed by the type (PLAN.md §16 PLACEHOLDER-1d81543a): dispatched
        // into the same worktree and branch the build session already left behind, never told
        // about a budget — the same entry scopes TaskConstraints to the build session that
        // already ran.
        string fixPrompt = AgentPromptBuilder.BuildSpikeFix(
            task, first.Reason, commandTimeout: _options.VerifyGateTimeout);
        (AgentResult Result, string ArtifactName)? fixOutcome = await DispatchAndAwaitAsync(
            runId, taskId, run, task, project, "spike-fix", fixPrompt, AgentRole.Fix, cancellationToken);
        if (fixOutcome is not { } fix)
        {
            return;
        }

        if (fix.Result.IsError)
        {
            await FailAsync(
                runId, taskId,
                $"The spike's one fix lap ended in error: {fix.Result.Summary ?? "(no message)"}",
                cancellationToken);
            return;
        }

        await File.WriteAllTextAsync(findingsPath, fix.Result.Summary ?? string.Empty, cancellationToken);
        string fixedFindingsText = fix.Result.Summary ?? string.Empty;

        (SpikeVerdict Verdict, string Reason)? secondPass = await RunReviewPassAsync(
            runId, taskId, run, task, project, runDirectory, fixedFindingsText, baseBranch, pass: 2, isFixLap: true,
            cancellationToken);
        if (secondPass is not { } second)
        {
            return;
        }

        // Whatever the second, final pass says stands — met or not-met — with no further fix lap
        // (PLAN.md §16 PLACEHOLDER-1d81543a): a spike that still does not satisfy the reviewer
        // after the fix lap records not-met with the reviewer's reason and exits cleanly, never
        // parking for a human.
        await FinalizeAsync(runId, taskId, run, task, project, second.Verdict, second.Reason, findingsPath, cancellationToken);
    }

    private async Task<(SpikeVerdict Verdict, string Reason)?> RunReviewPassAsync(
        Guid runId, Guid taskId, RunDetails run, TaskDetails task, ProjectDetails project, string runDirectory,
        string findingsText, string baseBranch, int pass, bool isFixLap, CancellationToken cancellationToken)
    {
        string prompt = AgentPromptBuilder.BuildSpikeReview(
            task, run.Branch, baseBranch, findingsText, isFixLap, commandTimeout: _options.VerifyGateTimeout);
        (AgentResult Result, string ArtifactName)? outcome = await DispatchAndAwaitAsync(
            runId, taskId, run, task, project, $"spike-review-{pass}", prompt, AgentRole.Review, cancellationToken);
        if (outcome is not { } review)
        {
            return null;
        }

        if (review.Result.IsError)
        {
            await FailAsync(
                runId, taskId,
                $"The spike's review session ended in error: {review.Result.Summary ?? "(no message)"}",
                cancellationToken);
            return null;
        }

        string reviewText = review.Result.Summary ?? string.Empty;
        await File.WriteAllTextAsync(RunPaths.SpikeReviewFile(runDirectory, pass), reviewText, cancellationToken);

        if (!SpikeReviewResultParser.TryParse(reviewText, out SpikeVerdict verdict, out string reason))
        {
            await FailAsync(
                runId, taskId,
                "The spike's review session ended without a usable verdict — no "
                + $"'{AgentPromptBuilder.SpikeVerdictMarker}' line. Retry the task to dispatch a fresh review.",
                cancellationToken);
            return null;
        }

        return (verdict, reason);
    }

    /// <summary>
    /// Spawns one bounded session into the build session's own retained worktree and blocks for
    /// its result — the one primitive both the review pass and the fix lap are built from, mirroring
    /// how <see cref="PrReviewEngine"/> is built entirely from <see cref="SessionResultWaiter"/> and
    /// its own dispatch rather than ReviewEngine's cycle machinery. Returns null (having already
    /// recorded a failure) when the run was reclaimed or killed out from under this dispatch.
    /// </summary>
    private async Task<(AgentResult Result, string ArtifactName)?> DispatchAndAwaitAsync(
        Guid runId, Guid taskId, RunDetails run, TaskDetails task, ProjectDetails project, string role,
        string prompt, AgentRole modelRole, CancellationToken cancellationToken)
    {
        await using (IQuerySession terminalCheck = store.QuerySession())
        {
            RunDetails? current = await terminalCheck.LoadAsync<RunDetails>(runId, cancellationToken);
            if (current is { State.IsTerminal: true })
            {
                logger.LogInformation(
                    "Run {RunId}: already {State} by the time the spike's own {Role} session was about to dispatch - not spawning",
                    runId, current.State.Value, role);
                return null;
            }
        }

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        Guid sessionId = DomainId.New();
        string artifactName = $"{role}-{sessionId:N}";
        AgentModel model = _options.ResolveModel(modelRole, task.Model, project.Model);
        string sessionName = SessionRoleName.For(DomainId.Short(taskId), role);

        SpawnedAgent agent = await executor.SpawnAsync(
            new AgentSpawnRequest(
                runId, sessionId, run.WorktreePath, runDirectory, prompt, (ExecutorMode)run.ExecutorMode, model,
                project.SkipPermissions, artifactName)
            {
                TaskId = taskId,
                SessionName = sessionName,
            },
            cancellationToken);

        string streamFile = RunPaths.SessionStreamFile(runDirectory, artifactName);
        SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
            streamFile, agent.ProcessId, agent.StartedAt, processManager,
            token => TouchActivityAsync(runId, token), cancellationToken);
        if (wait.Lingering.Count > 0)
        {
            logger.LogWarning(
                "Run {RunId}: the spike's own {Role} session left {Count} process(es) still running after its terminal result arrived",
                runId, role, wait.Lingering.Count);
        }

        if (wait.Result is null)
        {
            await FailAsync(runId, taskId, $"The spike's own {role} session died without a result.", cancellationToken);
            return null;
        }

        await using (IDocumentSession tokensSession = store.LightweightSession())
        {
            tokensSession.Events.Append(runId, wait.Result.ToTokensRecorded(runId, DateTimeOffset.UtcNow, model));
            await tokensSession.SaveChangesAsync(cancellationToken);
        }

        return (wait.Result, artifactName);
    }

    /// <summary>
    /// The recovery half of <see cref="RecordFindingsAsync"/>, mirroring
    /// <see cref="PrReviewEngine.EnsureAdversarialResultRecordedAsync"/>'s identical gap: a daemon
    /// restart landing between the build session's own completion and RunSupervisor's call to
    /// <see cref="RecordFindingsAsync"/> would otherwise reach here with nothing written yet.
    /// </summary>
    private async Task EnsureFindingsRecordedAsync(string runDirectory, CancellationToken cancellationToken)
    {
        string path = RunPaths.SpikeFindingsFile(runDirectory);
        if (File.Exists(path))
        {
            return;
        }

        string streamFile = RunPaths.StreamFile(runDirectory);
        if (!File.Exists(streamFile))
        {
            return;
        }

        string? summary = null;
        using (StreamReader reader = new(new FileStream(
            streamFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (StreamJsonParser.TryParseResult(line, out AgentResult result))
                {
                    summary = result.Summary ?? string.Empty;
                }
            }
        }

        if (summary is not null)
        {
            await RecordFindingsAsync(runDirectory, summary, cancellationToken);
        }
    }

    /// <summary>
    /// The one place a spike ever reaches Done (task: a spike is a run, not a walk): releases the
    /// worktree, disposes of the branch per its own kind (prototype pushed to origin, research
    /// kept locally, experiment deleted locally once its findings are copied out), copies the
    /// findings document into the idea's own workspace when this spike was cut from one, and
    /// appends <see cref="SpikeConcluded"/> alongside <see cref="TaskCompleted"/> with no pull
    /// request — a spike never opens one, so there is nothing for closeout's merge watch to ever
    /// find here.
    /// </summary>
    private async Task FinalizeAsync(
        Guid runId, Guid taskId, RunDetails run, TaskDetails task, ProjectDetails project, SpikeVerdict verdict,
        string reason, string findingsPath, CancellationToken cancellationToken)
    {
        bool hasCheckout = run.WorktreePath.IsNotBlank();
        if (hasCheckout)
        {
            // A local launch standing in this checkout comes down first (LocalLaunchProcesses' own
            // doc). A spike carries no review report and so no offer, but h9k task run-local is
            // not restricted to pr-review tasks, so the shape is the same one and gets the same
            // treatment rather than a reason it is exempt.
            LocalLaunchProcesses.EndAll(run.LocalLaunch);

            try
            {
                await worktrees.RemoveAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", run.WorktreePath);
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();

        // Read fresh, right before this method's own writes, rather than trusted from the `run`
        // parameter the caller loaded earlier: a wall-clock or token-budget kill already appended
        // RunKilled on THIS exact call path (KillOverBudgetRunAsync → EndOverBudgetAsync →
        // here) before this method ever runs, and the two branches below must never append a
        // second terminal event over an already-terminal run — the same discipline FailAsync's
        // own identical guard already holds itself to (independent pre-PR review, cycle 1,
        // adversarial lens: RunCompleted landing on top of a Killed run flipped RunDetails.State
        // back to Completed while FailureReason still read BudgetExceeded, and the fence-rejection
        // branch's own RunSuperseded carried the identical defect).
        RunDetails? currentRunDetails = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        bool runAlreadyTerminal = currentRunDetails is { State.IsTerminal: true };

        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, run.LeaseGeneration, nameof(RunCompleted), cancellationToken))
        {
            if (!runAlreadyTerminal && await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                TaskDetails? currentTask = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
                session.Events.Append(
                    runId, new RunSuperseded(runId, currentTask?.LeaseGeneration ?? run.LeaseGeneration, now));
                await session.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Run {RunId}: retired as superseded — the spike finalize found it was no longer task {TaskId}'s current generation",
                    runId, taskId);
            }

            return;
        }

        if (fenced is { } current && current.Task.State == TaskState.Claimed)
        {
            SpikeConcluded concluded = TaskDecider.ConcludeSpike(current.Task, runId, verdict, reason, findingsPath, now);
            TaskCompleted completed = TaskDecider.Complete(current.Task, runId, pullRequestUrl: null, now);
            // expectedVersion is the stream's version AFTER this whole batch lands (Marten's own
            // IEventOperations.Append doc: "expected maximum event version after append"), not
            // "current + 1" — this call appends two events, so it needs current.Version + 2, not
            // the single-event convention every sibling TaskDecider.Fail/Complete call site in
            // this codebase correctly uses for its own one-event append.
            session.Events.Append(taskId, expectedVersion: current.Version + 2, concluded, completed);

            if (current.Task.SourceIdeaId is { } ideaId)
            {
                IdeaAggregate? freshIdea = await session.Events.AggregateStreamAsync<IdeaAggregate>(ideaId, token: cancellationToken);
                if (freshIdea is not null)
                {
                    session.Events.Append(ideaId, IdeaDecider.RecordSpikeConcluded(freshIdea, taskId, verdict, reason, now));
                }
            }
        }

        if (!runAlreadyTerminal)
        {
            session.Events.Append(runId, new RunCompleted(runId, now));
        }

        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race finalizing the spike for run {RunId} — a newer claim committed first",
                taskId, runId);
            return;
        }

        // Local git side effects run after the durable record lands, not before: a crash between
        // the two leaves the task correctly Done with a verdict recorded, and only the branch's
        // own local fate (still pushed/kept/deleted best-effort below) to clean up by hand — the
        // opposite ordering would risk deleting a branch and then losing the verdict that referred
        // to it if the append below failed.
        if (task.SpikeKind.PushesBranch)
        {
            try
            {
                await ForceWithLeasePusher.PushAsync(
                    ExternalProcess.RunnerWithDeadline(PushDeadline),
                    project.RepositoryPath, run.Branch, new HashSet<string>(), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not push the prototype spike's own branch {Branch} to origin — it stays local; push it by hand", run.Branch);
            }
        }
        else if (task.SpikeKind == SpikeKind.Experiment)
        {
            try
            {
                // An experiment spike's branch is never pushed (task.SpikeKind.PushesBranch is
                // false for this kind), so it never reached origin at all — neither
                // RemoteBranchDeletionOwner value describes that honestly (independent pre-PR
                // review, cycle 1, both lenses): .Daemon asserts this platform owes a
                // `git push origin --delete` for a ref that was never created, and .GitHub asserts
                // the repository's own delete-on-merge setting is why nothing needs pushing, which
                // is simply not what happened here either. DeleteBranchEverywhereAsync's whole
                // plan is built for a MERGED pull request's branch; local-only deletion, under the
                // same per-repo lock every other git write in this worktree's repository takes
                // (Decisions Log #4), is the honest and complete answer for one that never left
                // this machine.
                await using IAsyncDisposable repositoryLock = await worktrees.AcquireRepositoryLockAsync(
                    project.RepositoryPath, cancellationToken);
                ProcessResult deleted = await ExternalProcess.Runner(
                    "git", ["branch", "-D", run.Branch], project.RepositoryPath, cancellationToken);
                if (deleted.ExitCode != 0)
                {
                    logger.LogDebug(
                        "Local branch {Branch} not deleted ({Error})", run.Branch, deleted.StandardError.Trim());
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not delete the experiment spike's own branch {Branch} — safe to delete by hand", run.Branch);
            }
        }

        if (task.SourceIdeaId is { } sourceIdeaId)
        {
            await CopyFindingsToIdeaWorkspaceAsync(sourceIdeaId, taskId, findingsPath, cancellationToken);
        }

        logger.LogInformation(
            "Run {RunId} task {TaskId}: spike concluded {Verdict} — {Reason}", runId, taskId, verdict.Value, reason);
    }

    /// <summary>
    /// Copies the findings document into the idea's own workspace at
    /// <c>spikes/&lt;task-id&gt;/findings.md</c> — never touching the idea's journal.md, which is
    /// the human-authored record of the walk itself, not a spike's own output. Best-effort: losing
    /// this copy never fails the run, since the run directory's own copy (read back by
    /// <c>h9k task show</c> off <see cref="TaskDetails.SpikeFindingsPath"/>) is the durable record
    /// either way.
    /// </summary>
    private async Task CopyFindingsToIdeaWorkspaceAsync(
        Guid ideaId, Guid taskId, string findingsPath, CancellationToken cancellationToken)
    {
        try
        {
            await using IQuerySession query = store.QuerySession();
            IdeaDetails? idea = await query.LoadAsync<IdeaDetails>(ideaId, cancellationToken);
            if (idea is null)
            {
                return;
            }

            if (idea.WorkspaceHome.HasValue && !idea.WorkspaceHome.IsNativeForm)
            {
                // A workspace home replicated from a node on a different operating system FAMILY
                // names a directory on THAT machine, never this one — building a path from it and
                // calling Directory.CreateDirectory would misread a foreign path's own syntax as
                // this host's and create a bogus directory tree relative to wherever that
                // misreading happens to land. Best-effort already covers this: the run directory's
                // own copy, read back by h9k task show, is the durable record either way (this
                // method's own doc comment). IsNativeForm tests path SHAPE, not host identity
                // (ProjectHome.IsNativeForm's own doc): a home replicated from another node of the
                // SAME family (macOS from Linux, or the reverse) still reads as native and reaches
                // the Directory.CreateDirectory below on a directory that may not exist on THIS
                // machine either — the catch around this whole method is what keeps that residual
                // case from failing the run either way.
                return;
            }

            string ideaDirectory = IdeaPaths.ResolveDirectory(
                idea.WorkspaceHome, IdeaDocumentRenderer.DirectoryName(idea), idea.Id);
            string destination = Path.Combine(IdeaPaths.WorkspaceDirectory(ideaDirectory), "spikes", taskId.ToString(), "findings.md");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string content = File.Exists(findingsPath)
                ? await File.ReadAllTextAsync(findingsPath, cancellationToken)
                : "(no findings recorded)";
            await File.WriteAllTextAsync(destination, content, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not copy spike {TaskId}'s findings into its idea's workspace", taskId);
        }
    }

    private static readonly TimeSpan PushDeadline = TimeSpan.FromMinutes(2);

    private async Task FailAsync(Guid runId, Guid taskId, string reason, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();

        RunDetails? run = await session.LoadAsync<RunDetails>(runId, cancellationToken);
        if (run is { State.IsTerminal: true })
        {
            logger.LogInformation(
                "Run {RunId}: already {State} by the time the spike loop's own failure was about to be recorded - not recording a failure over it",
                runId, run.State.Value);
            return;
        }

        session.Events.Append(runId, new RunFailed(runId, reason, now));

        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (fenced is { } current
            && TaskDecider.CanFail(current.Task)
            && (run is null || await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, run.LeaseGeneration, nameof(TaskFailed), cancellationToken)))
        {
            session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(current.Task, runId, reason, now));
            session.Delete<TaskLease>(taskId);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogInformation(
                "Task {TaskId}: lost the generation race recording a spike failure for run {RunId} — a newer claim committed first",
                taskId, runId);
            return;
        }

        logger.LogWarning("Run {RunId} spike failed: {Reason}", runId, reason);
    }

    private async Task TouchActivityAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        RunActivity activity = await session.LoadAsync<RunActivity>(runId, cancellationToken)
            ?? new RunActivity { Id = runId };
        activity.LastActivityAt = DateTimeOffset.UtcNow;
        session.Store(activity);
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Parses a spike review session's own verdict trailer — <see cref="AgentPromptBuilder.SpikeVerdictMarker"/>
/// followed by <see cref="AgentPromptBuilder.SpikeReasonMarker"/> — the narrow machine format the
/// review prompt itself prescribes, not a free-form summary.
/// </summary>
internal static class SpikeReviewResultParser
{
    public static bool TryParse(string summary, out SpikeVerdict verdict, out string reason)
    {
        verdict = SpikeVerdict.Unknown;
        reason = string.Empty;

        string[] lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (!line.StartsWith(AgentPromptBuilder.SpikeVerdictMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string word = line[AgentPromptBuilder.SpikeVerdictMarker.Length..].Trim().ToLowerInvariant();
            SpikeVerdict? parsed = word switch
            {
                "met" => SpikeVerdict.Met,
                "not-met" or "notmet" or "not_met" => SpikeVerdict.NotMet,
                _ => null,
            };
            if (parsed is null)
            {
                continue;
            }

            verdict = parsed;
            string foundReason = string.Empty;
            for (int reasonIndex = index + 1; reasonIndex < lines.Length; reasonIndex++)
            {
                string reasonLine = lines[reasonIndex].Trim();
                if (reasonLine.StartsWith(AgentPromptBuilder.SpikeReasonMarker, StringComparison.OrdinalIgnoreCase))
                {
                    foundReason = reasonLine[AgentPromptBuilder.SpikeReasonMarker.Length..].Trim();
                    break;
                }

                if (reasonLine.Length > 0)
                {
                    break;
                }
            }

            reason = foundReason.IsNotBlank() ? foundReason : "no reason stated";
        }

        return verdict != SpikeVerdict.Unknown;
    }
}
