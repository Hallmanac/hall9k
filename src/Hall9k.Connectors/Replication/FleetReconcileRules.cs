using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Replication;

namespace Hall9k.Connectors.Replication;

/// <summary>
/// Every decision a fleet reconcile makes (task 252bc5cf), as pure functions over already-read
/// state: which of this owner's own fleet nodes still need asking, when an unanswered reconcile
/// earns its one re-ask and when it is merely reported, and what reading an answer, a decline, or a
/// terminal envelope does to the record. Pure on purpose — the sweep, the catch-up inbox, the
/// replication inbox and <c>h9k status</c> all reach the same verdicts because they all read them
/// from here, and every one of these rules is provable without a database, a repository or a remote.
/// <para>
/// The invariant behind all of it: every node in an owner's fleet holds every fleet- and
/// team-scoped event of each project it registers, so any one of them is a sufficient answerer for
/// a teammate's own bootstrap. Brian, 2026-09-21: the Mac held almost none of arx-platform's
/// history while the Windows node held all of it, and a member invited from either one must receive
/// the whole project.
/// </para>
/// </summary>
public static class FleetReconcileRules
{
    /// <summary>
    /// This owner's own fleet as this project's ledger currently names it
    /// (<see cref="TrustedOwner.FleetNodeIds"/>: the root's own node plus every currently vouched
    /// node), minus this node itself. Empty when <paramref name="trustChain"/> holds no chain for
    /// <paramref name="myOwnerRootFingerprint"/> at all, which is the honest answer rather than a
    /// guess: a node whose own owner chain this read cannot see has no proven siblings to reconcile
    /// with. Deliberately not derived from probed outbox tips — a sibling that has never pushed an
    /// outbox for this project is still a sibling, and is exactly the one most likely to be missing
    /// history.
    /// </summary>
    public static IReadOnlyList<Guid> FleetPeers(TrustChain trustChain, string myOwnerRootFingerprint, Guid myNodeId) =>
        trustChain.OwnerChains.TryGetValue(myOwnerRootFingerprint, out TrustedOwner? owner)
            ? [.. owner.FleetNodeIds().Where(nodeId => nodeId != myNodeId)]
            : [];

    /// <summary>Whether <paramref name="trustChain"/> actually names this owner's own chain, which
    /// is what tells an EMPTY <see cref="FleetPeers"/> apart from an unknown one: a chain that is
    /// present and names only this node proves the fleet is a fleet of one, while a chain this read
    /// could not see at all proves nothing whatsoever. Only the first of those two may be read as
    /// "a recorded peer is no longer a sibling" (AGENTS.md: never guess at unobserved
    /// facts).</summary>
    public static bool FleetIsKnown(TrustChain trustChain, string myOwnerRootFingerprint) =>
        trustChain.OwnerChains.ContainsKey(myOwnerRootFingerprint);

    /// <summary>Whether <paramref name="candidateNodeId"/> is one of this owner's own fleet nodes
    /// other than this one — the test that decides whether a project-history request earns a
    /// reverse ask back. A request from a teammate's node, or from a node no chain here recognizes,
    /// answers false and is simply answered rather than reciprocated.</summary>
    public static bool IsFleetPeer(
        TrustChain trustChain, string myOwnerRootFingerprint, Guid candidateNodeId, Guid myNodeId) =>
        candidateNodeId != myNodeId
        && trustChain.OwnerChains.TryGetValue(myOwnerRootFingerprint, out TrustedOwner? owner)
        && owner.FleetNodeIds().Contains(candidateNodeId);

    /// <summary>
    /// Which fleet peers this sweep actually asks: every one with no reconcile record for this
    /// (peer, project) yet, except a peer this node currently has an outstanding bootstrap
    /// addressed to. The bootstrap exclusion keeps a brand-new node from having the whole project in
    /// flight from one peer twice at once; it is a wait, never a substitution, because a bootstrap's
    /// answer stops at the answering node's own replication switch-on point and a reconcile's does
    /// not. Once that bootstrap closes the peer is asked like any other, and the reconcile is what
    /// carries the pre-switch-on history the bootstrap could never have brought — the hole this
    /// whole rule set exists to close.
    /// </summary>
    public static IReadOnlyList<Guid> PeersToAsk(
        IReadOnlyList<Guid> fleetPeers, ISet<Guid> peersWithRecord, ISet<Guid> peersWithOutstandingBootstrap) =>
        [.. fleetPeers.Where(peer => !peersWithRecord.Contains(peer) && !peersWithOutstandingBootstrap.Contains(peer))];

    /// <summary>
    /// Whether <paramref name="record"/> has earned its one automatic re-ask: still incomplete,
    /// never re-asked before, and its live ask older than <paramref name="messageRetention"/>. The
    /// window is the outbox squash window (<c>DaemonOptions.MessageRetention</c>, 48 hours) because
    /// that is precisely how long an answer survives unread — past it the answer is gone, and the
    /// request, already closed on its first envelope, is never re-asked by anything else. A peer
    /// that answered <c>events-unavailable</c> is not re-asked: it gave a final answer, and
    /// <see cref="FleetProjectReconcile.UnavailableReason"/> records it.
    /// </summary>
    public static bool NeedsReAsk(FleetProjectReconcile record, TimeSpan messageRetention, DateTimeOffset now) =>
        record.CompletedAt is null
        && record.ReAskedAt is null
        && record.UnavailableReason is null
        && record.PeerLeftFleetAt is null
        && now - record.AskedAt >= messageRetention;

    /// <summary>Whether <paramref name="record"/>'s own re-ask has itself gone unanswered past
    /// <paramref name="messageRetention"/> — the "and then reported" half of the rule. Nothing
    /// automatic fires off it; it is what <c>h9k status</c> names so a human can run
    /// <c>h9k project reconcile</c>, and it never re-reports a record already marked.</summary>
    public static bool IsStalled(FleetProjectReconcile record, TimeSpan messageRetention, DateTimeOffset now) =>
        record.CompletedAt is null
        && record.UnavailableReason is null
        && record.PeerLeftFleetAt is null
        && record.ReAskedAt is { } reAskedAt
        && now - reAskedAt >= messageRetention;

    /// <summary>Whether this sweep's own view of the fleet says <paramref name="record"/>'s peer is
    /// gone: it is not in <paramref name="fleetPeers"/> and the record does not already say so. A
    /// record whose exchange already completed is left exactly as it is — that exchange is a
    /// finished fact about history the two nodes did share, and a later revoke does not unmake
    /// it.</summary>
    public static bool NeedsRetiring(FleetProjectReconcile record, ISet<Guid> fleetPeers) =>
        record.CompletedAt is null
        && record.PeerLeftFleetAt is null
        && !fleetPeers.Contains(record.PeerNodeId);

    /// <summary>Whether <paramref name="record"/>'s peer, retired out of the fleet earlier, is a
    /// sibling again — a node vouched back in, whose exchange starts over from the top rather than
    /// resuming, since what it answered while it was out is unknown.</summary>
    public static bool PeerIsBackInFleet(FleetProjectReconcile record, ISet<Guid> fleetPeers) =>
        record.PeerLeftFleetAt is not null && fleetPeers.Contains(record.PeerNodeId);

    /// <summary>This node's own observation that <paramref name="record"/>'s peer is no longer one
    /// of this owner's fleet nodes: the exchange is closed unanswered and the stall mark, which
    /// pointed at a hand command that walks only the current fleet, is dropped with it. Never
    /// touches <see cref="FleetProjectReconcile.CompletedAt"/> or
    /// <see cref="FleetProjectReconcile.UnavailableReason"/> — no answer was observed, and those two
    /// mean an answer was.</summary>
    public static void NotePeerLeftFleet(FleetProjectReconcile record, DateTimeOffset now)
    {
        record.PeerLeftFleetAt = now;
        record.StalledAt = null;
    }

    /// <summary>A fresh record for a pair never asked before.</summary>
    public static FleetProjectReconcile NewRecord(Guid peerNodeId, Guid projectId, Guid requestId, DateTimeOffset now) =>
        new()
        {
            Id = EventReplicationStreamId.ForFleetReconcile(peerNodeId, projectId),
            PeerNodeId = peerNodeId,
            ProjectId = projectId,
            RequestId = requestId,
            AskedAt = now,
        };

    /// <summary>
    /// Points <paramref name="record"/> at a brand-new ask and clears everything the previous one
    /// observed, because the counts describe one exchange rather than a running total: a fresh ask
    /// for the whole project has read no envelopes and applied no records yet, and carrying the old
    /// numbers forward would report a completed reconcile's figures against an ask still in flight.
    /// Used by the automatic re-ask (<paramref name="automatic"/> true, which records
    /// <see cref="FleetProjectReconcile.ReAskedAt"/> and so spends this record's one re-ask) and by
    /// everything that starts a genuinely new exchange instead, which restarts the ladder from the
    /// top: <c>h9k project reconcile</c> by hand, a peer vouched back into the fleet, and a stalled
    /// direction reopened by the peer's own ask arriving. Each of those is a new exchange rather
    /// than a second strike against the old one, so none of them spends the re-ask.
    /// </summary>
    public static void PointAtFreshAsk(
        FleetProjectReconcile record, Guid requestId, bool automatic, DateTimeOffset now)
    {
        record.RequestId = requestId;
        record.AskedAt = now;
        record.ReAskedAt = automatic ? now : null;
        record.FirstAnswerAt = null;
        record.EnvelopesRead = 0;
        record.RecordsApplied = 0;
        record.AnswerEnvelopeCount = null;
        record.HeldTailOnlyStreams = 0;
        record.UnavailableReason = null;
        record.CompletedAt = null;
        record.StalledAt = null;
        record.PeerLeftFleetAt = null;
    }

    /// <summary>One read's own answering envelopes folded in: the first ever answer stamps
    /// <see cref="FleetProjectReconcile.FirstAnswerAt"/> and no later one re-stamps it, and both
    /// counts add to their running totals. Deliberately does not touch
    /// <see cref="FleetProjectReconcile.CompletedAt"/>: an answer that applied everything and an
    /// answer whose every record was already held look identical here, which is exactly why
    /// completion waits for the peer's own terminal envelope. Still tallies after completion, so a
    /// tick that read the terminal envelope before its batches applied catches up on the retry
    /// rather than reporting a completed reconcile with nothing behind it.</summary>
    public static void NoteAnswerEnvelopes(
        FleetProjectReconcile record, int envelopes, int recordsApplied, DateTimeOffset now)
    {
        record.FirstAnswerAt ??= now;
        record.EnvelopesRead += envelopes;
        record.RecordsApplied += recordsApplied;
    }

    /// <summary>The peer's own terminal envelope read: this, and only this, completes a
    /// reconcile.</summary>
    public static void NoteComplete(FleetProjectReconcile record, int envelopeCount, DateTimeOffset now)
    {
        record.AnswerEnvelopeCount = envelopeCount;
        record.CompletedAt = now;
        record.StalledAt = null;
    }

    /// <summary>The peer's own <c>events-unavailable</c> read: it holds nothing matching, which is a
    /// final answer rather than a silence, so the record says so and closes rather than standing
    /// open and being re-asked forever.</summary>
    public static void NoteUnavailable(FleetProjectReconcile record, string reason, DateTimeOffset now)
    {
        record.UnavailableReason = reason;
        record.CompletedAt = now;
        record.StalledAt = null;
    }
}
