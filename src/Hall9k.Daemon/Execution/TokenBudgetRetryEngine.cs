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
/// The other half of budget-exhaustion recovery (backlog 40): <c>RunSupervisor</c> and
/// <c>ReviewEngine</c> park a run whose result carried the usage-limit shape, and this is what
/// un-parks it. The window resets on the clock, not on an event the platform can watch for, so
/// a patient poll — driven by <c>TokenBudgetRetryMonitor</c> — is the whole mechanism.
/// <para>
/// A run parked mid-primary-session resumes the same Claude session in the same worktree
/// (<c>--resume</c>, the log #5 pattern) rather than starting a fresh one: the point of parking
/// instead of failing was that the work — including whatever the agent was mid-thought on — is
/// intact, and a fresh session would throw that away for no reason the exhaustion itself gives.
/// </para>
/// <para>
/// A run parked mid-review-loop (a review pass or the fix session hit the limit instead) is
/// different: <c>RunAggregate.Apply(RunBudgetExhausted)</c> already cleared the exhausted leg
/// when the park landed, so there is no session left to resume — the retry is
/// <see cref="RunSupervisor.ResumeReviewLoop"/> re-entering the loop, which redispatches it
/// fresh over the same cycle. <see cref="ReviewPhase"/> on the reloaded aggregate is what tells
/// the two cases apart, since it is <see cref="ReviewPhase.None"/> for every run that never
/// entered the loop.
/// </para>
/// </summary>
public sealed class TokenBudgetRetryEngine(
    IDocumentStore store,
    NodeContext node,
    IExecutor executor,
    RunSupervisor supervisor,
    ILogger<TokenBudgetRetryEngine> logger)
{
    /// <summary>
    /// Every budget-parked run this node owns, retried once — a plain node-only filter, unlike
    /// RunSupervisor.ResumeStrandedPipelinesAsync's provenance comment once claimed: h9k task
    /// deliver records the delivering node's own id on AgentSessionCompleted (Decisions Log
    /// #103), so an interactively delivered run parked mid review loop already carries a real
    /// node id by the time it can reach BudgetParked, and DeliveredByNodeId is new alongside
    /// this feature, so no pre-fix stream with an empty NodeId here can exist to widen for
    /// (conformance review, cycle 4). Returns how many actually resumed.
    /// </summary>
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

        RunAggregate aggregate = await session.Events.AggregateStreamAsync<RunAggregate>(run.Id, token: cancellationToken)
            ?? throw new InvalidOperationException($"Run {run.Id} is budget-parked with no stream to resume.");
        if (aggregate.ReviewPhase != ReviewPhase.None)
        {
            // The park caught a review pass or the fix session, not the primary agent: the
            // exhausted leg was already cleared when RunBudgetExhausted landed, so there is no
            // process to --resume — re-entering the loop redispatches it fresh (backlog 40).
            supervisor.ResumeReviewLoop(run, cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retried after token-budget exhaustion mid-review — resuming the review loop", run.Id);
            return true;
        }

        if (task.Type == TaskType.PrReview && aggregate.PrReviewConformanceBudgetExhausted)
        {
            // The pr-review task's own loop never touches ReviewPhase (PrReviewEngine's class
            // doc comment explains why), so this is its equivalent of the branch above: the
            // park caught the conformance lens, not the primary adversarial session, and there
            // is no process left to --resume — PrReviewEngine.ReviewAsync redispatches it fresh.
            supervisor.ResumeReviewLoop(run, cancellationToken);
            logger.LogInformation(
                "Run {RunId}: retried after token-budget exhaustion mid-pr-review-conformance — resuming the review loop", run.Id);
            return true;
        }

        try
        {
            // A pr-review task's primary session is the adversarial lens reading another
            // contributor's pull-request head (RunLauncher's UntrustedWorkingDirectory), so a
            // resume of that same session carries the same distrust forward — otherwise the
            // resumed --resume spawn would load the foreign checkout's own .claude/ config
            // and CLAUDE.md/AGENTS.md under the owner's credentials (adversarial review, cycle 2).
            // Reuses the primary session's own recorded name (RunDispatched.SessionName) rather
            // than re-deriving it: a resume re-enters the same session, so it keeps the same
            // name it was dispatched under. A stream written before that field existed falls
            // back to the identical three-way split RunLauncher used to pick the name in the
            // first place — run.IsFollowUp and task.FollowUpKind are both already loaded here
            // for the isolation flag above, so recovering the role costs nothing extra.
            string sessionRole = task.Type == TaskType.PrReview
                ? SessionRoleName.ReviewAdversarial(1)
                : run.IsFollowUp
                    ? task.FollowUpKind == FollowUpKind.FailingChecks
                        ? SessionRoleName.Checks
                        : task.FollowUpKind == FollowUpKind.Rebase
                            ? SessionRoleName.Rebase
                            : SessionRoleName.Build
                    : SessionRoleName.Build;
            string sessionName = run.SessionName.IsNotBlank()
                ? run.SessionName
                : SessionRoleName.For(DomainId.Short(run.TaskId), sessionRole);
            SpawnedAgent agent = await executor.SpawnAsync(new AgentSpawnRequest(
                run.Id, DomainId.New(), run.WorktreePath, run.RunDirectory, AgentPromptBuilder.BuildBudgetRetry(),
                run.ExecutorMode, run.Model, project.SkipPermissions,
                ResumeSessionId: run.SessionId, UntrustedWorkingDirectory: task.Type == TaskType.PrReview)
            {
                SessionName = sessionName,
            }, cancellationToken);

            // The retry's stdout redirect truncates the run's stream file fresh (log #2),
            // so the tail cursor has to restart at zero with it — otherwise the monitor
            // seeks to an offset the new file has not grown to yet.
            session.Store(new RunActivity
            {
                Id = run.Id,
                LastActivityAt = DateTimeOffset.UtcNow,
                StreamBytesRead = 0,
            });
            session.Events.Append(
                run.Id, new RunResumed(run.Id, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, sessionName));
            await session.SaveChangesAsync(cancellationToken);

            supervisor.StartMonitoring(
                run.Id, run.RunDirectory, run.TaskId, agent.ProcessId, agent.StartedAt, cancellationToken);
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
