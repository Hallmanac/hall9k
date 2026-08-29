using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Linq.MatchesSql;

namespace Hall9k.Daemon.JiraWrites;

/// <summary>
/// One sweep's tally: how many stuck writes this node re-attempted, how many of those finally
/// went through, and how many queued merge notices (<see cref="JiraWriteRetryEngine.PollOnceAsync"/>'s
/// own doc comment) it drained.
/// </summary>
public sealed record JiraWriteRetrySweepResult(int Retried, int Succeeded, int MergeNoticesDrained = 0);

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
    NodeContext node,
    ProcessRunner twgRunner,
    ILogger<JiraWriteRetryEngine> logger)
{
    /// <summary>
    /// Two independent things this sweep drains, both left behind by a write that could not run
    /// immediately: a write itself, stuck on an expired or missing twg login
    /// (<see cref="TaskDetails.PendingJiraWriteIsAuthFailure"/>), and closeout's own merge notice,
    /// queued because another write was already outstanding when the merge was observed
    /// (<see cref="TaskDetails.HasQueuedJiraMergeNotice"/>, set by
    /// <c>CloseoutEngine.QueueJiraMergeNoticeAsync</c>). A queued notice is only ready once
    /// <see cref="TaskDetails.PendingJiraWriteId"/> is clear — while it is still set, either the
    /// auth-failure retry above will eventually clear it, or a fresh write is still resolving, and
    /// either way a second write in flight would race twg against itself.
    /// </summary>
    public async Task<JiraWriteRetrySweepResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TaskDetails> pending;
        IReadOnlyList<TaskDetails> queuedMergeNotices;
        await using (IQuerySession query = store.QuerySession())
        {
            // Abandoned is excluded on purpose (independent pre-PR review, cycle 5):
            // TaskDecider.RequestJiraWrite now refuses a fresh write against an abandoned task,
            // but a write that was already pending when the human abandoned it is not itself
            // undone by that guard, and this sweep has no lifecycle filter of its own — without
            // one, a stuck write on dead work retries forever, invisible on the attention pane
            // (AttentionComposer reports TaskAttention.None for an archived task), and files a
            // real card the moment twg login next succeeds, for work nobody intends to do.
            pending = await query.Query<TaskDetails>()
                .Where(task => task.PendingJiraWriteIsAuthFailure)
                .Where(task => task.MatchesSql("d.data ->> 'state' != ?", TaskState.Abandoned.Value))
                .ToListAsync(cancellationToken);

            queuedMergeNotices = await query.Query<TaskDetails>()
                .Where(task => task.HasQueuedJiraMergeNotice && task.PendingJiraWriteId == null)
                .Where(task => task.MatchesSql("d.data ->> 'state' != ?", TaskState.Abandoned.Value))
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

        int drained = 0;
        foreach (TaskDetails task in queuedMergeNotices)
        {
            try
            {
                await using IDocumentSession session = store.LightweightSession();
                ProjectDetails? project = await session.LoadAsync<ProjectDetails>(task.ProjectId, cancellationToken);
                if (project is null)
                {
                    logger.LogWarning(
                        "Task {TaskId} has a Jira merge notice queued but its project is not registered on "
                        + "this node; leaving it for a node that has it", task.Id);
                    continue;
                }

                if (await DrainMergeNoticeAsync(session, task.Id, project, cancellationToken))
                {
                    drained++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Jira merge-notice drain failed for task {TaskId}; leaving it queued", task.Id);
            }
        }

        return new JiraWriteRetrySweepResult(retried, succeeded, drained);
    }

    /// <summary>
    /// Attempts the queued merge comment now that nothing else is outstanding on the task, then
    /// clears the queue marker regardless of what the attempt came to — a permanent failure or a
    /// fresh auth-pending write both land on the ordinary Jira write event trail, which is where a
    /// human or the next sweep finds them, and re-queuing the same notice on top would only mean
    /// two records of the same intent.
    /// </summary>
    private async Task<bool> DrainMergeNoticeAsync(
        IDocumentSession session, Guid taskId, ProjectDetails project, CancellationToken cancellationToken)
    {
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task is null || !task.HasQueuedJiraMergeNotice || task.PendingJiraWriteId is not null)
        {
            // Resolved, or blocked again, by something else between the read above and this
            // attempt — a second node's sweep, or a fresh write an operator just submitted.
            return false;
        }

        if (task.ExternalReference is not { } reference || reference.Provider != WorkItemProvider.Jira)
        {
            // Nothing left to tell — the task was relinked away from Jira, or never carried a
            // reference at all. Drain the marker anyway so it does not spin this sweep forever
            // over a notice that can never be delivered.
            session.Events.Append(taskId, TaskDecider.RecordJiraMergeNoticeAttempted(task, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
            return false;
        }

        JiraWriteAttemptResult result = await JiraWriteCoordinator.SubmitAsync(
            session,
            taskId,
            JiraWriteOperation.Comment,
            reference.Reference,
            new JiraWritePayload(WorkItemType: null, Fields: null, Comment: CloseoutEngine.MergeComment(project, task)),
            project.JiraProjectKey,
            node.OwnerId,
            new TwgJiraExecutor(twgRunner),
            project.RepositoryPath,
            cancellationToken);

        // Re-aggregate: SubmitAsync appended and saved its own events on this session, so the
        // in-memory task above is stale by the time the queue marker itself needs clearing.
        TaskAggregate refreshed = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? task;
        if (refreshed.HasQueuedJiraMergeNotice)
        {
            session.Events.Append(taskId, TaskDecider.RecordJiraMergeNoticeAttempted(refreshed, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(cancellationToken);
        }

        switch (result.Outcome)
        {
            case JiraWriteOutcome.Succeeded:
                logger.LogInformation(
                    "Task {TaskId}: the queued merge notice for {Reference} went through ({IssueKey})",
                    taskId, reference, result.IssueKey);
                return true;
            case JiraWriteOutcome.PendingAuthentication:
                logger.LogWarning(
                    "Task {TaskId}: the queued merge notice for {Reference} is pending — twg is not "
                    + "authenticated. It retries automatically once 'twg login' runs", taskId, reference);
                return true;
            default:
                logger.LogWarning(
                    "Task {TaskId}: the queued merge notice for {Reference} failed: {Reason}",
                    taskId, reference, result.Message);
                return true;
        }
    }
}
