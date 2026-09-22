using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks.Projections;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Orchestrator;

/// <summary>
/// The orchestrator feed itself (idea 89471598, piece 2): a per-project cursor over this node's
/// own event log plus <see cref="OrchestratorFeedInterest"/>, and nothing else. There is no
/// second store and nothing is buffered anywhere — an item is an event past the cursor that the
/// filter admits, composed at read time and thrown away again.
/// <para>
/// One query over the event log per read (<c>QueryAllRawEvents</c> past the cursor, the same
/// scan shape <see cref="EventReplicationOutbox"/> already uses), then one document load per
/// distinct stream the filter actually admitted — cached within the read, so a burst of twenty
/// events on one run costs one load, and an event the filter rejected costs none at all. That
/// last part is what keeps the cost proportional to what is new rather than to how much history
/// the project has.
/// </para>
/// <para>
/// This class is only the database half. Which candidates are admitted, what each one reads as,
/// and how far the cursor may move are <see cref="OrchestratorFeedSelection"/>'s, so the rules
/// are unit tests rather than integration ones.
/// </para>
/// <para>
/// The scan is capped at <see cref="MaxEventsPerRead"/> events. A drain advances the cursor no
/// further than the capped scan actually reached, so a long history is read in bounded passes
/// rather than in one that tries to hold the whole log in memory — and a caller is told the cap
/// filled rather than left to think it saw everything. Nor does a drain reach the newest few
/// seconds of the log at all, which is
/// <see cref="OrchestratorFeedSelection.SettlingWindow"/>'s doing: those events are printed like
/// any other and come back on the next read, because a global sequence lower than one already
/// visible can still be uncommitted, and a cursor that passed it would never show it.
/// </para>
/// </summary>
public sealed class OrchestratorFeedReader(ReplicationProjectResolver ownership)
{
    /// <summary>How many raw events one read will inspect. Generous next to any real sweep's
    /// worth of new events, and small enough that a first-ever drain on a project with a long
    /// history returns rather than trying to read all of it.</summary>
    public const int MaxEventsPerRead = 2000;

    /// <summary>Where this project's feed currently stands, without reading anything past it.</summary>
    public static async Task<long> CursorAsync(
        IQuerySession session, Guid projectId, CancellationToken cancellationToken) =>
        (await session.LoadAsync<OrchestratorFeedCursor>(projectId, cancellationToken))?.LastDrainedGlobalSequence
        ?? OrchestratorFeedCursor.NeverDrained;

    /// <summary>The undrained items: everything past this project's own cursor that
    /// <paramref name="level"/> admits, oldest first.</summary>
    /// <param name="now">
    /// This read's own clock, which decides how far a drain of it may move the cursor: everything
    /// stamped within <see cref="OrchestratorFeedSelection.SettlingWindow"/> of it is printed but
    /// left undrained.
    /// </param>
    public async Task<OrchestratorFeedRead> ReadUndrainedAsync(
        IQuerySession session,
        Guid projectId,
        OrchestratorFeedLevel level,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long cursor = await CursorAsync(session, projectId, cancellationToken);
        IReadOnlyList<IEvent> raw = await session.Events.QueryAllRawEvents()
            .Where(e => e.Sequence > cursor)
            .OrderBy(e => e.Sequence)
            .Take(MaxEventsPerRead)
            .ToListAsync(cancellationToken);

        return await SelectAsync(session, projectId, level, raw, cursor, now, cancellationToken);
    }

    /// <summary>
    /// History from <paramref name="since"/> forward, whatever the cursor says — the read that
    /// never moves it. Ordered by sequence rather than by timestamp, so it reads in the same
    /// order a drain does even where two events share a recorded instant.
    /// </summary>
    /// <param name="since">
    /// Any instant, in any offset. Converted to UTC before it is bound, because Npgsql refuses to
    /// write a <see cref="DateTimeOffset"/> carrying a non-zero offset to a
    /// <c>timestamp with time zone</c> column at all — and a <c>--since</c> naming an absolute
    /// instant (<c>2026-09-19</c>, read in the machine's own zone) is exactly that. The instant
    /// is unchanged by the conversion; only its written offset is.
    /// </param>
    /// <param name="now">
    /// This read's own clock. It decides the drain frontier the same way it does for an undrained
    /// read, which nothing here acts on — a <c>--since</c> read never moves the cursor — but the
    /// number this read reports still means what it says rather than being quietly a different
    /// number for this one caller.
    /// </param>
    public async Task<OrchestratorFeedRead> ReadSinceAsync(
        IQuerySession session,
        Guid projectId,
        OrchestratorFeedLevel level,
        DateTimeOffset since,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset bindable = since.ToUniversalTime();
        IReadOnlyList<IEvent> raw = await session.Events.QueryAllRawEvents()
            .Where(e => e.Timestamp >= bindable)
            .OrderBy(e => e.Sequence)
            .Take(MaxEventsPerRead)
            .ToListAsync(cancellationToken);

        return await SelectAsync(
            session, projectId, level, raw, OrchestratorFeedCursor.NeverDrained, now, cancellationToken);
    }

    /// <summary>
    /// Move this project's cursor to <paramref name="throughSequence"/> — what <c>--drain</c>
    /// does once the items are printed. False when the cursor did not move: either this read
    /// settled nothing past where the cursor already stood, or another window drained further
    /// while this one was printing. Reported rather than swallowed, so a caller repeating a drain
    /// to get past the scan's cap can see that a pass made no progress instead of repeating it
    /// forever.
    /// </summary>
    public static async Task<bool> DrainAsync(
        IDocumentSession session,
        Guid projectId,
        long throughSequence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        OrchestratorFeedCursor? existing =
            await session.LoadAsync<OrchestratorFeedCursor>(projectId, cancellationToken);
        if (OrchestratorFeedCursor.Advanced(existing, projectId, throughSequence, now) is not { } advanced)
        {
            return false;
        }

        session.Store(advanced);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<OrchestratorFeedRead> SelectAsync(
        IQuerySession session,
        Guid projectId,
        OrchestratorFeedLevel level,
        IReadOnlyList<IEvent> raw,
        long startedFrom,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, ReplicationOwnership> resolved = [];
        return await OrchestratorFeedSelection.SelectAsync(
            [.. raw.Select(e =>
                new OrchestratorFeedCandidate(
                    e.Sequence, e.Timestamp, e.EventType, e.Data, e.StreamId,
                    e.GetHeader(ReplicationEventHeaders.OriginEventId) is not null))],
            projectId,
            level,
            startedFrom,
            settledThrough: now - OrchestratorFeedSelection.SettlingWindow,
            scanWasCapped: raw.Count >= MaxEventsPerRead,
            (candidate, token) => ScopeOfAsync(session, candidate, resolved, projectId, token),
            cancellationToken);
    }

    /// <summary>
    /// Which project and task one admitted candidate belongs to. Resolution is cached per stream
    /// for the life of the read: a burst of twenty events on one run costs one document load.
    /// </summary>
    private async ValueTask<OrchestratorFeedScope?> ScopeOfAsync(
        IQuerySession session,
        OrchestratorFeedCandidate candidate,
        Dictionary<Guid, ReplicationOwnership> resolved,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (candidate.Data is MessageReceived received)
        {
            // A message's own stream belongs to no project at all — the envelope carries this
            // install's own local project id instead, which is the only thing that can scope it.
            // Another project's message is answered on that id alone: the selection rejects it
            // there, so the task it names is a document nobody would ever read.
            return received.ProjectId != projectId
                ? new OrchestratorFeedScope(received.ProjectId, null)
                : new OrchestratorFeedScope(
                    received.ProjectId,
                    await AboutTaskAsync(session, received, projectId, cancellationToken));
        }

        if (!resolved.TryGetValue(candidate.StreamId, out ReplicationOwnership? owner))
        {
            owner = await ownership.ResolveAsync(session, candidate.StreamId, cancellationToken);
            resolved[candidate.StreamId] = owner;
        }

        return owner.ProjectId is { } owningProjectId
            ? new OrchestratorFeedScope(owningProjectId, owner.TaskId)
            : null;
    }

    /// <summary>
    /// The task a message says it is about, when it names one this project actually has.
    /// <c>--about</c> is free text carried through as typed (it may be an idea, a fragment, or
    /// nothing), so anything that does not resolve to one of this project's own tasks groups
    /// under the no-task heading rather than being guessed at.
    /// </summary>
    private static async Task<Guid?> AboutTaskAsync(
        IQuerySession session,
        MessageReceived received,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(received.About, out Guid aboutId))
        {
            return null;
        }

        TaskDetails? task = await session.LoadAsync<TaskDetails>(aboutId, cancellationToken);
        return task?.ProjectId == projectId ? task.Id : null;
    }
}
