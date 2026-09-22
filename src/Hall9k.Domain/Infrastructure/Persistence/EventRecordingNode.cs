using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Extensions;
using JasperFx.Events;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Which node actually recorded one event, read off the event's own headers in the one order that
/// survives replication (idea d805fd8b, piece 5). Two headers can answer the question and only one
/// of them is the true origin: <see cref="EventOriginStampingListener"/> stamps
/// <see cref="EventOriginStampingListener.NodeIdHeader"/> on every append, including the append
/// <c>EventReplicationInbox</c> makes when it applies somebody else's fact here, so on a
/// replicated event that header names THIS node rather than the node the fact came from.
/// <see cref="ReplicationEventHeaders.OriginNodeId"/> is set only by that inbox and never
/// overwritten by the listener, so its mere PRESENCE decides: an event carrying it was replicated,
/// and the stamped header on it names the wrong node by construction.
/// <para>
/// That is why a present-but-unusable origin header answers null rather than falling through to
/// the stamped one. The sending node stamps its own <see cref="Guid.Empty"/> while it is still
/// bootstrapping (<see cref="EventOriginStampingListener"/>'s own doc: a real, reachable state it
/// cannot wait out), and the inbox carries that empty value forward faithfully. Falling back then
/// would answer with the RECEIVING node and present another install's event as this one's own,
/// which for a reader deciding whether an agent it controls wrote something is the worst possible
/// wrong answer.
/// </para>
/// <para>
/// Answers null rather than a plausible node whenever nothing readable was stamped at all: an
/// event appended before the listener existed, one whose node id was that same still-bootstrapping
/// <see cref="Guid.Empty"/>, or a header that will not parse. A reader that needs to tell "this
/// node" from "another node" has to be able to tell both from "nobody observed" (AGENTS.md, never
/// guess at unobserved facts), and a projection has no store to resolve a better answer from.
/// </para>
/// </summary>
public static class EventRecordingNode
{
    /// <summary>
    /// Stamps <paramref name="nodeId"/> onto events as they are appended, for the one reader
    /// <see cref="EventOriginStampingListener"/> cannot serve: an INLINE projection of the very
    /// events being saved. Marten applies inline projections while it processes a session's
    /// appended events, which is before it calls
    /// <see cref="EventOriginStampingListener.BeforeSaveChangesAsync"/>, so a projection reading
    /// <see cref="EventOriginStampingListener.NodeIdHeader"/> during that pass reads a header
    /// nothing has set yet and writes a null recording node onto every row this node records
    /// itself (cycle-1 pre-PR review, both lenses: every agent-recorded lesson projected as
    /// recorded on a node nobody observed, and was then held out of every prompt).
    /// <para>
    /// A pre-stamp, not a second source of truth. The listener still runs afterwards and stamps
    /// the same header with the same node, resolved the same way from the same machine name, so
    /// the metadata that lands in <c>mt_events</c> is what it always was; this only brings the
    /// value forward to where an inline projection can read it. Deliberately narrow: an append
    /// site calls this only when its own inline projection needs the node, and the platform-wide
    /// guarantee stays the listener's.
    /// </para>
    /// <para>
    /// Stamps nothing for <see cref="Guid.Empty"/>, which is what a caller that cannot name its
    /// own node passes. An unstamped event reads back as a node nobody observed
    /// (<see cref="Of"/>), which is the honest answer rather than a plausible one.
    /// </para>
    /// </summary>
    public static void StampAtAppend(StreamAction stream, Guid nodeId)
    {
        if (nodeId == Guid.Empty)
        {
            return;
        }

        string nodeIdHeaderValue = nodeId.ToString();
        foreach (IEvent @event in stream.Events)
        {
            @event.SetHeader(EventOriginStampingListener.NodeIdHeader, nodeIdHeaderValue);
        }
    }

    public static Guid? Of(IEvent @event)
    {
        if (@event.GetHeader(ReplicationEventHeaders.OriginNodeId) is string originText)
        {
            return Guid.TryParse(originText, out Guid originNodeId) && originNodeId != Guid.Empty
                ? originNodeId
                : null;
        }

        string? stampedText = @event.GetHeader(EventOriginStampingListener.NodeIdHeader) as string;
        return stampedText.IsNotBlank() && Guid.TryParse(stampedText, out Guid stampedNodeId)
               && stampedNodeId != Guid.Empty
            ? stampedNodeId
            : null;
    }
}
