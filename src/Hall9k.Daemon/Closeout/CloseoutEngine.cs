using Hall9k.Daemon.Execution;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Infrastructure.Storage;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// One sweep's tally: runs whose pull request was actually inspected, and how many of
/// those inspections observed the merge. Feeds the monitor's cadence logging and the
/// startup catch-up report (Decisions Log #31).
/// </summary>
public sealed record CloseoutSweepResult(int RunsInspected, int MergesObserved);

/// <summary>
/// The closeout core (Decisions Log #18/#22), extracted from the monitor loop so it
/// tests against a bare store and a fake inspector. Each node watches the
/// awaiting-review runs it executed (RunDetails.NodeId — the task itself is Done and
/// lease-free, so run provenance is the only honest owner). Per PR it observes merge,
/// close, failing checks, unresolved Copilot review threads, and errored Copilot
/// reviews (an error placeholder produces zero threads — never mistaken for a clean
/// pass), dispatching follow-up runs through the standard reopen pipeline and
/// re-requesting errored reviews through the API until the bounded automatic budget is
/// spent — then it parks the run for the human and keeps watching for the merge only.
/// </summary>
public sealed class CloseoutEngine(
    IDocumentStore store,
    NodeContext node,
    DaemonConnection connection,
    IPullRequestInspector inspector,
    IWorktreeManager worktrees,
    IOptions<DaemonOptions> options,
    ILogger<CloseoutEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>How one run's inspection ended — unpersisted in-process outcome, so an enum is fine (TASK-MODEL.md §8).</summary>
    private enum InspectionOutcome
    {
        Skipped,
        Inspected,
        MergeObserved,
    }

    /// <summary>One sweep over this node's watched pull requests.</summary>
    public async Task<CloseoutSweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> watched;
        await using (IQuerySession query = store.QuerySession())
        {
            Guid nodeId = node.NodeId;
            // ReviewPending is watched too: a run holds there while an errored review's
            // re-request waits for the reviewer to answer.
            watched = await query.Query<RunDetails>()
                .Where(r => r.NodeId == nodeId)
                .Where(r => r.MatchesSql(
                    "d.data ->> 'state' in (?, ?, ?)",
                    RunState.AwaitingReview.Value, RunState.ReviewPending.Value, RunState.CloseoutParked.Value))
                .ToListAsync(cancellationToken);
        }

        int inspected = 0;
        int merges = 0;
        foreach (RunDetails run in watched)
        {
            try
            {
                switch (await InspectAndActAsync(run, cancellationToken))
                {
                    case InspectionOutcome.MergeObserved:
                        inspected++;
                        merges++;
                        break;
                    case InspectionOutcome.Inspected:
                        inspected++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Closeout poll failed for run {RunId} ({Url}); will retry next sweep",
                    run.Id, run.PullRequestUrl);
            }
        }

        return new CloseoutSweepResult(inspected, merges);
    }

    private async Task<InspectionOutcome> InspectAndActAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Fence before aggregating (the DispatchEngine order): the reopen below carries
        // this version as expectedVersion, so a task-stream write landing after this
        // point — h9k pr resolve above all — fails the commit instead of being silently
        // absorbed by a version fetched too late.
        StreamState? fence = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (fence is null)
        {
            return InspectionOutcome.Skipped;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            run.TaskId, version: fence.Version, token: cancellationToken);
        if (task is null)
        {
            return InspectionOutcome.Skipped;
        }

        // A newer run owns this task's PR now (a follow-up pushed after this one) — this
        // run's watch is over; retire it so the watch set stays bounded.
        if (task.CurrentRunId != run.Id)
        {
            if (task.CurrentRunId is not null)
            {
                session.Events.Append(run.Id, new RunSuperseded(run.Id, task.LeaseGeneration, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
            }

            return InspectionOutcome.Skipped;
        }

        // Only a Done task is in closeout; a reopened one has a follow-up in flight.
        if (task.State != TaskState.Done || task.PullRequestUrl.IsBlank() || run.PullRequestNumber is not > 0)
        {
            return InspectionOutcome.Skipped;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
        {
            return InspectionOutcome.Skipped;
        }

        PullRequestSnapshot snapshot = await inspector.InspectAsync(
            project.RepositoryPath, task.PullRequestUrl, run.PullRequestNumber.Value, cancellationToken);

        // The inspection is a slow network call. Revalidate the fence before acting: a
        // reopen that landed mid-call may already have a follow-up agent working in the
        // reused worktree, and the merged/closed paths below touch the filesystem with
        // no expectedVersion to protect them. Deferring one sweep is always safe.
        StreamState? current = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (current is null || current.Version != fence.Version)
        {
            logger.LogDebug(
                "Task {TaskId} advanced while inspecting {Url}; deferring to the next sweep",
                run.TaskId, run.PullRequestUrl);
            return InspectionOutcome.Skipped;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot.IsMerged)
        {
            await CompleteCloseoutAsync(session, run, project, snapshot, now, cancellationToken);
            return InspectionOutcome.MergeObserved;
        }

        if (snapshot.IsClosed)
        {
            await RecordClosedAsync(session, run, project, snapshot, now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        // A parked run gets merge/close detection only; dispatch decisions were handed
        // to the human when the automatic budget ran out.
        if (run.State == RunState.CloseoutParked)
        {
            return InspectionOutcome.Inspected;
        }

        if (snapshot.HasPendingChecks)
        {
            // The CI picture is incomplete; acting now would hand a follow-up run a
            // partial failure list. The next sweep sees the full result.
            return InspectionOutcome.Inspected;
        }

        if (snapshot.FailingChecks.Count > 0)
        {
            session.Events.Append(run.Id, new PullRequestChecksFailed(run.Id, snapshot.FailingChecks, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.FailingChecks,
                $"CI checks failing on the pull request: {string.Join(", ", snapshot.FailingChecks)}.",
                now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.UnresolvedCopilotThreadCount > 0)
        {
            session.Events.Append(run.Id, new ReviewFeedbackReceived(run.Id, snapshot.UnresolvedCopilotThreadCount, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.ReviewFeedback,
                $"{snapshot.UnresolvedCopilotThreadCount} unresolved Copilot review thread(s) on the pull request.",
                now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        if (snapshot.ErroredCopilotReview is { } erroredReview)
        {
            await RerequestReviewOrParkAsync(
                session, task, run, project.RepositoryPath, task.PullRequestUrl,
                run.PullRequestNumber.Value, erroredReview, now, cancellationToken);
            return InspectionOutcome.Inspected;
        }

        return InspectionOutcome.Inspected;
    }

    /// <summary>
    /// An errored review (zero threads, no verdict) must not read as review-clean: the
    /// run holds at ReviewPending while the monitor re-requests the review through the
    /// API — never the website, which may be down when this matters (origin incident:
    /// PR #6, 2026-08-17, GitHub partial outage). Each errored review is re-requested
    /// exactly once (the recorded review URL is the dedup key across sweeps), each
    /// re-request draws on the shared automatic budget, and a reviewer that keeps
    /// erroring parks the run with the errored review named for the human. A successful
    /// re-review stops matching as errored and flows through the normal thread path.
    /// </summary>
    private async Task RerequestReviewOrParkAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        string repositoryPath,
        string pullRequestUrl,
        int pullRequestNumber,
        ErroredReview erroredReview,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // This errored review was already re-requested; the reviewer just hasn't
        // answered yet. Re-requesting again every sweep would burn the budget on one
        // observation.
        if (erroredReview.Url == run.ErroredReviewUrl)
        {
            return;
        }

        session.Events.Append(run.Id, new ReviewErrored(run.Id, erroredReview.Reviewer, erroredReview.Url, now));

        if (AutomaticActionsSpent(task, run) >= _options.MaxAutomaticCloseoutRuns)
        {
            string parkReason =
                $"Copilot review keeps erroring: {erroredReview.Reviewer}'s latest review ({erroredReview.Url}) " +
                "says it was unable to review the pull request. " +
                $"Automatic closeout budget spent ({AutomaticActionsSpent(task, run)} action(s)). " +
                "Re-request the review by hand, merge without it, or grant another attempt with h9k pr resolve.";
            session.Events.Append(run.Id, new CloseoutParked(run.Id, parkReason, now));
            await session.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Run {RunId}: closeout parked for the human — {Reason}", run.Id, parkReason);
            return;
        }

        // The API call precedes the append: no ReviewRerequested lands without the
        // request actually made. A failure here rolls the observation back with it and
        // the next sweep retries the whole step.
        await inspector.RerequestReviewAsync(
            repositoryPath, pullRequestUrl, pullRequestNumber, erroredReview.Reviewer, cancellationToken);
        session.Events.Append(run.Id, new ReviewRerequested(run.Id, erroredReview.Reviewer, erroredReview.Url, now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: Copilot review errored ({Url}); re-requested review from {Reviewer} (action {Action}/{Max})",
            run.Id, erroredReview.Url, erroredReview.Reviewer,
            AutomaticActionsSpent(task, run) + 1, _options.MaxAutomaticCloseoutRuns);
    }

    /// <summary>
    /// One budget for every automatic closeout action: reopen dispatches count on the
    /// task (CloseoutAttempts), review re-requests on the watched run. h9k pr resolve
    /// resets both — the manual reopen zeroes the counter and supersedes the run.
    /// </summary>
    private static int AutomaticActionsSpent(TaskAggregate task, RunDetails run) =>
        task.CloseoutAttempts + run.ReviewRerequestCount;

    /// <summary>
    /// The merge is the end of the story: RunCompleted finally lands (the event
    /// TASK-MODEL.md reserved for exactly this), then the workspace is cleaned up — the
    /// worktree retained through closeout (log #21) and the task branch everywhere it
    /// lingers (origin incident: five merged task branches accumulated locally because
    /// nothing owned this step).
    /// <para>
    /// This is also the landing half of the handoff's capture-then-land split (Decisions Log
    /// #36). The text was captured from the agents' own session ends long before now, but the
    /// event carrying it is appended here, in the same transaction as PullRequestMerged and
    /// RunCompleted and immediately before the dependents are unblocked. That ordering IS the
    /// guarantee: an unmerged run has no RunHandoffRecorded, so its summary can never travel
    /// to work that builds on code which never landed.
    /// </para>
    /// </summary>
    private async Task CompleteCloseoutAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RunHandoffRecorded handoff = await ComposeHandoffAsync(session, run, now, cancellationToken);
        session.Events.Append(run.Id, new PullRequestMerged(run.Id, snapshot.MergedAt, now));
        session.Events.Append(run.Id, handoff);
        session.Events.Append(run.Id, new RunCompleted(run.Id, snapshot.MergedAt ?? now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: pull request {Url} merged — closeout complete", run.Id, run.PullRequestUrl);

        await UnblockDependentsAsync(run.TaskId, now, cancellationToken);
        await RemoveWorktreeBestEffortAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
        try
        {
            await worktrees.DeleteBranchEverywhereAsync(project.RepositoryPath, run.Branch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Branch cleanup failed for {Branch} (safe to delete by hand)", run.Branch);
        }
    }

    /// <summary>
    /// The handoff the task hands down, composed from every run that carried this pull
    /// request rather than from the run that happened to observe the merge (Decisions Log
    /// #36).
    /// <para>
    /// The completing run is almost never the run that did the work. Decision #22 makes
    /// review follow-ups automatic, so a merged pull request is normally an original run
    /// retired with RunSuperseded plus a follow-up that resolved the review threads and
    /// reached Completed. Reading only the completing run would hand a dependent the thread
    /// resolution and leave the description of the feature itself unread in a superseded
    /// run's directory. Origin incident: the first cut of this method did exactly that, and
    /// every task on main that had reached true closeout showed the shape (two runs, one
    /// superseded).
    /// </para>
    /// <para>
    /// Failed and killed runs are excluded, and that exclusion is the retry case: a run that
    /// died left work which never merged, so its summary must not travel. A superseded run is
    /// the opposite situation — it is the run whose work is in this merge.
    /// </para>
    /// </summary>
    private async Task<RunHandoffRecorded> ComposeHandoffAsync(
        IDocumentSession session, RunDetails completing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<HandoffParser.RunHandoff> authored = [];
        HandoffOutcome absence = HandoffOutcome.NotCaptured;
        foreach (Guid runId in await MergedRunsAsync(session, completing, cancellationToken))
        {
            (HandoffOutcome outcome, string? text) = await ReadHandoffAsync(runId, cancellationToken);
            if (text.IsNotBlank())
            {
                authored.Add(new HandoffParser.RunHandoff(runId, text));
                continue;
            }

            absence = LessCertainOf(absence, outcome);
        }

        return authored.Count == 0
            ? new RunHandoffRecorded(completing.Id, absence, null, now)
            : new RunHandoffRecorded(
                completing.Id,
                HandoffOutcome.Captured,
                HandoffParser.BoundForEvent(
                    HandoffParser.Compose(authored), [.. authored.Select(handoff => handoff.RunId)]),
                now);
    }

    /// <summary>
    /// The runs whose work is in this merge, oldest dispatch first, so the run that opened the
    /// work leads the composed handoff. The completing run is appended if the projection did
    /// not return it, because the run being closed out is a fact this method already holds.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> MergedRunsAsync(
        IQuerySession session, RunDetails completing, CancellationToken cancellationToken)
    {
        Guid taskId = completing.TaskId;
        IReadOnlyList<RunDetails> runs = await session.Query<RunDetails>()
            .Where(run => run.TaskId == taskId)
            .ToListAsync(cancellationToken);

        List<Guid> merged =
        [
            .. runs
                .Where(run => run.State != RunState.Failed && run.State != RunState.Killed)
                .OrderBy(run => run.DispatchedAt)
                .ThenBy(run => run.Id)
                .Select(run => run.Id),
        ];

        return merged.Contains(completing.Id) ? merged : [.. merged, completing.Id];
    }

    /// <summary>
    /// The absence the composed handoff reports when no run authored one. Certainty only ever
    /// decreases: a file that could not be read (<see cref="HandoffOutcome.Unknown"/>) outranks
    /// an empty one, because it might have held the very text the dependent wanted, and an
    /// empty one outranks a missing one, because at least one session's result was read and
    /// observed to carry nothing. Guessing a stronger absence than the reads support is exactly
    /// what the never-guess rule forbids.
    /// </summary>
    private static HandoffOutcome LessCertainOf(HandoffOutcome absence, HandoffOutcome observed) =>
        absence == HandoffOutcome.Unknown || observed == HandoffOutcome.Unknown
            ? HandoffOutcome.Unknown
            : absence == HandoffOutcome.NotAuthored || observed == HandoffOutcome.NotAuthored
                ? HandoffOutcome.NotAuthored
                : HandoffOutcome.NotCaptured;

    /// <summary>
    /// One run's handoff, read from the artifact its own session end wrote (Decisions Log
    /// #36). The file's three states are three observations and each maps to its own outcome,
    /// so the absence of a handoff is always a recorded answer rather than an empty string
    /// nobody can interpret: non-blank means the agent authored one, empty means its result
    /// was read and carried none, and absent means there was no session-end capture at all —
    /// a run parked and resolved by hand, or a stream from before handoffs existed. A run
    /// closing out without a usable handoff is perfectly valid; what is not valid is
    /// pretending to know why, which is why a file that exists but cannot be read records
    /// <see cref="HandoffOutcome.Unknown"/> rather than any of the three.
    /// </summary>
    private async Task<(HandoffOutcome Outcome, string? Handoff)> ReadHandoffAsync(
        Guid runId, CancellationToken cancellationToken)
    {
        string path = RunPaths.HandoffFile(runId);
        try
        {
            if (!File.Exists(path))
            {
                return (HandoffOutcome.NotCaptured, null);
            }

            string handoff = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            return handoff.IsBlank()
                ? (HandoffOutcome.NotAuthored, null)
                : (HandoffOutcome.Captured, handoff);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The artifact exists but could not be read, which says nothing about whether a
            // handoff was authored — so the ledger says it does not know, rather than
            // asserting the absence NotCaptured would claim. An unread file is not an
            // observed one (the never-guess rule).
            logger.LogWarning(exception, "Could not read the handoff artifact for run {RunId} at {Path}", runId, path);
            return (HandoffOutcome.Unknown, null);
        }
    }

    /// <summary>
    /// True closeout is the only completion signal a dependency chain accepts (Decisions Log
    /// #34), so this is where dependents re-evaluate: whichever node observed the merge is the
    /// node that unblocks them, and the doorbell tells every other node's dispatch loop to
    /// look. A failure here is logged rather than propagated — the merge is recorded either
    /// way, and the dispatch loop's own sweep re-evaluates blocked tasks each cycle.
    /// </summary>
    private async Task UnblockDependentsAsync(Guid taskId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            DependencyReevaluation reevaluation = await TaskDependencyResolver.ForDependencyAsync(
                session, taskId, now, cancellationToken);
            if (reevaluation.Unblocked.Count == 0)
            {
                // The pass may still have parked or recovered a dependent on one of its other
                // blockers; nothing there is claimable, so there is no doorbell to ring and no
                // count worth reporting as an unblocking.
                return;
            }

            logger.LogInformation(
                "Task {TaskId} closed out — {Unblocked} dependent(s) moved Blocked → Queued",
                taskId, reevaluation.Unblocked.Count);
            await Doorbell.RingAsync(connection.ConnectionString, $"dependencies-met:{taskId}", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Re-evaluating the dependents of task {TaskId} failed; the dispatch loop's sweep retries it", taskId);
        }
    }

    private async Task RecordClosedAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        session.Events.Append(run.Id, new PullRequestClosed(run.Id, snapshot.ClosedAt, now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Run {RunId}: pull request {Url} was closed without merge — worktree removed, branch kept (it holds unmerged work)",
            run.Id, run.PullRequestUrl);

        await RemoveWorktreeBestEffortAsync(project.RepositoryPath, run.WorktreePath, cancellationToken);
    }

    private async Task DispatchFollowUpOrParkAsync(
        IDocumentSession session,
        TaskAggregate task,
        RunDetails run,
        long fenceVersion,
        FollowUpKind kind,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (AutomaticActionsSpent(task, run) >= _options.MaxAutomaticCloseoutRuns)
        {
            string parkReason =
                $"{reason} Automatic closeout budget spent ({AutomaticActionsSpent(task, run)} action(s)). " +
                "Fix or merge the pull request by hand, close it, or grant another attempt with h9k pr resolve.";
            session.Events.Append(run.Id, new CloseoutParked(run.Id, parkReason, now));
            await session.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Run {RunId}: closeout parked for the human — {Reason}", run.Id, parkReason);
            return;
        }

        // The reopen races the CLI's h9k pr resolve on the fence version captured before
        // the aggregate was read; losing just means someone else already dispatched.
        session.Events.Append(task.Id, expectedVersion: fenceVersion + 1, TaskDecider.Reopen(
            task, run.Id, run.Branch, reason, kind, automatic: true, now, node.OwnerId));

        // The reopen hands the pull request to a successor, so this run's watch ends
        // with it — retire it in the same transaction (TASK-MODEL.md §2.2). A lost race
        // rolls back both appends and leaves the run watched for the next sweep.
        // Generation + 1 is the generation this reopen grants: Claim always increments,
        // so the successor's claim lands there — recorded now to keep the field's
        // "superseded BY" meaning even though the claim itself commits later.
        session.Events.Append(run.Id, new RunSuperseded(run.Id, task.LeaseGeneration + 1, now));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            logger.LogDebug("Task {TaskId} was reopened concurrently; skipping this dispatch", task.Id);
            return;
        }

        logger.LogInformation(
            "Task {TaskId} reopened automatically ({Kind}, attempt {Attempt}/{Max}): {Reason}",
            task.Id, kind.Value, task.CloseoutAttempts + 1, _options.MaxAutomaticCloseoutRuns, reason);
    }

    private async Task RemoveWorktreeBestEffortAsync(
        string repositoryPath, string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(worktreePath))
            {
                await worktrees.RemoveAsync(repositoryPath, worktreePath, cancellationToken);
            }
            else
            {
                // Gone out-of-band (crash, manual rm): collect the stale registration
                // now rather than leaving it for the startup prune.
                await worktrees.PruneAsync(repositoryPath, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", worktreePath);
        }
    }
}
