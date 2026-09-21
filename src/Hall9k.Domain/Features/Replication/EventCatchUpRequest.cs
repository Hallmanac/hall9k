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

    /// <summary>
    /// Set when a later ask for the identical stream deliberately closed this one out
    /// (<c>h9k task pull --again</c>): this request is no longer outstanding, and
    /// <see cref="SupersededByRequestId"/> names the one that took its place. Deliberately not
    /// <see cref="AnsweredAt"/>: nothing answered it, and recording a supersede as an answer would
    /// claim an observation nobody made. This is also the only lever that clears a request minted
    /// before v0.10.5, whose decline was read by an inbox that closed a candidate cascade alone and
    /// left a broadcast standing outstanding for good (request 01a0bac1 of 2026-09-19 is that shape).
    /// </summary>
    public DateTimeOffset? SupersededAt { get; set; }

    /// <summary>The fresh request that closed this one out, written with <see cref="SupersededAt"/> and never alone.</summary>
    public Guid? SupersededByRequestId { get; set; }

    /// <summary>
    /// Set when the platform minted this request on its own because a task that had just landed
    /// here names it as a blocked-by or stacked-on dependency whose stream this node does not hold
    /// (<c>TaskDependencyCatchUp</c>): the task that named it, so <c>h9k status</c> can say whose
    /// dependency is being fetched rather than print a bare stream id nobody typed. Null on every
    /// request a human's own command queued.
    /// </summary>
    public Guid? ForDependencyOfTaskId { get; set; }

    /// <summary>The most recent reason a candidate explicitly declined (idea 202383dc: "a peer that
    /// cannot answer says so") — informational only; a decline moves this request to the next
    /// candidate immediately rather than waiting out the timeout, and on a broadcast request,
    /// which has no next candidate, it closes the ask instead (see
    /// <see cref="AnsweredAt"/>). Kept beside <see cref="Declines"/> rather than derived from it,
    /// because a request document written by a build older than that list has this and nothing
    /// else, and a reader that guessed the node and time from a bare reason string would be
    /// inventing provenance (AGENTS.md: "never guess at unobserved facts").</summary>
    public string? DeclinedReason { get; set; }

    /// <summary>
    /// Every decline this request has drawn, in the order this node read them — the node that
    /// declined, when, and why (<see cref="EventCatchUpDecline"/>). One entry per declining node:
    /// a second envelope from a peer that already declined this request records nothing new, since
    /// one node's inability to answer one request is one fact.
    /// <para>
    /// A candidate-cascading request collects one per candidate that declines as it walks its
    /// ranked list. A broadcast request closes on its FIRST decline (see <see cref="AnsweredAt"/>),
    /// but later declines from other members still land here rather than being dropped: they are
    /// observations about who in the fleet holds nothing, which is exactly what a human chasing an
    /// absent stream wants to know, and closing the ask is not a reason to stop recording them.
    /// </para>
    /// </summary>
    public List<EventCatchUpDecline> Declines { get; set; } = [];

    /// <summary>
    /// The one decline that actually ended this request, when a decline is what ended it: a
    /// broadcast's first decline (which closes it outright, see <see cref="AnsweredAt"/>), or the
    /// last ranked candidate's decline, which leaves nothing further to try and exhausts the
    /// cascade. Null on a request that ended some other way — an answer that applied, or a cascade
    /// that timed out rather than being declined — and null on any request document written before
    /// this field existed, since which node closed one of those is not something this build
    /// observed and is never filled in plausibly (AGENTS.md: "never guess at unobserved facts").
    /// <para>
    /// This is what <c>h9k status</c> reads for its "declined by &lt;node&gt; at &lt;time&gt;"
    /// line: an explicit record rather than a guess reconstructed by matching a decline's own time
    /// against <see cref="AnsweredAt"/>, which cannot tell a cascade closed by its last decline
    /// from one whose next candidate answered for real.
    /// </para>
    /// </summary>
    public EventCatchUpDecline? ClosedByDecline { get; set; }

    /// <summary>The candidate this request is currently outstanding against, or null once every
    /// candidate is exhausted or for a broadcast request.</summary>
    public Guid? CurrentCandidateNodeId => CandidateIndex >= 0 && CandidateIndex < Candidates.Count ? Candidates[CandidateIndex] : null;

    /// <summary>Still waiting on an answer — the set <c>h9k status</c> shows.</summary>
    public bool IsOutstanding => AnsweredAt is null && SupersededAt is null && !Exhausted;
}
