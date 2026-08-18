using Hall9k.Daemon.Worktrees;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Turns a fresh claim into a live agent: worktree → RunDispatched → spawn →
/// RunProcessStarted → monitor. Failures at any step fail the run and task honestly.
/// </summary>
public sealed class RunLauncher(
    IDocumentStore store,
    IWorktreeManager worktrees,
    IExecutor executor,
    RunSupervisor supervisor,
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
            // A reopened task carries the branch of its existing PR: the follow-up run
            // resumes that branch instead of cutting a fresh one off the base (log #20).
            (string Branch, string PullRequestUrl)? followUp =
                task.FollowUpBranch.IsNotBlank() && task.PullRequestUrl.IsNotBlank()
                    ? (task.FollowUpBranch, task.PullRequestUrl)
                    : null;

            Worktree worktree = followUp is { } resume
                ? await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, resume.Branch, taskId, runId),
                    cancellationToken)
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
            string prompt = followUp is { } review
                ? task.FollowUpKind == Domain.Features.Tasks.FollowUpKind.FailingChecks
                    ? AgentPromptBuilder.BuildFixChecks(task, project, worktree.Branch, review.PullRequestUrl)
                    : AgentPromptBuilder.BuildFollowUp(task, project, worktree.Branch, review.PullRequestUrl)
                : AgentPromptBuilder.Build(task, project, worktree.Branch, worktree.Path);
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
    /// A retried task resumes its failed run's branch through the same checkout path
    /// follow-up runs use — the retained worktree, or a fresh worktree on the surviving
    /// branch. When the branch is gone everywhere, the retry starts clean from the base
    /// branch instead of failing the run (Decisions Log #25).
    /// </summary>
    private async Task<Worktree> CheckoutFreshOrRetryAsync(
        TaskDetails task, ProjectDetails project, Guid taskId, Guid runId, CancellationToken cancellationToken)
    {
        if (task.RetryBranch.IsNotBlank())
        {
            try
            {
                return await worktrees.CheckoutExistingAsync(
                    new FollowUpWorktreeRequest(project.RepositoryPath, task.RetryBranch, taskId, runId),
                    cancellationToken);
            }
            catch (WorktreeException exception)
            {
                logger.LogInformation(
                    "Retry of task {TaskId} cannot resume branch {Branch} ({Reason}); starting clean from {BaseBranch}",
                    taskId, task.RetryBranch, exception.Message, project.BaseBranch);
            }
        }

        return await worktrees.CreateAsync(
            new WorktreeRequest(project.RepositoryPath, project.BaseBranch, taskId, runId, task.Objective),
            cancellationToken);
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

            Domain.Features.Tasks.TaskAggregate? task =
                await session.Events.AggregateStreamAsync<Domain.Features.Tasks.TaskAggregate>(taskId, token: cancellationToken);
            if (task is not null && !task.State.IsTerminal)
            {
                session.Events.Append(taskId, Domain.Features.Tasks.Handlers.TaskDecider.Fail(
                    task, runId, $"Launch failed: {reason}", DateTimeOffset.UtcNow));
            }

            session.Delete<Domain.Features.Tasks.Documents.TaskLease>(taskId);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to record launch failure for run {RunId}", runId);
        }
    }
}
