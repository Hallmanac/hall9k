using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Features.Idea;

/// <summary>
/// Resolves an idea's own assignee and creator to the root fingerprints
/// <see cref="Tasks.Handlers.TaskOwnerRule"/> judges, from this node's own store, the same way the
/// receive gate reads them: an idea captured natively on this node reads its creator from the root its
/// genesis event was stamped with (<see cref="EventOriginStampingListener.OwnerRootFingerprintHeader"/>),
/// falling back to <see cref="OwnerRootFingerprintResolver"/> only for a genesis stamped before any
/// root was claimed, and an idea that replicated in reads the verified
/// <see cref="IdeaCreatorRootRecord"/>, which stays unresolved until a direct act from the creator has
/// confirmed it. The rule takes no holder for an idea, so that fact is always absent. Read-only;
/// never backfills the record.
/// </summary>
public static class IdeaOwnerFactsReader
{
    public static async Task<TaskOwnerFacts> ReadAsync(
        IQuerySession session, IdeaAggregate idea, CancellationToken cancellationToken)
    {
        OwnerRootFact assigned = await ReadAssignedAsync(session, idea, cancellationToken);
        OwnerRootFact creator = assigned.State == OwnerRootFactState.Absent
            ? await ReadCreatorAsync(session, idea, cancellationToken)
            : OwnerRootFact.Absent;
        return new TaskOwnerFacts(OwnerRootFact.Absent, assigned, creator);
    }

    /// <summary>
    /// A recorded fingerprint is the assignment. An assignment recorded by owner id alone (the
    /// assigning node had no root for them yet) resolves through <see cref="OwnerRootFingerprintResolver"/>,
    /// and stays unresolved when this node has no row for that owner.
    /// </summary>
    private static async Task<OwnerRootFact> ReadAssignedAsync(
        IQuerySession session, IdeaAggregate idea, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(idea.AssigneeOwnerFingerprint))
        {
            return OwnerRootFact.Known(idea.AssigneeOwnerFingerprint);
        }

        if (idea.AssigneeOwnerId is not { } assigneeOwnerId)
        {
            return OwnerRootFact.Absent;
        }

        string? resolved = await OwnerRootFingerprintResolver.ResolveAsync(session, assigneeOwnerId, cancellationToken);
        return string.IsNullOrEmpty(resolved)
            ? OwnerRootFact.Unresolved
            : OwnerRootFact.Known(resolved);
    }

    /// <summary>
    /// The creator fact on its own, for the receive gate, which reads it without needing the assignee
    /// (it has already judged the idea's own assignee from the aggregate it replayed).
    /// </summary>
    public static async Task<OwnerRootFact> ReadCreatorAsync(
        IQuerySession session, IdeaAggregate idea, CancellationToken cancellationToken)
    {
        IdeaCreatorRootRecord? creatorRoot = await session.LoadAsync<IdeaCreatorRootRecord>(idea.Id, cancellationToken);
        if (creatorRoot is not null)
        {
            return string.IsNullOrEmpty(creatorRoot.CreatorRootFingerprint)
                ? OwnerRootFact.Unresolved
                : OwnerRootFact.Known(creatorRoot.CreatorRootFingerprint);
        }

        IReadOnlyList<IEvent> ideaEvents = await session.Events.FetchStreamAsync(idea.Id, token: cancellationToken);
        IEvent? genesis = ideaEvents.Count > 0
            ? ideaEvents[0]
            : null;
        if (genesis is null || genesis.GetHeader(ReplicationEventHeaders.OriginNodeId) is not null)
        {
            return OwnerRootFact.Unresolved;
        }

        string? creatorFingerprint = genesis.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) is string { Length: > 0 } stamped
            ? stamped
            : await OwnerRootFingerprintResolver.ResolveAsync(session, idea.OwnerId, cancellationToken);
        return string.IsNullOrEmpty(creatorFingerprint)
            ? OwnerRootFact.Unresolved
            : OwnerRootFact.Known(creatorFingerprint);
    }
}
