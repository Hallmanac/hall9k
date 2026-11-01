using Hall9k.Connectors.Prompts;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Daemon.Execution;

/// <summary>
/// Where a work session's final message enters the daemon, this reads the closing block of task
/// notes out of it (<see cref="TaskNotesBlock"/>), records one note per bullet, and hands back the
/// message with every block removed, so everything the daemon derives from the message afterwards
/// (the handoff, the pull request summary, a park or failure reason, a position file, a review-thread
/// reply) is built from text that never carried one.
/// <para>
/// The split is applied here, at each work-session site, and not in <c>StreamJsonParser</c>: that
/// parser serves every session and cannot tell a work session from a review, so splitting there
/// would make a report that quotes the marker lose lines with no note recorded. Only a build,
/// follow-up or fix session is read; a review, pr-review or spike session is returned untouched.
/// </para>
/// <para>
/// Nothing in here can change a run's outcome. A message without a block is returned as the very
/// same string with no store read at all. A failure to record is logged with the run id and the
/// message is still returned clean, because the block must stay out of derived text whether or not
/// the notes landed. The note ids are derived from the run and the bullet, so reading the same
/// message twice (an error-result retry, a resumed pipeline) records each note once.
/// </para>
/// </summary>
internal static class TaskNoteCapture
{
    /// <summary>
    /// The message with its notes block removed, after recording the block's notes when this is a
    /// work session. <paramref name="thisNodeId"/> is stamped onto the events so the inline projection
    /// can read the recording node. <paramref name="sessionId"/> and <paramref name="sessionName"/>
    /// name the session that wrote the message when it is not the run's primary one (a fix session);
    /// null takes the run's own.
    /// </summary>
    public static async Task<string?> ExtractAndRecordAsync(
        IDocumentStore store, ILogger logger, Guid thisNodeId, Guid runId, Guid taskId, string? message,
        CancellationToken cancellationToken, Guid? sessionId = null, string? sessionName = null)
    {
        TaskNotesBlock.Result block = TaskNotesBlock.Extract(message);
        if (!block.HadBlock)
        {
            return message;
        }

        TaskDetails? task;
        RunDetails? run;
        try
        {
            await using IQuerySession query = store.QuerySession();
            task = await query.LoadAsync<TaskDetails>(taskId, cancellationToken);
            run = await query.LoadAsync<RunDetails>(runId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Run {RunId}: could not tell whether the final message's task notes block belongs to a work "
                + "session, so it was left in place and no note was recorded", runId);
            return message;
        }

        // A review, pr-review or spike session is read exactly as it always was: its report may well
        // quote the marker, and a quoted marker must lose no line and record no note. Every other
        // task type is built by a work session, which is how the dispatcher itself reads it.
        if (task is null || task.Type == TaskType.PrReview || task.Type == TaskType.Spike)
        {
            return message;
        }

        if (block.Notes.Count > 0)
        {
            try
            {
                await RecordAsync(store, thisNodeId, task, run, runId, block.Notes, sessionId, sessionName, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Run {RunId}: could not record the {Count} task note(s) from the final message's closing "
                    + "block; the run carries on unchanged", runId, block.Notes.Count);
            }
        }

        return block.Clean;
    }

    /// <summary>
    /// The result of a work session with its notes block removed, or the same instance when it
    /// carried none.
    /// </summary>
    public static async Task<AgentResult> ExtractAndRecordAsync(
        IDocumentStore store, ILogger logger, Guid thisNodeId, Guid runId, Guid taskId, AgentResult result,
        CancellationToken cancellationToken, Guid? sessionId = null, string? sessionName = null)
    {
        string? clean = await ExtractAndRecordAsync(
            store, logger, thisNodeId, runId, taskId, result.Summary, cancellationToken, sessionId, sessionName);
        return ReferenceEquals(clean, result.Summary) ? result : result with { Summary = clean };
    }

    private static async Task RecordAsync(
        IDocumentStore store, Guid thisNodeId, TaskDetails task, RunDetails? run, Guid runId,
        IReadOnlyList<string> bullets, Guid? sessionId, string? sessionName, CancellationToken cancellationToken)
    {
        // What the platform observed, never what the session claims: the owner and session are the
        // run's own record, and a daemon-dispatched session is unattended by construction.
        RecordedProvenance provenance = new(run?.OwnerId ?? Guid.Empty, runId, task.Id, HumanAttendance.Unattended);
        IReadOnlyList<TaskNoteRecorded> notes = TaskNoteDecider.FromClosingBlock(
            task.Id, runId, bullets, provenance,
            (sessionId ?? run?.SessionId) is { } id && id != Guid.Empty ? id.ToString() : null,
            sessionName ?? (run is { SessionName: { Length: > 0 } primaryName } ? primaryName : null),
            DateTimeOffset.UtcNow);

        HashSet<Guid> already = [.. task.Notes.Select(entry => entry.Note.NoteId)];
        object[] fresh = [.. notes.Where(note => !already.Contains(note.NoteId))];
        if (fresh.Length == 0)
        {
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        StreamAction stream = session.Events.Append(task.Id, fresh);
        EventRecordingNode.StampAtAppend(stream, thisNodeId);
        await session.SaveChangesAsync(cancellationToken);
    }
}
