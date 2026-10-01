using Hall9k.Connectors.Replication;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Hall9k.Domain.Features.Tasks.Queries;
using Hall9k.Domain.Features.Trust;
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
        IReadOnlyList<OwnerDetails> owners = await session.Query<OwnerDetails>().ToListAsync(cancellationToken);
        OwnerDetails? thisOwner = owners.FirstOrDefault();
        MemberLabelLookup labels = await LabelLookupAsync(session, projectId, thisOwner, cancellationToken);
        OrchestratorFeedViewer viewer = await ViewerAsync(session, thisOwner, cancellationToken);
        ScopeLookup lookup = new(
            session,
            projectId,
            owners
                .Where(owner => !string.IsNullOrEmpty(owner.RootFingerprint))
                .ToDictionary(owner => owner.Id, owner => owner.RootFingerprint!));
        return await OrchestratorFeedSelection.SelectAsync(
            [.. raw.Select(e =>
                new OrchestratorFeedCandidate(
                    e.Sequence, e.Timestamp, e.EventType, e.Data, e.StreamId,
                    e.GetHeader(ReplicationEventHeaders.OriginEventId) is not null,
                    e.GetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint) as string))],
            projectId,
            level,
            startedFrom,
            settledThrough: now - OrchestratorFeedSelection.SettlingWindow,
            scanWasCapped: raw.Count >= MaxEventsPerRead,
            (candidate, token) => ScopeOfAsync(lookup, candidate, token),
            labels,
            viewer,
            cancellationToken);
    }

    /// <summary>
    /// This project's own current member labels (task b7d8222e), plus this machine's own owner
    /// root fingerprint so a node-id line about this owner's own fleet reads as a bare id rather
    /// than naming "me" (<see cref="MemberLabelResolver.LabelForNodeId"/>'s own doc). Read from the
    /// <see cref="ProjectMemberLabels"/> projection alone — never a live ledger walk — the identical
    /// single-owner-per-install lookup <c>NodeBootstrap.EnsureAsync</c> already relies on.
    /// </summary>
    private static async Task<MemberLabelLookup> LabelLookupAsync(
        IQuerySession session, Guid projectId, OwnerDetails? owner, CancellationToken cancellationToken)
    {
        ProjectMemberLabels? labels = await session.LoadAsync<ProjectMemberLabels>(projectId, cancellationToken);
        return new MemberLabelLookup(labels, owner?.RootFingerprint);
    }

    /// <summary>
    /// Who is reading: this install's owner and the node on this machine, found the way every
    /// other pane finds them (<c>StatusCommand.WriteIdentityLineAsync</c>).
    /// </summary>
    private static async Task<OrchestratorFeedViewer> ViewerAsync(
        IQuerySession session, OwnerDetails? owner, CancellationToken cancellationToken)
    {
        string machineName = Environment.MachineName;
        NodeDetails? node = (await session.Query<NodeDetails>()
            .Where(n => n.MachineName == machineName)
            .Take(1).ToListAsync(cancellationToken)).FirstOrDefault();
        return new OrchestratorFeedViewer(owner?.RootFingerprint, owner?.Id, node?.Id);
    }

    /// <summary>
    /// Which project and task one admitted candidate belongs to. Resolution is cached per stream
    /// for the life of the read: a burst of twenty events on one run costs one document load.
    /// </summary>
    private async ValueTask<OrchestratorFeedScope?> ScopeOfAsync(
        ScopeLookup lookup, OrchestratorFeedCandidate candidate, CancellationToken cancellationToken)
    {
        IQuerySession session = lookup.Session;
        Guid projectId = lookup.ProjectId;
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

        // A root-key rotation's own stream (idea 6be68ee2, PR B) belongs to no project either —
        // it is keyed by owner root and promoted node id (RootRotationStreamId.For), never a
        // project, task, idea, or epic stream ReplicationProjectResolver below could ever resolve.
        // The event itself carries the project id directly, the identical shape MessageReceived's
        // own arm above already uses, with no task to name.
        if (candidate.Data is RootRotationObserved or RootRotationRevoked)
        {
            Guid rotationProjectId = candidate.Data switch
            {
                RootRotationObserved observed => observed.ProjectId,
                RootRotationRevoked revoked => revoked.ProjectId,
                _ => throw new InvalidOperationException("Unreachable: candidate.Data matched neither rotation type."),
            };
            return new OrchestratorFeedScope(rotationProjectId, null);
        }

        if (!lookup.Resolved.TryGetValue(candidate.StreamId, out ReplicationOwnership? owner))
        {
            owner = await ownership.ResolveAsync(session, candidate.StreamId, cancellationToken);
            lookup.Resolved[candidate.StreamId] = owner;
        }

        if (owner.ProjectId is not { } owningProjectId)
        {
            return null;
        }

        // Ownership is asked about only for a replicated event naming a task: a local event is this
        // node's own work, and the selection reads a scope with no facts as having no opinion.
        TaskOwnerFacts? facts = candidate.IsReplicated && owner.TaskId is { } taskId
            ? await OwnerFactsAsync(lookup, taskId, cancellationToken)
            : null;
        return new OrchestratorFeedScope(owningProjectId, owner.TaskId, facts);
    }

    /// <summary>
    /// The facts card C's ownership rule judges for one task, read off its board row the way
    /// <c>TaskStatusComposer</c> reads them for the viewer's board, so the feed and the board give the
    /// same answer for the same task. A task with no row on this node has no holder, no assignee and
    /// an unresolved creator, which reads as an owner nobody can name.
    /// </summary>
    private static async Task<TaskOwnerFacts> OwnerFactsAsync(
        ScopeLookup lookup, Guid taskId, CancellationToken cancellationToken)
    {
        if (lookup.Facts.TryGetValue(taskId, out TaskOwnerFacts? cached))
        {
            return cached;
        }

        TaskListItem? row = await lookup.Session.LoadAsync<TaskListItem>(taskId, cancellationToken);
        TaskOwnerFacts facts;
        if (row is null)
        {
            facts = new TaskOwnerFacts(OwnerRootFact.Absent, OwnerRootFact.Absent, OwnerRootFact.Unresolved);
        }
        else
        {
            OwnerRootFact? creator = TaskListItemOwnerFacts.NeedsCreator(row)
                ? (await TaskOwnerFactsReader.ReadCreatorsAsync(
                    lookup.Session, [taskId], lookup.OwnerRoot, cancellationToken)).GetValueOrDefault(taskId)
                : null;
            facts = TaskListItemOwnerFacts.From(row, lookup.OwnerRoot, creator);
        }

        lookup.Facts[taskId] = facts;
        return facts;
    }

    /// <summary>What one read's scope lookups share: the session, this project, the roots this node
    /// knows its owners by, and the per-read caches that keep a burst of events on one stream or
    /// task to one load.</summary>
    private sealed class ScopeLookup(IQuerySession session, Guid projectId, IReadOnlyDictionary<Guid, string> ownerRoots)
    {
        public IQuerySession Session { get; } = session;

        public Guid ProjectId { get; } = projectId;

        public Dictionary<Guid, ReplicationOwnership> Resolved { get; } = [];

        public Dictionary<Guid, TaskOwnerFacts> Facts { get; } = [];

        public string? OwnerRoot(Guid ownerId) => ownerRoots.GetValueOrDefault(ownerId);
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
