using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Replication;
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
        if (creatorRoot is not null)
        {
            return string.IsNullOrEmpty(creatorRoot.CreatorRootFingerprint)
                ? OwnerRootFact.Unresolved
                : OwnerRootFact.Known(creatorRoot.CreatorRootFingerprint);
        }

        IReadOnlyList<IEvent> taskEvents = await session.Events.FetchStreamAsync(task.Id, token: cancellationToken);
        IEvent? genesis = taskEvents.Count > 0
            ? taskEvents[0]
            : null;
        if (genesis is null || genesis.GetHeader(ReplicationEventHeaders.OriginNodeId) is not null)
        {
            return OwnerRootFact.Unresolved;
        }

        string? stampedFingerprint = genesis.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) as string;
        string? creatorFingerprint = string.IsNullOrEmpty(stampedFingerprint)
            ? await OwnerRootFingerprintResolver.ResolveAsync(session, task.AddedByOwnerId, cancellationToken)
            : stampedFingerprint;
        return string.IsNullOrEmpty(creatorFingerprint)
            ? OwnerRootFact.Unresolved
            : OwnerRootFact.Known(creatorFingerprint);
    }
}
