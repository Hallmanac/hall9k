using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Replication;

/// <summary>One sweep's own outcome reading one sender's <c>events-request</c>/<c>events-unavailable</c>/
/// <c>events-answer-complete</c> envelopes for one project. <paramref name="ReverseAsksQueued"/> and
/// <paramref name="ReconcilesCompleted"/> are the fleet-reconcile halves (task 252bc5cf): a sibling's
/// own project-history ask reciprocated, and a reconcile closed by the peer's own terminal
/// envelope.</summary>
public sealed record EventCatchUpInboxReadResult(
    int RequestsAnswered, int DeclinesObserved, int ReverseAsksQueued = 0, int ReconcilesCompleted = 0);

/// <summary>
/// The receiving half of the catch-up protocol (idea 202383dc, M2b, task 9408d525): reads
/// <paramref name="senderNodeId"/>'s outbox the same way <c>EventReplicationInbox</c> does — same
/// transport, same chain verification, same per-sender gap-stop rule — but keeps its own cursor
/// (<see cref="EventCatchUpInboxCursor"/>) and looks only at
/// <see cref="MessageKind.EventsRequest"/>/<see cref="MessageKind.EventsUnavailable"/>/
/// <see cref="MessageKind.EventsAnswerComplete"/> envelopes,
/// answering a request addressed to this node via <see cref="EventCatchUpResponder"/> and marking an
/// outstanding request declined when its current candidate says it cannot answer. Kept independent
/// of <c>EventReplicationInbox</c> and <c>MessageInbox</c> for the identical reason those two are
/// independent of each other: a second, narrower reader of the identical outbox ref, at the cost of
/// one extra transport read per moved sender per tick, in exchange for never touching either
/// class's own delicate, heavily-tested flow.
/// <para>
/// Two fleet-reconcile duties ride here too (task 252bc5cf), both of them about the same outbox
/// content this reader already decodes. A project-history request from a fleet sibling earns a
/// reverse ask back, so the pair reconciles in both directions with nobody typing anything; and a
/// peer's own terminal <see cref="MessageKind.EventsAnswerComplete"/> envelope is what marks a
/// reconcile record complete, which is why completion is a fact rather than an inference from
/// whatever happened to apply.
/// </para>
/// </summary>
public sealed class EventCatchUpInbox(
    IMessageTransport transport, EventCatchUpResponder responder, ILogger<EventCatchUpInbox>? logger = null)
{
    public async Task<EventCatchUpInboxReadResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid projectId,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        TrustChain? trustChain,
        CancellationToken cancellationToken)
    {
        Guid cursorId = EventReplicationStreamId.ForCatchUpInboxCursor(senderNodeId, projectId);
        EventCatchUpInboxCursor? cursor = await session.LoadAsync<EventCatchUpInboxCursor>(cursorId, cancellationToken);
        long sinceSeq = cursor?.HighestSeqInspected ?? 0;

        TransportReadResult read = await transport.ReadSinceAsync(repositoryPath, senderNodeId, sinceSeq, cancellationToken, trustChain);
        if (!read.SenderVouched)
        {
            // A not-vouched sender is already named for h9k status by EventReplicationInbox's own
            // read of the identical outbox — nothing further to record here.
            return new EventCatchUpInboxReadResult(0, 0);
        }

        // This project's own ledger-derived key, resolved once — the identical resolution
        // MessageInbox.ReadFromAsync and EventReplicationInbox.ReadFromAsync both apply, so a
        // request or decline carrying some OTHER local project's own key is refused here exactly as
        // it already is on those two other readers of the identical outbox ref (independent pre-PR
        // review, cycle 1, both lenses, medium/low: this was the one reader of the three that skipped
        // the check, so a foreign-keyed bootstrap request was answered in full — this project's whole
        // event history queued and pushed for nothing, since the requester's own EventReplicationInbox
        // refuses the answer on the very key check this reader used to skip).
        string? localProjectKey = await ResolveLocalProjectKeyAsync(session, projectId, trustChain, cancellationToken);

        int answered = 0;
        int declined = 0;
        int reverseAsks = 0;
        int reconcilesCompleted = 0;
        long highestSeqConsidered = sinceSeq;
        foreach (TransportEnvelope raw in read.Envelopes.OrderBy(envelope => envelope.Seq))
        {
            highestSeqConsidered = raw.Seq;

            MessageEnvelopeCodec.DecodeResult decoded = MessageEnvelopeCodec.Decode(raw.Content);
            if (decoded.Outcome != MessageEnvelopeCodec.DecodeOutcome.Parsed)
            {
                continue;
            }

            MessageEnvelopeV1 envelope = decoded.Envelope!;
            if (envelope.Seq != raw.Seq || envelope.FromNode != senderNodeId)
            {
                continue;
            }

            // Checked before the audience match below, the identical order MessageInbox.ReadFromAsync
            // and EventReplicationInbox.ReadFromAsync both apply.
            if (envelope.ProjectKey is { Length: 26 } candidateKey
                && await IsProjectKeyMismatchAsync(session, projectId, candidateKey, localProjectKey, cancellationToken))
            {
                logger?.LogWarning(
                    "Sender {SenderNodeId}'s catch-up envelope {Seq} carries a project key that does not match "
                    + "this project's own ledger-derived key, refused", senderNodeId, raw.Seq);
                continue;
            }

            if (!envelope.To.Matches(myNodeId, myOwnerFingerprint))
            {
                continue;
            }

            if (envelope.Kind == MessageKind.EventsRequest)
            {
                EventReplicationCodec.EventsRequestRecord? request = EventReplicationCodec.DecodeRequest(envelope.Body);
                if (request is null)
                {
                    logger?.LogWarning(
                        "Events-request envelope {Seq} from sender {SenderNodeId} had a malformed body — skipped",
                        raw.Seq, senderNodeId);
                    continue;
                }

                // The reverse ask (task 252bc5cf), queued BEFORE the answer below rather than after
                // it: the answer can be megabytes of batches, and the reciprocal ask is the cheap
                // half that must not be lost to a mid-answer failure. A sibling's project-history
                // request means that sibling is reconciling this project, and reconciling is
                // symmetric — each side holds history the other does not — so this node asks back
                // instead of waiting for its own sweep to reach the same conclusion a tick later.
                // Guarded by the record alone, which is what makes a loop impossible: the record
                // written here means this node's own sweep will not ask again, and the sibling
                // reading this ask already holds a record for this node from its own first ask.
                // Never for a request from outside this owner's own fleet — a teammate's node gets
                // its answer and nothing more, since it is not this owner's business to hold
                // everything a teammate holds.
                if (trustChain is not null
                    && request is { ForStreamId: null, ForOriginNodeId: null, SinceGlobalSequence: not null }
                    && FleetReconcileRules.IsFleetPeer(trustChain, myOwnerFingerprint, senderNodeId, myNodeId)
                    && await EventCatchUpCoordinator.RequestFleetReconcileIfUnrecordedAsync(
                        session, projectId, senderNodeId, myNodeId, myOwnerFingerprint, now, cancellationToken))
                {
                    reverseAsks++;
                }

                await responder.AnswerAsync(
                    session, repositoryPath, myNodeId, myOwnerFingerprint, projectId, senderNodeId, request, now,
                    trustChain, cancellationToken);
                answered++;
            }
            else if (envelope.Kind == MessageKind.EventsUnavailable)
            {
                EventReplicationCodec.EventsUnavailableRecord? unavailable = EventReplicationCodec.DecodeUnavailable(envelope.Body);
                if (unavailable is null)
                {
                    continue;
                }

                EventCatchUpRequest? outstanding = await session.LoadAsync<EventCatchUpRequest>(unavailable.RequestId, cancellationToken);
                if (outstanding is null)
                {
                    continue;
                }

                // Recorded before any of the three arms below decide what to DO about it, and
                // recorded whatever they decide — including on a request that already closed. A
                // decline is an observation about which node in the fleet holds nothing for this
                // ask, and that stays worth knowing after the ask ended: on a broadcast every
                // member answers, the first answer closes the request, and dropping the rest would
                // leave h9k status able to name one declining node out of however many actually
                // said so.
                bool newlyRecorded = EventCatchUpDeclineLog.Record(
                    outstanding, senderNodeId, now, unavailable.Reason);

                // IsOutstanding rather than the two fields it used to read: a request a later ask
                // closed out as superseded (h9k task pull --again, task 9eb5b245) is no longer an
                // ask at all, and a decline arriving for it must neither advance its cascade nor
                // stamp AnsweredAt onto it — nothing answered it, and the audit trail already
                // records what actually happened to it. The decline itself is still recorded, above;
                // it is the newlyRecorded arm below that stores it without touching the request.
                if (outstanding is { IsOutstanding: true } && outstanding.CurrentCandidateNodeId == senderNodeId)
                {
                    await EventCatchUpCoordinator.AdvanceToNextCandidateAsync(
                        session, myNodeId, myOwnerFingerprint, outstanding, now, cancellationToken);
                    if (outstanding.Exhausted)
                    {
                        // The last ranked candidate declined, so the cascade ran out on a decline
                        // rather than on a timeout. Recorded here rather than inside
                        // AdvanceToNextCandidateAsync, which a silent timeout also calls and which
                        // would have to guess which of the two ended the request. Looked up by
                        // sender rather than taken off the end of the list, so a re-read of a
                        // decline this request already holds names the decline that node actually
                        // sent rather than whichever entry happens to be last.
                        outstanding.ClosedByDecline = outstanding.Declines
                            .Last(decline => decline.DeclinedByNodeId == senderNodeId);
                        session.Store(outstanding);
                    }

                    declined++;
                }
                else if (outstanding is { IsOutstanding: true, Candidates.Count: 0 })
                {
                    // A BROADCAST request — a whole-project history pull (h9k project pull), a
                    // stream request (h9k task pull, or the ledger-record adoption path) — has no
                    // current candidate to match this sender against, so the arm above can never
                    // see its decline, and it has no cascade to advance and no timeout behind it
                    // either. A peer holding nothing that matches answers exactly once, with this
                    // decline, and nothing else ever arrives from it — so closing the ask on it is
                    // what keeps it from standing forever, reported by h9k status for good and
                    // refusing every later ask for the same thing through the coordinator's own
                    // already-outstanding guards (independent pre-PR review, cycle 1, both lenses,
                    // medium, for the project pull; cycle 4, adversarial lens, medium, for the
                    // stream request — one mistyped character of a task id queued a broadcast no
                    // member could ever answer and no command could ever clear). The reason is
                    // recorded, and closing early costs nothing a human cannot redo: another
                    // member's own answer still applies when it lands, since an events batch is
                    // applied on arrival and never gated on a request document, and a re-run of
                    // the command simply asks again.
                    outstanding.AnsweredAt = now;
                    outstanding.ClosedByDecline = outstanding.Declines
                        .Last(decline => decline.DeclinedByNodeId == senderNodeId);
                    session.Store(outstanding);
                    declined++;
                }
                else if (newlyRecorded)
                {
                    // A decline for a request that already ended — the second and later members of
                    // a project answering a broadcast the first one's decline already closed, or a
                    // peer answering a cascade that has since moved on or been answered for real.
                    // Nothing about the request's own state changes; the decline is stored because
                    // it is a fact about the fleet, and h9k status naming one declining node when
                    // three actually declined would read as "one peer could not help" rather than
                    // "nobody could".
                    session.Store(outstanding);
                }

                // task 252bc5cf: a fleet reconcile's own record says so too, in the peer's own
                // words. A peer holding nothing for the project has given its final answer rather
                // than gone quiet, so the record closes on it — otherwise the retention clock would
                // re-ask a peer that already told this node it has nothing, every two days,
                // forever. Matched on the request id AND on this sender being the record's own
                // peer, so neither a decline against a superseded request (a re-ask already
                // replaced it) nor some other member's envelope can close the live exchange.
                FleetProjectReconcile? declining = await LoadReconcileForRequestAsync(
                    session, projectId, senderNodeId, unavailable.RequestId, cancellationToken);
                if (declining is { CompletedAt: null })
                {
                    FleetReconcileRules.NoteUnavailable(declining, unavailable.Reason, now);
                    session.Store(declining);
                }
            }
            else if (envelope.Kind == MessageKind.EventsAnswerComplete)
            {
                EventReplicationCodec.EventsAnswerCompleteRecord? complete =
                    EventReplicationCodec.DecodeAnswerComplete(envelope.Body);
                if (complete is null)
                {
                    logger?.LogWarning(
                        "Events-answer-complete envelope {Seq} from sender {SenderNodeId} had a malformed body — skipped",
                        raw.Seq, senderNodeId);
                    continue;
                }

                // The one thing that completes a reconcile (task 252bc5cf). Nothing else can: a
                // whole-project answer over history this node already holds applies nothing at all,
                // so counts and silence are indistinguishable from this side. The envelope count is
                // kept beside this node's own count of envelopes read rather than replacing it —
                // the two disagreeing is how an answer partly lost to an outbox squash reads, and
                // that is worth seeing. This reader and the events reader keep independent cursors,
                // so a tick whose events read failed can complete a record before its batches
                // applied; the counts catch up on the retry, because reading an answering envelope
                // still tallies after completion.
                FleetProjectReconcile? completing = await LoadReconcileForRequestAsync(
                    session, projectId, senderNodeId, complete.RequestId, cancellationToken);
                if (completing is { CompletedAt: null })
                {
                    FleetReconcileRules.NoteComplete(completing, complete.EnvelopeCount, now);
                    session.Store(completing);
                    reconcilesCompleted++;
                }
            }
        }

        highestSeqConsidered = Math.Max(highestSeqConsidered, read.HighestSeqInspected);
        if (highestSeqConsidered > sinceSeq)
        {
            session.Store(new EventCatchUpInboxCursor
            {
                Id = cursorId,
                SenderNodeId = senderNodeId,
                ProjectId = projectId,
                HighestSeqInspected = highestSeqConsidered,
            });
        }

        await session.SaveChangesAsync(cancellationToken);
        return new EventCatchUpInboxReadResult(answered, declined, reverseAsks, reconcilesCompleted);
    }

    /// <summary>The fleet reconcile <paramref name="requestId"/> is the live ask of, with
    /// <paramref name="senderNodeId"/> as its own peer — or null when none is, which is the ordinary
    /// case for every catch-up shape that is not a reconcile (a gap-fill, a bootstrap, a stream
    /// request, a hand pull), and also for a reconcile already re-asked under a newer request id.
    /// <para>
    /// Both halves of the match carry weight. The request id is what keeps an answer to a superseded
    /// ask from closing or declining the exchange currently in flight. The peer is what keeps
    /// SOMEBODY ELSE from closing it: every project member's own sweep reads every seq above its
    /// cursor on a shared outbox ref and decodes each envelope before checking the audience, so a
    /// teammate's node under a different owner learns this reconcile's request id simply by reading
    /// the ask go past, and could otherwise address this node an <c>events-unavailable</c> or an
    /// <c>events-answer-complete</c> carrying that id and close a reconcile the real peer never
    /// answered — and an unavailable reason also spends the record's one automatic re-ask, so the
    /// exchange would end on a sentence its peer never said (independent pre-PR review, cycle 1,
    /// adversarial lens, medium). The record already names the one node entitled to answer it.
    /// </para></summary>
    private static async Task<FleetProjectReconcile?> LoadReconcileForRequestAsync(
        IDocumentSession session, Guid projectId, Guid senderNodeId, Guid requestId,
        CancellationToken cancellationToken) =>
        await session.Query<FleetProjectReconcile>()
            .Where(record => record.ProjectId == projectId && record.RequestId == requestId
                && record.PeerNodeId == senderNodeId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>This project's own ledger-derived key: the live trust chain's own value when a
    /// caller actually computed one this tick, falling back to this install's own local mirror
    /// (<see cref="ProjectDetails.ProjectKey"/>) only when it did not. Null when neither source has
    /// one yet, in which case a mismatch can never be judged and nothing carrying a project key is
    /// refused on that basis alone; the identical resolution <c>MessageInbox.ReadFromAsync</c>'s own
    /// <c>ResolveLocalProjectKeyAsync</c> applies.</summary>
    private static async Task<string?> ResolveLocalProjectKeyAsync(
        IDocumentSession session, Guid projectId, TrustChain? trustChain, CancellationToken cancellationToken)
    {
        if (trustChain?.ProjectKey is { } ledgerKey)
        {
            return ledgerKey;
        }

        ProjectDetails? localProject = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        return localProject?.ProjectKey;
    }

    /// <summary>Whether a genuinely 26-character <paramref name="candidateKey"/> fails to name this
    /// project: a direct mismatch against <paramref name="localProjectKey"/> when this install
    /// already knows it, or a hit against some OTHER local project's own recorded key when it does
    /// not. The identical check <c>MessageInbox.ReadFromAsync</c>'s own
    /// <c>IsProjectKeyMismatchAsync</c> applies.</summary>
    private static async Task<bool> IsProjectKeyMismatchAsync(
        IDocumentSession session, Guid projectId, string candidateKey, string? localProjectKey, CancellationToken cancellationToken)
    {
        if (localProjectKey is not null)
        {
            return candidateKey != localProjectKey;
        }

        ProjectDetails? resolvedByKey = await session.Query<ProjectDetails>()
            .Where(candidate => candidate.ProjectKey == candidateKey)
            .FirstOrDefaultAsync(cancellationToken);
        return resolvedByKey is not null && resolvedByKey.Id != projectId;
    }
}
