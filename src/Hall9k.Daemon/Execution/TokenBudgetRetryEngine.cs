using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// The other half of budget-exhaustion recovery (Decisions Log #40): <c>RunSupervisor</c>
/// parks a run whose result carried the usage-limit shape, and this is what un-parks it. The
/// window resets on the clock, not on an event the platform can watch for, so a patient poll
/// — driven by <c>TokenBudgetRetryMonitor</c> — is the whole mechanism.
/// <para>
/// A retry resumes the same Claude session in the same worktree (<c>--resume</c>, the log #5
/// pattern) rather than starting a fresh one: the point of parking instead of failing was
/// that the work — including whatever the agent was mid-thought on — is intact, and a fresh
/// session would throw that away for no reason the exhaustion itself gives.
/// </para>
/// </summary>
public sealed class TokenBudgetRetryEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    RunSupervisor supervisor,
    ILogger<TokenBudgetRetryEngine> logger)
{
    /// <summary>Every budget-parked run this node owns, retried once. Returns how many actually resumed.</summary>
    public async Task<int> RetryParkedRunsAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession query = store.QuerySession();
        Guid nodeId = node.NodeId;
        IReadOnlyList<RunDetails> parked = await query.Query<RunDetails>()
            .Where(r => r.NodeId == nodeId)
            .Where(r => r.MatchesSql("d.data ->> 'state' = ?", RunState.BudgetParked.Value))
            .ToListAsync(cancellationToken);

        int retried = 0;
        foreach (RunDetails run in parked)
        {
            if (await RetryOneAsync(run, cancellationToken))
            {
                retried++;
            }
        }

        return retried;
    }

    private async Task<bool> RetryOneAsync(RunDetails run, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskDetails? task = await session.LoadAsync<TaskDetails>(run.TaskId, cancellationToken);
        ProjectDetails? project = task is null
            ? null
            : await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
        if (task is null || project is null || task.State != TaskState.Claimed || task.CurrentRunId != run.Id)
        {
            // The claim moved on — a human intervened, or a later generation took the task
            // — so there is nothing here left to resume.
            return false;
        }

        try
        {
            SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
                run.Id, DomainId.New(), run.WorktreePath, AgentPromptBuilder.BuildBudgetRetry(),
                run.ExecutorMode, run.Model, project.SkipPermissions,
                ResumeSessionId: run.SessionId), cancellationToken);

            // The retry's stdout redirect truncates the run's stream file fresh (log #2),
            // so the tail cursor has to restart at zero with it — otherwise the monitor
            // seeks to an offset the new file has not grown to yet.
            session.Store(new RunActivity
            {
                Id = run.Id,
                LastActivityAt = DateTimeOffset.UtcNow,
                StreamBytesRead = 0,
            });
            session.Events.Append(run.Id, new RunResumed(run.Id, agent.ProcessId, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(run.Id, run.TaskId, agent.ProcessId, agent.StartedAt, cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retried after token-budget exhaustion (pid {ProcessId})", run.Id, agent.ProcessId);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Run {RunId}: token-budget retry failed; will retry next sweep", run.Id);
            return false;
        }
    }
}
