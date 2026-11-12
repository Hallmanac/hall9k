using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Worktrees;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Idea.Rendering;
using Hall9k.Domain.Features.Plan.Projections;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Challenge;

/// <summary>
/// One sweep's tally: how many challenge sessions this node dispatched, how many of them recorded a verdict, how many
/// ended with a failure, how many sessions a restart left behind that were recorded as failed, and how many dispatches
/// of a node that never came back were ended on the ceiling.
/// </summary>
public sealed record DraftChallengeSweepResult(int Dispatched, int Verdicts, int Failed, int Recovered, int Expired);

/// <summary>
/// Turns a challenge request into one headless session and records what came of it (<c>h9k task challenge</c>).
/// <para>
/// The session tests a draft's claims against the code and writes a report whose closing block holds a verdict; the
/// daemon reads that block (<see cref="ChallengeReport"/>) and records the verdict and the counts of its changes as one
/// event. It applies nothing: a challenge changes nothing about the draft, and the session itself is refused every
/// command that would (<c>DispatchedSessionCommandClassification</c>). Nothing the session wrote verbatim reaches the
/// stream, because the ledger is public and a verdict carries security detail; the prose stays in the report file.
/// </para>
/// <para>
/// The shape is <see cref="Publication.CardPublicationEngine"/>'s, copied rather than shared because the failure modes
/// differ: the request is node-scoped, so a request is this node's by construction and there is no owner filter to
/// apply; the dispatch is saved under a version fence before the spawn, so a crash cannot leave a live session with
/// nothing on the stream and a second sweep cannot start a second one; and recovery is
/// <see cref="Closeout.CloseoutTriageSessions"/>'s rather than the publication engine's adoption, because a
/// challenge's cost is the session itself: a dispatch with nothing running it is recorded as failed and never
/// dispatched again, and the draft can be challenged again by repeating the verb. A dispatch another node made and
/// never recorded is ended once it has stood past <see cref="DaemonOptions.ForeignPublicationCeiling"/>, the only
/// way out of a stranding no node can judge.
/// </para>
/// <para>
/// Challenges run one at a time on a node, on purpose: each holds a skip-permissions session over the same reading
/// checkout, and a serial sweep also keeps one dispatch's checkout refresh from moving the tree under another's
/// session. A challenge holds no run slot (<see cref="DaemonOptions.ChallengeTimeout"/> bounds it instead), so a node
/// carries one such session above its run ceiling, as <c>h9k task start</c> does. The serial sweep does not reach a plan's
/// refinement session, which reads the same checkout on a loop of its own, so a session joins
/// <see cref="ReadingCheckoutLeases"/> before it refreshes and holds the lease until it ends: a checkout someone else is
/// reading is left where it stands, and the prompt says so.
/// </para>
/// <para>
/// A request a plan made is the plan's to hold: while the plan is parked it waits, and when the plan has ended it is
/// withdrawn, because a parked plan starts no new session for any of its drafts and a cancelled one starts none at all.
/// A request a person made on a draft the plan holds is not the plan's and is answered as usual.
/// </para>
/// </summary>
public sealed class DraftChallengeEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    IProcessManager processManager,
    IWorktreeManager worktrees,
    ProcessRunner processRunner,
    ReadingCheckoutLeases checkoutLeases,
    IOptions<DaemonOptions> options,
    ILogger<DraftChallengeEngine> logger,
    TimeProvider? clock = null)
{
    /// <summary>How long the shutdown path gets to record an outcome: it runs while the daemon is stopping.</summary>
    private static readonly TimeSpan ShutdownRecordTimeout = TimeSpan.FromSeconds(5);

    private readonly DaemonOptions _options = options.Value;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly HashSet<Guid> _running = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ForeignDispatchSettle _foreignSettle = new(options.Value.ForeignSessionSettlePeriod, clock ?? TimeProvider.System);

    /// <summary>What one request came to, for the sweep's tally.</summary>
    private enum Attempt
    {
        /// <summary>Not this sweep's after all: the request was already answered or the task moved under it.</summary>
        NotThisSweeps,

        /// <summary>The request ended without a session: a guard refused it where the session would have started.</summary>
        Refused,

        /// <summary>A session was spawned and recorded a verdict.</summary>
        Verdict,

        /// <summary>A session was spawned and recorded a failure.</summary>
        SessionFailed,
    }

    /// <summary>One sweep: settle what a restart left behind, then answer this node's outstanding requests, one at a time.</summary>
    public async Task<DraftChallengeSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            (int recovered, int expired) = await RecoverAsync(cancellationToken);

            IReadOnlyList<TaskDetails> pending;
            await using (IQuerySession query = store.QuerySession())
            {
                pending = await query.Query<TaskDetails>()
                    .Where(task => task.ChallengeRequested)
                    .OrderBy(task => task.ChallengeRequestedAt)
                    .ToListAsync(cancellationToken);
            }

            int dispatched = 0;
            int verdicts = 0;
            int failed = 0;
            foreach (TaskDetails task in pending)
            {
                try
                {
                    switch (await DispatchAsync(task.Id, task.ChallengeRequestedAt, cancellationToken))
                    {
                        case Attempt.Verdict:
                            dispatched++;
                            verdicts++;
                            break;
                        case Attempt.SessionFailed:
                            dispatched++;
                            failed++;
                            break;
                        case Attempt.Refused:
                            failed++;
                            break;
                        case Attempt.NotThisSweeps:
                        default:
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // What reaches here failed before a session existed, or after one was recorded as ended:
                    // DispatchAsync owns everything between the spawn and the outcome. Recorded as a start
                    // failure so the request does not wait forever and the draft can be challenged again.
                    logger.LogError(exception, "Challenge failed for task {TaskId}; recording it and moving on", task.Id);
                    await RecordFailureQuietlyAsync(task.Id, null, ChallengeFailureReason.StartFailed, null, cancellationToken);
                    failed++;
                }
            }

            return new DraftChallengeSweepResult(dispatched, verdicts, failed, recovered, expired);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Dispatches on this node's own record that nothing is running, recorded as failed and never dispatched again, and
    /// ends another node's dispatch that has stood past the foreign-session ceiling. The sweep is serial, so a dispatch of
    /// this node's own seen at the top of a sweep is one no live session of this process accounts for.
    /// </summary>
    private async Task<(int Recovered, int Expired)> RecoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDetails> running;
        await using (IQuerySession query = store.QuerySession())
        {
            running = await query.Query<TaskDetails>()
                .Where(task => task.RunningChallengeSessionId != null)
                .ToListAsync(cancellationToken);
        }

        _foreignSettle.KeepOnly(running.Select(task => task.RunningChallengeSessionId).OfType<Guid>().ToHashSet());
        int recovered = 0;
        int expired = 0;
        foreach (TaskDetails task in running)
        {
            if (task.RunningChallengeSessionId is not { } sessionId)
            {
                continue;
            }

            if (task.RunningChallengeNodeId == node.NodeId)
            {
                if (_running.Contains(sessionId))
                {
                    continue;
                }

                logger.LogWarning(
                    "Task {TaskId}: challenge session {SessionId} was dispatched by this node and nothing is watching it; stopping it and recording it as failed",
                    task.Id, sessionId);
                await TerminateLeftBehindAsync(task.Id, sessionId, cancellationToken);
                await RecordAsync(
                    task.Id, sessionId, node.NodeId, null, ChallengeFailureReason.DaemonRestarted,
                    task.RunningChallengeModel, task.RunningChallengeEffort, null, cancellationToken);
                recovered++;
            }
            else if (task.RunningChallengeDispatchedAt is { } dispatchedAt
                && _clock.GetUtcNow() - dispatchedAt > _options.ForeignPublicationCeiling
                && _foreignSettle.HasSettled(sessionId))
            {
                logger.LogWarning(
                    "Task {TaskId}: challenge session {SessionId} dispatched by node {Node} at {DispatchedAt:u} has stood longer than {Ceiling} with no outcome; ending it here",
                    task.Id, sessionId, task.RunningChallengeNodeId, dispatchedAt, _options.ForeignPublicationCeiling);
                await RecordAsync(
                    task.Id, sessionId, task.RunningChallengeNodeId ?? node.NodeId, null,
                    ChallengeFailureReason.DispatchExpired, task.RunningChallengeModel, task.RunningChallengeEffort,
                    null, cancellationToken);
                _foreignSettle.Forget(sessionId);
                expired++;
            }
        }

        return (recovered, expired);
    }

    private async Task<Attempt> DispatchAsync(Guid taskId, DateTimeOffset? requestedAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Fence before reading, the DispatchEngine order: the dispatch append below carries this version, so a
        // second sweep or a revise landing in between fails the commit rather than being absorbed, which is
        // what makes "exactly one session per request" true rather than likely.
        StreamState? fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken);
        TaskAggregate? aggregate = fence is null
            ? null
            : await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken);
        if (fence is null || aggregate is null || !aggregate.ChallengeRequested)
        {
            return Attempt.NotThisSweeps;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(aggregate.ProjectId, cancellationToken);
        AgentModel model = _options.ResolveChallengeModel(project?.Model);
        AgentEffort effort = _options.ResolveChallengeEffort(project?.Effort);

        // The rules the verb enforced, enforced again where the session would start: the request outlives the
        // moment it was made, and a draft can be published, a project archived, or a checkout removed in between.
        if (aggregate.State != TaskState.Draft)
        {
            return await RefuseAsync(
                session, fence, taskId, ChallengeFailureReason.NoLongerDraft, model, effort,
                $"task is {aggregate.State.Value}, not Draft", cancellationToken);
        }

        if (project is null || project.IsArchived)
        {
            return await RefuseAsync(
                session, fence, taskId, ChallengeFailureReason.StartFailed, model, effort,
                project is null
                    ? "its project is not registered on this node"
                    : "its project is archived",
                cancellationToken);
        }

        string checkout = ProjectCheckout.ForReading(project);
        if (!Directory.Exists(checkout) || ProjectCheckout.IsBare(checkout))
        {
            return await RefuseAsync(
                session, fence, taskId, ChallengeFailureReason.StartFailed, model, effort,
                $"there is no working checkout of the project at {checkout}", cancellationToken);
        }

        // What the plan that requested this challenge says about starting it: a parked plan holds the request, and an ended
        // one refuses it. Asked twice: before the join, so a request that is held for days does not move the reading
        // checkout on every sweep for a session that cannot start, and again after it, which can wait on another session's
        // refresh, because the plan can park or be cancelled in the meantime and must not get a session it was promised it
        // would not.
        async Task<Attempt?> PlanDecidesAsync()
        {
            PlanDetails? requestingPlan = requestedAt is { } asked
                ? (await session.Query<PlanDetails>().ToListAsync(cancellationToken))
                    .FirstOrDefault(plan => plan.Draft(taskId)?.ChallengeRequestedAt == asked)
                : null;
            switch (requestingPlan)
            {
                case { IsLive: true, IsParked: true }:
                    logger.LogDebug("Task {TaskId}: its plan {PlanId} is parked, so the challenge it requested waits", taskId, requestingPlan.Id);
                    return Attempt.NotThisSweeps;
                case { IsLive: false }:
                    return await RefuseAsync(
                        session, fence, taskId, ChallengeFailureReason.PlanEnded, model, effort, $"plan {requestingPlan.Id} has ended",
                        cancellationToken);
                default:
                    return null;
            }
        }

        if (await PlanDecidesAsync() is { } beforeJoin)
        {
            return beforeJoin;
        }

        // Only the home's own dev/ is moved, the publication engine's rule: a project registered before homes
        // reads from somebody's ordinary clone, and that one is theirs to move. The outcome is carried into the
        // prompt, because a session testing a stale tree must be told so, and a refresh that failed is otherwise
        // only a log line. The lease is held to the end of this dispatch, session included, so that no other
        // session (a plan's refinement, say) moves the tree while this one tests against its commit.
        ReadingCheckoutJoin join = await checkoutLeases.JoinAsync(
            checkout,
            ProjectCheckout.IsHomeDevWorktree(project, checkout)
                ? () => worktrees.RefreshReadingCheckoutAsync(checkout, project.BaseBranch, cancellationToken)
                : null,
            cancellationToken);
        using IDisposable lease = join.Lease;

        if (await PlanDecidesAsync() is { } afterJoin)
        {
            return afterJoin;
        }

        CheckoutRefresh? refresh = join.Refresh;
        if (refresh is not null)
        {
            logger.Log(
                refresh.UpToDate
                    ? LogLevel.Information
                    : LogLevel.Warning,
                "Challenge session for task {TaskId} reads {Checkout}, which {Detail}", taskId, checkout, refresh.Detail);
        }

        string? commit = await HeadCommitAsync(checkout, cancellationToken);

        // The draft as the store has it now: the sweep's snapshot named the request, and a revise may have landed
        // since, so the prompt is built from a fresh read.
        TaskDetails current = await session.LoadAsync<TaskDetails>(taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Task {taskId} has a stream and no document.");

        Guid sessionId = DomainId.New();
        string sessionDirectory = RunPaths.GlobalDirectory(sessionId);
        string reportPath = RunPaths.ChallengeReportFile(sessionId);
        (string? ideaWorkspace, string? taskDirectory) = await LocateDraftFilesAsync(session, project, current, cancellationToken);
        string? journal = taskDirectory is null
            ? null
            : FindJournal(taskDirectory);
        bool skipPermissions = project.SkipPermissions;
        string prompt = AgentPromptBuilder.BuildChallenge(
            current, project,
            new ChallengePromptFacts(
                project.HomeDirectory.HasValue
                    ? project.HomeDirectory.Value
                    : null,
                checkout, commit, refresh, reportPath, sessionDirectory, ideaWorkspace, journal,
                // A probe is a skip-permissions shell's affordance; the prompt says so only when it is true.
                skipPermissions
                    ? _options.ResolveCourierModel(project.Model).Value
                    : null));

        // Recorded before anything is spawned, RunLauncher's order: committing first is what makes a crash lose too.
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, new DraftChallengeDispatched(
            taskId, sessionId, node.NodeId, DateTimeOffset.UtcNow, model, effort));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} advanced while its challenge was being dispatched; leaving it", taskId);
            return Attempt.NotThisSweeps;
        }

        _running.Add(sessionId);
        try
        {
            return await RunSessionAsync(
                taskId, sessionId, checkout, sessionDirectory, reportPath, prompt, model, effort, skipPermissions,
                [.. new[] { ideaWorkspace, taskDirectory }.OfType<string>()], cancellationToken);
        }
        finally
        {
            _running.Remove(sessionId);
        }
    }

    private async Task<Attempt> RunSessionAsync(
        Guid taskId,
        Guid sessionId,
        string checkout,
        string sessionDirectory,
        string reportPath,
        string prompt,
        AgentModel model,
        AgentEffort effort,
        bool skipPermissions,
        IReadOnlyList<string> extraReadRoots,
        CancellationToken cancellationToken)
    {
        SpawnedAgent agent;
        try
        {
            // The session's directory is the platform-global one: a challenge belongs to no task directory, having
            // no RunDispatched of its own, and the id names it so the report and transcript stay findable.
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    sessionId, sessionId, checkout, sessionDirectory, prompt, ExecutorMode.Subscription, model, effort,
                    skipPermissions,
                    // Without skip-permissions the session gets the review permission file with its one write: the
                    // report. With it, the same shell a build session has, which is what a probe needs.
                    UsesReviewPermissions: !skipPermissions,
                    AssessmentFile: skipPermissions
                        ? null
                        : reportPath)
                {
                    TaskId = taskId,
                    SessionName = SessionRoleName.For(DomainId.Short(taskId), SessionRoleName.Challenge),
                    ExtraReadRoots = skipPermissions
                        ? []
                        : extraReadRoots,
                    // The report is the only write the session may make.
                    AllowMentionAnswer = false,
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Task {TaskId}: the challenge session could not be spawned", taskId);
            await RecordAsync(
                taskId, sessionId, node.NodeId, null, ChallengeFailureReason.StartFailed, model, effort, null,
                cancellationToken);
            return Attempt.SessionFailed;
        }

        await RecordProcessIdentityAsync(taskId, sessionId, agent);

        logger.LogInformation(
            "Task {TaskId}: challenge session {SessionId} dispatched (pid {ProcessId}, model {Model}, artifacts in {Directory})",
            taskId, sessionId, agent.ProcessId, model.Value, sessionDirectory);

        AgentResult? result = null;
        bool timedOut = false;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.ChallengeTimeout);
        try
        {
            result = (await SessionResultWaiter.WaitAsync(
                RunPaths.StreamFile(sessionDirectory), agent.ProcessId, agent.StartedAt, processManager,
                onOutput: null, budget.Token)).Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(taskId, agent);
            timedOut = true;
            logger.LogWarning(
                "Task {TaskId}: the challenge session exceeded {Timeout} and was terminated", taskId, _options.ChallengeTimeout);
        }
        catch (OperationCanceledException)
        {
            // The daemon is stopping: the session is detached and would outlive it, so it is stopped and its
            // outcome recorded with a token of its own. If that does not land, the next start records it.
            Terminate(taskId, agent);
            try
            {
                using CancellationTokenSource shutdown = new(ShutdownRecordTimeout);
                await RecordAsync(
                    taskId, sessionId, node.NodeId, null, ChallengeFailureReason.DaemonRestarted, model, effort, null,
                    shutdown.Token);
            }
            catch (Exception recording)
            {
                logger.LogWarning(
                    recording, "Task {TaskId}: could not record the challenge outcome during shutdown; the next start does", taskId);
            }

            throw;
        }
        catch (Exception exception)
        {
            // Nothing can read this session's end any more, and a detached session that nothing watches would
            // keep spending: it is stopped and recorded as an error rather than left for the next start.
            logger.LogError(
                exception, "Task {TaskId}: the challenge session (pid {ProcessId}) could not be seen through to an outcome",
                taskId, agent.ProcessId);
            Terminate(taskId, agent);
            await RecordAsync(
                taskId, sessionId, node.NodeId, null, ChallengeFailureReason.SessionError, model, effort, null,
                cancellationToken);
            return Attempt.SessionFailed;
        }

        ChallengeReading? reading = null;
        ChallengeFailureReason? failure = null;
        if (timedOut)
        {
            failure = ChallengeFailureReason.TimedOut;
        }
        else if (result is null)
        {
            failure = ChallengeFailureReason.NoResult;
        }
        else if (result.IsError)
        {
            failure = ChallengeFailureReason.SessionError;
        }
        else
        {
            try
            {
                (reading, failure) = await ChallengeReport.ReadAsync(reportPath, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Task {TaskId}: the challenge report could not be read", taskId);
                failure = ChallengeFailureReason.NoReport;
            }
        }

        await RecordAsync(taskId, sessionId, node.NodeId, reading, failure, model, effort, result, cancellationToken);
        return reading is null
            ? Attempt.SessionFailed
            : Attempt.Verdict;
    }

    /// <summary>A request that ended before any session started: recorded as a failure with no session, the draft left alone.</summary>
    private async Task<Attempt> RefuseAsync(
        IDocumentSession session, StreamState fence, Guid taskId, ChallengeFailureReason reason, AgentModel model,
        AgentEffort effort, string why, CancellationToken cancellationToken)
    {
        logger.LogWarning("Task {TaskId}: no challenge session was started: {Why}", taskId, why);
        session.Events.Append(taskId, expectedVersion: fence.Version + 1, Failure(taskId, null, node.NodeId, reason, model, effort));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            return Attempt.NotThisSweeps;
        }

        return Attempt.Refused;
    }

    private static DraftChallengeRecorded Failure(
        Guid taskId, Guid? sessionId, Guid nodeId, ChallengeFailureReason reason, AgentModel? model, AgentEffort? effort) =>
        new(
            taskId, sessionId, nodeId, ChallengeVerdict.Unknown, reason, 0, 0, 0, model ?? AgentModel.Unknown,
            effort ?? AgentEffort.Unknown, DateTimeOffset.UtcNow);

    /// <summary>
    /// Records how a challenge ended, with the session's spend in the same transaction when it reported any. Spend
    /// rides <see cref="PublicationTokensRecorded"/>, which the period spend, the budget pressure, and the project purge
    /// already fold, under the model the session ran on.
    /// </summary>
    private async Task RecordAsync(
        Guid taskId, Guid? sessionId, Guid nodeId, ChallengeReading? reading, ChallengeFailureReason? failure,
        AgentModel? model, AgentEffort? effort, AgentResult? result, CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await using IDocumentSession session = store.LightweightSession();
            if (result is not null)
            {
                session.Events.Append(
                    taskId, result.ToPublicationTokensRecorded(taskId, now, model ?? AgentModel.Unknown));
            }

            session.Events.Append(
                taskId,
                reading is { } read
                    ? new DraftChallengeRecorded(
                        taskId, sessionId, nodeId, read.Verdict, ChallengeFailureReason.Unknown, read.MustCount,
                        read.ShouldCount, read.ParkCount, model ?? AgentModel.Unknown, effort ?? AgentEffort.Unknown, now)
                    : Failure(taskId, sessionId, nodeId, failure ?? ChallengeFailureReason.StartFailed, model, effort));
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The dispatch stays on the stream with no outcome, which the next start records as failed: a stuck
            // marker is visible, and nothing re-dispatches it.
            logger.LogError(exception, "Could not record the challenge outcome for task {TaskId}", taskId);
        }
    }

    private async Task RecordFailureQuietlyAsync(
        Guid taskId, Guid? sessionId, ChallengeFailureReason reason, AgentModel? model, CancellationToken cancellationToken) =>
        await RecordAsync(taskId, sessionId, node.NodeId, null, reason, model, null, null, cancellationToken);

    /// <summary>The commit the checkout is at, read by the daemon rather than asked of the session; null when git cannot answer.</summary>
    private async Task<string?> HeadCommitAsync(string checkout, CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await processRunner("git", ["rev-parse", "HEAD"], checkout, cancellationToken);
            return result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } sha
                ? sha
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read HEAD in {Checkout} for a challenge session", checkout);
            return null;
        }
    }

    /// <summary>
    /// The idea's workspace and the draft's own directory, when each exists on this node. A path a project's home does
    /// not hold, or an idea workspace replicated from another operating system's layout, is left out rather than guessed.
    /// </summary>
    internal static async Task<(string? IdeaWorkspace, string? TaskDirectory)> LocateDraftFilesAsync(
        IDocumentSession session, ProjectDetails project, TaskDetails task, CancellationToken cancellationToken)
    {
        string? taskDirectory = project.HomeDirectory.HasValue
            ? HomeEntryWriter.FindExistingDirectory(
                ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), task.Id,
                alternateRoots: [ProjectHomePaths.ArchivedTasksDirectory(project.HomeDirectory.Value)])
            : null;

        string? ideaWorkspace = null;
        if (task.SourceIdeaId is { } ideaId
            && await session.LoadAsync<IdeaDetails>(ideaId, cancellationToken) is { } idea
            && (!idea.WorkspaceHome.HasValue || idea.WorkspaceHome.IsNativeForm))
        {
            string workspace = IdeaPaths.WorkspaceDirectory(
                IdeaPaths.ResolveDirectory(idea.WorkspaceHome, IdeaDocumentRenderer.DirectoryName(idea), idea.Id));
            ideaWorkspace = Directory.Exists(workspace)
                ? workspace
                : null;
        }

        return (ideaWorkspace, taskDirectory);
    }

    /// <summary>The draft's <c>journal.md</c>, in its directory or its workspace, when there is one.</summary>
    internal static string? FindJournal(string taskDirectory) =>
        new[]
        {
            Path.Combine(taskDirectory, "journal.md"),
            Path.Combine(ProjectHomePaths.TaskWorkspaceDirectory(taskDirectory), "journal.md"),
        }.FirstOrDefault(File.Exists);

    /// <summary>
    /// Writes the session's pid and start time beside its report. The session is detached by design, so a daemon that
    /// dies while it runs leaves it running; this file is what lets the next start stop it. A write that fails is
    /// logged and the session carries on: the 30-minute bound still holds for every case but a hard daemon crash.
    /// <para>
    /// The write takes no cancellation token on purpose: the session is already running, so a stop signal landing
    /// here must not throw before the guarded wait that terminates the session. The file is a few bytes, so there is
    /// nothing worth cancelling.
    /// </para>
    /// </summary>
    private async Task RecordProcessIdentityAsync(Guid taskId, Guid sessionId, SpawnedAgent agent)
    {
        try
        {
            string file = RunPaths.ChallengeProcessFile(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? throw new InvalidOperationException($"No directory for {file}."));
            await File.WriteAllTextAsync(
                file, $"{agent.ProcessId}\n{agent.StartedAt.UtcTicks}\n", CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception, "Task {TaskId}: could not write the challenge session's process identity; a daemon crash would leave it running",
                taskId);
        }
    }

    /// <summary>
    /// Stops the session a previous daemon process left running, found by the identity file written at spawn. The pid
    /// and start time together are the identity, so a pid the operating system has since reused is not touched. A
    /// session with no file (the crash fell between the dispatch and the spawn, or the write failed) has nothing to stop.
    /// </summary>
    private async Task TerminateLeftBehindAsync(Guid taskId, Guid sessionId, CancellationToken cancellationToken)
    {
        string file = RunPaths.ChallengeProcessFile(sessionId);
        try
        {
            if (!File.Exists(file))
            {
                return;
            }

            string[] lines = await File.ReadAllLinesAsync(file, cancellationToken);
            if (lines.Length >= 2 && int.TryParse(lines[0], out int processId) && long.TryParse(lines[1], out long ticks))
            {
                DateTimeOffset startedAt = new(ticks, TimeSpan.Zero);
                logger.LogInformation(
                    "Task {TaskId}: stopping challenge session {SessionId} (pid {ProcessId}) if a previous daemon process left it running",
                    taskId, sessionId, processId);
                processManager.Terminate(processId, startedAt);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Task {TaskId}: could not stop challenge session {SessionId}; recording it as failed anyway", taskId, sessionId);
        }
    }

    private void Terminate(Guid taskId, SpawnedAgent agent)
    {
        try
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Task {TaskId}: could not terminate the challenge session (pid {ProcessId})", taskId, agent.ProcessId);
        }
    }
}
