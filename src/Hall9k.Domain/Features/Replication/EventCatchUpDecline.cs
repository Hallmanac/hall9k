namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// One peer's own explicit "I cannot answer that" for one <see cref="EventCatchUpRequest"/>
/// (idea 202383dc: "a peer that cannot answer says so"), recorded as an observation rather than a
/// summary: which node said it, when this node read it, and the reason that node gave.
/// <para>
/// The list of these on a request is what <c>h9k status</c> reads to say who declined and when.
/// Before it existed, only the most recent reason string was kept
/// (<see cref="EventCatchUpRequest.DeclinedReason"/>) with no node and no time on it — so a
/// broadcast that closed on a decline could be seen to have ended, but not by whom, and a human
/// looking at a task that never arrived had nothing to go on but the reason text. Origin incident
/// (2026-09-19 13:41): the Mac answered events-unavailable for one Windows-queued stream request,
/// and ten hours later the only record of that was a reason string on a document nothing printed.
/// </para>
/// </summary>
/// <param name="DeclinedByNodeId">The peer that said it holds nothing matching this request.</param>
/// <param name="DeclinedAt">When this node read the decline — never when the decliner wrote it, which does not travel.</param>
/// <param name="Reason">The reason that peer gave, verbatim.</param>
public sealed record EventCatchUpDecline(Guid DeclinedByNodeId, DateTimeOffset DeclinedAt, string Reason);
