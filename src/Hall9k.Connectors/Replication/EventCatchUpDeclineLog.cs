using Hall9k.Domain.Features.Replication;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// The bookkeeping <see cref="EventCatchUpInbox"/> does when a peer answers a catch-up request
/// with <c>events-unavailable</c>: who said it, when, and why, appended to the request's own
/// <see cref="EventCatchUpRequest.Declines"/>. Pure and store-free, so every rule in it is
/// assertable without a container.
/// <para>
/// Before this existed the inbox kept one string
/// (<see cref="EventCatchUpRequest.DeclinedReason"/>) with no node and no time on it, overwritten
/// by each later decline. Origin incident (2026-09-19): Windows queued a broadcast request for one
/// stream at 13:40, the Mac answered events-unavailable at 13:41, and the only surviving trace ten
/// hours later was that one string on a document nothing printed — so the node that could not
/// answer, and when it said so, had to be reconstructed from two machines' logs by hand.
/// </para>
/// </summary>
public static class EventCatchUpDeclineLog
{
    /// <summary>The reason recorded when a peer's decline arrives with none of its own — an honest
    /// label for an absence rather than a plausible sentence nobody actually sent (AGENTS.md:
    /// "never guess at unobserved facts").</summary>
    public const string UnstatedReason = "no reason given";

    /// <summary>
    /// Appends <paramref name="declinedByNodeId"/>'s decline to <paramref name="request"/>, and
    /// refreshes <see cref="EventCatchUpRequest.DeclinedReason"/> to the reason it carried so the
    /// long-standing "most recent reason" field keeps meaning what it always did.
    /// <para>
    /// One entry per declining node, kept at the FIRST decline that node sent: a peer that already
    /// said it holds nothing for this request cannot make that more true by saying it again, and a
    /// re-delivered envelope from it must not grow this list without bound. What a repeat does
    /// still do is refresh the reason string, since that field has always meant "the most recent
    /// reason" rather than "the first".
    /// </para>
    /// </summary>
    /// <returns>Whether this call added an entry, as opposed to seeing a node that had already declined.</returns>
    public static bool Record(
        EventCatchUpRequest request, Guid declinedByNodeId, DateTimeOffset declinedAt, string? reason)
    {
        string recordedReason = string.IsNullOrWhiteSpace(reason) ? UnstatedReason : reason;
        request.DeclinedReason = recordedReason;

        if (request.Declines.Any(decline => decline.DeclinedByNodeId == declinedByNodeId))
        {
            return false;
        }

        request.Declines.Add(new EventCatchUpDecline(declinedByNodeId, declinedAt, recordedReason));
        return true;
    }
}
