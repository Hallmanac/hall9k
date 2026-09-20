using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Replication;

/// <summary>One sweep's own outcome reading one sender's <c>events-request</c>/<c>events-unavailable</c>
/// envelopes for one project.</summary>
public sealed record EventCatchUpInboxReadResult(int RequestsAnswered, int DeclinesObserved);

/// <summary>
/// The receiving half of the catch-up protocol (idea 202383dc, M2b, task 9408d525): reads
/// <paramref name="senderNodeId"/>'s outbox the same way <c>EventReplicationInbox</c> does — same
/// transport, same chain verification, same per-sender gap-stop rule — but keeps its own cursor
/// (<see cref="EventCatchUpInboxCursor"/>) and looks only at
/// <see cref="MessageKind.EventsRequest"/>/<see cref="MessageKind.EventsUnavailable"/> envelopes,
/// answering a request addressed to this node via <see cref="EventCatchUpResponder"/> and marking an
/// outstanding request declined when its current candidate says it cannot answer. Kept independent
/// of <c>EventReplicationInbox</c> and <c>MessageInbox</c> for the identical reason those two are
/// independent of each other: a second, narrower reader of the identical outbox ref, at the cost of
/// one extra transport read per moved sender per tick, in exchange for never touching either
/// class's own delicate, heavily-tested flow.
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

                await responder.AnswerAsync(
                    session, myNodeId, myOwnerFingerprint, projectId, senderNodeId, request, now, cancellationToken, trustChain);
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
                if (outstanding is { AnsweredAt: null, Exhausted: false } && outstanding.CurrentCandidateNodeId == senderNodeId)
                {
                    outstanding.DeclinedReason = unavailable.Reason;
                    await EventCatchUpCoordinator.AdvanceToNextCandidateAsync(
                        session, myNodeId, myOwnerFingerprint, outstanding, now, cancellationToken);
                    declined++;
                }
                else if (outstanding is { AnsweredAt: null, Exhausted: false, Candidates.Count: 0 })
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
                    outstanding.DeclinedReason = unavailable.Reason;
                    outstanding.AnsweredAt = now;
                    session.Store(outstanding);
                    declined++;
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
        return new EventCatchUpInboxReadResult(answered, declined);
    }

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
