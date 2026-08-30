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
/// Thrown by <see cref="JiraWriteCoordinator.SubmitAsync"/> only when its caller passed
/// <c>distinguishPostAppendFailures: true</c> and this write's own intent had already been
/// durably appended when something afterward — recording twg's own outcome, or linking a created
/// card — failed before that outcome could be recorded (independent pre-PR review, cycle 6). Its
/// mere presence is the whole signal: unlike <c>PendingJiraWriteId</c>, which only ever answers
/// "is a write outstanding" and not whose, this exception is thrown from the one place that knows
/// unambiguously that this call's own append committed, so a caller that needs to tell "my own
/// write may already have reached twg" apart from "a different write raced in before mine was
/// ever appended" catches this type rather than re-deriving the answer from the task's state
/// afterward. <c>JiraWriteRetryEngine.DrainMergeNoticeAsync</c> is the only caller that
/// opts in — an operator's own <c>h9k task write-jira</c> and closeout's own merge comment attempt
/// (<c>CloseoutEngine.TellJiraAsync</c>) both let a post-append failure propagate unwrapped
/// instead, since neither needs this discrimination and wrapping it would hide the underlying
/// exception (an <c>NpgsqlException</c>, a <c>DomainConflictException</c>) from the CLI's own
/// exception-to-exit-code mapping (independent pre-PR review, cycle 7).
/// </summary>
public sealed class JiraWriteSubmissionException(Exception innerException)
    : Exception(innerException.Message, innerException);

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
    /// How long <see cref="AttemptAsync"/> gets to record a cancelled write's own outcome once the
    /// caller's own token has already fired. Short on purpose, the same reasoning
    /// <c>CardPublicationEngine.ShutdownRecordTimeout</c> documents: this runs while something is
    /// already stopping, and a stop that waits long on Postgres is worse than a write left for the
    /// daemon's own <c>JiraWriteRetryEngine</c> ceiling sweep to end later.
    /// </summary>
    private static readonly TimeSpan CancellationRecordingGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Submit a freshly composed write: validate it against the executor's own guardrails, record
    /// the intent with the full payload, then attempt it once.
    /// </summary>
    /// <param name="distinguishPostAppendFailures">
    /// Set only by a caller that itself needs to tell "this call's own write was durably appended
    /// before something afterward failed" apart from an ordinary refusal that appended nothing —
    /// <see cref="JiraWriteSubmissionException"/>'s own doc comment has the reasoning and names the
    /// one caller that needs it. Everything else leaves this false, so a post-append failure (a
    /// transient <c>NpgsqlException</c> from recording the outcome, a <see cref="DomainConflictException"/>
    /// from a concurrent actor resolving the write first) propagates exactly as it always did,
    /// reaching the CLI's own exception-to-exit-code mapping and closeout's already-generic catch
    /// unwrapped (independent pre-PR review, cycle 7).
    /// </param>
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
        CancellationToken cancellationToken,
        bool distinguishPostAppendFailures = false)
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

        // Our own intent is durably on the stream by this point, so any exception from here on is
        // ambiguous in a way nothing earlier in this method is: it can no longer mean "nothing of
        // ours was appended" the way the two throws above do. Wrapped only for a caller that opted
        // in (JiraWriteRetryEngine's merge-notice drain is the one that needs to tell those two
        // cases apart); every other caller gets the failure exactly as it happened, so it still
        // reaches the CLI's own exception-to-exit-code mapping (independent pre-PR review, cycle 6,
        // narrowed cycle 7).
        if (!distinguishPostAppendFailures)
        {
            return await AttemptAsync(
                session, taskId, writeId, operation, requested.IssueKey, payload, defaultBoard, executor,
                workingDirectory, cancellationToken);
        }

        try
        {
            return await AttemptAsync(
                session, taskId, writeId, operation, requested.IssueKey, payload, defaultBoard, executor,
                workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new JiraWriteSubmissionException(exception);
        }
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

        // A create can go stale while it sits pending: the task may have acquired its external
        // item some other way in the meantime — an operator running h9k task link-jira by hand,
        // having seen no card yet because the login that stuck this create had not been noticed.
        // TaskDecider.RequestJiraWrite's one-card-per-task guard only ever runs once, at submit,
        // so a retry has to re-check ExternalReference itself or it would file a second card for
        // the same work; the marker search alone cannot catch this, because a card linked by
        // another route carries no hall9k-task: marker (independent pre-PR review, cycle 5).
        if (task.PendingJiraWriteOperation == JiraWriteOperation.Create
            && task.ExternalReference is { } existing && existing.Provider == WorkItemProvider.Jira)
        {
            return await RecordAlreadyLinkedAsync(session, taskId, writeId, existing, executor, workingDirectory, cancellationToken);
        }

        JiraWritePayload payload = JiraWritePayload.FromJson(task.PendingJiraWritePayloadJson ?? "{}");
        return await AttemptAsync(
            session, taskId, writeId, task.PendingJiraWriteOperation, task.PendingJiraWriteIssueKey, payload,
            defaultBoard, executor, workingDirectory, cancellationToken);
    }

    /// <summary>
    /// Confirms, with the same read-back discipline every other outcome on this stream gets, that
    /// the card another route linked while this create sat pending still exists, rather than
    /// recording the linked reference verbatim: <see cref="JiraWriteSucceeded.IssueKey"/>'s own
    /// doc comment says plainly it "is what Jira answered when read back, never what a create or
    /// an update call merely claimed", and the reference an operator's own <c>h9k task link-jira</c>
    /// verified at the moment it ran is not this write's own read-back (independent pre-PR review,
    /// adversarial lens, cycle 3). A verification failure here — the card was since deleted, or
    /// twg is not authenticated — is recorded the ordinary way any other attempt's failure is,
    /// rather than papered over with the unverified claim.
    /// </summary>
    private static async Task<JiraWriteAttemptResult> RecordAlreadyLinkedAsync(
        IDocumentSession session, Guid taskId, Guid writeId, ExternalReference existing, TwgJiraExecutor executor,
        string workingDirectory, CancellationToken cancellationToken)
    {
        TwgWriteResult verified;
        try
        {
            verified = await executor.VerifyExistsAsync(existing.Reference, workingDirectory, cancellationToken);
        }
        catch (TwgExecutionException exception)
        {
            return await RecordFailureAsync(session, taskId, writeId, exception.Message, exception.IsAuthFailure, cancellationToken);
        }

        return await RecordSuccessAsync(
            session, taskId, writeId, JiraWriteOperation.Create,
            new TwgWriteResult(
                verified.IssueKey,
                $"Task {taskId} was linked to {existing} while this create sat pending; nothing new "
                + "was created. Read back now to confirm it still exists."),
            cancellationToken);
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
        TwgWriteResult result;
        try
        {
            result = operation == JiraWriteOperation.Create
                ? await CreateWithDedupAsync(executor, defaultBoard, payload, taskId, workingDirectory, cancellationToken)
                : operation == JiraWriteOperation.Comment
                    ? await executor.CommentAsync(
                        RequireKey(issueKey, taskId), payload.Comment ?? string.Empty, payload.EffectiveFormat,
                        workingDirectory, cancellationToken)
                    : await executor.UpdateAsync(RequireKey(issueKey, taskId), payload, workingDirectory, cancellationToken);
        }
        catch (TwgExecutionException exception)
        {
            return await RecordFailureAsync(session, taskId, writeId, exception.Message, exception.IsAuthFailure, cancellationToken);
        }
        // A Ctrl-C on an operator's own h9k task write-jira, or the daemon stopping mid-sweep,
        // used to leave this write's own outcome unrecorded: PendingJiraWriteId was already set by
        // SubmitAsync before this method ran, and — unlike CardPublicationEngine's own spawned
        // agent sessions, which a later sweep can adopt by pid — a synchronous twg call leaves no
        // process behind for anything to adopt, so nothing else in the platform could ever clear
        // it (independent pre-PR review, cycle 1, both lenses). Recorded here with a grace period
        // of its own — cancellationToken has already fired and cannot also be the token this save
        // waits on, the same shape CardPublicationEngine.StopForShutdownAsync uses for the
        // identical reason. If even that grace is not enough (a hard kill, not a graceful stop),
        // nothing in-process can finish the job; JiraWriteRetryEngine's own ceiling sweep is the
        // backstop that ends a write still pending this long on the clock alone.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource grace = new(CancellationRecordingGrace);
            return await RecordFailureAsync(
                session, taskId, writeId,
                "The write was interrupted (Ctrl-C, or the daemon stopping) before twg's own answer was "
                + "read back, so whether it went through could not be observed here. For a create, "
                + "resubmit with h9k task write-jira — the marker search this executor runs first will "
                + "find the card if it exists rather than filing a second one; for an update or a "
                + "comment, check the board before resubmitting.",
                isAuthFailure: false,
                grace.Token);
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

        // twg already carried out and verified this write by the time control reaches here, so
        // RecordSuccessAsync deliberately runs outside every catch above: folding it into them
        // once turned a completed, verified write into a recorded JiraWriteFailed — for a card
        // that genuinely exists on the board — the moment recording the outcome itself hit a
        // transient failure (independent pre-PR review, cycle 3, conformance lens). Left to
        // propagate instead, the task simply stays pending: a create's own marker search finds
        // the existing card on the next attempt rather than duplicating it, and
        // JiraWriteRetryEngine's stale-pending ceiling sweep is the eventual backstop for a write
        // that never gets a fresh attempt at all.
        return await RecordSuccessAsync(session, taskId, writeId, operation, result, cancellationToken);
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
    /// Link a card twg just created and verified, tolerant of any failure at all: the write itself
    /// is already safely recorded by the time this runs (<see cref="RecordSuccessAsync"/> saves it
    /// first), so nothing here may ever be allowed to propagate back into <see cref="AttemptAsync"/>'s
    /// own catch clauses, which record a <em>write</em> failure — and would find
    /// <c>PendingJiraWriteId</c> already cleared by the success just saved, so
    /// <see cref="TaskDecider.RecordJiraWriteFailure"/> would throw its own <see cref="DomainConflictException"/>
    /// out of the coordinator entirely, replacing the real error with an unrelated one (independent
    /// pre-PR review, adversarial lens, cycle 9). The two named causes are a lost race — a human or
    /// another node linking this task to something else in the moment between the create being
    /// requested (which the decider refuses unless the task carries no reference yet) and this
    /// call — and a validation refusal from a key twg answered with but left blank; either way a
    /// lost race or a refusal here costs a card that is not yet reflected in
    /// <see cref="ExternalReference"/> rather than an unrecorded write.
    /// </summary>
    private static async Task<JiraWriteAttemptResult> LinkCreatedCardAsync(
        IDocumentSession session, Guid taskId, TwgWriteResult result, Guid requestedByOwnerId, CancellationToken cancellationToken)
    {
        try
        {
            TaskAggregate task = await session.Events.AggregateStreamAsync<TaskAggregate>(taskId, token: cancellationToken)
                ?? throw new DomainNotFoundException($"No task {taskId}.");
            ExternalReference reference = new(WorkItemProvider.Jira, result.IssueKey);
            if (TaskDecider.AlreadyLinkedTo(task, reference))
            {
                return new JiraWriteAttemptResult(JiraWriteOutcome.Succeeded, result.IssueKey, result.Summary);
            }

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
        catch (Exception exception) when (exception is not OperationCanceledException)
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
