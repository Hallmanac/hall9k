using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Documents;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Ids;
using Marten;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Spawns the "--resume" leg of a primary-session recovery: the exact process-spawn, session-
/// naming, and isolation mechanics token-budget recovery already uses
/// (<see cref="TokenBudgetRetryEngine"/>), factored out here so a second caller — the short-
/// backoff error-result retry (task: a session that reports an error result is retried once in
/// place, <see cref="RunSupervisor"/>) — spawns an identical resumed session rather than a
/// second, slightly different copy of the same rules. Neither caller may depend on the other
/// directly (<see cref="TokenBudgetRetryEngine"/> already depends on <see cref="RunSupervisor"/>
/// to re-enter the review loop), so this sits underneath both.
/// </summary>
public sealed class PrimarySessionResumer(IExecutor executor)
{
    /// <summary>
    /// Spawns the resumed session and appends its <see cref="RunResumed"/> milestone to
    /// <paramref name="session"/> — the caller's own transaction, so it lands atomically beside
    /// whatever else that caller is recording (a budget park's retry, or a session-error retry's
    /// own <see cref="RunSessionErrorRetried"/>) rather than in a second round trip. The caller
    /// still owns <c>SaveChangesAsync</c>, the <see cref="RunActivity"/> stream-cursor reset that
    /// a stdout-redirect truncation demands, and <c>RunSupervisor.StartMonitoring</c>.
    /// </summary>
    public async Task<SpawnedAgent> ResumeAsync(
        IDocumentSession session, RunDetails run, TaskDetails task, ProjectDetails project, string prompt,
        CancellationToken cancellationToken)
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
        // first place.
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
            run.Id, DomainId.New(), run.WorktreePath, run.RunDirectory, prompt,
            run.ExecutorMode, run.Model, project.SkipPermissions,
            ResumeSessionId: run.SessionId, UntrustedWorkingDirectory: task.Type == TaskType.PrReview)
        {
            SessionName = sessionName,
        }, cancellationToken);

        // The retry's stdout redirect truncates the run's stream file fresh (log #2), so the
        // tail cursor has to restart at zero with it — otherwise the monitor seeks to an offset
        // the new file has not grown to yet.
        session.Store(new RunActivity
        {
            Id = run.Id,
            LastActivityAt = DateTimeOffset.UtcNow,
            StreamBytesRead = 0,
        });
        session.Events.Append(
            run.Id, new RunResumed(run.Id, agent.ProcessId, agent.StartedAt, DateTimeOffset.UtcNow, sessionName));

        return agent;
    }
}
