namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// One node's own standing catch-up ask for one project (idea 202383dc, M2b, task 9408d525): a gap
/// in a sender's sequence, a brand-new node's own bootstrap, a ledger-record adoption or
/// <c>h9k task pull</c> whose stream is absent locally, or a whole-project history pull
/// (<c>h9k project pull --since</c>). A plain document, not an event-sourced aggregate — purely local,
/// mechanical bookkeeping about an outstanding request this node is waiting on an answer for, the
/// same reason <see cref="EventReplicationOutboxPosition"/> and <see cref="EventOriginProgress"/>
/// are. <see cref="Id"/> is the request's own id (<c>DomainId.New()</c>, never derived — a fresh
/// request each time, unlike a cursor keyed by a fixed pair).
/// <para>
/// Exactly one of <see cref="ForOriginNodeId"/>, <see cref="ForStreamId"/>, or
/// <see cref="SinceGlobalSequence"/> is set, or none of them (all null: a brand-new node's own
/// "everything" bootstrap) — see <see cref="EventReplicationCodec.EventsRequestRecord"/>'s own doc
/// for the four shapes.
/// </para>
/// <para>
/// <see cref="Candidates"/> is the ranked peer order this request cascades through, most preferred
/// first (idea 202383dc: "asks the voucher first, then owner-role members, then any member, most
/// recently moved outbox first within a rank") — empty for a broadcast request (addressed to the
/// whole project rather than one peer at a time), which both CLI-minted shapes use (the
/// ledger-record adoption path and <c>h9k task pull</c>, and <c>h9k project pull</c>) since they
/// run from a CLI command with no live trust chain or transport to rank candidates from
/// (never touches git or a network on its own): every project member's own daemon sweep reads it
/// and answers if it can, and a broadcast request never advances through <see cref="CandidateIndex"/>
/// or times out — there is no "next candidate" left to try.
/// </para>
/// </summary>
public sealed class EventCatchUpRequest
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Set for a gap-fill request: the origin node whose events are missing.</summary>
    public Guid? ForOriginNodeId { get; set; }

    /// <summary>The coarse "since" bound for a gap-fill request — ignored when <see cref="ForOriginNodeId"/> is null.</summary>
    public long SinceOriginSequence { get; set; }

    /// <summary>Set for the ledger-record adoption path and <c>h9k task pull</c>: the one stream
    /// this request asks for, whoever originated it.</summary>
    public Guid? ForStreamId { get; set; }

    /// <summary>Set for <c>h9k project pull --since</c>: the lower bound, on the ANSWERING node's
    /// own global sequence, this request asks a whole project's history from (0 for
    /// <c>--since all</c>). Null for the other three shapes — a bootstrap is this shape's own
    /// unbounded twin, and is told apart from it by this being null rather than 0.</summary>
    public long? SinceGlobalSequence { get; set; }

    /// <summary>The ranked peer order, most preferred first — empty for a broadcast request (see this type's own doc).</summary>
    public List<Guid> Candidates { get; set; } = [];

    /// <summary>Which <see cref="Candidates"/> entry this request is currently outstanding against.</summary>
    public int CandidateIndex { get; set; }

    /// <summary>When the currently-outstanding candidate was asked — the per-candidate timeout clock starts here.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>Set once a candidate's own answer (an events batch this request's own coordinator
    /// observed arriving from the current candidate) is seen — never re-cleared. A BROADCAST
    /// request (<see cref="Candidates"/> empty: either shape) also closes on a peer's explicit
    /// decline, with <see cref="DeclinedReason"/> recording why: it has no cascade to advance and
    /// no timeout behind it, so a decline is the only answer it will ever get from a peer holding
    /// nothing that matches, and without this it would stand outstanding for good — which a
    /// mistyped task id no member holds made permanent and unclearable.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>Set once every ranked candidate has timed out with no answer — never true for a
    /// broadcast request, which has no candidate cascade to exhaust.</summary>
    public bool Exhausted { get; set; }

    /// <summary>The most recent reason a candidate explicitly declined (idea 202383dc: "a peer that
    /// cannot answer says so") — informational only; a decline moves this request to the next
    /// candidate immediately rather than waiting out the timeout, and on a broadcast request,
    /// which has no next candidate, it closes the ask instead (see
    /// <see cref="AnsweredAt"/>).</summary>
    public string? DeclinedReason { get; set; }

    /// <summary>The candidate this request is currently outstanding against, or null once every
    /// candidate is exhausted or for a broadcast request.</summary>
    public Guid? CurrentCandidateNodeId => CandidateIndex >= 0 && CandidateIndex < Candidates.Count ? Candidates[CandidateIndex] : null;

    /// <summary>Still waiting on an answer — the set <c>h9k status</c> shows.</summary>
    public bool IsOutstanding => AnsweredAt is null && !Exhausted;
}
