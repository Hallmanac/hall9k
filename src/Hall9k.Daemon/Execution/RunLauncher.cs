using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Closeout;
using Hall9k.Daemon.Dispatch;
using Hall9k.Daemon.ProjectHomes;
using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project;
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
using Hall9k.Domain.Features.Tasks.Rendering;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Infrastructure.Storage;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;
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
    BlockerContextAssembler blockerContext,
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

        // The generation fence, checked before any worktree checkout or spawn rather than
        // only on the already-merged path below (Copilot review, PR #30): a reclaim during
        // dispatch — a requeued lease's expiry sweep and a startup adoption both landing in
        // the same window, the origin incident GenerationFence documents — must stop this
        // stale generation here, or it checks out a worktree and spawns a second live agent
        // for a task a fresh generation already owns.
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, "to launch", cancellationToken))
        {
            return;
        }

        try
        {
            if (task.PullRequestUrl.IsNotBlank()
                && await TryCloseOutMergedPullRequestAsync(
                    task, project, taskId, runId, nodeId, leaseGeneration, cancellationToken))
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
            // Resolved once, here, and carried to both the spawn and the record: the model
            // the run is dispatched on and the model it actually runs on are the same fact
            // (Decisions Log #33), so they can never disagree.
            AgentModel model = options.Value.ResolveModel(AgentRole.Build, task.Model, project.Model);

            // Resolved once, here, exactly like the worktree above: this run's directory is
            // under the task's own directory when the project has a home (backlog 49), and
            // every consumer reads the recorded value from here on rather than rederiving it.
            //
            // The task's directory NAME, though, is read from disk rather than freshly computed
            // from the task's live objective (adversarial review, cycle 1): the doorbell-woken
            // render sweep owns renaming that directory when a revision changes its slug, and it
            // runs on its own schedule, not synchronously with dispatch. Trusting the freshly
            // computed name here would, on a revise-then-redispatch landing ahead of that sweep,
            // create the not-yet-renamed directory itself — stranding the true, already-populated
            // one under its old name as an orphan the next reconciliation pass only marks, never
            // merges. Resolving against whatever directory already exists for this task keeps the
            // run pointed at the one directory that is actually there; the eventual sweep still
            // renames it, runs/ and all, exactly as it always has.
            string taskDirectoryName = project.HomeDirectory.HasValue
                && HomeEntryWriter.FindExistingDirectory(
                    ProjectHomePaths.TasksDirectory(project.HomeDirectory.Value), task.Id) is { } existingTaskDirectory
                    ? Path.GetFileName(existingTaskDirectory)
                    : TaskDocumentRenderer.DirectoryName(task);
            string runDirectory = RunPaths.ResolveDirectory(project.HomeDirectory, taskDirectoryName, runId);

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, nodeId, ownerId, leaseGeneration, sessionId,
                worktree.Path, worktree.Branch, mode, DateTimeOffset.UtcNow,
                IsFollowUp: followUp is not null, Model: model, RunDirectory: runDirectory));
            await session.SaveChangesAsync(cancellationToken);

            // The reopen's kind picks the follow-up prompt; Unknown (reopens recorded
            // before the vocabulary existed) keeps the historic review-feedback meaning.
            // The commit style resolves project-over-platform (Decisions Log #26).
            CommitStyle commitStyle = CommitStyle.Resolve(project.CommitStyle, options.Value.DefaultCommitStyle);
            string prompt;
            if (followUp is { } review)
            {
                // A follow-up resumes work the original session already did with its blockers'
                // context in hand; re-routing it now would pay for a second synthesis to tell
                // the agent what it was told the first time. Its job is the review feedback.
                prompt = task.FollowUpKind == FollowUpKind.FailingChecks
                    ? AgentPromptBuilder.BuildFixChecks(task, project, worktree.Branch, review.PullRequestUrl, commitStyle)
                    : AgentPromptBuilder.BuildFollowUp(task, project, worktree.Branch, review.PullRequestUrl, commitStyle);
            }
            else
            {
                // Context routing (Decisions Log #36). The run stream exists by now, so the
                // synthesis pass has somewhere to record itself, and the build session has not
                // spawned yet — the whole point is that the dependent starts already knowing.
                string? handoffs = await blockerContext.AssembleAsync(
                    runId, runDirectory, task, project, worktree.Path, mode, cancellationToken);
                prompt = AgentPromptBuilder.Build(
                    task, project, worktree.Branch, worktree.Path, resumesPreviousWork, handoffs);
            }

            SpawnedAgent agent = await executor.SpawnAsync(
                new AgentSpawnRequest(
                    runId, sessionId, worktree.Path, runDirectory, prompt, mode, model, project.SkipPermissions),
                cancellationToken);

            await using IDocumentSession startSession = store.LightweightSession();
            startSession.Events.Append(runId, new RunProcessStarted(runId, agent.ProcessId, agent.StartedAt));
            startSession.Store(new RunActivity { Id = runId, LastActivityAt = DateTimeOffset.UtcNow, StreamBytesRead = 0 });
            await startSession.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(runId, runDirectory, taskId, agent.ProcessId, agent.StartedAt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Launch failed for run {RunId}", runId);
            await RecordLaunchFailureAsync(taskId, runId, leaseGeneration, exception.Message, cancellationToken);
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
    /// back to a normal dispatch rather than blocking the task. True means "do not
    /// dispatch": either this run closed the task out, or the PR is merged but this run's
    /// generation is stale, in which case the live generation owns the task and this run
    /// must not fall through to a fresh dispatch either.
    /// </summary>
    private async Task<bool> TryCloseOutMergedPullRequestAsync(
        TaskDetails task, ProjectDetails project, Guid taskId, Guid runId, Guid nodeId, int leaseGeneration,
        CancellationToken cancellationToken)
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
        if (!await GenerationFence.AllowsAsync(
            session, logger, taskId, runId, leaseGeneration, nameof(TaskCompleted), cancellationToken))
        {
            // The PR is merged, but this run is no longer its task's current generation —
            // the live generation owns closing this out (or already has). Reporting this as
            // "not merged" would send the caller to CheckoutFreshOrRetryAsync and spawn a
            // fresh agent to rebuild work already on main: the exact origin incident above.
            // A stale generation must never write task state, but it must also never fall
            // through to a dispatch it has no right to make.
            logger.LogInformation(
                "Task {TaskId}: pull request {Url} already merged but run {RunId} is a stale generation; skipping dispatch without closing out",
                taskId, pullRequestUrl, runId);
            return true;
        }

        (TaskAggregate Task, long Version)? fenced = await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
        if (fenced is not { } current
            || current.Task.State != TaskState.Claimed || current.Task.CurrentRunId != runId)
        {
            return false;
        }

        session.Events.Append(
            taskId, expectedVersion: current.Version + 1, TaskDecider.Complete(current.Task, runId, pullRequestUrl, now));
        session.Delete<TaskLease>(taskId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            // A claim landed between the version above and this commit — the live
            // generation now owns the task, so this run must stop exactly like the
            // !AllowsAsync branch above: true means "do not dispatch", never fall
            // through to CheckoutFreshOrRetryAsync and spawn a second agent.
            logger.LogInformation(
                "Task {TaskId}: lost the generation race closing out pull request {Url} — a newer claim committed first; skipping dispatch",
                taskId, pullRequestUrl);
            return true;
        }

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

    private async Task RecordLaunchFailureAsync(
        Guid taskId, Guid runId, int leaseGeneration, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            if (await session.Events.FetchStreamStateAsync(runId, cancellationToken) is not null)
            {
                session.Events.Append(runId, new RunFailed(runId, reason, DateTimeOffset.UtcNow));
            }

            // LoadFencedAsync's read must happen before the AllowsAsync identity check below
            // — not after — so a reclaim landing between the two is caught by AllowsAsync's
            // fresh read rather than baked into `current.Task` as an already-stale ownership
            // fact that AllowsAsync never gets asked about (adversarial review, cycle 2).
            (TaskAggregate Task, long Version)? fenced =
                await GenerationFence.LoadFencedAsync(session, taskId, cancellationToken);
            if (!await GenerationFence.AllowsAsync(
                session, logger, taskId, runId, leaseGeneration, nameof(TaskFailed), cancellationToken))
            {
                await session.SaveChangesAsync(cancellationToken);
                return;
            }

            if (fenced is { } current && TaskDecider.CanFail(current.Task))
            {
                session.Events.Append(taskId, expectedVersion: current.Version + 1, TaskDecider.Fail(
                    current.Task, runId, $"Launch failed: {reason}", DateTimeOffset.UtcNow));
            }

            session.Delete<TaskLease>(taskId);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (EventStreamUnexpectedMaxEventIdException)
            {
                // A claim landed between LoadFencedAsync and this commit; the live
                // generation owns the task now and this stale failure must not land.
                logger.LogInformation(
                    "Task {TaskId}: lost the generation race recording a launch failure — a newer claim committed first",
                    taskId);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record launch failure for run {RunId}", runId);
        }
    }
}
