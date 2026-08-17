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
                : await worktrees.CreateAsync(
                    new WorktreeRequest(project.RepositoryPath, project.BaseBranch, taskId, runId, task.Objective),
                    cancellationToken);

            Guid sessionId = DomainId.New();
            ExecutorMode mode = ExecutorMode.Subscription;

            session.Events.StartStream<RunAggregate>(runId, new RunDispatched(
                runId, taskId, nodeId, ownerId, leaseGeneration, sessionId,
                worktree.Path, worktree.Branch, mode, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);

            string prompt = followUp is { } review
                ? AgentPromptBuilder.BuildFollowUp(task, project, worktree.Branch, review.PullRequestUrl)
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
