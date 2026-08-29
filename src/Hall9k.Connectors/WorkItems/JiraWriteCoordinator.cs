using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Marten.Events;

namespace Hall9k.Connectors.WorkItems;

/// <summary>Whether a submitted write finished this attempt, is still pending an expired login, or ended in a failure that needs a freshly composed write rather than a retry.</summary>
public enum JiraWriteOutcome
{
    Succeeded,
    PendingAuthentication,
    Failed,
}

/// <summary>What one submit or retry attempt came to — the same shape every caller (the CLI's write-jira, the daemon's retry sweep, closeout's own merge comment) reads to decide what to tell whoever is watching.</summary>
public sealed record JiraWriteAttemptResult(JiraWriteOutcome Outcome, string? IssueKey, string Message);

/// <summary>
/// The one place that turns a composed <see cref="JiraWritePayload"/> into a recorded, audited,
/// verified write against Jira (Brian's design, 2026-08-28). Every caller — an operator or an
/// agent invoking <c>h9k task write-jira</c>, the daemon's own retry sweep once <c>twg login</c>
/// succeeds, closeout commenting a merged pull request onto the linked card — goes through here,
/// so there is exactly one path by which hall9k ever writes to Jira and exactly one place the
/// intent/execute/verify/record sequence is written down.
/// <para>
/// The shape is Requested, then zero or more auth failures, then a success or a terminal failure
/// (the events' own doc comments have the reasoning): <see cref="SubmitAsync"/> is the first of
/// those, appending the intent under a fence before anything reaches twg, and <see cref="RetryPendingAsync"/>
/// re-attempts an already-recorded pending write with its own payload, appending no new intent —
/// which is what makes a retry after re-authentication finish the request it already made rather
/// than mint a second one.
/// </para>
/// </summary>
public static class JiraWriteCoordinator
{
    /// <summary>
    /// Submit a freshly composed write: validate it against the executor's own guardrails, record
    /// the intent with the full payload, then attempt it once.
    /// </summary>
    public static async Task<JiraWriteAttemptResult> SubmitAsync(
        IDocumentSession session,
        Guid taskId,
        JiraWriteOperation operation,
        string? issueKey,
        JiraWritePayload payload,
        JiraProjectKey defaultBoard,
        Guid actingOwnerId,
        TwgJiraExecutor executor,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        // Checked before anything is recorded: a refused payload never reaches the stream at all,
        // so a disallowed field cannot be replayed later as though it had once been a real intent.
        payload.Validate(operation);

        StreamState fence = await session.Events.FetchStreamStateAsync(taskId, cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(
                taskId, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        Guid writeId = DomainId.New();
        JiraWriteRequested requested = TaskDecider.RequestJiraWrite(
            task, operation, issueKey, payload.ToJson(), writeId, DateTimeOffset.UtcNow, actingOwnerId);

        session.Events.Append(taskId, expectedVersion: fence.Version + 1, requested);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Task {taskId} changed while this write was being prepared, so nothing was requested. "
                + $"Check it with h9k task show {taskId} and submit again if it still needs this write.");
        }

        return await AttemptAsync(
            session, taskId, writeId, operation, requested.IssueKey, payload, defaultBoard, executor,
            workingDirectory, cancellationToken);
    }

    /// <summary>
    /// Re-attempt a task's outstanding write with the payload it was already recorded with — the
    /// retry that recovers a write stuck on an expired or missing twg login, without composing
    /// anything new and without minting a second intent. Returns null when the task has nothing
    /// pending, or its pending write is not stuck on authentication (a terminal failure needs a
    /// freshly composed write, not a retry).
    /// </summary>
    public static async Task<JiraWriteAttemptResult?> RetryPendingAsync(
        IDocumentSession session,
        Guid taskId,
        JiraProjectKey defaultBoard,
        TwgJiraExecutor executor,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        TaskAggregate? task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken);
        if (task?.PendingJiraWriteId is not { } writeId || !task.PendingJiraWriteIsAuthFailure)
        {
            return null;
        }

        JiraWritePayload payload = JiraWritePayload.FromJson(task.PendingJiraWritePayloadJson ?? "{}");
        return await AttemptAsync(
            session, taskId, writeId, task.PendingJiraWriteOperation, task.PendingJiraWriteIssueKey, payload,
            defaultBoard, executor, workingDirectory, cancellationToken);
    }

    private static async Task<JiraWriteAttemptResult> AttemptAsync(
        IDocumentSession session,
        Guid taskId,
        Guid writeId,
        JiraWriteOperation operation,
        string? issueKey,
        JiraWritePayload payload,
        JiraProjectKey defaultBoard,
        TwgJiraExecutor executor,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            TwgWriteResult result = operation == JiraWriteOperation.Create
                ? await CreateWithDedupAsync(executor, defaultBoard, payload, taskId, workingDirectory, cancellationToken)
                : operation == JiraWriteOperation.Comment
                    ? await executor.CommentAsync(
                        RequireKey(issueKey, taskId), payload.Comment ?? string.Empty, workingDirectory, cancellationToken)
                    : await executor.UpdateAsync(RequireKey(issueKey, taskId), payload, workingDirectory, cancellationToken);

            return await RecordSuccessAsync(session, taskId, writeId, operation, result, cancellationToken);
        }
        catch (TwgExecutionException exception)
        {
            return await RecordFailureAsync(session, taskId, writeId, exception.Message, exception.IsAuthFailure, cancellationToken);
        }
        // Anything else — a malformed board key from JiraProjectKey.Parse, a missing target key
        // from RequireKey, any bug — is caught here too rather than left to escape (independent
        // pre-PR review, cycle 1): the intent was already recorded before this attempt ran, and
        // nothing but this method's own outcome append can ever clear PendingJiraWriteId. Left
        // uncaught, the task is wedged with a permanently pending write and every later Jira
        // write on it — including closeout's own merge comment — refused forever. Recorded as a
        // non-auth failure, since none of these are "run it again once twg login succeeds".
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await RecordFailureAsync(session, taskId, writeId, exception.Message, isAuthFailure: false, cancellationToken);
        }
    }

    /// <summary>
    /// Search for a card already carrying this task's marker before creating a second one — the
    /// physical dedup gate, run on every attempt (first and every later one alike), because the
    /// failure it guards against is exactly a repeat: twg creating the card, then hall9k failing
    /// to record that it did for any reason, before a fresh attempt (with its own new write id)
    /// tries again. Keyed to the task rather than to <paramref name="writeId"/> would-be-marker,
    /// because a fresh attempt always mints a fresh write id — a marker scoped to it could never
    /// be found by the very retry it exists to protect (independent pre-PR review, cycle 1, both
    /// lenses).
    /// </summary>
    private static async Task<TwgWriteResult> CreateWithDedupAsync(
        TwgJiraExecutor executor,
        JiraProjectKey defaultBoard,
        JiraWritePayload payload,
        Guid taskId,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (await executor.FindByMarkerAsync(taskId, workingDirectory, cancellationToken) is { } existingKey)
        {
            return new TwgWriteResult(
                existingKey,
                $"A card carrying this task's marker already exists ({existingKey}); an earlier attempt "
                + "created it, so nothing new was created.");
        }

        JiraProjectKey board = payload.ProjectKey.IsNotBlank() ? JiraProjectKey.Parse(payload.ProjectKey) : defaultBoard;
        return await executor.CreateAsync(board, payload, taskId, workingDirectory, cancellationToken);
    }

    private static string RequireKey(string? issueKey, Guid taskId) =>
        issueKey.IsNotBlank()
            ? issueKey
            : throw new DomainValidationException(
                $"Task {taskId}'s pending write names no Jira item to write to, and it is not a create.");

    private static async Task<JiraWriteAttemptResult> RecordSuccessAsync(
        IDocumentSession session, Guid taskId, Guid writeId, JiraWriteOperation operation,
        TwgWriteResult result, CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        JiraWriteSucceeded succeeded = TaskDecider.RecordJiraWriteSuccess(
            task, writeId, result.IssueKey, result.Summary, DateTimeOffset.UtcNow);
        session.Events.Append(taskId, succeeded);
        Guid requestedByOwnerId = task.PendingJiraWriteRequestedByOwnerId;

        // The audit event is saved on its own first: twg already carried out and verified this
        // write, so it must be recorded even if linking below hits a race and cannot be, rather
        // than losing the whole outcome to a conflict over a fact (that the write happened) which
        // is not in dispute.
        await session.SaveChangesAsync(cancellationToken);

        // A create's success is the moment this task acquires its external item — recorded the
        // identical way an agent's own h9k task link-jira always has, so dedup, closeout, and
        // every other reader of ExternalReference cannot tell a hall9k-created card apart from an
        // adopted one (backlog: the from-jira/link-jira/dedup/untracked flows behave identically
        // to the github-issues policy).
        if (operation == JiraWriteOperation.Create)
        {
            return await LinkCreatedCardAsync(session, taskId, result, requestedByOwnerId, cancellationToken);
        }

        return new JiraWriteAttemptResult(JiraWriteOutcome.Succeeded, result.IssueKey, result.Summary);
    }

    /// <summary>
    /// Link a card twg just created and verified, tolerant of the one race worth naming: a human
    /// or another node linking this task to something else in the moment between the create being
    /// requested (which the decider refuses unless the task carries no reference yet) and this
    /// call. The write itself is already safely recorded by the time this runs
    /// (<see cref="RecordSuccessAsync"/> saves it first), so a lost race here costs a card that is
    /// not yet reflected in <see cref="ExternalReference"/> rather than an unrecorded write.
    /// </summary>
    private static async Task<JiraWriteAttemptResult> LinkCreatedCardAsync(
        IDocumentSession session, Guid taskId, TwgWriteResult result, Guid requestedByOwnerId, CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");
        ExternalReference reference = new(WorkItemProvider.Jira, result.IssueKey);
        if (TaskDecider.AlreadyLinkedTo(task, reference))
        {
            return new JiraWriteAttemptResult(JiraWriteOutcome.Succeeded, result.IssueKey, result.Summary);
        }

        try
        {
            // Neither the title nor the status was actually read here: the create's own
            // verification search only ever confirms the key exists (TwgJiraExecutor.VerifyAsync),
            // so both observed fields are the honest "unknown" WorkItemLinked's own contract asks
            // for, rather than the key masquerading as a title nobody read (independent pre-PR
            // review, cycle 1, both lenses).
            WorkItemLinked linked = TaskDecider.LinkWorkItem(
                task, reference, "unknown", "unknown", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                requestedByOwnerId);
            session.Events.Append(taskId, linked);
            await session.SaveChangesAsync(cancellationToken);
            return new JiraWriteAttemptResult(JiraWriteOutcome.Succeeded, result.IssueKey, result.Summary);
        }
        catch (DomainConflictException exception)
        {
            return new JiraWriteAttemptResult(
                JiraWriteOutcome.Succeeded,
                result.IssueKey,
                $"{result.Summary} The card was verified but could not be linked to this task: "
                + $"{exception.Message} Check h9k task show {taskId} and link it by hand with "
                + $"h9k task link-jira {taskId} {result.IssueKey} if it should still carry it.");
        }
    }

    private static async Task<JiraWriteAttemptResult> RecordFailureAsync(
        IDocumentSession session, Guid taskId, Guid writeId, string reason, bool isAuthFailure, CancellationToken cancellationToken)
    {
        TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No task {taskId}.");

        JiraWriteFailed failed = TaskDecider.RecordJiraWriteFailure(task, writeId, reason, isAuthFailure, DateTimeOffset.UtcNow);
        session.Events.Append(taskId, failed);
        await session.SaveChangesAsync(cancellationToken);

        return new JiraWriteAttemptResult(
            isAuthFailure ? JiraWriteOutcome.PendingAuthentication : JiraWriteOutcome.Failed, null, reason);
    }
}
