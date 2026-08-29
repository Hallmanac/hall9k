using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.JiraWrites;

/// <summary>One sweep's tally: how many stuck writes this node re-attempted, and how many of those finally went through.</summary>
public sealed record JiraWriteRetrySweepResult(int Retried, int Succeeded);

/// <summary>
/// What makes an expired or missing twg login a handled state rather than a lost write (Brian's
/// design, 2026-08-28): a Jira write that failed to authenticate stays recorded as pending on its
/// task (<c>TaskAggregate.PendingJiraWriteIsAuthFailure</c>), and this engine periodically
/// re-attempts the identical payload through <see cref="JiraWriteCoordinator.RetryPendingAsync"/>
/// — no doorbell, because nothing on this machine observes the moment <c>twg login</c> succeeds,
/// so a patient poll (<see cref="DaemonOptions.JiraWriteRetryInterval"/>) is the whole mechanism,
/// the same shape <c>TokenBudgetRetryEngine</c> already uses for a clock nobody can ring a bell on.
/// <para>
/// Covers every caller equally: an operator's own <c>h9k task write-jira</c> and closeout's own
/// merge comment both leave the identical pending marker on the task when twg refuses to
/// authenticate, and this sweep does not care which one composed the payload it is retrying.
/// </para>
/// </summary>
public sealed class JiraWriteRetryEngine(
    IDocumentStore store,
    ProcessRunner twgRunner,
    IOptions<DaemonOptions> options,
    ILogger<JiraWriteRetryEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    public async Task<JiraWriteRetrySweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDetails> pending;
        await using (IQuerySession query = store.QuerySession())
        {
            pending = await query.Query<TaskDetails>()
                .Where(task => task.PendingJiraWriteIsAuthFailure)
                .ToListAsync(cancellationToken);
        }

        int retried = 0;
        int succeeded = 0;
        foreach (TaskDetails task in pending)
        {
            try
            {
                await using IDocumentSession session = store.LightweightSession();
                ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
                if (project is null)
                {
                    logger.LogWarning(
                        "Task {TaskId} has a Jira write pending but its project is not registered on this "
                        + "node; leaving it for a node that has it", task.Id);
                    continue;
                }

                JiraWriteAttemptResult? result = await JiraWriteCoordinator.RetryPendingAsync(
                    session, task.Id, project.JiraProjectKey, new TwgJiraExecutor(twgRunner),
                    project.RepositoryPath, cancellationToken);
                if (result is null)
                {
                    // Resolved by something else between the read above and this attempt — a
                    // second node's sweep, or an operator's own retry — so there is nothing left
                    // here to do.
                    continue;
                }

                retried++;
                switch (result.Outcome)
                {
                    case JiraWriteOutcome.Succeeded:
                        succeeded++;
                        logger.LogInformation(
                            "Task {TaskId}: the pending Jira write went through on retry ({IssueKey})",
                            task.Id, result.IssueKey);
                        break;
                    case JiraWriteOutcome.Failed:
                        logger.LogWarning(
                            "Task {TaskId}: the pending Jira write failed on retry for a reason other than "
                            + "authentication and needs a freshly composed write: {Reason}",
                            task.Id, result.Message);
                        break;
                    case JiraWriteOutcome.PendingAuthentication:
                    default:
                        // Still stuck; the next sweep tries again. Logged at debug rather than
                        // warning, since a login left unattended for a while is the ordinary case
                        // this whole design expects, not a fault worth an operator's attention
                        // beyond the h9k status row already surfacing it.
                        logger.LogDebug("Task {TaskId}: the pending Jira write is still not authenticated", task.Id);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Jira write retry failed for task {TaskId}; leaving it pending", task.Id);
            }
        }

        return new JiraWriteRetrySweepResult(retried, succeeded);
    }
}
