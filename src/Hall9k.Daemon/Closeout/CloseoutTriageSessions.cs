using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
using Hall9k.Daemon.Review;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>What closeout does about one thread set's triage this poll.</summary>
internal enum CloseoutTriageAction
{
    /// <summary>The set is already triaged, or its triage is running: nothing to do.</summary>
    None,

    /// <summary>Dispatch the set's one triage.</summary>
    Dispatch,

    /// <summary>The task is past its triage allowance: record that, and run nothing.</summary>
    PastCap,

    /// <summary>The triage was dispatched and nothing is running it any more: record that it failed.</summary>
    Abandoned,
}

/// <summary>What one poll decided about a thread set's triage, and where that leaves it.</summary>
/// <param name="FenceVersion">The task-stream version later appends must be fenced against.</param>
/// <param name="Triage">The set's triage as it stands after this poll's appends, or null when none has been considered.</param>
internal sealed record CloseoutTriageConsideration(long FenceVersion, CloseoutTriage? Triage);

/// <summary>
/// The read-only triage closeout runs in place of a review-feedback lap while a task's interactive flag
/// is on (task: analysis-only closeout). The read-only evaluator pattern of
/// <see cref="FeedbackEvaluators"/>, copied rather than shared because the unit differs: that evaluator
/// assesses one piece of a stranger's feedback, and this one triages a whole set of threads.
/// <para>
/// Each distinct set of outstanding threads earns at most one session, ever. The dispatch is a task event
/// saved, with the report's path, before the session is spawned, so another poll observing the same set,
/// or a daemon restarting mid-triage, finds it on the stream and starts nothing; a set whose dispatch is
/// on the stream with nothing running it is recorded as failed, never re-dispatched. A task's triages are
/// also capped for its lifetime (<see cref="DaemonOptions.CloseoutTriageCap"/>), decided over the stream
/// so a restart cannot reset it; threads past the cap are still reported, and the row says no triage ran.
/// </para>
/// <para>
/// The session runs in the background and never in the poll, under the pr-review permission file with one
/// extra write path, its own report, never with permissions skipped, and its prompt fences the thread
/// text as data. Only the counts of its dispositions are recorded: the report's prose stays in the file,
/// because the orchestrator window reads status and the feed with permissions bypassed.
/// </para>
/// </summary>
public sealed class CloseoutTriageSessions(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<CloseoutTriageSessions> logger) : IDisposable
{
    private readonly DaemonOptions _options = options.Value;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = [];

    /// <summary>
    /// The canonical key of a set of outstanding threads: their ids, sorted, so the same threads in
    /// another order are the same set. A snapshot that carries a count and no ids (an inspector that
    /// cannot say which) is keyed by the count, which is the only identity it has.
    /// </summary>
    internal static string ThreadSetKey(IReadOnlyList<string> threadIds, int threadCount) =>
        threadIds.Count > 0
            ? string.Join('|', threadIds.Order(StringComparer.Ordinal))
            : $"count:{threadCount}";

    /// <summary>
    /// What to do about a set's triage this poll. Pure over its arguments so the cap and the
    /// once-per-set rule are testable with no database and no process.
    /// </summary>
    /// <param name="existing">The set's triage on the stream, or null when none was ever considered.</param>
    /// <param name="dispatchedSoFar">How many triages the task has ever dispatched.</param>
    /// <param name="cap">The task's lifetime allowance.</param>
    /// <param name="isRunning">Whether a triage session is in flight in this process.</param>
    internal static CloseoutTriageAction Plan(
        CloseoutTriage? existing, int dispatchedSoFar, int cap, Func<Guid, bool> isRunning) =>
        existing switch
        {
            null => dispatchedSoFar < cap ? CloseoutTriageAction.Dispatch : CloseoutTriageAction.PastCap,
            { Status.Value: "Running" } running when !(running.SessionId is { } session && isRunning(session)) =>
                CloseoutTriageAction.Abandoned,
            _ => CloseoutTriageAction.None,
        };

    /// <summary>
    /// Decides and records this poll's triage step for one task's thread set, then starts the session a
    /// dispatch earned. Returns null when a concurrent write to the task stream won the race, in which
    /// case nothing was recorded or started and the next poll decides again.
    /// </summary>
    internal async Task<CloseoutTriageConsideration?> ConsiderAsync(
        IDocumentSession session,
        TaskAggregate task,
        ProjectDetails project,
        RunDetails run,
        IReadOnlyList<string> threadIds,
        int threadCount,
        ReviewReadScope? scope,
        long fenceVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string key = ThreadSetKey(threadIds, threadCount);
        task.CloseoutTriages.TryGetValue(key, out CloseoutTriage? existing);
        CloseoutTriageAction action = Plan(existing, task.CloseoutTriagesDispatched, _options.CloseoutTriageCap, _running.ContainsKey);
        if (action == CloseoutTriageAction.None)
        {
            return new CloseoutTriageConsideration(fenceVersion, existing);
        }

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        object @event;
        CloseoutTriage after;
        TriageJob? job = null;
        switch (action)
        {
            case CloseoutTriageAction.Dispatch:
                Guid sessionId = DomainId.New();
                string reportPath = RunPaths.CloseoutTriageReportFile(runDirectory, KeyHash(key), sessionId);
                @event = new CloseoutTriageDispatched(task.Id, key, sessionId, reportPath, threadCount, now);
                after = new CloseoutTriage(
                    key, FeedbackEvaluationStatus.Running, threadCount, sessionId, reportPath, 0, 0, 0, null, now);
                job = new TriageJob(
                    task.Id, key, task.Objective, task.AcceptanceCriteria, task.PullRequestUrl ?? run.PullRequestUrl ?? "",
                    threadIds, threadCount, scope, sessionId, reportPath, run, runDirectory,
                    _options.ResolveModel(AgentRole.Review, task.Model, project.Model),
                    _options.ResolveEffort(AgentRole.Review, task.Effort, project.Effort));
                break;
            case CloseoutTriageAction.PastCap:
                string capReason =
                    $"this task has already used its allowance of {_options.CloseoutTriageCap} triage sessions";
                @event = new CloseoutTriageRecorded(
                    task.Id, key, FeedbackEvaluationStatus.PastCap, null, 0, 0, 0, capReason, now);
                after = new CloseoutTriage(
                    key, FeedbackEvaluationStatus.PastCap, threadCount, null, null, 0, 0, 0, capReason, now);
                break;
            case CloseoutTriageAction.Abandoned when existing is { } abandoned:
                const string AbandonedReason =
                    "the triage never recorded an outcome, most likely because the daemon restarted while it ran";
                @event = new CloseoutTriageRecorded(
                    task.Id, key, FeedbackEvaluationStatus.Failed, abandoned.SessionId, 0, 0, 0, AbandonedReason, now);
                after = abandoned with
                {
                    Status = FeedbackEvaluationStatus.Failed,
                    Reason = AbandonedReason,
                    RecordedAt = now,
                };
                break;
            default:
                return new CloseoutTriageConsideration(fenceVersion, existing);
        }

        session.Events.Append(task.Id, expectedVersion: fenceVersion + 1, @event);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} changed while its closeout triage was recorded; deferring to the next poll", task.Id);
            return null;
        }

        if (job is not null)
        {
            // Registered before the background task starts and removed only by its own last act, so a
            // poll that runs while the session is live never mistakes it for an abandoned one, and a
            // session that finishes at once cannot be re-registered after it has been removed.
            TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[job.SessionId] = finished.Task;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunAsync(job, _stopping.Token);
                    }
                    finally
                    {
                        _running.TryRemove(job.SessionId, out _);
                        finished.SetResult();
                    }
                },
                CancellationToken.None);
        }

        return new CloseoutTriageConsideration(fenceVersion + 1, after);
    }

    /// <summary>Completes once every triage started so far has recorded its outcome; the daemon never waits on it.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>A short, stable name for a thread set's key, safe in a file name whatever the key holds.</summary>
    internal static string KeyHash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    private async Task RunAsync(TriageJob job, CancellationToken stopping)
    {
        try
        {
            (TriageCounts? counts, string? reason) = await TriageAsync(job, stopping);
            await RecordAsync(job, counts, reason, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The daemon is stopping; the next one records the set as failed rather than leave it running.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Task {TaskId}: the closeout triage for a thread set failed before an outcome could be recorded",
                job.TaskId);
        }
    }

    /// <summary>Runs the session and reads its report: the counts of its dispositions, or the reason there are none.</summary>
    private async Task<(TriageCounts? Counts, string? Reason)> TriageAsync(TriageJob job, CancellationToken stopping)
    {
        string prompt = AgentPromptBuilder.BuildCloseoutTriage(
            job.Objective, job.AcceptanceCriteria, job.PullRequestUrl, job.ThreadIds, job.Scope, job.ReportPath);
        string sessionName = SessionRoleName.For(DomainId.Short(job.TaskId), SessionRoleName.CloseoutTriage);
        string artifact = SessionRoleName.CloseoutTriageFor(KeyHash(job.ThreadSetKey));
        string workingDirectory = Directory.Exists(job.Run.WorktreePath) ? job.Run.WorktreePath : job.RunDirectory;

        SpawnedAgent agent;
        try
        {
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    job.Run.Id, job.SessionId, workingDirectory, job.RunDirectory, prompt, job.Run.ExecutorMode,
                    job.Model, job.Effort, SkipPermissions: false, SessionArtifactName: artifact,
                    // A reviewer's words reach this session without the owner's go, so the checkout's own
                    // project settings and CLAUDE.md are left out of it, as they are for a pull request's head.
                    UntrustedWorkingDirectory: true, MaxTurns: _options.CloseoutTriageMaxTurns,
                    UsesReviewPermissions: true, AssessmentFile: job.ReportPath)
                {
                    TaskId = job.TaskId,
                    SessionName = sessionName,
                },
                stopping);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Task {TaskId}: the closeout triage could not be spawned", job.TaskId);
            return (null, "the triage session could not be started");
        }

        AgentResult? result;
        string streamFile = RunPaths.SessionStreamFile(job.RunDirectory, artifact);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_options.CloseoutTriageTimeout);
        try
        {
            SessionWaitResult wait = await SessionResultWaiter.WaitAsync(
                streamFile, agent.ProcessId, agent.StartedAt, processManager, onOutput: null, timeout.Token);
            result = wait.Result;
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
            logger.LogWarning(
                "Task {TaskId}: the closeout triage exceeded its {Timeout} bound and was terminated",
                job.TaskId, _options.CloseoutTriageTimeout);
            return (null, "the triage session timed out");
        }
        catch (OperationCanceledException)
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
            throw;
        }

        if (result is null)
        {
            return (null, "the triage session ended without a result");
        }

        return result.IsError
            ? (null, "the triage session ended in error")
            : await ReadReportAsync(job.ReportPath, job.ThreadIds, job.ThreadCount, stopping);
    }

    /// <summary>How many thread dispositions each class of a report holds.</summary>
    internal readonly record struct TriageCounts(int Fix, int Decline, int Route);

    /// <summary>The most of a report this reads: a session steered by a stranger's text can write anything.</summary>
    private const int MaxReportChars = 200_000;

    /// <summary>
    /// The counts of the dispositions in a report, from its thread-disposition blocks, read by the same
    /// parser every other triage the platform records goes through. One thread counts once, by its last
    /// block, and only a thread the set named counts when the set is known, so a report cannot inflate
    /// the counts by repeating a block or inventing threads. Nothing else of the file is read into the
    /// stream, so the prose of a report a stranger's text may have steered stays in the file.
    /// </summary>
    internal static async Task<(TriageCounts? Counts, string? Reason)> ReadReportAsync(
        string reportPath, IReadOnlyList<string> threadIds, int threadCount, CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
        {
            return (null, "the triage session wrote no report");
        }

        char[] head = new char[MaxReportChars];
        int read;
        using (StreamReader reader = new(reportPath, Encoding.UTF8))
        {
            read = await reader.ReadBlockAsync(head, cancellationToken);
        }

        Dictionary<string, ReviewThreadDisposition> byThread = new(StringComparer.Ordinal);
        foreach (ReviewThreadOutcome outcome in ReviewResultParser.ParseThreadDispositions(new string(head, 0, read)))
        {
            bool known = threadIds.Count == 0 || threadIds.Contains(outcome.ThreadId, StringComparer.Ordinal);
            if (known && outcome.Disposition != ReviewThreadDisposition.Unknown)
            {
                byThread[outcome.ThreadId] = outcome.Disposition;
            }
        }

        // With ids unknown the set is only a count, so the count is also the most a report may claim.
        IEnumerable<ReviewThreadDisposition> classified = threadIds.Count == 0
            ? byThread.Values.Take(Math.Max(threadCount, 0))
            : byThread.Values;
        ReviewThreadDisposition[] counted = [.. classified];
        int fix = counted.Count(disposition => disposition == ReviewThreadDisposition.Fix);
        int decline = counted.Count(disposition => disposition == ReviewThreadDisposition.Decline);
        int route = counted.Count(disposition => disposition == ReviewThreadDisposition.Route);

        return fix + decline + route == 0
            ? (null, "the triage report held no thread disposition block")
            : (new TriageCounts(fix, decline, route), null);
    }

    /// <summary>How many times an outcome retries a lost race with another append to the task stream.</summary>
    private const int RecordAttempts = 5;

    /// <summary>
    /// Records the outcome, unless the set is no longer the one this session was dispatched for, in which
    /// case it lands as nothing. The task stream is written by closeout polls and the owner's commands
    /// too, so a lost race reloads the stream and decides again rather than dropping an outcome that
    /// still applies.
    /// </summary>
    private async Task RecordAsync(
        TriageJob job, TriageCounts? counts, string? reason, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= RecordAttempts; attempt++)
        {
            await using IDocumentSession session = store.LightweightSession();
            StreamState? fence = await session.Events.FetchStreamStateAsync(job.TaskId, cancellationToken);
            TaskAggregate? task = fence is null
                ? null
                : await session.Events.AggregateStreamAsync<TaskAggregate>(
                    job.TaskId, version: fence.Version, token: cancellationToken);
            if (fence is null
                || task is null
                || !task.CloseoutTriages.TryGetValue(job.ThreadSetKey, out CloseoutTriage? current)
                || current.Status != FeedbackEvaluationStatus.Running
                || current.SessionId != job.SessionId)
            {
                logger.LogInformation(
                    "Task {TaskId}: a closeout triage finished after its thread set changed; its outcome is dropped",
                    job.TaskId);
                return;
            }

            session.Events.Append(job.TaskId, expectedVersion: fence.Version + 1, new CloseoutTriageRecorded(
                job.TaskId, job.ThreadSetKey,
                counts is null ? FeedbackEvaluationStatus.Failed : FeedbackEvaluationStatus.Assessed,
                job.SessionId, counts?.Fix ?? 0, counts?.Decline ?? 0, counts?.Route ?? 0,
                counts is null ? reason : null, DateTimeOffset.UtcNow,
                counts is null ? null : job.ReportPath));
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (EventStreamUnexpectedMaxEventIdException exception) when (attempt < RecordAttempts)
            {
                logger.LogDebug(exception, "Task {TaskId}: the task stream moved while a triage outcome was recorded; retrying", job.TaskId);
            }
        }
    }

    private sealed record TriageJob(
        Guid TaskId,
        string ThreadSetKey,
        string Objective,
        IReadOnlyList<string> AcceptanceCriteria,
        string PullRequestUrl,
        IReadOnlyList<string> ThreadIds,
        int ThreadCount,
        ReviewReadScope? Scope,
        Guid SessionId,
        string ReportPath,
        RunDetails Run,
        string RunDirectory,
        AgentModel Model,
        AgentEffort Effort);
}
