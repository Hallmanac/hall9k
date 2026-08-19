using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Documents;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Turns a fresh claim into a live agent: worktree → RunDispatched → spawn →
/// RunProcessStarted → monitor. Failures at any step fail the run and task honestly.
/// A task carrying a pull-request URL is checked against the provider first: already
/// merged means close out, never redispatch.
/// </summary>
public sealed class RunLauncher(
    IDocumentStore store,
    IWorktreeManager worktrees,
    IExecutor executor,
    RunSupervisor supervisor,
    IPullRequestInspector inspector,
    IOptions<DaemonOptions> options,
    ILogger<RunLauncher> logger)
{
    public async Task LaunchAsync(Guid taskId, Guid runId, Guid nodeId, Guid ownerId, int leaseGeneration, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(taskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);

        if (task is null || project is null)
        {
            logger.LogError("Cannot launch run {RunId}: task or project missing", runId);
            return;
        }

        try
        {
            if (task.PullRequestUrl.IsNotBlank()
                && await TryCloseOutMergedPullRequestAsync(task, project, taskId, runId, nodeId, cancellationToken))
            {
                return;
            }

            // A reopened task carries the branch of its existing PR: the follow-up run
            // resumes that branch instead of cutting a fresh one off the base (log #20).
            (string Branch, string PullRequestUrl)? followUp =
                task.FollowUpBranch.IsNotBlank() && task.PullRequestUrl.IsNotBlank()
                    ? (task.FollowUpBranch, task.PullRequestUrl)
                    : null;

            (Worktree worktree, bool resumesPreviousWork) = followUp is { } resume
                ? (await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, resume.Branch, taskId, runId),
                    cancellationToken), true)
                : await CheckoutFreshOrRetryAsync(task, project, taskId, runId, cancellationToken);

            Guid sessionId = DomainId.New();
            ExecutorMode mode = ExecutorMode.Subscription;

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, nodeId, ownerId, leaseGeneration, sessionId,
                worktree.Path, worktree.Branch, mode, DateTimeOffset.UtcNow,
                IsFollowUp: followUp is not null));
            await session.SaveChangesAsync(cancellationToken);

            // The reopen's kind picks the follow-up prompt; Unknown (reopens recorded
            // before the vocabulary existed) keeps the historic review-feedback meaning.
            // The commit style resolves project-over-platform (Decisions Log #26).
            CommitStyle commitStyle = CommitStyle.Resolve(project.CommitStyle, options.Value.DefaultCommitStyle);
            string prompt = followUp is { } review
                ? task.FollowUpKind == FollowUpKind.FailingChecks
                    ? AgentPromptBuilder.BuildFixChecks(task, project, worktree.Branch, review.PullRequestUrl, commitStyle)
                    : AgentPromptBuilder.BuildFollowUp(task, project, worktree.Branch, review.PullRequestUrl, commitStyle)
                : AgentPromptBuilder.Build(task, project, worktree.Branch, worktree.Path, resumesPreviousWork);
            SpawnedAgent agent = await executor.SpawnAsync(
                new AgentSpawnRequest(runId, sessionId, worktree.Path, prompt, mode, project.SkipPermissions),
                cancellationToken);

            await using IDocumentSession startSession = store.LightweightSession();
            startSession.Events.Append(runId, new RunProcessStarted(runId, agent.ProcessId, agent.StartedAt));
            startSession.Store(new RunActivity { Id = runId, LastActivityAt = DateTimeOffset.UtcNow, StreamBytesRead = 0 });
            await startSession.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(runId, taskId, agent.ProcessId, agent.StartedAt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Launch failed for run {RunId}", runId);
            await RecordLaunchFailureAsync(taskId, runId, exception.Message, cancellationToken);
        }
    }

    /// <summary>
    /// A task reaching dispatch with a pull-request URL is a requeue or reopen — and the
    /// PR may have merged while the task sat queued. Ask the provider before spawning:
    /// merged work closes out (the task completes with its PR, the lease releases, the
    /// workspace is cleaned) and is never rebuilt. Origin incident (2026-08-18): after
    /// PR #11 merged, the storm-killed generation 5's lease expiry requeued the task and
    /// generation 6 spawned a fresh agent to rebuild the feature already on main.
    /// Inspection failure (the network is often still down right after a wake) falls
    /// back to a normal dispatch rather than blocking the task.
    /// </summary>
    private async Task<bool> TryCloseOutMergedPullRequestAsync(
        TaskDetails task, ProjectDetails project, Guid taskId, Guid runId, Guid nodeId, CancellationToken cancellationToken)
    {
        string pullRequestUrl = task.PullRequestUrl!;
        int pullRequestNumber = PullRequestUrls.ParseNumber(pullRequestUrl);
        if (pullRequestNumber <= 0)
        {
            return false;
        }

        PullRequestSnapshot snapshot;
        try
        {
            snapshot = await inspector.InspectAsync(
                project.RepositoryPath, pullRequestUrl, pullRequestNumber, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not check pull request {Url} before dispatching run {RunId}; dispatching normally",
                pullRequestUrl, runId);
            return false;
        }

        if (!snapshot.IsMerged)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? aggregate = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (aggregate is null || aggregate.State != TaskState.Claimed || aggregate.CurrentRunId != runId)
        {
            return false;
        }

        session.Events.Append(taskId, TaskDecider.Complete(aggregate, runId, pullRequestUrl, now));
        session.Delete<TaskLease>(taskId);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Task {TaskId}: pull request {Url} already merged — closed out instead of dispatching run {RunId}",
            taskId, pullRequestUrl, runId);

        await CleanUpMergedWorkspaceAsync(task, project.RepositoryPath, taskId, nodeId, cancellationToken);
        return true;
    }

    /// <summary>
    /// The launch-time twin of CloseoutEngine.CompleteCloseoutAsync's cleanup: the dead
    /// generation's retained worktree still has the merged branch checked out, so remove
    /// this node's worktrees for the task first, then delete the branch everywhere it
    /// lingers. Best-effort — the merge already closed the story.
    /// </summary>
    private async Task CleanUpMergedWorkspaceAsync(
        TaskDetails task, string repositoryPath, Guid taskId, Guid nodeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunDetails> previousRuns;
        await using (IQuerySession query = store.QuerySession())
        {
            previousRuns = await query.Query<RunDetails>()
                .Where(r => r.TaskId == taskId && r.NodeId == nodeId)
                .ToListAsync(cancellationToken);
        }

        foreach (RunDetails previous in previousRuns.Where(r => r.WorktreePath.IsNotBlank() && Directory.Exists(r.WorktreePath)))
        {
            try
            {
                await worktrees.RemoveAsync(repositoryPath, previous.WorktreePath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Worktree removal failed for {Path} (safe to prune later)", previous.WorktreePath);
            }
        }

        string? branch = task.FollowUpBranch.IsNotBlank()
            ? task.FollowUpBranch
            : task.RetryBranch.IsNotBlank()
                ? task.RetryBranch
                : previousRuns.OrderByDescending(r => r.DispatchedAt).FirstOrDefault()?.Branch;
        if (branch.IsBlank())
        {
            return;
        }

        try
        {
            await worktrees.DeleteBranchEverywhereAsync(repositoryPath, branch, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Branch cleanup failed for {Branch} (safe to delete by hand)", branch);
        }
    }

    /// <summary>
    /// A retried task resumes its failed run's branch through the same checkout path
    /// follow-up runs use — the retained worktree, or a fresh worktree on the surviving
    /// branch. When the branch is gone everywhere, the retry starts clean from the base
    /// branch instead of failing the run (Decisions Log #25). The flag reports which
    /// path won, so the prompt tells a resuming agent to review the previous attempt's
    /// work — possibly uncommitted in the retained worktree — before starting over.
    /// </summary>
    private async Task<(Worktree Worktree, bool ResumesPreviousWork)> CheckoutFreshOrRetryAsync(
        TaskDetails task, ProjectDetails project, Guid taskId, Guid runId, CancellationToken cancellationToken)
    {
        if (task.RetryBranch.IsNotBlank())
        {
            try
            {
                return (await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, task.RetryBranch, taskId, runId),
                    cancellationToken), true);
            }
            catch (WorktreeException exception)
            {
                logger.LogInformation(
                    "Retry of task {TaskId} cannot resume branch {Branch} ({Reason}); starting clean from {BaseBranch}",
                    taskId, task.RetryBranch, exception.Message, project.BaseBranch);
            }
        }

        return (await worktrees.CreateAsync(
            new WorktreeRequest(project.RepositoryPath, project.BaseBranch, taskId, runId, task.Objective),
            cancellationToken), false);
    }

    private async Task RecordLaunchFailureAsync(Guid taskId, Guid runId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                session.Events.Append(runId, new RunFailed(runId, reason, DateTimeOffset.UtcNow));
            }

            TaskAggregate? task =
                await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
            if (task is not null && TaskDecider.CanFail(task))
            {
                session.Events.Append(taskId, TaskDecider.Fail(
                    task, runId, $"Launch failed: {reason}", DateTimeOffset.UtcNow));
            }

            session.Delete<TaskLease>(taskId);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record launch failure for run {RunId}", runId);
        }
    }
}
