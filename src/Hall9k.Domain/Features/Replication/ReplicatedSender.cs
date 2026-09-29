using JasperFx.Events;
using Marten;
using Marten.Events;

namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// Which node handed this node an event, read off the header <c>EventReplicationInbox</c> stamps on
/// every fact it applies (<see cref="ReplicationEventHeaders.ReceivedFromNodeId"/>), the verified
/// SENDER of the record, the one identity in the chain of custody this node authenticated itself.
/// Never a payload author id (<c>ResolvedByOwnerId</c>, <c>RetriedByOwnerId</c>,
/// <c>TaskHandoffNoted.AuthorNodeId</c>): those are values the sender chose to write, so a reader
/// deciding whether to trust a replicated sentence cannot lean on them.
/// <para>
/// Three answers, because a prompt has to tell three cases apart. Null is a native event: nothing
/// replicated it, so its text is this node's own. A node id is a replicated event and the node that
/// sent it. <see cref="Guid.Empty"/> is a replicated event whose sender was not recorded: an event
/// applied before the inbox stamped the sender carries <see cref="ReplicationEventHeaders.OriginEventId"/>
/// and no <see cref="ReplicationEventHeaders.ReceivedFromNodeId"/>. It names no node, so it is in no
/// owner's fleet, which is the fail-closed reading of "this was replicated and nobody can say by
/// whom".
/// </para>
/// </summary>
public static class ReplicatedSender
{
    public static Guid? Of(IEvent @event)
    {
        if (@event.GetHeader(ReplicationEventHeaders.ReceivedFromNodeId) is string senderText)
        {
            return Guid.TryParse(senderText, out Guid senderNodeId) ? senderNodeId : Guid.Empty;
        }

        return @event.GetHeader(ReplicationEventHeaders.OriginEventId) is null ? null : Guid.Empty;
    }

    /// <summary>
    /// The node the sender CLAIMED the event began on (<see cref="ReplicationEventHeaders.OriginNodeId"/>),
    /// with the same three answers as <see cref="Of"/>: null for a native event, a node id, or
    /// <see cref="Guid.Empty"/> for a replicated event whose origin header is missing or unreadable.
    /// It is a claim, so it can only ever tighten a decision, never grant one: it differs from the
    /// sender exactly when the record was forwarded (a catch-up answer serves any project-scoped
    /// event this node holds, own or already replicated, with its true origin preserved), and a
    /// forwarded record is only as local as BOTH the node that handed it over and the node it began
    /// on. Without it, a teammate's note that one of your nodes had applied would arrive at your
    /// next node, in a catch-up answer from the first, looking like your own fleet's.
    /// </summary>
    public static Guid? OriginOf(IEvent @event)
    {
        if (@event.GetHeader(ReplicationEventHeaders.OriginNodeId) is string originText)
        {
            return Guid.TryParse(originText, out Guid originNodeId) ? originNodeId : Guid.Empty;
        }

        return @event.GetHeader(ReplicationEventHeaders.OriginEventId) is null ? null : Guid.Empty;
    }

    /// <summary>Both readings of one event, for a caller that persists or judges them together.</summary>
    public static ReplicatedFrom From(IEvent @event) => new(Of(@event), OriginOf(@event));

    /// <summary>
    /// The sender and claimed origin of the newest <typeparamref name="TEvent"/> on <paramref name="streamId"/>, read
    /// off the stream itself for the aggregates that are live-aggregated rather than projected (a
    /// run), whose <c>Apply</c> methods take the bare event and so never see a header. Null when the
    /// stream has no such event, which reads the same as native (both null).
    /// </summary>
    public static async Task<ReplicatedFrom> OfLatestAsync<TEvent>(
        IQuerySession session, Guid streamId, CancellationToken cancellationToken)
        where TEvent : notnull
    {
        IReadOnlyList<IEvent> latest = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamId == streamId && e.EventTypesAre(typeof(TEvent)))
            .OrderByDescending(e => e.Sequence)
            .Take(1)
            .ToListAsync(cancellationToken);

        return latest.Count == 0 ? new ReplicatedFrom(null, null) : From(latest[0]);
    }

    /// <summary>
    /// Every <typeparamref name="TEvent"/> on <paramref name="streamId"/> with its sender and claimed
    /// origin, oldest first: the whole-history counterpart of <see cref="OfLatestAsync{TEvent}"/>, for a
    /// reader that has to judge each record it shows rather than only the newest.
    /// </summary>
    public static async Task<IReadOnlyList<(TEvent Data, ReplicatedFrom From)>> AllAsync<TEvent>(
        IQuerySession session, Guid streamId, CancellationToken cancellationToken)
        where TEvent : notnull
    {
        IReadOnlyList<IEvent> events = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamId == streamId && e.EventTypesAre(typeof(TEvent)))
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        return [.. events.OfType<IEvent<TEvent>>().Select(e => (e.Data, From(e)))];
    }
}

/// <summary>
/// Who handed an event to this node and where the sender says it began, as
/// <see cref="ReplicatedSender"/> reads them. Both null for a native event.
/// </summary>
public readonly record struct ReplicatedFrom(Guid? SenderNodeId, Guid? OriginNodeId);
