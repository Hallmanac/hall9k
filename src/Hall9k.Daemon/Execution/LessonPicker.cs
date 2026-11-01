using Hall9k.Daemon.ProcessManagement;
using Hall9k.Domain.Features.Learning;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Picks, once per task and before its first build prompt is written, which of the project's lessons
/// the task's prompts carry (<see cref="LessonPickerMode"/>). One small session reads the task and
/// every lesson the composing node may put in a prompt and answers with lesson ids; the daemon reads
/// the answer as ids alone and records the outcome as a <see cref="LessonPickRecorded"/> on the task's
/// stream.
/// <para>
/// It never decides whether the build goes ahead. Whatever goes wrong, a session that would not
/// start, ended in error, said nothing, ran past <see cref="DaemonOptions.LessonPickerTimeout"/>, or
/// gave an answer that was not ids, the caller dispatches on newest-first and the record says why.
/// The wait is on the critical path, since <c>RunLauncher.LaunchAsync</c> is awaited inside the
/// dispatch loop, which is why the bound is seconds and not minutes, and why a session past it is
/// terminated rather than left running. The session has no tools and one turn: the task's text can
/// come from outside the project, and a session that can only answer can do nothing with whatever
/// that text says.
/// </para>
/// <para>
/// The model and effort are the courier's for this project and node
/// (<see cref="DaemonOptions.ResolveCourierModel"/>, <see cref="AgentRole.Courier"/>), so the
/// cheap-tier choice stays in one place, and a task's own override never moves them.
/// </para>
/// </summary>
public sealed class LessonPicker(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<LessonPicker> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// Whether a pick should run now: the setting is shadow or on, the task has no pick on record,
    /// the task is neither a spike nor a pull-request review, and the caps would hold back at least
    /// one eligible lesson (when they hold back none there is nothing for a pick to change).
    /// </summary>
    public static bool ShouldPick(TaskDetails task, LessonCandidates candidates) =>
        candidates.PickerMode.Picks
        && task.LessonPick is null
        && task.Type != TaskType.Spike
        && task.Type != TaskType.PrReview
        && candidates.NewestFirst.HeldForCap > 0;

    /// <summary>
    /// Runs the pick when <see cref="ShouldPick"/> says to and returns the record it made, or null
    /// when it ran nothing. The caller composes with <see cref="LessonCandidates.For"/> on the result.
    /// </summary>
    public async ValueTask<LessonPickRecorded?> PickAsync(
        Guid runId,
        string runDirectory,
        TaskDetails task,
        ProjectDetails project,
        LessonCandidates candidates,
        ExecutorMode mode,
        CancellationToken cancellationToken)
    {
        if (!ShouldPick(task, candidates))
        {
            return null;
        }

        AgentModel model = _options.ResolveCourierModel(project.Model);
        AgentEffort effort = _options.ResolveEffort(AgentRole.Courier, taskEffort: null, project.Effort);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Attempt attempt = await RunSessionAsync(
            runId, runDirectory, task, project, candidates, mode, model, effort, cancellationToken);

        logger.LogInformation(
            "Task {TaskId}: lesson pick {Outcome}: {Picked} of {Eligible} eligible lessons picked, {Dropped} id(s) dropped (model {Model})",
            task.Id, attempt.Outcome.Value, attempt.PickedIds.Count, candidates.Eligible.Count, attempt.DroppedIdCount,
            model.Value);
        return await RecordAsync(
            runId, task, candidates, attempt, model, startedAt, cancellationToken);
    }

    /// <summary>What one picker session came to, before it is written down.</summary>
    private sealed record Attempt(
        LessonPickOutcome Outcome, IReadOnlyList<Guid> PickedIds, int DroppedIdCount, AgentResult? Result);

    /// <summary>
    /// Starts the session, waits for it under the bound and reads its answer. Every failure is an
    /// outcome here rather than an exception, because the pick may never be the reason a dispatch
    /// fails; only the daemon's own cancellation escapes, after the session is terminated.
    /// </summary>
    private async Task<Attempt> RunSessionAsync(
        Guid runId,
        string runDirectory,
        TaskDetails task,
        ProjectDetails project,
        LessonCandidates candidates,
        ExecutorMode mode,
        AgentModel model,
        AgentEffort effort,
        CancellationToken cancellationToken)
    {
        Guid sessionId = DomainId.New();
        string artifactName = $"lesson-picker-{sessionId.ToString("N")[..8]}";
        SpawnedAgent? unfinished = null;
        AgentResult? result = null;
        try
        {
            SpawnedAgent agent;
            try
            {
                agent = await executor.SpawnAsync(new AgentSpawnRequest(
                    runId, sessionId, runDirectory, runDirectory,
                    LessonPickerPromptBuilder.Compose(task, candidates),
                    mode, model, effort,
                    // Nothing for a session with no tools to be permitted.
                    SkipPermissions: false,
                    SessionArtifactName: artifactName,
                    UntrustedWorkingDirectory: true,
                    MaxTurns: 1,
                    NoTools: true)
                {
                    TaskId = task.Id,
                    SessionName = SessionRoleName.For(DomainId.Short(task.Id), SessionRoleName.LessonPicker),
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Task {TaskId}: the lesson picker session could not be started, so building on newest-first lessons",
                    task.Id);
                return new Attempt(LessonPickOutcome.StartFailed, [], 0, null);
            }

            unfinished = agent;
            (result, bool timedOut) = await WaitWithinBudgetAsync(runId, runDirectory, artifactName, agent, cancellationToken);

            // The wait either read the session's own result or terminated it on expiry, so from here
            // on there is nothing of ours still running to abandon.
            unfinished = null;

            if (timedOut)
            {
                return new Attempt(LessonPickOutcome.TimedOut, [], 0, result);
            }

            if (result is null || (!result.IsError && result.Summary.IsBlank()))
            {
                return new Attempt(LessonPickOutcome.NoResult, [], 0, result);
            }

            if (result.IsError)
            {
                return new Attempt(LessonPickOutcome.SessionError, [], 0, result);
            }

            LessonPickAnswerReading reading = LessonPickAnswer.Read(result.Summary, candidates.Eligible);
            if (!reading.Usable)
            {
                return new Attempt(LessonPickOutcome.UnusableAnswer, [], 0, result);
            }

            InjectedLessons? selected = LessonInjection.SelectPicked(candidates, reading.LessonIds);
            return selected is null
                ? new Attempt(LessonPickOutcome.EmptyPick, [], reading.DroppedIdCount, result)
                : new Attempt(
                    LessonPickOutcome.Picked, [.. selected.Lessons.Select(lesson => lesson.Id)],
                    reading.DroppedIdCount, result);
        }
        catch (Exception exception)
        {
            // Any exit that is not the wait's own leaves a session nobody is monitoring: the daemon
            // shutting down mid-wait, or a throw between the spawn and the wait. A picker this
            // dispatch has stopped caring about must not outlive it, because nothing adopts it.
            Terminate(runId, unfinished);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            logger.LogWarning(exception,
                "Task {TaskId}: the lesson picker failed, so building on newest-first lessons", task.Id);
            return new Attempt(LessonPickOutcome.SessionError, [], 0, result);
        }
    }

    /// <summary>
    /// Records the session's tokens on the run (under the model it ran on, so the spend ledger
    /// sees it) and then the pick on the task's stream, in its own session. A task keeps at most one
    /// pick: when one is already there, whichever writer lost the race changes nothing and the
    /// record that stands is returned.
    /// </summary>
    private async Task<LessonPickRecorded> RecordAsync(
        Guid runId,
        TaskDetails task,
        LessonCandidates candidates,
        Attempt attempt,
        AgentModel model,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (attempt.Result is { } result)
        {
            await using IDocumentSession tokens = store.LightweightSession();
            tokens.Events.Append(runId, result.ToTokensRecorded(runId, now, model));
            await tokens.SaveChangesAsync(cancellationToken);
        }

        LessonPickRecorded recorded = new(
            task.Id, candidates.PickerMode, attempt.Outcome, attempt.PickedIds,
            [.. candidates.NewestFirst.Lessons.Select(lesson => lesson.Id)],
            candidates.Eligible.Count, attempt.DroppedIdCount, model, now - startedAt, now);
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? current = await session.LoadAsync<TaskDetails>(task.Id, cancellationToken);
        if (current?.LessonPick is { } existing)
        {
            return existing;
        }

        session.Events.Append(task.Id, recorded);
        await session.SaveChangesAsync(cancellationToken);
        return recorded;
    }

    /// <summary>
    /// The waiter under <see cref="DaemonOptions.LessonPickerTimeout"/>. On expiry the stream is
    /// re-read once before the session is terminated, so a result the still-alive session had
    /// already written is not lost to the wait's own cadence: its tokens are real spend. The
    /// outcome is still timed out, since the session ran past its bound. Cancellation of the daemon
    /// itself is not a timeout and propagates, after the caller terminates the session.
    /// </summary>
    private async Task<(AgentResult? Result, bool TimedOut)> WaitWithinBudgetAsync(
        Guid runId, string runDirectory, string artifactName, SpawnedAgent agent, CancellationToken cancellationToken)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.LessonPickerTimeout);
        try
        {
            SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
                RunPaths.SessionStreamFile(runDirectory, artifactName), agent.ProcessId, agent.StartedAt,
                processManager, onOutput: null, budget.Token);
            if (wait.Lingering.Count > 0)
            {
                logger.LogWarning(
                    "Run {RunId}: the lesson picker session left {Count} process(es) running after its result, and it was terminated pid(s) {Pids}",
                    runId, wait.Lingering.Count, string.Join(", ", wait.Lingering));
            }

            return (wait.Result, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AgentResult? result = await TryReadAlreadyPresentResultAsync(runDirectory, artifactName, cancellationToken);
            logger.LogWarning(
                "Run {RunId}: the lesson picker exceeded {Timeout}, so it was terminated and the build uses newest-first lessons",
                runId, _options.LessonPickerTimeout);
            Terminate(runId, agent);
            return (result, true);
        }
    }

    private static async Task<AgentResult?> TryReadAlreadyPresentResultAsync(
        string runDirectory, string artifactName, CancellationToken cancellationToken)
    {
        string streamFile = RunPaths.SessionStreamFile(runDirectory, artifactName);
        if (!File.Exists(streamFile))
        {
            return null;
        }

        try
        {
            return await StreamTailReader.ReadFinalResultAsync(streamFile, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Kills an abandoned picker session, never at the cost of the exception on its way out.</summary>
    private void Terminate(Guid runId, SpawnedAgent? agent)
    {
        if (agent is not { } session)
        {
            return;
        }

        try
        {
            processManager.Terminate(session.ProcessId, session.StartedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Run {RunId}: could not terminate the abandoned lesson picker session (pid {ProcessId})",
                runId, session.ProcessId);
        }
    }
}
