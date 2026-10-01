using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// Resolves a task's own holder, assignee and creator to the root fingerprints
/// <see cref="Handlers.TaskOwnerRule"/> judges, from this node's own store, the same way the
/// receive gate reads them (<c>EventReplicationInbox.ResolveCreatorRootFingerprintAsync</c>): a
/// task created natively on this node reads its creator from the root its genesis event was
/// stamped with (<see cref="EventOriginStampingListener.OwnerRootFingerprintHeader"/>), falling back
/// to <see cref="OwnerRootFingerprintResolver"/> only for a genesis stamped before any root was
/// claimed, and a task that replicated in reads the verified <see cref="TaskCreatorRootRecord"/>,
/// which stays unresolved until a direct act from the creator has confirmed it. The holder and the
/// assignee are the roots recorded on the task, as the gate reads them, so after an owner's root
/// changes the guard refuses what peers would drop. Read-only; never backfills the record.
/// </summary>
public static class TaskOwnerFactsReader
{
    public static async Task<TaskOwnerFacts> ReadAsync(
        IQuerySession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        OwnerRootFact holder = OwnerRootFact.KnownOrAbsent(task.HolderOwnerRootFingerprint);
        OwnerRootFact assigned = await ReadAssignedAsync(session, task, cancellationToken);
        OwnerRootFact creator = holder.State == OwnerRootFactState.Absent && assigned.State == OwnerRootFactState.Absent
            ? await ReadCreatorAsync(session, task, cancellationToken)
            : OwnerRootFact.Absent;
        return new TaskOwnerFacts(holder, assigned, creator);
    }

    /// <summary>
    /// A recorded fingerprint is the assignment. An assignment recorded by owner id alone (a task
    /// from before the fingerprint existed, or a forced takeover, which clears it) resolves through
    /// <see cref="OwnerRootFingerprintResolver"/>, and stays unresolved when this node has no row
    /// for that owner.
    /// </summary>
    private static async Task<OwnerRootFact> ReadAssignedAsync(
        IQuerySession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(task.AssignedOwnerFingerprint))
        {
            return OwnerRootFact.Known(task.AssignedOwnerFingerprint);
        }

        if (task.AssignedOwnerId is not { } assignedOwnerId)
        {
            return OwnerRootFact.Absent;
        }

        string? resolved = await OwnerRootFingerprintResolver.ResolveAsync(session, assignedOwnerId, cancellationToken);
        return string.IsNullOrEmpty(resolved)
            ? OwnerRootFact.Unresolved
            : OwnerRootFact.Known(resolved);
    }

    private static async Task<OwnerRootFact> ReadCreatorAsync(
        IQuerySession session, TaskAggregate task, CancellationToken cancellationToken)
    {
        TaskCreatorRootRecord? creatorRoot = await session.LoadAsync<TaskCreatorRootRecord>(task.Id, cancellationToken);
        IReadOnlyList<IEvent> taskEvents = creatorRoot is null
            ? await session.Events.FetchStreamAsync(task.Id, token: cancellationToken)
            : [];
        IEvent? genesis = taskEvents.Count > 0
            ? taskEvents[0]
            : null;

        // Only a genesis stamped before any root was claimed needs the adding owner's current root,
        // so that lookup is made then and no other time.
        string? addedByRoot = creatorRoot is null && IsNative(genesis) && StampedRootOf(genesis) is null
            ? await OwnerRootFingerprintResolver.ResolveAsync(session, task.AddedByOwnerId, cancellationToken)
            : null;
        return CreatorOf(creatorRoot, genesis, _ => addedByRoot);
    }

    /// <summary>
    /// The creator of each task in <paramref name="taskIds"/>, read the way <see cref="ReadAsync"/>
    /// reads one, for a board that holds many rows and would otherwise pay a stream fetch for each.
    /// A task with a <see cref="TaskCreatorRootRecord"/> is answered from it; every other task is
    /// answered from its genesis event, all of them fetched in one query. The creator root is stamped
    /// on that event by <see cref="EventOriginStampingListener"/> after the inline projections have
    /// run, so no projection document can carry it; this is where a surface that holds only
    /// <see cref="Projections.TaskListItem"/> documents gets it.
    /// </summary>
    /// <param name="ownerRoot">
    /// The root fingerprint an owner id currently claims on this node, for a genesis stamped before
    /// any root was claimed, which resolves through the owner that added the task.
    /// </param>
    public static async Task<IReadOnlyDictionary<Guid, OwnerRootFact>> ReadCreatorsAsync(
        IQuerySession session, IReadOnlyCollection<Guid> taskIds, Func<Guid, string?> ownerRoot,
        CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return new Dictionary<Guid, OwnerRootFact>();
        }

        Guid[] ids = [.. taskIds];
        Dictionary<Guid, TaskCreatorRootRecord> records =
            (await session.LoadManyAsync<TaskCreatorRootRecord>(cancellationToken, ids)).ToDictionary(record => record.Id);
        Guid[] withoutRecord = [.. ids.Where(id => !records.ContainsKey(id))];
        Dictionary<Guid, IEvent> genesisEvents = withoutRecord.Length == 0
            ? []
            : (await session.Events.QueryAllRawEvents()
                    .Where(e => e.StreamId.IsOneOf(withoutRecord) && e.Version == 1)
                    .ToListAsync(cancellationToken))
                .ToDictionary(e => e.StreamId);

        return ids.ToDictionary(
            id => id,
            id => CreatorOf(records.GetValueOrDefault(id), genesisEvents.GetValueOrDefault(id), ownerRoot));
    }

    /// <summary>
    /// The creator fact from what a node holds about one task, in the order the receive gate reads it
    /// (<c>EventReplicationInbox.ResolveCreatorRootFingerprintAsync</c>): a verified record decides
    /// first, empty meaning a relayed genesis no direct act has confirmed yet; with no record, only a
    /// genesis this node wrote itself has a creator, read from the root it was stamped with and
    /// falling back to the adding owner's current root only when it was stamped before one was claimed.
    /// </summary>
    private static OwnerRootFact CreatorOf(TaskCreatorRootRecord? record, IEvent? genesis, Func<Guid, string?> ownerRoot)
    {
        if (record is not null)
        {
            return string.IsNullOrEmpty(record.CreatorRootFingerprint)
                ? OwnerRootFact.Unresolved
                : OwnerRootFact.Known(record.CreatorRootFingerprint);
        }

        if (!IsNative(genesis))
        {
            return OwnerRootFact.Unresolved;
        }

        string? creatorFingerprint = StampedRootOf(genesis)
            ?? (genesis!.Data is TaskAdded added ? ownerRoot(added.AddedByOwnerId) : null);
        return string.IsNullOrEmpty(creatorFingerprint)
            ? OwnerRootFact.Unresolved
            : OwnerRootFact.Known(creatorFingerprint);
    }

    /// <summary>Whether this node wrote the genesis itself: no genesis at all, or one a peer replicated, has no creator to read.</summary>
    private static bool IsNative(IEvent? genesis) =>
        genesis is not null && genesis.GetHeader(ReplicationEventHeaders.OriginNodeId) is null;

    private static string? StampedRootOf(IEvent? genesis) =>
        genesis?.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) is string { Length: > 0 } stamped
            ? stamped
            : null;
}
