namespace Hall9k.Domain.Features.Replication;

/// <summary>
/// One node's own standing catch-up ask for one project (idea 202383dc, M2b, task 9408d525): a gap
/// in a sender's sequence, a brand-new node's own bootstrap, or a ledger-record adoption whose
/// stream is absent locally. A plain document, not an event-sourced aggregate — purely local,
/// mechanical bookkeeping about an outstanding request this node is waiting on an answer for, the
/// same reason <see cref="EventReplicationOutboxPosition"/> and <see cref="EventOriginProgress"/>
/// are. <see cref="Id"/> is the request's own id (<c>DomainId.New()</c>, never derived — a fresh
/// request each time, unlike a cursor keyed by a fixed pair).
/// <para>
/// Exactly one of <see cref="ForOriginNodeId"/> or <see cref="ForStreamId"/> is set, or neither
/// (both null: a brand-new node's own "everything" bootstrap) — see
/// <see cref="EventReplicationCodec.EventsRequestRecord"/>'s own doc for the three shapes.
/// </para>
/// <para>
/// <see cref="Candidates"/> is the ranked peer order this request cascades through, most preferred
/// first (idea 202383dc: "asks the voucher first, then owner-role members, then any member, most
/// recently moved outbox first within a rank") — empty for a broadcast request (addressed to the
/// whole project rather than one peer at a time), which the ledger-record adoption path uses since
/// it runs from a CLI command with no live trust chain or transport to rank candidates from
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

    /// <summary>Set for the ledger-record adoption path: the one stream this request asks for, whoever originated it.</summary>
    public Guid? ForStreamId { get; set; }

    /// <summary>The ranked peer order, most preferred first — empty for a broadcast request (see this type's own doc).</summary>
    public List<Guid> Candidates { get; set; } = [];

    /// <summary>Which <see cref="Candidates"/> entry this request is currently outstanding against.</summary>
    public int CandidateIndex { get; set; }

    /// <summary>When the currently-outstanding candidate was asked — the per-candidate timeout clock starts here.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>Set once a candidate's own answer (an events batch this request's own coordinator
    /// observed arriving from the current candidate) is seen — never re-cleared.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>Set once every ranked candidate has timed out with no answer — never true for a
    /// broadcast request, which has no candidate cascade to exhaust.</summary>
    public bool Exhausted { get; set; }

    /// <summary>The most recent reason a candidate explicitly declined (idea 202383dc: "a peer that
    /// cannot answer says so") — informational only; a decline moves this request to the next
    /// candidate immediately rather than waiting out the timeout.</summary>
    public string? DeclinedReason { get; set; }

    /// <summary>The candidate this request is currently outstanding against, or null once every
    /// candidate is exhausted or for a broadcast request.</summary>
    public Guid? CurrentCandidateNodeId => CandidateIndex >= 0 && CandidateIndex < Candidates.Count ? Candidates[CandidateIndex] : null;

    /// <summary>Still waiting on an answer — the set <c>h9k status</c> shows.</summary>
    public bool IsOutstanding => AnsweredAt is null && !Exhausted;
}
