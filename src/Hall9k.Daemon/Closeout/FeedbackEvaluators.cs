using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Hall9k.Daemon.Execution;
using Hall9k.Daemon.ProcessManagement;
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

/// <summary>What closeout does about one needs-you item's evaluator this poll.</summary>
internal enum FeedbackEvaluatorAction
{
    /// <summary>Dispatch the item's one evaluator.</summary>
    Dispatch,

    /// <summary>The task is past its evaluator allowance: record that, and run nothing.</summary>
    PastCap,

    /// <summary>The evaluator was dispatched and nothing is running it any more: record that it failed.</summary>
    Abandoned,
}

/// <summary>One item and what to do about its evaluator.</summary>
internal sealed record FeedbackEvaluatorStep(NonMemberFeedbackItem Item, FeedbackEvaluatorAction Action);

/// <summary>
/// The read-only evaluator that assesses a non-member's feedback before the owner decides on it
/// (task: before the owner decides on a non-member's review feedback, an evaluator session assesses
/// it against the task's intent, and the assessment appears on that feedback's needs-you item).
/// <para>
/// Closeout hands it the items its membership gate just held and the ones still waiting; each item
/// earns at most one evaluator, ever. The dispatch is a task event saved before the session is
/// spawned, so another poll observing the same feedback, or a daemon restarting mid-wait, finds it on
/// the stream and starts nothing; an item whose dispatch is on the stream with nothing running it
/// (the daemon restarted) is recorded as failed, never re-dispatched. A task's evaluators are also
/// capped for its lifetime (<see cref="DaemonOptions.NonMemberFeedbackEvaluatorCap"/>), decided over
/// the stream so a restart cannot reset it; feedback past the cap is still held for the owner and
/// the item says no evaluator ran.
/// </para>
/// <para>
/// The session runs in the background and never in the poll: the owner's go and decline are task
/// events the poll reads, so they must work while an evaluator is still running, and an outcome that
/// lands after the owner acted is dropped. The session runs under the pr-review permission file with
/// one extra write path, its own assessment file, never with permissions skipped, and its prompt
/// fences the feedback as data. Only the recommendation word is recorded: the assessment's prose stays
/// in the file, because the orchestrator window reads status and the feed with permissions bypassed.
/// </para>
/// </summary>
public sealed class FeedbackEvaluators(
    IDocumentStore store,
    IExecutor executor,
    IProcessManager processManager,
    IOptions<DaemonOptions> options,
    ILogger<FeedbackEvaluators> logger) : IDisposable
{
    private readonly DaemonOptions _options = options.Value;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = [];

    /// <summary>
    /// Which items need an evaluator decision this poll, in a stable order: undecided items no
    /// evaluator has been considered for (dispatched while the allowance lasts, then past-cap), and
    /// undecided items whose dispatch nothing is running. Pure over its arguments so the cap and the
    /// one-per-item rule are testable with no database and no process.
    /// </summary>
    /// <param name="items">The task's items, as of the start of the poll.</param>
    /// <param name="newlyHeld">Items the gate raised this poll, not yet in <paramref name="items"/>.</param>
    /// <param name="withdrawn">Keys the gate withdrew this poll.</param>
    /// <param name="dispatchedSoFar">How many evaluators the task has ever dispatched.</param>
    /// <param name="cap">The task's lifetime allowance.</param>
    /// <param name="isRunning">Whether an evaluator session is in flight in this process.</param>
    internal static IReadOnlyList<FeedbackEvaluatorStep> Plan(
        IEnumerable<NonMemberFeedbackItem> items,
        IEnumerable<NonMemberFeedbackItem> newlyHeld,
        IReadOnlySet<string> withdrawn,
        int dispatchedSoFar,
        int cap,
        Func<Guid, bool> isRunning)
    {
        HashSet<string> newKeys = [.. newlyHeld.Select(item => item.Key)];
        IEnumerable<NonMemberFeedbackItem> candidates = items
            .Where(item => item.Status.NeedsOwner && !withdrawn.Contains(item.Key) && !newKeys.Contains(item.Key))
            .OrderBy(item => item.ObservedAt)
            .Concat(newlyHeld);

        int remaining = Math.Max(0, cap - dispatchedSoFar);
        List<FeedbackEvaluatorStep> steps = [];
        foreach (NonMemberFeedbackItem item in candidates)
        {
            FeedbackEvaluation evaluation = item.EvaluationState;
            if (evaluation.Status == FeedbackEvaluationStatus.Unknown)
            {
                steps.Add(remaining > 0
                    ? new FeedbackEvaluatorStep(item, FeedbackEvaluatorAction.Dispatch)
                    : new FeedbackEvaluatorStep(item, FeedbackEvaluatorAction.PastCap));
                remaining = Math.Max(0, remaining - 1);
            }
            else if (evaluation.Status == FeedbackEvaluationStatus.Running
                && !(evaluation.SessionId is { } session && isRunning(session)))
            {
                steps.Add(new FeedbackEvaluatorStep(item, FeedbackEvaluatorAction.Abandoned));
            }
        }

        return steps;
    }

    /// <summary>
    /// Decides and records this poll's evaluator steps for one task, then starts the sessions the
    /// dispatches earned. Returns the task-stream version later appends must be fenced against, or
    /// null when a concurrent write to the stream (the owner's go above all) won the race, in which
    /// case nothing was recorded or started and the next poll decides again.
    /// </summary>
    internal async Task<long?> ConsiderAsync(
        IDocumentSession session,
        TaskAggregate task,
        ProjectDetails project,
        RunDetails run,
        IReadOnlyList<HeldFeedback> newlyHeld,
        IReadOnlyCollection<string> withdrawn,
        long fenceVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NonMemberFeedbackItem> held =
        [
            .. newlyHeld.Select(feedback => new NonMemberFeedbackItem(
                feedback.Key, feedback.Kind, feedback.Reviewer, feedback.Url, now, NonMemberFeedbackStatus.Undecided,
                feedback.Review, CommentCopies: feedback.CommentCopies)),
        ];
        IReadOnlyList<FeedbackEvaluatorStep> steps = Plan(
            task.NonMemberFeedback.Values, held, withdrawn.ToHashSet(),
            task.NonMemberEvaluatorsDispatched, _options.NonMemberFeedbackEvaluatorCap, _running.ContainsKey);
        if (steps.Count == 0)
        {
            return fenceVersion;
        }

        string runDirectory = RunPaths.ResolveCurrentDirectory(run.RunDirectory);
        List<object> events = [];
        List<EvaluatorJob> jobs = [];
        foreach (FeedbackEvaluatorStep step in steps)
        {
            switch (step.Action)
            {
                case FeedbackEvaluatorAction.Dispatch:
                    Guid sessionId = DomainId.New();
                    string assessmentPath = RunPaths.FeedbackAssessmentFile(runDirectory, KeyHash(step.Item.Key), sessionId);
                    events.Add(new NonMemberFeedbackEvaluatorDispatched(task.Id, step.Item.Key, sessionId, assessmentPath, now));
                    jobs.Add(new EvaluatorJob(
                        task.Id, task.Objective, task.AcceptanceCriteria, task.PullRequestUrl ?? run.PullRequestUrl ?? "",
                        step.Item, sessionId, assessmentPath, run, runDirectory,
                        _options.ResolveModel(AgentRole.Review, task.Model, project.Model),
                        _options.ResolveEffort(AgentRole.Review, task.Effort, project.Effort)));
                    break;
                case FeedbackEvaluatorAction.PastCap:
                    events.Add(new NonMemberFeedbackEvaluationRecorded(
                        task.Id, step.Item.Key, FeedbackEvaluationStatus.PastCap, null, null,
                        $"this task has already used its allowance of {_options.NonMemberFeedbackEvaluatorCap} evaluators",
                        now));
                    break;
                default:
                    events.Add(new NonMemberFeedbackEvaluationRecorded(
                        task.Id, step.Item.Key, FeedbackEvaluationStatus.Failed, step.Item.EvaluationState.SessionId, null,
                        "the evaluator never recorded an outcome, most likely because the daemon restarted while it ran",
                        now));
                    break;
            }
        }

        session.Events.Append(task.Id, expectedVersion: fenceVersion + events.Count, events);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} changed while its feedback evaluators were recorded; deferring to the next poll", task.Id);
            return null;
        }

        foreach (EvaluatorJob job in jobs)
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

        return fenceVersion + events.Count;
    }

    /// <summary>Completes once every evaluator started so far has recorded its outcome; the daemon never waits on it.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_running.Values);

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>A short, stable name for a feedback key, safe in a file name whatever the key holds.</summary>
    internal static string KeyHash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    private async Task RunAsync(EvaluatorJob job, CancellationToken stopping)
    {
        try
        {
            (FeedbackRecommendation? recommendation, string? reason) = await EvaluateAsync(job, stopping);
            await RecordAsync(job, recommendation, reason, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The daemon is stopping; the next one records the item as failed rather than leave it running.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Task {TaskId}: the feedback evaluator for {Key} failed before an outcome could be recorded",
                job.TaskId, job.Item.Key);
        }
    }

    /// <summary>Runs the session and reads its assessment: a recommendation, or the reason there is none.</summary>
    private async Task<(FeedbackRecommendation? Recommendation, string? Reason)> EvaluateAsync(
        EvaluatorJob job, CancellationToken stopping)
    {
        string prompt = FeedbackEvaluatorPromptBuilder.Build(
            job.Item, job.Objective, job.AcceptanceCriteria, job.PullRequestUrl, job.AssessmentPath);
        string sessionName = SessionRoleName.For(DomainId.Short(job.TaskId), SessionRoleName.FeedbackEvaluator);
        string artifact = SessionRoleName.FeedbackEvaluatorFor(KeyHash(job.Item.Key));
        string workingDirectory = Directory.Exists(job.Run.WorktreePath) ? job.Run.WorktreePath : job.RunDirectory;

        SpawnedAgent agent;
        try
        {
            agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    job.Run.Id, job.SessionId, workingDirectory, job.RunDirectory, prompt, job.Run.ExecutorMode,
                    job.Model, job.Effort, SkipPermissions: false, SessionArtifactName: artifact,
                    // The first session a stranger's text reaches without the owner's go, so the checkout's own
                    // project settings and CLAUDE.md are left out of it as they are for a pull request's head.
                    UntrustedWorkingDirectory: true, MaxTurns: _options.NonMemberFeedbackEvaluatorMaxTurns,
                    UsesReviewPermissions: true, AssessmentFile: job.AssessmentPath)
                {
                    TaskId = job.TaskId,
                    SessionName = sessionName,
                },
                stopping);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Task {TaskId}: the feedback evaluator could not be spawned", job.TaskId);
            return (null, "the evaluator could not be started");
        }

        AgentResult? result;
        string streamFile = RunPaths.SessionStreamFile(job.RunDirectory, artifact);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_options.NonMemberFeedbackEvaluatorTimeout);
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
                "Task {TaskId}: the feedback evaluator exceeded its {Timeout} bound and was terminated",
                job.TaskId, _options.NonMemberFeedbackEvaluatorTimeout);
            return (null, "the evaluator timed out");
        }
        catch (OperationCanceledException)
        {
            processManager.Terminate(agent.ProcessId, agent.StartedAt);
            throw;
        }

        if (result is null)
        {
            return (null, "the evaluator ended without a result");
        }

        return result.IsError
            ? (null, "the evaluator ended in error")
            : await ReadAssessmentAsync(job.AssessmentPath, stopping);
    }

    /// <summary>
    /// The recommendation on the assessment file's first line, which must read exactly
    /// <c>Recommendation: fix</c>, <c>decline</c> or <c>ask</c>. Nothing else of the file is read into
    /// the stream, so the prose of an assessment a stranger's text may have steered stays in the file.
    /// </summary>
    internal static async Task<(FeedbackRecommendation? Recommendation, string? Reason)> ReadAssessmentAsync(
        string assessmentPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(assessmentPath))
        {
            return (null, "the evaluator wrote no assessment");
        }

        // Only the head of the file is read: a session steered by a stranger's text can write anything,
        // and a recommendation line is a few words.
        char[] head = new char[256];
        int read;
        using (StreamReader reader = new(assessmentPath, Encoding.UTF8))
        {
            read = await reader.ReadBlockAsync(head, cancellationToken);
        }

        string first = new string(head, 0, read).Split('\n', 2)[0].TrimEnd('\r');

        const string Prefix = "Recommendation:";
        FeedbackRecommendation recommendation = first.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? FeedbackRecommendation.Parse(first[Prefix.Length..])
            : FeedbackRecommendation.Unknown;
        return recommendation.IsRecognized
            ? (recommendation, null)
            : (null, first.Trim().Length == 0
                ? "the evaluator wrote an empty assessment"
                : "the evaluator's recommendation was not one of fix, decline or ask");
    }

    /// <summary>How many times an outcome retries a lost race with another append to the task stream.</summary>
    private const int RecordAttempts = 5;

    /// <summary>
    /// Records the outcome, unless the owner has already acted on the item or the item is no longer
    /// the one this session was dispatched for, in which case it lands as nothing. The task stream is
    /// written by closeout polls, the owner's commands and sibling evaluators too, so a lost race
    /// reloads the stream and decides again rather than dropping an outcome that still applies.
    /// </summary>
    private async Task RecordAsync(
        EvaluatorJob job, FeedbackRecommendation? recommendation, string? reason, CancellationToken cancellationToken)
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
                || !task.NonMemberFeedback.TryGetValue(job.Item.Key, out NonMemberFeedbackItem? current)
                || !current.Status.NeedsOwner
                || current.EvaluationState.SessionId != job.SessionId)
            {
                logger.LogInformation(
                    "Task {TaskId}: the feedback evaluator for {Key} finished after the owner acted or the item changed; its outcome is dropped",
                    job.TaskId, job.Item.Key);
                return;
            }

            // Fenced to the version the item was judged against, so an owner's go or decline that commits
            // in between loses this append to a retry that then sees the item decided and drops the outcome.
            session.Events.Append(job.TaskId, expectedVersion: fence.Version + 1, new NonMemberFeedbackEvaluationRecorded(
                job.TaskId, job.Item.Key,
                recommendation is null ? FeedbackEvaluationStatus.Failed : FeedbackEvaluationStatus.Assessed,
                job.SessionId, recommendation, recommendation is null ? reason : null, DateTimeOffset.UtcNow,
                recommendation is null ? null : job.AssessmentPath));
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (EventStreamUnexpectedMaxEventIdException exception) when (attempt < RecordAttempts)
            {
                logger.LogDebug(exception, "Task {TaskId}: the task stream moved while an evaluation outcome was recorded; retrying", job.TaskId);
            }
        }
    }

    private sealed record EvaluatorJob(
        Guid TaskId,
        string Objective,
        IReadOnlyList<string> AcceptanceCriteria,
        string PullRequestUrl,
        NonMemberFeedbackItem Item,
        Guid SessionId,
        string AssessmentPath,
        RunDetails Run,
        string RunDirectory,
        AgentModel Model,
        AgentEffort Effort);
}
