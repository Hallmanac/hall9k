using Hall9k.Connectors.Processes;
using Hall9k.Connectors.WorkItems;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Marten.Events;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.JiraWrites;

/// <summary>
/// One sweep's tally: how many stuck writes this node re-attempted, how many of those finally
/// went through, how many queued merge notices (<see cref="JiraWriteRetryEngine.PollOnceAsync"/>'s
/// own doc comment) it drained, and how many writes it ended on
/// <see cref="DaemonOptions.PendingJiraWriteCeiling"/> alone because nothing was working on them
/// any more.
/// </summary>
public sealed record JiraWriteRetrySweepResult(int Retried, int Succeeded, int MergeNoticesDrained = 0, int Expired = 0);

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
    IOptions<DaemonOptions> options,
    ILogger<JiraWriteRetryEngine> logger)
{
    private readonly DaemonOptions _options = options.Value;

    /// <summary>
    /// Test-only seam (independent pre-PR review, cycle 6): runs, when set, immediately before
    /// <see cref="DrainMergeNoticeAsync"/> calls <see cref="JiraWriteCoordinator.SubmitAsync"/> —
    /// the narrow window after this method's own outstanding-write guard has already read
    /// <c>PendingJiraWriteId</c> as null but before <c>SubmitAsync</c>'s own
    /// <c>FetchStreamStateAsync</c> fences the stream for its append. A racing write landing there
    /// is otherwise unreachable from a test without a second engine or process actually running
    /// concurrently.
    /// </summary>
    internal Func<CancellationToken, Task>? OnBeforeMergeNoticeSubmitAsync { get; set; }

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
        IReadOnlyList<TaskDetails> stalePending;
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

            // Not auth-failure and still pending is not the ordinary case: JiraWriteCoordinator
            // records an outcome for every write it attempts, in the same call, before returning —
            // so a write sitting here with PendingJiraWriteIsAuthFailure false was cut short
            // mid-attempt (a cancellation the coordinator's own grace period could not outrun, or a
            // harder process death) rather than merely slow. The age check runs in memory below,
            // the same shape CardPublicationEngine.ExpireForeignAsync uses for its own ceiling,
            // rather than in this query, so every candidate is read the same way regardless of how
            // old it turns out to be.
            stalePending = await query.Query<TaskDetails>()
                .Where(task => task.PendingJiraWriteId != null)
                .Where(task => !task.PendingJiraWriteIsAuthFailure)
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

                // The strict lookup: a pending write retried here can be closeout's own merge
                // comment (recorded pending rather than queued, when its first attempt reached twg
                // but failed to authenticate), so a null site would reach TwgJiraExecutor exactly as
                // it would for the drain half below and target whatever tenant twg's own ambient
                // auth.conf resolves to — the same hazard DrainMergeNoticeAsync's own doc comment
                // already guards against (independent pre-PR review, adversarial lens, cycle 5).
                // Skipping this task for one sweep on an unresolved connection, rather than
                // guessing, still leaves the write for the next sweep — nothing here is lost, only
                // deferred.
                Uri site;
                try
                {
                    ConnectionDetails? connection = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken);
                    if (connection?.SiteUrl is not { } resolvedSite)
                    {
                        logger.LogWarning(
                            "Task {TaskId} has a Jira write pending but this node has no usable Jira "
                            + "connection; leaving it pending for a node that has one", task.Id);
                        continue;
                    }

                    site = resolvedSite;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Task {TaskId}: could not resolve this node's Jira connection for the pending "
                        + "write; leaving it pending for the next sweep", task.Id);
                    continue;
                }

                JiraWriteAttemptResult? result = await JiraWriteCoordinator.RetryPendingAsync(
                    session, task.Id, project.JiraProjectKey, new TwgJiraExecutor(twgRunner, site),
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

        int expired = 0;
        foreach (TaskDetails task in stalePending)
        {
            if (task.PendingJiraWriteRequestedAt is not { } requestedAt
                || DateTimeOffset.UtcNow - requestedAt <= _options.PendingJiraWriteCeiling)
            {
                continue;
            }

            try
            {
                if (await ExpireStaleWriteAsync(task, requestedAt, cancellationToken))
                {
                    expired++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not end the stale Jira write for task {TaskId}; it stays pending", task.Id);
            }
        }

        return new JiraWriteRetrySweepResult(retried, succeeded, drained, expired);
    }

    /// <summary>
    /// Ends a write that has sat pending, not stuck on authentication, longer than
    /// <see cref="DaemonOptions.PendingJiraWriteCeiling"/> — the only way out of a write cut short
    /// by a cancellation the coordinator's own recording grace could not outrun, or a harder
    /// process death, since neither leaves anything else behind for this platform to ever observe
    /// (independent pre-PR review, cycle 1, both lenses). Records an ordinary
    /// <c>JiraWriteFailed</c>, exactly as if the write itself had failed for this reason — the
    /// same event a live attempt appends, so a resubmission through <c>h9k task write-jira</c> and
    /// a queued merge notice both find nothing outstanding once this runs.
    /// </summary>
    private async Task<bool> ExpireStaleWriteAsync(TaskDetails task, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        TaskAggregate? aggregate = await session.Events.AggregateStreamAsync<TaskAggregate>(task.Id, token: cancellationToken);
        if (aggregate?.PendingJiraWriteId is not { } writeId || writeId != task.PendingJiraWriteId)
        {
            // Resolved by something else between the read above and this attempt — an operator's
            // own retry, or another node's sweep — or, when a write id is standing but it does not
            // match the one this sweep snapshotted as stale, a different write entirely: this
            // task's stale write already resolved and a fresh one (an operator's retry, or
            // closeout's own merge comment) is now outstanding in its place. Ending that one would
            // record a healthy write as timed out under the stale write's own timestamp
            // (independent pre-PR review, adversarial lens, cycle 3).
            return false;
        }

        logger.LogWarning(
            "Task {TaskId}: a Jira write requested at {RequestedAt:u} has stood pending longer than "
            + "{Ceiling} with no outcome and no authentication problem — ending it here",
            task.Id, requestedAt, _options.PendingJiraWriteCeiling);

        JiraWriteFailed failed = TaskDecider.RecordJiraWriteFailure(
            aggregate,
            writeId,
            $"This write was requested at {requestedAt:u} and no outcome was ever recorded for it — most "
            + "likely a cancellation (an operator's own Ctrl-C, or the daemon stopping) that outran the "
            + $"time it was given to record that outcome. Nothing has stood a chance to finish it in over "
            + $"{_options.PendingJiraWriteCeiling}, so it is ended here rather than left blocking every "
            + "later Jira write on this task. Resubmit with h9k task write-jira if the board still needs "
            + "it; a create's own marker search will find the card first if it turns out twg made one "
            + "before this write was cut short.",
            isAuthFailure: false,
            DateTimeOffset.UtcNow);
        session.Events.Append(task.Id, failed);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Attempts the queued merge comment now that nothing else is outstanding on the task, then
    /// clears the queue marker regardless of what the attempt came to — a permanent failure or a
    /// fresh auth-pending write both land on the ordinary Jira write event trail, which is where a
    /// human or the next sweep finds them, and re-queuing the same notice on top would only mean
    /// two records of the same intent.
    /// <para>
    /// The site is resolved with the strict <see cref="WorkItemConnections.FindJiraConnectionAsync"/>,
    /// the same lookup this sweep's own pending-write loop above now uses too (independent pre-PR
    /// review, adversarial lens, cycle 5): this is the queued half of closeout's own merge comment, posting the identical
    /// <see cref="CloseoutEngine.MergeComment"/>, so it gets the same guard
    /// <see cref="CloseoutEngine.TellJiraAsync"/>'s own doc comment describes — a null site here
    /// reaches <see cref="TwgJiraExecutor"/> exactly the same way and targets whatever tenant
    /// twg's own ambient <c>auth.conf</c> resolves to (independent pre-PR review, adversarial lens,
    /// cycle 4). Unlike closeout's own one-shot attempt, an unresolved connection here is left
    /// queued rather than dropped — this method runs on a poll, so a later sweep gets another
    /// chance once the connection is fixed.
    /// </para>
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

        Uri site;
        try
        {
            ConnectionDetails? connection = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken);
            if (connection?.SiteUrl is not { } resolvedSite)
            {
                logger.LogWarning(
                    "Task {TaskId} has a Jira merge notice queued but this node has no usable Jira "
                    + "connection; leaving it queued for a node that has one", taskId);
                return false;
            }

            site = resolvedSite;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Task {TaskId}: could not resolve this node's Jira connection for the queued merge "
                + "notice; leaving it queued", taskId);
            return false;
        }

        JiraWriteAttemptResult result;
        try
        {
            if (OnBeforeMergeNoticeSubmitAsync is { } beforeSubmit)
            {
                await beforeSubmit(cancellationToken);
            }

            result = await JiraWriteCoordinator.SubmitAsync(
                session,
                taskId,
                JiraWriteOperation.Comment,
                reference.Reference,
                new JiraWritePayload(WorkItemType: null, Fields: null, Comment: CloseoutEngine.MergeComment(project, task), Format: "plain"),
                project.JiraProjectKey,
                node.OwnerId,
                new TwgJiraExecutor(twgRunner, site),
                project.RepositoryPath,
                cancellationToken,
                distinguishPostAppendFailures: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // What tells "nothing from this attempt ever reached the stream" apart from "our own
            // intent was appended, and twg may already have run" is not whether a write is
            // outstanding now (independent pre-PR review, cycle 6): PendingJiraWriteId being set,
            // or clear, says a write is or is not outstanding, not whose it was — a write our own
            // attempt genuinely appended and got executed by twg can still read back as null later
            // (something else, racing the outcome-recording step itself, can resolve that exact
            // same write out from under us), the identical final state a write that never got
            // appended in the first place leaves too. So this does not re-derive "was it ours" from
            // whatever the task shows now; JiraWriteCoordinator.SubmitAsync already knows the answer
            // at the only moment it is unambiguous — immediately after the append itself either
            // committed or did not — and hands it down as a JiraWriteSubmissionException exactly
            // when it did commit: TaskDecider.RequestJiraWrite's own "already has a Jira write
            // outstanding" guard (an operator's own write-jira racing in between this method's own
            // guard above and SubmitAsync's own fence) and a lost optimistic-concurrency race on the
            // intent append itself (JiraWriteCoordinator.cs) are the two ways SubmitAsync refuses
            // before ever appending anything of its own, and neither is wrapped — so their absence
            // is itself the "somebody else's, nothing of ours got in" answer.
            if (exception is not JiraWriteSubmissionException)
            {
                // SubmitAsync refuses before ever appending anything for several reasons — a
                // payload validation failure, a missing task stream, TaskDecider.RequestJiraWrite's
                // own outstanding-write guard, or a lost optimistic-concurrency race on the intent
                // append — and this catch cannot tell which one happened, only that none of them put
                // anything of ours on the stream. Naming just one (independent pre-PR review,
                // adversarial lens, cycle 10) would assert a cause nobody observed; the attached
                // exception carries the real one.
                logger.LogWarning(exception,
                    "Task {TaskId}: the queued merge notice for {Reference} was refused before "
                    + "reaching twg; it stays queued for the next sweep", taskId, reference);
                return false;
            }

            // This attempt's own write was appended and twg may genuinely have run for it before
            // something raced the outcome-recording step itself out from under it —
            // JiraWriteCoordinator.AttemptAsync's own doc comment is why that failure is left to
            // propagate rather than being swallowed into an ordinary JiraWriteFailed. But unlike an
            // operator's own write-jira, this notice retries itself automatically on the very next
            // sweep once JiraWriteRetryEngine's own stale-pending ceiling clears the pending marker
            // — and a Comment write has no dedup gate the way a Create's own marker search does, so
            // leaving the notice queued here would let a later sweep post the identical comment a
            // second time with nobody watching, exactly the "retry loop around an unwatched write"
            // this refuses to become (independent pre-PR review, cycle 4). Marked attempted here
            // instead of risking a duplicate.
            //
            // The read and the compensating append are themselves further calls on the same session
            // whose own failure (a database outage, most plausibly) is what put this catch block in
            // play — so they are guarded separately: if they too fail, the notice cannot be marked
            // attempted, but that must not escape as an unhandled exception, which would skip this
            // logging entirely and leave the sweep silently retrying (independent pre-PR review,
            // adversarial lens, cycle 5).
            try
            {
                TaskAggregate? afterFailure = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
                if (afterFailure is { HasQueuedJiraMergeNotice: true })
                {
                    session.Events.Append(taskId, TaskDecider.RecordJiraMergeNoticeAttempted(afterFailure, DateTimeOffset.UtcNow));
                    await session.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception markException) when (markException is not OperationCanceledException)
            {
                logger.LogError(markException,
                    "Task {TaskId}: the queued merge notice for {Reference} could not be marked "
                    + "attempted after twg's own call ran, on top of the failure below — it stays "
                    + "queued and may post a duplicate comment on a later sweep; check the board and "
                    + "this task's Jira write history by hand", taskId, reference);
            }

            logger.LogError(exception,
                "Task {TaskId}: the queued merge notice for {Reference} could not have its own "
                + "outcome recorded after twg's own call ran, so it is not retried automatically; "
                + "check the board and this task's Jira write history before resubmitting by hand",
                taskId, reference);
            return true;
        }

        // SubmitAsync has already run and recorded its own outcome by this point — twg's own call
        // is over, successfully or not — so a failure re-aggregating or clearing the queue marker
        // here must not escape to PollOnceAsync's own per-task catch, whose generic "left it
        // queued" logging would misreport a comment that already posted (result.Outcome ==
        // Succeeded) as if nothing had happened. Guarded the same way the failure path above
        // already guards its own compensating read and append, for the identical reason
        // (independent pre-PR review, adversarial lens, cycles 8 and 9): the re-aggregation read
        // is itself a further call on the same session whose own failure is what this catch exists
        // for, so it belongs inside the guard, not just the append and save that follow it.
        try
        {
            // Re-aggregate: SubmitAsync appended and saved its own events on this session, so the
            // in-memory task above is stale by the time the queue marker itself needs clearing.
            TaskAggregate refreshed = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
                ?? task;
            if (refreshed.HasQueuedJiraMergeNotice)
            {
                session.Events.Append(taskId, TaskDecider.RecordJiraMergeNoticeAttempted(refreshed, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Task {TaskId}: the queued merge notice for {Reference} was submitted to twg (outcome: "
                + "{Outcome}) but the queue marker could not be cleared afterward — it stays queued and "
                + "may post a duplicate comment on a later sweep; check the board and this task's Jira "
                + "write history by hand", taskId, reference, result.Outcome);
            return true;
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
