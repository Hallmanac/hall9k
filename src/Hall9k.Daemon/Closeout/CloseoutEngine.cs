using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// The closeout core (Decisions Log #18/#22), extracted from the monitor loop so it
/// tests against a bare store and a fake inspector. Each node watches the
/// awaiting-review runs it executed (RunDetails.NodeId — the task itself is Done and
/// lease-free, so run provenance is the only honest owner). Per PR it observes merge,
/// close, failing checks, and unresolved Copilot review threads, dispatching follow-up
/// runs through the standard reopen pipeline until the bounded automatic budget is
/// spent — then it parks the run for the human and keeps watching for the merge only.
/// </summary>
public sealed class CloseoutEngine(
    IDocumentStore store,
    NodeContext node,
    IPullRequestInspector inspector,
    IWorktreeManager worktrees,
    IOptions<DaemonOptions> options,
    ILogger<CloseoutEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>One sweep over this node's watched pull requests. Returns how many runs were inspected.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> watched;
        await using (IQuerySession query = store.QuerySession())
        {
            Guid nodeId = node.NodeId;
            watched = await query.Query<RunDetails>()
                .Where(r => r.NodeId == nodeId)
                .Where(r => r.MatchesSql(
                    "d.data ->> 'state' in (?, ?)", RunState.AwaitingReview.Value, RunState.CloseoutParked.Value))
                .ToListAsync(cancellationToken);
        }

        int inspected = 0;
        foreach (RunDetails run in watched)
        {
            try
            {
                if (await InspectAndActAsync(run, cancellationToken))
                {
                    inspected++;
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

        return inspected;
    }

    private async Task<bool> InspectAndActAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        // Fence before aggregating (the DispatchEngine order): the reopen below carries
        // this version as expectedVersion, so a task-stream write landing after this
        // point — h9k pr resolve above all — fails the commit instead of being silently
        // absorbed by a version fetched too late.
        StreamState? fence = await session.Events.FetchStreamStateAsync(run.TaskId, cancellationToken);
        if (fence is null)
        {
            return false;
        }

        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(
            run.TaskId, version: fence.Version, token: cancellationToken);
        if (task is null)
        {
            return false;
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

            return false;
        }

        // Only a Done task is in closeout; a reopened one has a follow-up in flight.
        if (task.State != TaskState.Done || task.PullRequestUrl.IsBlank() || run.PullRequestNumber is not > 0)
        {
            return false;
        }

        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (project is null)
        {
            return false;
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
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot.IsMerged)
        {
            await CompleteCloseoutAsync(session, run, project, snapshot, now, cancellationToken);
            return true;
        }

        if (snapshot.IsClosed)
        {
            await RecordClosedAsync(session, run, project, snapshot, now, cancellationToken);
            return true;
        }

        // A parked run gets merge/close detection only; dispatch decisions were handed
        // to the human when the automatic budget ran out.
        if (run.State == RunState.CloseoutParked)
        {
            return true;
        }

        if (snapshot.HasPendingChecks)
        {
            // The CI picture is incomplete; acting now would hand a follow-up run a
            // partial failure list. The next sweep sees the full result.
            return true;
        }

        if (snapshot.FailingChecks.Count > 0)
        {
            session.Events.Append(run.Id, new PullRequestChecksFailed(run.Id, snapshot.FailingChecks, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.FailingChecks,
                $"CI checks failing on the pull request: {string.Join(", ", snapshot.FailingChecks)}.",
                now, cancellationToken);
            return true;
        }

        if (snapshot.UnresolvedCopilotThreadCount > 0)
        {
            session.Events.Append(run.Id, new ReviewFeedbackReceived(run.Id, snapshot.UnresolvedCopilotThreadCount, now));
            await DispatchFollowUpOrParkAsync(
                session, task, run, fence.Version,
                FollowUpKind.ReviewFeedback,
                $"{snapshot.UnresolvedCopilotThreadCount} unresolved Copilot review thread(s) on the pull request.",
                now, cancellationToken);
            return true;
        }

        return true;
    }

    /// <summary>
    /// The merge is the end of the story: RunCompleted finally lands (the event
    /// TASK-MODEL.md reserved for exactly this), then the workspace is cleaned up — the
    /// worktree retained through closeout (log #21) and the task branch everywhere it
    /// lingers (origin incident: five merged task branches accumulated locally because
    /// nothing owned this step).
    /// </summary>
    private async Task CompleteCloseoutAsync(
        IDocumentSession session,
        RunDetails run,
        ProjectDetails project,
        PullRequestSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        session.Events.Append(run.Id, new PullRequestMerged(run.Id, snapshot.MergedAt, now));
        session.Events.Append(run.Id, new RunCompleted(run.Id, snapshot.MergedAt ?? now));
        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Run {RunId}: pull request {Url} merged — closeout complete", run.Id, run.PullRequestUrl);

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
        if (task.CloseoutAttempts >= _options.MaxAutomaticCloseoutRuns)
        {
            string parkReason =
                $"{reason} Automatic follow-up budget spent ({task.CloseoutAttempts} run(s)). " +
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
