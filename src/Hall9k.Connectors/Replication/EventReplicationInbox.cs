using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Owner;
using Hall9k.Domain.Features.Project.Events;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Replication;

/// <summary>One sweep's own outcome reading one sender's <c>events</c>-kind envelopes for one
/// project. <see cref="StalledAtSeq"/> is <see cref="TransportReadResult.StalledAtSeq"/>, or, absent
/// that, <see cref="TransportReadResult.PrunedBelowSeq"/> — a numeric gap in this sender's own
/// outbox this call could not even inspect (idea 202383dc, M2b, task 9408d525: "a node that finds a
/// gap in a sender's sequence... asks a peer"), OR a range a squash's own low-water mark skipped
/// this call past without ever inspecting it either (independent pre-PR review, cycle 1, both
/// lenses, medium: a peer may still hold that range even though the sender's own outbox no longer
/// does, and folding it in here is what lets the mark's own cursor-advance-past-a-gap never quietly
/// drop the one recovery path this platform has for it). Either way, the trigger
/// <c>Hall9k.Connectors.Replication.EventCatchUpCoordinator.RequestGapFillAsync</c> is built for.</summary>
public sealed record EventReplicationReadResult(bool SenderIgnored, int EventsApplied, long? StalledAtSeq = null);

/// <summary>
/// The inbound half of event replication (idea 202383dc, M2a): reads <paramref name="senderNodeId"/>'s
/// outbox the same way <c>MessageInbox.ReadFromAsync</c> does — same transport, same chain
/// verification, same per-sender gap-stop rule — but keeps its own cursor
/// (<see cref="EventReplicationInboxCursor"/>) and looks only at <see cref="MessageKind.Events"/>
/// envelopes, applying each one's own batch rather than recording an ordinary received message:
/// an events envelope never shows up in <c>h9k messages</c>. Kept independent of
/// <c>MessageInbox</c> rather than folded into it, deliberately: this is a second, narrower reader
/// of the identical outbox ref, at the cost of one extra transport read per moved sender per tick,
/// in exchange for never touching that class's own delicate, heavily-tested flow.
/// <para>
/// Idempotent by origin event id (<see cref="ReplicatedEventRecord"/>): a re-delivered batch finds
/// every record already stored and applies nothing a second time, and a duplicate origin event later
/// in the SAME read — ordinary once the outbox re-batches an already-sent event — is caught the same
/// way, tracked uncommitted for the identical reason <c>streamsStartedThisRead</c> tracks streams:
/// <c>LightweightSession.LoadAsync</c> alone only ever sees committed rows. Each applied event is
/// appended to a local stream, starting it when it does not exist yet, with no decider in the way —
/// it is a fact this node is recording happened elsewhere, not a decision this node is making. That
/// stream is <see cref="EventReplicationCodec.ReplicatedEventRecord.StreamId"/> as sent for a Task,
/// Idea, Epic, or Run event (those ids ARE shared across installs) and for one of the Project
/// aggregate's own per-install lifecycle events (a foreign coordinate, deliberately never this
/// receiver's own stream), but this node's OWN local Project stream id for the Project aggregate's
/// team-facing events — the sender's own id there is a foreign coordinate, never this receiver's
/// (<see cref="ProjectStreamReplicationRules"/>).
/// </para>
/// <para>
/// A stream's events only ever arrive in order, and this class now enforces that rather than
/// assuming it: an event is appended, never inserted, so a record belonging BEHIND one the local
/// stream already holds from the same origin is refused with a warning instead
/// (<see cref="OriginHighWaterAsync"/>). The shape that reaches that check is a stream this node
/// holds only the post-switch-on TAIL of, whose pre-switch-on head an explicit pull then serves
/// (task a56cf16e, Decisions Log #235) — applying that head would replay the stream backwards and
/// leave the aggregate reading as it did at its creation. The Project aggregate's own team-facing
/// events are exempt (<see cref="ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/>):
/// they land on this node's own Project stream, which no origin owns, and their readers order them
/// by each event's own stamp instead, so no arrival order can leave one stale.
/// </para>
/// <para>
/// Each record this class applies is saved on its own, rather than every record a read's own
/// batch of envelopes carries being staged into one save at the end: a record whose own inline
/// projection throws (<see cref="JasperFx.Events.Daemon.ApplyEventException"/> — a genuinely
/// malformed fact, or a defect in whatever projection its event type feeds) is caught, logged
/// with its own event id and sender, and recorded as handled without ever landing, so this exact
/// event is never retried and never blocks the records around it (<see cref="ApplyAsync"/>). The
/// cursor still advances past the envelope that carried it either way — <see cref="ReadFromAsync"/>
/// tracks the highest sequence considered independently of whether any single record inside it
/// applied — so a poison event can cost this node that one fact, never a sender's whole future.
/// </para>
/// </summary>
/// <param name="chainReader">
/// Reads the SOURCE project's own ledger trust chain for the one cross-project append the
/// stream-ownership guard admits: an event on an idea this node holds under one of its projects,
/// arriving through the outbox of the project the idea was moved to (<see cref="IdeaAssignedToProject"/>,
/// <see cref="EvaluateCrossProjectIdeaEvent"/>). Null refuses every such event, the same as any
/// other cross-project append; the daemon always supplies one.
/// </param>
public sealed class EventReplicationInbox(
    IMessageTransport transport, ILogger<EventReplicationInbox>? logger = null, ILedgerChainReader? chainReader = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolves an EXISTING target stream's own project inside <see cref="ApplyAsync"/> — the
    /// stream-ownership guard (idea 6be68ee2, the trust-ledger review's own stream-ownership item):
    /// a replicated event may only land on a stream its own project owns. Stateless and cheap to
    /// construct fresh, the identical reason every other caller of this resolver
    /// (<c>EventCatchUpResponder</c>, <c>EventReplicationOutbox</c>, every test) just news one up
    /// rather than taking it as a constructor dependency.
    /// </summary>
    private readonly ReplicationProjectResolver projectResolver = new();

    public async Task<EventReplicationReadResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid projectId,
        Guid myNodeId,
        string myOwnerFingerprint,
        DateTimeOffset now,
        TrustChain trustChain,
        CancellationToken cancellationToken)
    {
        Guid cursorId = EventReplicationStreamId.ForInboxCursor(senderNodeId, projectId);
        EventReplicationInboxCursor? cursor = await session.LoadAsync<EventReplicationInboxCursor>(cursorId, cancellationToken);
        long sinceSeq = cursor?.HighestSeqInspected ?? 0;

        TransportReadResult read = await transport.ReadSinceAsync(repositoryPath, senderNodeId, sinceSeq, cancellationToken, trustChain);
        if (!read.SenderVouched)
        {
            logger?.LogWarning(
                "Sender {SenderNodeId}'s outbox could not be vouched for by that node's own node file — "
                + "its events envelopes are ignored", senderNodeId);
            session.Store(new EventReplicationInboxCursor
            {
                Id = cursorId,
                SenderNodeId = senderNodeId,
                ProjectId = projectId,
                HighestSeqInspected = sinceSeq,
                // Nothing was inspected on this sweep, so the stamp stays on whatever sweep last
                // advanced the cursor: a refusal to vouch says nothing about when this node last
                // genuinely heard from that outbox, and overwriting the stamp here would tell an
                // operator reading h9k task take --force's evidence line that it heard just now.
                HighestSeqInspectedAt = cursor?.HighestSeqInspectedAt,
                SenderIgnored = true,
                IgnoredReason = read.NotVouchedReason ?? "no node file vouches for this sender's outbox",
                IgnoredForProjectKeyMismatch = false,
                IgnoredAt = now,
            });
            await session.SaveChangesAsync(cancellationToken);
            return new EventReplicationReadResult(SenderIgnored: true, EventsApplied: 0);
        }

        // The verified key this exact read resolved for senderNodeId — never the sender's own
        // wire-level claim — is what ApplyAsync's own gate checks a gated event's sender against
        // (idea 6be68ee2, trust-ledger findings 1 and 6). Read is vouched at this point, so the
        // transport always resolved one; null is kept possible only for the type's own honesty
        // toward a transport that somehow did not (ApplyAsync then treats it as no key at all,
        // never as "skip the gate").
        string? senderFingerprint = read.SenderFingerprint;

        int applied = 0;
        long highestSeqConsidered = sinceSeq;
        // Tracks every stream this call has already issued a StartStream for, uncommitted:
        // FetchStreamStateAsync only ever sees what is actually saved, so a second record for the
        // same stream later in the identical batch (ordinary for a task with several events in one
        // sweep) would see no stream yet and try to StartStream it again, which Marten refuses as a
        // collision within one session's own pending changes — the exact shape
        // MessageSweepEngine.PersistUnverifiedWritesAsync's own doc already names as a defect this
        // feature must not repeat.
        HashSet<Guid> streamsStartedThisRead = [];
        // Tracks every origin event id this call has already applied, uncommitted: LightweightSession's
        // own LoadAsync only ever sees committed rows, so a second copy of the identical origin event
        // later in the same read (ordinary once EventReplicationOutbox re-batches an event already
        // sent — independent pre-PR review, cycle 3, adversarial lens) would find no stored
        // ReplicatedEventRecord yet and apply a duplicate. The same uncommitted-visibility gap
        // streamsStartedThisRead already exists to close for StartStream collisions.
        HashSet<Guid> originEventIdsAppliedThisRead = [];
        // This read's own uncommitted view of each origin's highest applied sequence
        // (idea 202383dc, M2b): seeded lazily, per origin, from EventOriginProgress the first time
        // that origin is seen this read, then kept current here rather than re-loaded — the
        // identical uncommitted-visibility gap streamsStartedThisRead already exists to close,
        // since a LightweightSession's own LoadAsync only ever sees committed rows.
        Dictionary<Guid, long> originProgressThisRead = [];
        // This project's own ledger-derived key, resolved once, preferring the live trust chain
        // over this install's own possibly-stale local mirror, the identical resolution
        // MessageInbox.ReadFromAsync's own ResolveLocalProjectKeyAsync applies.
        string? localProjectKey = await ResolveLocalProjectKeyAsync(session, projectId, trustChain, cancellationToken);
        // Set the moment any envelope this read inspects carries a project key that does not match
        // this project's own ledger-derived key above (idea 202383dc, M2; Brian's ruling
        // 2026-09-17: a project's identity no longer depends on which ledger a message arrived
        // through), refused rather than applied, and named the identical way an unvouched sender
        // already is (h9k status's own WriteReplicatedEventsIgnoredSendersAsync reads
        // EventReplicationInboxCursor.SenderIgnored regardless of which reason set it). Once this
        // project has a key of its own, a null envelope key or one that is not shaped like a
        // 26-character ULID is refused the identical way (ProjectKeyMismatch.IsMismatchAsync, idea
        // 6be68ee2, trust-ledger finding 13); only a project with no key of its own yet reads either
        // shape as "no opinion" (MessageEnvelopeV1.ProjectKey's own doc).
        bool projectKeyMismatch = false;
        string? projectKeyMismatchReason = null;
        // Every stream this read's own applied batches named, regardless of whether ApplyAsync
        // actually stored anything new for it (a re-delivered, already-applied stream still counts
        // as answered): the only signal available to close a BROADCAST catch-up request
        // (EventCatchUpRequest.Candidates empty, EventCatchUpRequest.ForStreamId set), which has no
        // single current candidate to match a sender against below (independent pre-PR review,
        // cycle 1, both lenses, medium).
        HashSet<Guid> streamIdsAnsweredThisRead = [];
        // Every catch-up request this read saw an answer to, by request id: only
        // EventCatchUpResponder.AnswerAsync ever addresses an events envelope to THIS NODE ALONE
        // (MessageAudience.Node(requesterNodeId)) — an ordinary outbox flush is always
        // MessageAudience.Project — and it stamps the answered request's own id into that
        // envelope's about field. The whole-project history pull below closes on its own id
        // appearing here: that shape names no stream to match and its answer may apply nothing at
        // all (independent pre-PR review, cycle 1, both lenses, medium), and matching on the id
        // rather than on "some node-addressed answer went by" is what keeps a SIBLING request's
        // answer — a task pull's, say, arriving in the same read — from closing the pull before any
        // peer has answered it (independent pre-PR review, cycle 4, conformance lens, low).
        HashSet<Guid> catchUpRequestIdsAnsweredThisRead = [];
        // The same signal from a peer on a build that predates the stamped id above (task
        // a56cf16e): such a peer decodes a project pull's request as a bootstrap — the
        // SinceGlobalSequence field it does not know about defaults to null — and answers it with
        // an events envelope carrying no about at all. Closing on that is still better than
        // standing forever, and it is the only case left that a sibling answer can close early.
        bool unattributedCatchUpAnswerThisRead = false;
        // How much of a fleet reconcile's own answer actually arrived and applied this read, per
        // request id (task 252bc5cf): one entry per answering envelope read, and the records each
        // one applied. Kept per envelope rather than as one total because the two numbers answer
        // different questions — "did the answer reach me" and "did it teach me anything" — and an
        // envelope whose every record this node already held is the ordinary shape of the second
        // being zero while the first is not.
        Dictionary<Guid, (int Envelopes, int RecordsApplied)> reconcileTallyThisRead = [];
        // Per stream, the highest origin sequence this node already holds from each origin node —
        // read once per stream per read, before anything is appended to it, and kept current as
        // records land. See ApplyAsync's own doc for the invariant it enforces.
        Dictionary<Guid, Dictionary<Guid, long>> originHighWaterByStream = [];
        // Every stream a record failed to START this read (ApplyAsync's own ApplyEventException
        // catch, reached while streamExists was false): Marten auto-materialises a document for the
        // very next event this node applies to that same stream regardless of that event's own
        // type — the identical "starts a document even with no matching Create" behaviour
        // IdeaDetailsProjection's own doc already relies on for legitimate out-of-order delivery —
        // and here there is no true genesis event still to come that would ever repopulate it, since
        // the one that failed is now permanently skipped. A later record in the SAME read for a
        // stream that never actually started is refused the identical way, rather than left to
        // auto-vivify a document nothing will ever repair. This set is this read's own uncommitted
        // cache of that fact — ApplyAsync also checks the PERSISTED form
        // (a stored ReplicatedEventRecord with Applied false for this stream id) before ever
        // starting a stream, so a follow-up arriving in a LATER read, after the failed genesis's
        // own record already committed, is refused the identical way (independent pre-PR review,
        // cycle 1, both lenses, medium: this set alone lapses the moment one read ends).
        HashSet<Guid> streamsThatFailedToStartThisRead = [];
        // Every stream a genesis started fresh THIS READ, whose own previously-held tail (if any)
        // still needs replaying (ApplyAsync's own doc on why the replay itself is deferred rather
        // than run inline the moment the genesis lands). Drained once, below, only after this whole
        // read's own batches have all landed in their own order.
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead = [];
        // Every origin node id whose gated project-settings-shaped event
        // (ProjectStreamReplicationRules.IsProjectAggregateStreamEvent) this read dropped without
        // recording (GatedEventVerdict.DroppedWithoutRecording) — forwarded here by a sender this
        // gate does not currently vouch for as an owner. The true origin, or some other sender the
        // gate DOES allow, may still deliver it later (ApplyAsync's own doc on that verdict), but
        // only if whatever outstanding request actually covers it is left standing rather than
        // closed by the REST of this same answer applying (independent pre-PR review, cycle 1,
        // adversarial lens, medium: a request closed on partial content never asks for the missing
        // gated event again).
        HashSet<Guid> gatedDropOriginNodeIdsThisRead = [];
        // The matching stream ids for the same drops — always this receiver's own local Project
        // stream for every one of the six gated event types
        // (ProjectStreamReplicationRules.IsProjectAggregateStreamEvent's own doc: each merges onto
        // it), so this only ever holds projectId.
        HashSet<Guid> gatedDropStreamIdsThisRead = [];
        // Every task id a Task/Run act's own conditional gate held for this read, waiting on a
        // fact (its own assignment or holder, or, for a Run act, its task at all) that has not
        // replicated here yet (idea 6be68ee2, trust-ledger finding 5) — asked for below, after this
        // read's own save, the identical deferred-ask shape TaskDependencyCatchUp's own doc already
        // uses for a missing dependency stream.
        HashSet<Guid> taskActCatchUpAskTaskIdsThisRead = [];
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

            // Checked before the Events-kind filter below, the identical order MessageInbox.ReadFromAsync
            // applies (independent pre-PR review, cycle 1, both lenses, low): a mismatch found only
            // AFTER the kind filter lets an ordinary non-events envelope stamped with the same foreign
            // key clear a standing mismatch mark below without its own key ever being examined, since
            // highestSeqConsidered still advances past it either way.
            if (await ProjectKeyMismatch.IsMismatchAsync(
                session, projectId, envelope.ProjectKey, localProjectKey, cancellationToken))
            {
                projectKeyMismatch = true;
                string keyDescription = envelope.ProjectKey switch
                {
                    null => "no project key at all",
                    { Length: 26 } wellFormed => $"project key {wellFormed}",
                    string malformed => $"a malformed project key ({malformed})",
                };
                projectKeyMismatchReason =
                    $"events envelope {raw.Seq} carries {keyDescription}, which does not match this project's "
                    + "own ledger-derived key, refused rather than applied";
                logger?.LogWarning(
                    "Sender {SenderNodeId}'s events envelope {Seq} carries a project key that does not match "
                    + "this project's own ledger-derived key (or is missing or malformed), refused",
                    senderNodeId, raw.Seq);
                continue;
            }

            // A catch-up answer (EventCatchUpResponder.AnswerAsync) addresses its own "events"
            // envelope to the one requester by node id, but every OTHER project member's own sweep
            // reads the identical answering node's outbox ref too — with no audience check, each of
            // them would apply the same forwarded batch onto its own store, including the batch's
            // own true origin node, which holds no ReplicatedEventRecord for events it produced
            // natively and would re-append them as an undeduped second copy onto its own stream
            // (independent pre-PR review, cycle 1, adversarial lens, high). On main, an ordinary
            // outbox flush's own "events" envelope was always MessageAudience.Project, so this check
            // was a no-op for it — Matches("project", ...) always returned true — and only ever
            // refused a catch-up answer addressed elsewhere. Idea 8c5993c5 changed that: the outbox
            // now also addresses a fleet-scoped item's own events to MessageAudience.Owner(<fingerprint>),
            // so this same check does real work for an ordinary flush too, the moment fleet scope is
            // in play — it is what keeps a fleet item off another owner's node, not only a catch-up
            // answer off an uninvolved member's.
            if (!envelope.To.Matches(myNodeId, myOwnerFingerprint))
            {
                continue;
            }

            if (envelope.Kind != MessageKind.Events)
            {
                continue;
            }

            IReadOnlyList<EventReplicationCodec.ReplicatedEventRecord>? batch = EventReplicationCodec.DecodeBatch(envelope.Body);
            if (batch is null)
            {
                logger?.LogWarning(
                    "Events envelope {Seq} from sender {SenderNodeId} had a malformed batch body — skipped",
                    raw.Seq, senderNodeId);
                continue;
            }

            // Guid.Empty is EventOriginStampingListener's own "genuinely unclaimed at append time"
            // sentinel, never a real node's own identity — EventReplicationOutbox.ToRecord and
            // ReplicationEventOriginResolver both now resolve it to the appending node's own id
            // before it ever reaches the wire, but an envelope already in flight when this node
            // upgrades, or one flushed by a peer still on an older build, can still carry it.
            // Normalized to senderNodeId here, once, before anything below reads OriginNodeId: left
            // as Guid.Empty, it reads as a foreign origin under the new admission gate (idea
            // 6be68ee2, trust-ledger findings 4 and 7) and is dropped forever outside any catch-up
            // answer, which permanently loses a genuine native event an unfixed outbox shipped —
            // if that record was a stream's own genesis, every later event on that stream would stay
            // held indefinitely (independent pre-PR review, cycle 1, adversarial lens, medium).
            batch = [.. batch.Select(record =>
                record.OriginNodeId == Guid.Empty ? record with { OriginNodeId = senderNodeId } : record)];

            // Which catch-up request, if any, this envelope answers — resolved once here and read
            // again by the fleet-reconcile tally below, which needs the identical verdict against
            // the identical envelope and must not re-derive it (task 252bc5cf).
            Guid? answeredRequestId = null;
            if (batch.Count > 0 && envelope.To == MessageAudience.Node(myNodeId))
            {
                if (Guid.TryParse(envelope.About, out Guid parsedRequestId))
                {
                    answeredRequestId = parsedRequestId;
                    catchUpRequestIdsAnsweredThisRead.Add(parsedRequestId);
                }
                else
                {
                    unattributedCatchUpAnswerThisRead = true;
                }
            }

            // idea 6be68ee2, trust-ledger findings 4 and 7: a record whose own claimed OriginNodeId
            // differs from senderNodeId is a forwarded record, and a teammate may forward one only
            // inside an answer to a catch-up request THIS node minted, never on its own say-so — the
            // attack that otherwise needs no interaction at all, where one flush lets any sender
            // pre-empt any origin's dedupe with a huge OriginSequence and freeze that stream on every
            // node. A Task or Run act is no exception (independent pre-PR review, cycle 8, terminal
            // lap): a cycle-8 exemption once let a forwarded Task/Run act skip this guard entirely on
            // the theory that ApplyAsync's own classification gate judged it instead, but that gate
            // never inspects the claimed OriginNodeId on its Allowed branch, so a forged forward — a
            // member relaying a TaskRevised on its own task while claiming origin = the owner's node
            // with a huge OriginSequence — applied and froze the owner's own later events on that
            // stream. Every forwarded record, Task and Run acts included, now passes this identical
            // gate. Looked up once per envelope, never per record, since IsForwardedRecordAdmitted's
            // own verdict for every record in this batch depends on the identical matched request —
            // and only when the batch actually needs it, since an ordinary flush
            // (EventReplicationOutbox's own doc: never ships anything but this sender's own
            // native-origin events) never does.
            bool batchHasForeignOrigin = batch.Any(record => record.OriginNodeId != senderNodeId);
            EventCatchUpRequest? matchedForeignOriginRequest =
                batchHasForeignOrigin && answeredRequestId is { } requestIdForForeignOriginMatch
                    ? await session.Query<EventCatchUpRequest>()
                        .Where(candidate => candidate.ProjectId == projectId && candidate.Id == requestIdForForeignOriginMatch)
                        .FirstOrDefaultAsync(cancellationToken)
                    : null;
            // Resolved once per envelope, the identical set EventCatchUpResponder itself would have
            // answered a ForStreamId ask from (that stream plus its own run streams) — narrows
            // IsForwardedRecordAdmitted's own ForStreamId match to what the request actually asked
            // for, rather than any stream id a forwarding sender cares to claim (independent pre-PR
            // review, cycle 1, conformance lens, medium).
            List<Guid> requestedStreamIdsForForeignOriginMatch = matchedForeignOriginRequest?.ForStreamId is { } matchedForStreamId
                ? await EventCatchUpResponder.ResolveRequestedStreamIdsForAdmissionAsync(session, matchedForStreamId, cancellationToken)
                : [];

            int appliedBeforeThisEnvelope = applied;
            foreach (EventReplicationCodec.ReplicatedEventRecord record in batch)
            {
                if (!IsForwardedRecordAdmitted(
                        record, senderNodeId, matchedForeignOriginRequest, trustChain, senderFingerprint,
                        requestedStreamIdsForForeignOriginMatch, now))
                {
                    // Never stored under the claimed OriginEventId: recording a ReplicatedEventRecord
                    // there would let a forger pre-empt the genuine event's own future dedupe the
                    // moment the true origin, or a sender this rule actually allows, delivers it for
                    // real. Not counted into streamIdsAnsweredThisRead either — a broadcast request
                    // must never read this drop as though it had been genuinely answered.
                    logger?.LogWarning(
                        "Replicated event {OriginEventId} claims origin {OriginNodeId}, but arrived from "
                        + "sender {SenderNodeId} outside any catch-up answer this sender is entitled to "
                        + "speak in for this project — dropped, never applied", record.OriginEventId,
                        record.OriginNodeId, senderNodeId);
                    continue;
                }

                streamIdsAnsweredThisRead.Add(record.StreamId);
                applied += await ApplyAsync(
                    session, record, senderNodeId, projectId, envelope.ProjectKey, streamsStartedThisRead,
                    streamsThatFailedToStartThisRead, originEventIdsAppliedThisRead, originProgressThisRead,
                    originHighWaterByStream, streamsAwaitingHeldTailReplayThisRead, gatedDropOriginNodeIdsThisRead,
                    gatedDropStreamIdsThisRead, taskActCatchUpAskTaskIdsThisRead, trustChain, senderFingerprint, now,
                    cancellationToken);
            }

            // task 252bc5cf: tallied per envelope, after its own batch has applied, and only for an
            // answer stamped with a request id — the held-tail replay below adds to `applied` too
            // and belongs to no single envelope, so attributing it to one would overcount.
            if (answeredRequestId is { } tallyRequestId)
            {
                (int envelopes, int recordsApplied) = reconcileTallyThisRead.GetValueOrDefault(tallyRequestId);
                reconcileTallyThisRead[tallyRequestId] =
                    (envelopes + 1, recordsApplied + (applied - appliedBeforeThisEnvelope));
            }
        }

        // Every genesis this read started fresh replays its own held tail only now, after every
        // envelope this read inspected has already applied in order (ApplyAsync's own doc explains
        // why): a `.ToArray()` snapshot, since nothing this loop replays can legitimately add a NEW
        // entry to the set it is draining — a held-tail replay only ever appends to a stream
        // streamsStartedThisRead already names as existing, so ApplyAsync's own !streamExists branch
        // that populates this set never fires again for it.
        foreach (Guid streamId in streamsAwaitingHeldTailReplayThisRead.ToArray())
        {
            applied += await ApplyHeldTailAsync(
                session, streamId, streamsStartedThisRead, streamsThatFailedToStartThisRead,
                originEventIdsAppliedThisRead, originProgressThisRead, originHighWaterByStream,
                streamsAwaitingHeldTailReplayThisRead, gatedDropOriginNodeIdsThisRead, gatedDropStreamIdsThisRead,
                taskActCatchUpAskTaskIdsThisRead, trustChain, now, cancellationToken);
        }

        highestSeqConsidered = Math.Max(highestSeqConsidered, read.HighestSeqInspected);

        // Whether this sweep actually inspected anything past what the LAST sweep already
        // recorded: once the cursor has advanced past a mismatched envelope, a later sweep with
        // nothing new to read finds read.Envelopes empty and projectKeyMismatch false regardless
        // of the standing refusal: overwriting SenderIgnored from that empty loop would clear the
        // flag the moment after it was set, so h9k status would only ever show the refusal for the
        // one sweep that first saw it (independent pre-PR review, cycle 1, conformance lens,
        // medium). A sweep that inspected nothing new instead carries the previous cursor's own
        // MISMATCH mark forward unchanged, the identical "clears only on a genuine advance past it"
        // rule MessageInboxAggregate.Apply(InboxCursorAdvanced) already applies to
        // IgnoredForVerificationFailure — but a NOT-VOUCHED mark is a different fact (the sender's
        // current vouch status, not a specific envelope) and must clear the moment this sweep
        // proves the sender vouched again, whether or not it also inspected anything new: this
        // block is only reached once read.SenderVouched is already true, so a standing not-vouched
        // mark carried forward unconditionally would never clear again for a sender that stopped
        // sending (independent pre-PR review, cycle 1, both lenses, medium) — the identical
        // distinction MessageInbox.ReadFromAsync's own ConfirmVouched branch draws against
        // IgnoredForVerificationFailure.
        bool inspectedNewContent = highestSeqConsidered > sinceSeq;
        bool stickyMismatch = !inspectedNewContent && cursor is { SenderIgnored: true, IgnoredForProjectKeyMismatch: true };
        bool senderIgnored = inspectedNewContent ? projectKeyMismatch : stickyMismatch;
        string? ignoredReason = inspectedNewContent
            ? (projectKeyMismatch ? projectKeyMismatchReason : null)
            : (stickyMismatch ? cursor?.IgnoredReason : null);
        bool ignoredForProjectKeyMismatch = inspectedNewContent ? projectKeyMismatch : stickyMismatch;
        DateTimeOffset? ignoredAt = inspectedNewContent
            ? (projectKeyMismatch ? now : null)
            : (stickyMismatch ? cursor?.IgnoredAt : null);

        session.Store(new EventReplicationInboxCursor
        {
            Id = cursorId,
            SenderNodeId = senderNodeId,
            ProjectId = projectId,
            HighestSeqInspected = highestSeqConsidered,
            // Stamped only by a sweep that actually got further into this sender's outbox than the
            // last one did (task 054d5ab0). A sweep that inspected nothing new keeps the earlier
            // stamp, so the pair reads as "this node had reached seq N by this time" rather than
            // as "a sweep ran at this time", which is the fact h9k task take --force prints.
            HighestSeqInspectedAt = inspectedNewContent ? now : cursor?.HighestSeqInspectedAt,
            SenderIgnored = senderIgnored,
            IgnoredReason = ignoredReason,
            IgnoredForProjectKeyMismatch = ignoredForProjectKeyMismatch,
            IgnoredAt = ignoredAt,
        });

        if (applied > 0 || streamIdsAnsweredThisRead.Count > 0)
        {
            // idea 202383dc, M2b: an outstanding catch-up request this node is currently waiting on
            // an answer FROM this exact sender is treated as answered the moment new content from
            // it actually applies — an approximation (this sender may not have fully satisfied the
            // gap or the bootstrap), but the honest one available without re-deriving whether every
            // originally-missing event landed: a partial answer is still real progress, and a
            // genuinely still-incomplete gap surfaces again the next time this sender's outbox
            // stalls or the receiver notices the stream still absent.
            IReadOnlyList<EventCatchUpRequest> outstanding = await session.Query<EventCatchUpRequest>()
                .Where(request => request.ProjectId == projectId && request.AnsweredAt == null
                    && request.SupersededAt == null && !request.Exhausted)
                .ToListAsync(cancellationToken);
            foreach (EventCatchUpRequest request in outstanding)
            {
                // A request whose own concern is exactly an origin, a stream, or "everything" that
                // this same read dropped a gated event for without recording
                // (GatedEventVerdict.DroppedWithoutRecording) is left standing here rather than
                // closed on the rest of this answer's content: the true origin, or some other sender
                // this gate DOES allow, may still deliver it, and closing the request on this
                // partial answer would stop the cascade or broadcast from ever reaching one
                // (independent pre-PR review, cycle 1, adversarial lens, medium).
                bool concernIncludesGatedDrop =
                    (request.ForOriginNodeId is { } forOriginNodeIdConcern
                        && gatedDropOriginNodeIdsThisRead.Contains(forOriginNodeIdConcern))
                    || (request.ForStreamId is { } forStreamIdConcern
                        && gatedDropStreamIdsThisRead.Contains(forStreamIdConcern))
                    || (request is { ForOriginNodeId: null, ForStreamId: null }
                        && gatedDropOriginNodeIdsThisRead.Count > 0);
                if (concernIncludesGatedDrop)
                {
                    continue;
                }

                if (request.CurrentCandidateNodeId == senderNodeId && applied > 0)
                {
                    request.AnsweredAt = now;
                    session.Store(request);
                }
                else if (request.Candidates.Count == 0 && request.ForStreamId is { } forStreamId
                    && streamIdsAnsweredThisRead.Contains(forStreamId))
                {
                    // A broadcast request (the ledger-record adoption path) has no single current
                    // candidate to match a sender against — ANY project member's own answer for the
                    // exact stream it asked for closes it, or it would sit IsOutstanding forever,
                    // reported by h9k status as outstanding long after the stream actually arrived
                    // (independent pre-PR review, cycle 1, both lenses, medium).
                    request.AnsweredAt = now;
                    session.Store(request);
                }
                else if (request is { Candidates.Count: 0, ForStreamId: null, SinceGlobalSequence: not null }
                    && (catchUpRequestIdsAnsweredThisRead.Contains(request.Id) || unattributedCatchUpAnswerThisRead))
                {
                    // h9k project pull's own broadcast (task a56cf16e) names no single stream to
                    // match, so the arm above can never close it, and what actually APPLIED cannot
                    // close it either: a pull answered in full with events this node already holds
                    // dedupes every record by origin event id and applies nothing, which is the
                    // ordinary outcome of the "am I missing anything?" pull. Waiting on applied > 0
                    // left such a pull outstanding forever — reported by h9k status for good, and
                    // refusing every later pull at the same or a shallower bound through
                    // EventCatchUpCoordinator.RequestProjectHistoryBroadcastAsync's own guard
                    // (independent pre-PR review, cycle 1, both lenses, medium). A peer's answer
                    // ARRIVING is the honest signal, and an answer stamped with THIS request's own
                    // id is narrow enough to read neither an unrelated live flush nor a sibling
                    // catch-up answer as this pull's (cycle 4, conformance lens, low).
                    request.AnsweredAt = now;
                    session.Store(request);
                }
            }
        }

        // Folded in BEFORE the commit, so the counts land in the very transaction that advances the
        // cursor past the envelopes they describe (independent pre-PR review, cycle 1, adversarial
        // lens, low: a tally committed afterwards is lost for good when that second commit fails,
        // leaving EnvelopesRead permanently short of AnswerEnvelopeCount, which h9k status reads as
        // an answer partly lost to an outbox squash — a failure invented out of a failed write).
        IReadOnlyList<FleetProjectReconcile> tallied = await FoldAnswerTallyAsync(
            session, projectId, senderNodeId, reconcileTallyThisRead, now, cancellationToken);

        await session.SaveChangesAsync(cancellationToken);

        // The held-tail snapshot, and only it, stays after the commit above rather than inside it
        // (task 252bc5cf): it is a query over HeldReplicatedEventRecord, and the replay above
        // DELETES the rows whose genesis finally arrived, which a same-session query would not yet
        // see. A second commit costs nothing a reader can observe, and a failure in it now loses
        // only a count the next answer recomputes from scratch, never the answer tally above.
        await SnapshotHeldTailAsync(session, projectId, tallied, cancellationToken);

        // A task that just landed here may name blocked-by or stacked-on ids whose own streams this
        // node does not hold, and nothing asked for those — TaskDecider.Assign then refuses the
        // assignment for a dependency the platform could have fetched itself (task 9eb5b245). Run
        // after this read's own save rather than inside it: MessageOutbox.QueueAsync saves the
        // session it is handed, so an ask queued mid-read would commit this read's cursor early,
        // alongside a write that has nothing to do with it.
        if (applied > 0)
        {
            IReadOnlyList<TaskDependencyCatchUp.MissingDependency> dependencyAsks =
                await TaskDependencyCatchUp.QueueMissingAsync(
                    session, projectId, streamIdsAnsweredThisRead, myNodeId, myOwnerFingerprint, now,
                    TaskDependencyCatchUp.ReMintCooldown, cancellationToken);
            foreach (TaskDependencyCatchUp.MissingDependency ask in dependencyAsks)
            {
                logger?.LogInformation(
                    "Task {TaskId} landed naming dependency {DependencyStreamId}, whose stream is not held here — "
                    + "an events-request for it is queued", ask.NamedByTaskId, ask.StreamId);
            }

            // idea 6be68ee2, trust-ledger finding 5: a held Task/Run act's own missing fact may have
            // just landed above, whoever sent it — re-checked after every read that applies anything
            // for this project, never only after the read whose own sender's act first held it.
            applied += await ReCheckHeldTaskActsAsync(session, projectId, trustChain, now, cancellationToken);
        }

        // Every task a Task/Run act's own gate held for the first time this read gets one broadcast
        // stream ask for its own task stream, the identical deferred-ask shape
        // TaskDependencyCatchUp's own doc already uses, and for the identical reason run here rather
        // than inside ApplyAsync: an ask queued mid-read would commit alongside a write that has
        // nothing to do with it.
        if (taskActCatchUpAskTaskIdsThisRead.Count > 0)
        {
            EventCatchUpCoordinator coordinator = new();
            foreach (Guid taskId in taskActCatchUpAskTaskIdsThisRead)
            {
                await coordinator.RequestStreamBroadcastAsync(
                    session, projectId, taskId, myNodeId, myOwnerFingerprint, now, cancellationToken,
                    again: false, reMintCooldown: TaskActHoldCatchUpCooldown);
            }
        }

        return new EventReplicationReadResult(SenderIgnored: senderIgnored, applied, read.StalledAtSeq ?? read.PrunedBelowSeq);
    }

    /// <summary>How long a Task/Run act's own held-stream ask holds off a fresh one for the
    /// identical task, should one ever be minted again — the same re-mint shape
    /// <see cref="TaskDependencyCatchUp.ReMintCooldown"/> exists for, scaled down. Not a periodic
    /// re-ask timer of its own: the ask is queued only once, the moment a task's own hold is FIRST
    /// created (<see cref="HoldTaskActAsync"/>'s own doc on <c>taskActCatchUpAskTaskIdsThisRead"</c>);
    /// <see cref="ReCheckHeldTaskActsAsync"/> re-judges an already-held record on every later read
    /// that applies anything for the project, but never asks again on its own. This cooldown only
    /// matters the rarer time a SECOND, distinct record for the identical task lands here while the
    /// first ask is already closed (independent pre-PR review, cycle 1, conformance lens, low: an
    /// earlier version of this doc read as though the ask itself re-fired on a schedule, which would
    /// have asked at most three times inside the 24-hour hold age in <see cref="MaxTaskActHoldAge"/>).
    /// </summary>
    private static readonly TimeSpan TaskActHoldCatchUpCooldown = TimeSpan.FromHours(1);

    /// <summary>
    /// Folds this read's own answering envelopes into the reconcile records they belong to (task
    /// 252bc5cf), and returns the records it touched so the held-tail snapshot below can revisit
    /// exactly those without re-deriving which they were. Nothing is folded into a record whose own
    /// <see cref="FleetProjectReconcile.RequestId"/> no answer in this read names: every other
    /// catch-up shape (a gap-fill, a bootstrap, a stream request, a hand pull) answers no reconcile
    /// at all, and a reconcile already re-asked under a newer request id is no longer the live
    /// exchange.
    /// <para>
    /// Nor into a record whose own peer is not <paramref name="senderNodeId"/>. A reconcile's own
    /// request id travels in the clear past every project member — each one's sweep decodes every
    /// envelope on the shared outbox ref before it checks the audience — so a teammate's node under
    /// a different owner could otherwise address this node a node-audienced events envelope stamped
    /// with that id and have its own batches tallied against a sibling's exchange, the same shape
    /// <c>EventCatchUpInbox.LoadReconcileForRequestAsync</c>'s own peer match refuses for the
    /// terminal and declining envelopes (independent pre-PR review, cycle 1, adversarial lens,
    /// medium; this is that finding's sibling site, found by its own class sweep).
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<FleetProjectReconcile>> FoldAnswerTallyAsync(
        IDocumentSession session, Guid projectId, Guid senderNodeId,
        Dictionary<Guid, (int Envelopes, int RecordsApplied)> tally, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (tally.Count == 0)
        {
            return [];
        }

        IReadOnlyList<FleetProjectReconcile> records = await session.Query<FleetProjectReconcile>()
            .Where(record => record.ProjectId == projectId && record.PeerNodeId == senderNodeId)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, FleetProjectReconcile> byRequestId = [];
        foreach (FleetProjectReconcile record in records)
        {
            byRequestId[record.RequestId] = record;
        }

        List<FleetProjectReconcile> touched = [];
        foreach ((Guid requestId, (int envelopes, int recordsApplied)) in tally)
        {
            if (!byRequestId.TryGetValue(requestId, out FleetProjectReconcile? record))
            {
                continue;
            }

            FleetReconcileRules.NoteAnswerEnvelopes(record, envelopes, recordsApplied, now);
            session.Store(record);
            touched.Add(record);
        }

        return touched;
    }

    /// <summary>
    /// Snapshots onto each record this read just tallied how many streams this node now holds ONLY
    /// as <see cref="HeldReplicatedEventRecord"/>s (task 252bc5cf) — a tail whose genesis no answer
    /// carried, which is the one shape a whole-project answer genuinely cannot repair: a replicated
    /// event is appended, and the head cannot be put in front of a tail already here. Counted and
    /// reported by <c>h9k status</c> rather than silently skipped, because that count is what tells
    /// a human the reconcile landed AND that some streams still need the held-tail ask.
    /// <para>
    /// The count is project-wide as of right now rather than attributed to this one answer, which is
    /// the honest reading: a held record exists precisely because its stream has no genesis here,
    /// and the replay this read performed already deleted every one whose genesis it supplied, so
    /// whatever is left is exactly what this reconcile did not fix.
    /// </para>
    /// </summary>
    private static async Task SnapshotHeldTailAsync(
        IDocumentSession session, Guid projectId, IReadOnlyList<FleetProjectReconcile> tallied,
        CancellationToken cancellationToken)
    {
        if (tallied.Count == 0)
        {
            return;
        }

        IReadOnlyList<Guid> heldStreamIds = await session.Query<HeldReplicatedEventRecord>()
            .Where(held => held.ProjectId == projectId)
            .Select(held => held.StreamId)
            .ToListAsync(cancellationToken);
        int heldTailOnlyStreams = heldStreamIds.Distinct().Count();

        foreach (FleetProjectReconcile record in tallied)
        {
            record.HeldTailOnlyStreams = heldTailOnlyStreams;
            session.Store(record);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Returns 0, applying nothing, when this origin event id is already stored — the
    /// idempotency a re-delivered batch relies on — or already applied earlier in this identical
    /// read, uncommitted (<paramref name="originEventIdsAppliedThisRead"/>): the stored-row check
    /// alone only ever sees committed rows, so a second copy of the same origin event later in the
    /// same read would otherwise find nothing yet and apply a duplicate. Also 0, with a
    /// warning, when appending this record would put it BEHIND an event the local stream already
    /// holds from the same origin — see <paramref name="originHighWaterByStream"/>. Otherwise 1 for
    /// this record alone: a genesis that just started a stream fresh here does NOT replay that
    /// stream's own held tail inline — it only records the stream in
    /// <paramref name="streamsAwaitingHeldTailReplayThisRead"/> for <see cref="ReadFromAsync"/> to
    /// replay once this whole read's own batches have landed (that method's own doc explains why).</summary>
    private async Task<int> ApplyAsync(
        IDocumentSession session, EventReplicationCodec.ReplicatedEventRecord record, Guid senderNodeId, Guid projectId,
        string? originProjectKey, HashSet<Guid> streamsStartedThisRead, HashSet<Guid> streamsThatFailedToStartThisRead,
        HashSet<Guid> originEventIdsAppliedThisRead, Dictionary<Guid, long> originProgressThisRead,
        Dictionary<Guid, Dictionary<Guid, long>> originHighWaterByStream,
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead, HashSet<Guid> gatedDropOriginNodeIdsThisRead,
        HashSet<Guid> gatedDropStreamIdsThisRead, HashSet<Guid> taskActCatchUpAskTaskIdsThisRead,
        TrustChain trustChain, string? senderFingerprint, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!originEventIdsAppliedThisRead.Add(record.OriginEventId))
        {
            return 0;
        }

        if (await session.LoadAsync<ReplicatedEventRecord>(record.OriginEventId, cancellationToken) is not null)
        {
            return 0;
        }

        Type? eventType = ReplicationEventTypeCatalog.Resolve(record.EventTypeName);
        if (eventType is null)
        {
            logger?.LogWarning(
                "Replicated event of type {EventType} (origin {OriginEventId}) is not a type this build "
                + "knows — skipped", record.EventTypeName, record.OriginEventId);
            return 0;
        }

        // A well-behaved sender's own outbox already filters to ProjectScoped, never-the-identity-
        // event events (EventReplicationOutbox.QueuePendingAsync) — checked again here so a sender
        // running an older build, or a misbehaving one, can never make this node apply a NodeScoped
        // event (a node-owner claim, this install's own local settings) sight unseen, nor apply
        // ProjectRegistered itself, which mints a foreign project's own id
        // (ProjectStreamReplicationRules.IsProjectIdentityEvent's own doc). Every other project-
        // scoped event on the Project aggregate's own stream IS eligible — its effective stream id
        // (this receiver's own Project stream for the team-facing subset, the sender's own foreign
        // stream id for the per-install lifecycle events) is decided below, rather than excluded
        // here.
        if (EventScopeRegistry.ClassificationOf(eventType) != EventScope.ProjectScoped
            || ProjectStreamReplicationRules.IsProjectIdentityEvent(eventType))
        {
            logger?.LogWarning(
                "Replicated event of type {EventType} (origin {OriginEventId}) is never eligible to travel — skipped",
                record.EventTypeName, record.OriginEventId);
            return 0;
        }

        // idea 6be68ee2, trust-ledger findings 1 and 6: a project-settings-shaped event — one of
        // ProjectStreamReplicationRules.IsProjectAggregateStreamEvent's own six — changes team
        // policy, records a run skill, or sets a prompt addendum, so it applies here only when the
        // verified sender key this read resolved (senderFingerprint, never anything the wire record
        // itself claims) belongs to some Owner-role project member's own chain FOR senderNodeId
        // specifically. Checked here, well before the "genesis missing, hold it" branch further
        // down: not merely an ordering guard, since none of the six ever reaches that branch at
        // all — IsProjectAggregateStreamEvent is exactly what forces mergesOntoLocalProjectStream
        // true below, which makes genesisRequired false, and the effective stream id these six
        // always merge onto (this receiver's own local Project stream) already exists on any
        // project this gate runs for. So ApplyHeldTailAsync/ReplayHeldTailAsync never replay one of
        // these six at all, gated or not (independent pre-PR review, cycle 1, adversarial lens,
        // low: an earlier version of this comment read as though a held gated record were a real,
        // merely-refused shape rather than a structurally unreachable one).
        GatedEventVerdict gateVerdict =
            EvaluateGatedEvent(trustChain, senderFingerprint, senderNodeId, record.OriginNodeId, eventType);
        if (gateVerdict != GatedEventVerdict.Allowed)
        {
            logger?.LogWarning(
                "Replicated event {OriginEventId} of type {EventType} from sender {SenderNodeId} (fingerprint "
                + "{SenderFingerprint}) is gated to an owner-role project member's own chain, and this sender's "
                + "key does not currently belong to one for this node id — dropped, never applied",
                record.OriginEventId, record.EventTypeName, senderNodeId, senderFingerprint ?? "(none)");

            // Burned (recorded so a retry can never apply it) only when this sender IS the record's
            // own claimed origin — a member signing its own gated event directly. A record whose
            // origin differs from this sender (a legitimate owner event merely forwarded here inside
            // a catch-up answer served by a non-owner peer) is left unrecorded, so it can still apply
            // the moment it arrives from a sender this gate actually allows.
            if (gateVerdict == GatedEventVerdict.DroppedAndRefusedPermanently)
            {
                session.Store(new ReplicatedEventRecord
                {
                    Id = record.OriginEventId,
                    StreamId = projectId,
                    ProjectId = projectId,
                    AppliedAt = now,
                    Applied = false,
                });
                await session.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // Recoverable: this sender merely forwarded somebody else's record, so the true
                // origin — an owner this gate would allow — may still send or forward it later.
                // Recorded here so the outstanding-request closing logic below (ReadFromAsync) never
                // treats a request for this exact origin, stream, or "everything" as satisfied by the
                // rest of this same answer (independent pre-PR review, cycle 1, adversarial lens,
                // medium).
                gatedDropOriginNodeIdsThisRead.Add(record.OriginNodeId);
                gatedDropStreamIdsThisRead.Add(projectId);
            }

            return 0;
        }

        JsonNode? dataNode;
        try
        {
            dataNode = JsonNode.Parse(record.EventDataJson);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(
                exception, "Replicated event {OriginEventId} of type {EventType} failed to deserialize — skipped",
                record.OriginEventId, record.EventTypeName);
            return 0;
        }

        // The project id is a per-install coordinate, never this event's shared identity (the
        // ledger repository is that identity) — rewritten here to the local id this inbox is
        // reading for, on whichever field actually carries it, before the event is ever applied.
        if (dataNode is JsonObject dataObject)
        {
            RewriteProjectIdField(dataObject, projectId);
        }

        object? data;
        try
        {
            data = dataNode?.Deserialize(eventType, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(
                exception, "Replicated event {OriginEventId} of type {EventType} failed to deserialize — skipped",
                record.OriginEventId, record.EventTypeName);
            return 0;
        }

        if (data is null)
        {
            return 0;
        }

        // The Project aggregate's own team-facing events (ProjectTeamSettingsChanged, the
        // membership audit trail) apply to THIS node's own Project stream id, never the sender's —
        // the sender's own id is a foreign coordinate here. Every other project-scoped event (Task,
        // Idea, Epic, Run, and the Project aggregate's own per-install lifecycle events — archive,
        // reactivate, rename, schedule or cancel a purge) keeps its own stream id instead: a
        // Task/Idea/Epic/Run id IS shared across installs, only its own ProjectId field, rewritten
        // above, was ever a per-install coordinate; a lifecycle event's own stream id stays the
        // sender's foreign one deliberately, so a teammate's local archive, rename, or purge
        // decision about THEIR OWN install's copy of the project is recorded as a fact without ever
        // acting on this receiver's own project (independent pre-PR review, cycle 3, conformance
        // lens — ProjectStreamReplicationRules.IsProjectLifecycleEvent's own doc).
        bool mergesOntoLocalProjectStream = ProjectStreamReplicationRules.IsProjectAggregateStreamEvent(eventType);
        Guid effectiveStreamId = mergesOntoLocalProjectStream
            ? projectId
            : record.StreamId;

        bool streamExists = streamsStartedThisRead.Contains(effectiveStreamId)
            || await session.Events.FetchStreamStateAsync(effectiveStreamId, cancellationToken) is not null;

        // Stream-ownership guard (idea 6be68ee2, trust findings 10/12 and the review's own
        // stream-ownership item): a replicated event may only append to a stream its own project
        // owns, never onto a stream belonging to a DIFFERENT project this node also hosts, and a
        // project LIFECYCLE event specifically (ProjectStreamReplicationRules.IsProjectLifecycleEvent
        // — the one family that keeps record.StreamId raw above and skips the genesis requirement
        // below, since a teammate's own archive/rename/purge decision about THEIR install is meant
        // to phantom-stream under their own foreign coordinate) may never land on ANY Project stream
        // this node itself registered (a ProjectRegistered genesis of its own) — not only the one
        // this read happens to be scoped to: a member of two projects this node hosts could otherwise
        // learn the sibling project's own local id from ITS replicated lifecycle events and forge a
        // ProjectPurgeScheduled at that id instead, through the first project's own outbox
        // (independent pre-PR review, cycle 1, both lenses, high — the earlier build here only
        // compared against this read's own projectId). A NON-lifecycle event (Task, Idea, Epic, Run)
        // whose stream resolves as a Project stream at all — this read's own, a sibling's, or a
        // still-phantom one — is refused outright: none of those events ever legitimately belongs on
        // a Project aggregate's own stream (independent pre-PR review, cycle 1, both lenses, medium).
        // Skipped for a stream this read already started itself — streamsStartedThisRead's own
        // membership already means an earlier record in this same read passed this identical gate for
        // it, so a later record for the SAME stream (two lifecycle events for one still-fresh phantom
        // stream in one batch, say) counts as cleared without paying for the query again — and
        // skipped for mergesOntoLocalProjectStream, whose effectiveStreamId is always projectId by
        // construction and is never what this guard is checking. Runs before the Task/Run act gate
        // just below, so a record whose own target stream belongs to another project is refused on
        // that ground alone, whatever its own event type — a foreign stream's act is never this
        // gate's to judge.
        if (streamExists && !streamsStartedThisRead.Contains(effectiveStreamId) && !mergesOntoLocalProjectStream)
        {
            ReplicationOwnership existingOwnership =
                await projectResolver.ResolveAsync(session, effectiveStreamId, cancellationToken);

            bool isLifecycleEvent = ProjectStreamReplicationRules.IsProjectLifecycleEvent(eventType);

            // A project lifecycle event's own stream id stays record.StreamId raw (this method's
            // own doc above) and is meant to land only on a stream that is either brand new here or
            // already resolves as the Project aggregate's own — still-phantom or registered — stream:
            // never onto an existing Task, Idea, Epic, or Run stream, same project or a different
            // one, since appending a project-shaped event there materialises a phantom ProjectDetails
            // doc at that stream's own id, and this guard's own refuseAsNonLifecycleOntoProjectStream
            // arm then permanently refuses every later genuine event on it (independent pre-PR
            // review, cycle 4, conformance lens, high — the earlier build here only ever refused a
            // lifecycle event that resolved as a REGISTERED Project stream, leaving an ordinary
            // same-project Task/Idea/Epic/Run stream, whose ownership check never fires for a
            // lifecycle event's foreign coordinate the same way, unrefused).
            if (isLifecycleEvent && !existingOwnership.IsProjectStreamItself)
            {
                logger?.LogWarning(
                    "Replicated event {OriginEventId} (origin {OriginNodeId}) of type {EventType} from sender "
                    + "{SenderNodeId} targets stream {StreamId}, which resolves as an existing Task, Idea, Epic, "
                    + "or Run stream, never a legitimate target for a project lifecycle event — refused, never "
                    + "applied", record.OriginEventId, record.OriginNodeId, record.EventTypeName, senderNodeId,
                    effectiveStreamId);
                session.Store(new ReplicatedEventRecord
                {
                    Id = record.OriginEventId,
                    StreamId = effectiveStreamId,
                    ProjectId = projectId,
                    AppliedAt = now,
                    Applied = false,
                });
                await session.SaveChangesAsync(cancellationToken);
                return 0;
            }

            // A Run whose owning task has not replicated here yet resolves with no project at all
            // (ReplicationProjectResolver's own doc: TaskId is set from the run itself, but ProjectId
            // stays null when the owning task doesn't load) — recoverable, not foreign. This run's
            // own genesis already landed through THIS project's own inbox read, unchallenged (a
            // genesis bypasses this whole guard, since streamExists is false the moment it lands), so
            // a later, NON-genesis event for the identical stream is held back only by a missing
            // dependency, never by a different project's ownership. Treated the same as the
            // cross-project idea case's own unreadable-ledger branch below: dropped without
            // recording, so a request for this origin or stream stays open and the record applies
            // the moment the task itself arrives, rather than burned for good the instant this
            // node's own sweep happens to run first (independent pre-PR review, cycle 4, adversarial
            // lens, high — the prior build read this null ProjectId as foreign, like any other, and
            // refused it permanently). Excluded for a genesis-shaped event: that shape targeting an
            // ALREADY existing stream is always a second, duplicate genesis regardless of whether
            // the owning task ever resolves, and must still fall through to either
            // crossesIntoAnotherProject below or the dedicated second-genesis discard further down —
            // both of which burn it, exactly as a locally-dispatched run's own foreign
            // RunRecordReconstructed duplicate must be (that test's own doc, and the second-genesis
            // guard's own comment on "nothing about a second one ever resolves by holding it for
            // later").
            bool unresolvedRunOwningTask =
                !existingOwnership.IsProjectStreamItself
                && existingOwnership.ProjectId is null
                && existingOwnership.TaskId is not null
                && !AggregateGenesisEventTypes.IsGenesis(eventType);
            if (unresolvedRunOwningTask)
            {
                logger?.LogWarning(
                    "Replicated event {OriginEventId} (origin {OriginNodeId}) of type {EventType} from sender "
                    + "{SenderNodeId} targets stream {StreamId}, whose owning task {TaskId} has not replicated "
                    + "here yet — dropped, never applied, left for a later delivery once the task arrives",
                    record.OriginEventId, record.OriginNodeId, record.EventTypeName, senderNodeId,
                    effectiveStreamId, existingOwnership.TaskId);
                gatedDropOriginNodeIdsThisRead.Add(record.OriginNodeId);
                gatedDropStreamIdsThisRead.Add(effectiveStreamId);
                return 0;
            }

            // A null ProjectId left here is an owner-scoped Decision/Learning stream (the one other
            // family ReplicationProjectResolver ever returns one for) — never equals this read's own
            // project either, treated the same as any other foreign project rather than tolerated as
            // a pass-through (independent pre-PR review, cycle 1, adversarial lens: the earlier
            // `is { } existingProjectId` pattern only ever compared a non-null id, so a null one
            // slipped past both branches unchecked).
            bool crossesIntoAnotherProject =
                !existingOwnership.IsProjectStreamItself && existingOwnership.ProjectId != projectId;

            // The one legitimate cross-project append: an idea moved from one shared project to
            // another (h9k idea assign). The outbound flush resolves each event's project live, so the
            // move, and any of the idea's own earlier events not yet flushed when it happened, travel
            // through the DESTINATION project's outbox while the idea still resolves to the source
            // project here until the move itself applies. Refusing them left the idea under the source
            // project on every peer forever (independent pre-PR review, cycle 2, adversarial lens,
            // medium). Admitted only when the target stream is an idea and the sender is a current
            // member of the source project by that project's OWN ledger: such a sender could append
            // the identical event through the source project's own outbox anyway, while a member of
            // this project alone still cannot pull another project's idea across or write into it.
            bool isIdeaStream = crossesIntoAnotherProject
                && await session.LoadAsync<IdeaDetails>(effectiveStreamId, cancellationToken) is not null;
            if (isIdeaStream)
            {
                GatedEventVerdict ideaVerdict;
                try
                {
                    TrustChain? sourceChain =
                        await ComputeSourceProjectChainAsync(session, existingOwnership.ProjectId, cancellationToken);
                    ideaVerdict = EvaluateCrossProjectIdeaEvent(
                        sourceChain, senderFingerprint, senderNodeId, record.OriginNodeId);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The source ledger could not be read this sweep, which says nothing about the
                    // sender either way: refusing would burn a possibly legitimate event for good, and
                    // letting the exception fail the whole read would stall every other record this
                    // sender carries for as long as that ledger stays unreadable. Left recoverable
                    // instead, the same as a forwarded record the source ledger does not vouch for.
                    logger?.LogWarning(
                        exception, "Reading project {SourceProjectId}'s own ledger failed while checking replicated "
                        + "event {OriginEventId} on idea stream {StreamId}", existingOwnership.ProjectId,
                        record.OriginEventId, effectiveStreamId);
                    ideaVerdict = GatedEventVerdict.DroppedWithoutRecording;
                }

                if (ideaVerdict == GatedEventVerdict.DroppedWithoutRecording)
                {
                    // A forwarded record whose forwarder the source project does not vouch for (or an
                    // unreadable source ledger, above): the true origin, or a forwarder it does vouch
                    // for, may still deliver it, so it is neither applied nor burned, and a request
                    // for its origin or stream stays open (the identical shape the settings gate
                    // above leaves recoverable).
                    logger?.LogWarning(
                        "Replicated event {OriginEventId} (origin {OriginNodeId}) of type {EventType} from sender "
                        + "{SenderNodeId} targets idea stream {StreamId} under project {SourceProjectId}, whose own "
                        + "ledger could not vouch this sender — dropped, never applied, left for a later delivery",
                        record.OriginEventId, record.OriginNodeId, record.EventTypeName, senderNodeId,
                        effectiveStreamId, existingOwnership.ProjectId);
                    gatedDropOriginNodeIdsThisRead.Add(record.OriginNodeId);
                    gatedDropStreamIdsThisRead.Add(effectiveStreamId);
                    return 0;
                }

                crossesIntoAnotherProject = ideaVerdict != GatedEventVerdict.Allowed;
            }

            bool refuseAsNonLifecycleOntoProjectStream =
                existingOwnership.IsProjectStreamItself && !isLifecycleEvent;
            bool refuseAsLifecycleOntoRegisteredProject = existingOwnership.IsProjectStreamItself && isLifecycleEvent
                && await IsGenesisRegisteredProjectStreamAsync(session, effectiveStreamId, cancellationToken);

            // A Task/Idea/Epic/Run/Decision/Learning event whose own family does not match the
            // family already resolved for this SAME-project stream: none of the checks above catch
            // it, since it crosses no project boundary and the target is not a Project stream at
            // all. A TaskAbandoned aimed at an existing idea stream, say, materialises a phantom
            // TaskDetails row there (Marten's own "starts a document even with no matching Create"),
            // and refuseAsNonLifecycleOntoProjectStream then permanently refuses every later
            // legitimate idea event once ReplicationProjectResolver resolves that phantom first
            // (independent pre-PR review, cycle 4, adversarial lens, medium — the guard's own
            // lifecycle-vs-Project-stream check already closes this identical shape for the Project
            // family alone). Skipped when the resolved family is Unknown: a stream this resolver
            // does not currently recognise at all says nothing about a mismatch, and refusing on
            // that basis alone would be a new, unrelated regression this check must not cause.
            bool refuseAsMismatchedFamilyOntoExistingStream =
                !existingOwnership.IsProjectStreamItself
                && !crossesIntoAnotherProject
                && existingOwnership.Family != ReplicationStreamFamily.Unknown
                && existingOwnership.Family != FamilyOf(eventType);

            if (crossesIntoAnotherProject || refuseAsNonLifecycleOntoProjectStream
                || refuseAsLifecycleOntoRegisteredProject || refuseAsMismatchedFamilyOntoExistingStream)
            {
                logger?.LogWarning(
                    "Replicated event {OriginEventId} (origin {OriginNodeId}) of type {EventType} from sender "
                    + "{SenderNodeId} targets stream {StreamId}, which {Reason} — refused, never applied",
                    record.OriginEventId, record.OriginNodeId, record.EventTypeName, senderNodeId, effectiveStreamId,
                    refuseAsLifecycleOntoRegisteredProject
                        ? "resolves as a Project stream this node itself registered, never a legitimate target "
                          + "for a project lifecycle event"
                        : refuseAsNonLifecycleOntoProjectStream
                            ? "resolves as a Project aggregate's own stream, never a legitimate target for this event type"
                            : refuseAsMismatchedFamilyOntoExistingStream
                                ? $"already resolves as a {existingOwnership.Family} stream, never a legitimate target "
                                  + $"for a {FamilyOf(eventType)} event"
                                : isIdeaStream
                                    ? "is an idea under a different project whose own ledger does not vouch this "
                                      + "sender as a member"
                                    : "belongs to a different project than the one this read is scoped to");
                session.Store(new ReplicatedEventRecord
                {
                    Id = record.OriginEventId,
                    StreamId = effectiveStreamId,
                    ProjectId = projectId,
                    AppliedAt = now,
                    Applied = false,
                });
                await session.SaveChangesAsync(cancellationToken);
                return 0;
            }
        }

        // idea 6be68ee2, trust-ledger finding 5: a Task or Run "act" — one of
        // TaskActClassificationRegistry's own entries — applies from a non-owner sender only when
        // the task it targets is currently that sender's own to act on. Runs only once this
        // record's own effective stream already exists: when it does not, the task's (or the run's
        // own task's) genesis has simply never arrived, and the missing-genesis hold just below
        // already defers everything, sender fingerprint included, until the genesis lands and this
        // gate runs fresh at replay (never reached, or judged, while the stream is still missing).
        if (streamExists && TaskActClassificationRegistry.TryClassificationOf(eventType) is { } classification)
        {
            // Resolved once, up front, for every act reaching this gate — cheap, in-memory, no
            // store read — rather than only for the ones a classification would otherwise force a
            // read for: independent pre-PR review, cycle 8, conformance and adversarial lenses,
            // high, needs it here too, ahead of the "earlier held for this origin" check just below.
            SenderResolution? recordSender = ResolveSender(trustChain, senderFingerprint, senderNodeId);
            bool senderIsOwner = recordSender is { Role: MembershipRole.Owner };

            // Every later record from this exact origin targeting this exact stream is held behind
            // an earlier one this origin already has waiting here, whatever its OWN classification
            // says — appending it first would let its own append set this stream's per-origin
            // high-water mark past the held record's own sequence, and the guard below would then
            // refuse the held record as out of order the moment it finally clears
            // (OriginHighWaterAsync's own doc). Skipped for an owner-role sender's own direct act:
            // an owner always applies "whatever classification says" (the owner override just
            // below), and queuing it behind some OTHER sender's still-unresolved claim about a
            // different act on the same origin would block a fact this node already trusts
            // unconditionally, for no protective reason at all (independent pre-PR review, cycle 8,
            // conformance lens, high).
            if (!senderIsOwner)
            {
                HeldTaskActRecord? earlierHeldForThisOrigin = await session.Query<HeldTaskActRecord>()
                    .Where(held => held.ProjectId == projectId && held.StreamId == effectiveStreamId
                        && held.OriginNodeId == record.OriginNodeId && held.OriginSequence < record.OriginSequence)
                    .OrderBy(held => held.OriginSequence)
                    .FirstOrDefaultAsync(cancellationToken);
                if (earlierHeldForThisOrigin is not null)
                {
                    // This record itself is judged no further once it queues — the earlier held record
                    // is the only one ReCheckHeldTaskActsAsync ever re-judges — so if THIS record is the
                    // true origin's own direct delivery (record.OriginNodeId == senderNodeId), it is also
                    // the one proof that can ever backfill TaskCreatorRootRecord.CreatorRootFingerprint
                    // for a relayed genesis (ResolveCreatorRootFingerprintAsync's own doc). Run before
                    // queuing, not after, or a draft revised entirely through direct deliveries — with no
                    // held relayed act ever superseded by a later run event on a different stream — would
                    // queue forever behind a held head that can never itself resolve who the creator is
                    // (independent pre-PR review, cycle 4, adversarial lens, high).
                    await ResolveCreatorRootFingerprintAsync(
                        session, task: null, earlierHeldForThisOrigin.TaskId, record.OriginNodeId, senderNodeId,
                        recordSender, cancellationToken);
                    await HoldTaskActAsync(
                        session, record, effectiveStreamId, earlierHeldForThisOrigin.TaskId, projectId, senderNodeId,
                        senderFingerprint, originProjectKey, now, taskActCatchUpAskTaskIdsThisRead: null, cancellationToken);
                    return 0;
                }
            }

            // Plain MemberSafe — every entry but TaskAssigned's own special rule — is always
            // Allowed here without ever consulting the task or the creator root, exactly as
            // EvaluateTaskActVerdict's own first check decides, native or forwarded alike (independent
            // pre-PR review, cycle 8, terminal lap: a forwarded claim now reaches this gate only after
            // IsForwardedRecordAdmitted has already verified the relay is speaking inside a catch-up
            // answer this node itself minted, so narrowing this shortcut to native deliveries only
            // held every forwarded TaskCompleted and run event a member relay legitimately served,
            // until the true origin re-sent it directly — undercutting the very catch-up ask this
            // gate exists to let a held act rely on). An owner-role sender's act is Allowed too,
            // whatever classification says, the moment the sender alone is known. Both skip the
            // aggregate reads below entirely — a full replay of the task stream, the run stream too
            // for a Run act, and (inside the creator-root backfill) a native task's own full stream
            // fetch — none of which either verdict was ever going to consult (independent pre-PR
            // review, cycle 4, both lenses, medium: on a bootstrap or repair applying a run's history
            // one event at a time, that cost was quadratic in the run's own event count for every
            // plain observation and every owner-sent act alike).
            bool alwaysAllowed = classification == TaskActClassification.MemberSafe && eventType != typeof(TaskAssigned);
            SenderResolution? actSender = alwaysAllowed ? null : recordSender;
            if (!alwaysAllowed && actSender is not { Role: MembershipRole.Owner })
            {
                TaskActTargetResolution target =
                    await ResolveTaskActTargetAsync(session, eventType, effectiveStreamId, cancellationToken);
                string? creatorRootFingerprint = await ResolveCreatorRootFingerprintAsync(
                    session, target.Task, target.TaskId, record.OriginNodeId, senderNodeId, actSender, cancellationToken);
                TaskActVerdict verdict = EvaluateTaskActVerdict(
                    classification, eventType, data, target.Task, actSender,
                    record.OriginNodeId, senderNodeId, creatorRootFingerprint);

                switch (verdict)
                {
                    case TaskActVerdict.Allowed:
                        break;

                    case TaskActVerdict.Held:
                        logger?.LogWarning(
                            "Replicated task/run act {EventType} (origin {OriginEventId}) from sender {SenderNodeId} "
                            + "(fingerprint {SenderFingerprint}) targets task {TaskId}, whose current assignment or "
                            + "holder is not yet known here — held until it clears or 24 hours pass",
                            record.EventTypeName, record.OriginEventId, senderNodeId, senderFingerprint ?? "(none)",
                            target.TaskId);
                        await HoldTaskActAsync(
                            session, record, effectiveStreamId, target.TaskId, projectId, senderNodeId, senderFingerprint,
                            originProjectKey, now, taskActCatchUpAskTaskIdsThisRead, cancellationToken);
                        return 0;

                    case TaskActVerdict.DroppedAndRefusedPermanently:
                        LogTaskActDropped(record, senderNodeId, senderFingerprint, target.TaskId, permanent: true);
                        session.Store(new ReplicatedEventRecord
                        {
                            Id = record.OriginEventId,
                            StreamId = effectiveStreamId,
                            ProjectId = projectId,
                            AppliedAt = now,
                            Applied = false,
                        });
                        await session.SaveChangesAsync(cancellationToken);
                        return 0;

                    case TaskActVerdict.DroppedWithoutRecording:
                        // This sender merely relayed somebody else's act (record.OriginNodeId !=
                        // senderNodeId is exactly what earns this verdict over
                        // DroppedAndRefusedPermanently) — the true origin may still deliver it directly.
                        // Held, not discarded: appending anything else from this same origin on this same
                        // stream first would let its own append push the per-origin high-water mark past
                        // this record's own sequence, and the true origin's later direct delivery of the
                        // identical act would then be refused as out of order for good, permanently
                        // losing it (independent pre-PR review, cycle 4, conformance lens, high). Also
                        // recorded as a gated drop, exactly like the sibling project-settings gate above,
                        // so an outstanding catch-up request this exact answer would otherwise satisfy is
                        // left standing for the true origin to answer instead.
                        LogTaskActDropped(record, senderNodeId, senderFingerprint, target.TaskId, permanent: false);
                        await HoldTaskActAsync(
                            session, record, effectiveStreamId, target.TaskId, projectId, senderNodeId, senderFingerprint,
                            originProjectKey, now, taskActCatchUpAskTaskIdsThisRead: null, cancellationToken);
                        gatedDropOriginNodeIdsThisRead.Add(record.OriginNodeId);
                        gatedDropStreamIdsThisRead.Add(effectiveStreamId);
                        return 0;
                }
            }

            // Reaching here means this exact origin event has just been judged fresh and is about
            // to apply for real (Allowed, or alwaysAllowed) — every other verdict above already
            // returned. HeldTaskActRecord's own Id IS record.OriginEventId, so a row still sitting
            // here under that id is a hold this same event earned earlier, from an earlier read,
            // under whichever sender answered it then: a relay's own DroppedWithoutRecording
            // verdict (idea 6be68ee2), or this act simply queuing behind an earlier-held record
            // from the same origin that has since cleared. Left in place, that stale row would
            // out-rank every later same-origin/same-stream act forever under the "earlier held for
            // this origin" check above — this event is now applying for real, so the row's own
            // verdict no longer describes anything, yet ReCheckHeldTaskActsAsync would keep
            // re-judging it against its own stale sender until it expires, taking every legitimate
            // record queued behind it with it (independent pre-PR review, cycle 5, conformance
            // lens, high). Cleared in its own save, ahead of the append below, so it is gone
            // whether or not that append itself goes on to succeed.
            if (await session.LoadAsync<HeldTaskActRecord>(record.OriginEventId, cancellationToken) is not null)
            {
                session.Delete<HeldTaskActRecord>(record.OriginEventId);
                await session.SaveChangesAsync(cancellationToken);
            }
        }

        // A record for a stream this read already tried, and failed, to START
        // (streamsThatFailedToStartThisRead's own doc), or one a PAST read already tried and
        // failed to start — the identical fact, resolved before this read began and found here as
        // a persisted ReplicatedEventRecord for this stream with Applied false — is refused the
        // same way rather than left to StartStream a fresh, headless document: the record that
        // would have been this stream's true genesis is now permanently skipped, so nothing is
        // ever coming to repair it. The persisted check only ever runs while streamExists is
        // false, so it can never mistake a stream that genuinely holds applied history for one
        // whose genesis failed (independent pre-PR review, cycle 1, both lenses, medium).
        if (!streamExists)
        {
            bool genesisPermanentlyFailed = streamsThatFailedToStartThisRead.Contains(effectiveStreamId)
                || await session.Query<ReplicatedEventRecord>()
                    .Where(existing => existing.StreamId == effectiveStreamId && !existing.Applied)
                    .AnyAsync(cancellationToken);
            if (genesisPermanentlyFailed)
            {
                streamsThatFailedToStartThisRead.Add(effectiveStreamId);
                logger?.LogWarning(
                    "Replicated event {OriginEventId} (origin {OriginNodeId} sequence {OriginSequence}) targets "
                    + "stream {StreamId}, whose own genesis record already failed to apply — skipped rather "
                    + "than starting a headless document nothing will ever repair",
                    record.OriginEventId, record.OriginNodeId, record.OriginSequence, effectiveStreamId);
                session.Store(new ReplicatedEventRecord
                {
                    Id = record.OriginEventId,
                    StreamId = effectiveStreamId,
                    ProjectId = projectId,
                    AppliedAt = now,
                    Applied = false,
                });
                await session.SaveChangesAsync(cancellationToken);
                return 0;
            }

            // A stream this node has never started can only legally start from that aggregate's
            // own genesis (AggregateGenesisEventTypes) — never from a Project aggregate stream
            // event, whose effective stream id is deliberately either the receiver's own local
            // Project stream (already started long before any team-facing event replicates) or a
            // foreign per-install lifecycle stream that never carries a genesis at all
            // (ProjectStreamReplicationRules.IsProjectLifecycleEvent's own doc — a teammate's own
            // ProjectArchived is meant to phantom-stream there, deliberately). Anything else —
            // a task whose TaskAdded predates the sender's outbox (the switch-on truncation task
            // a56cf16e already names), or a run whose parent task never arrived — is held rather
            // than started: this is the recoverable twin of the permanently-failed guard just
            // above, since a genesis that simply has not arrived YET, unlike one that already
            // failed, may still show up in a later envelope and complete the story.
            bool genesisRequired = !mergesOntoLocalProjectStream
                && !ProjectStreamReplicationRules.IsProjectLifecycleEvent(eventType);
            if (genesisRequired && !AggregateGenesisEventTypes.IsGenesis(eventType))
            {
                // A copy of an event this node ALREADY holds, re-delivered — which is the ordinary
                // outcome of the held-tail ask itself (EventCatchUpCoordinator's own
                // RequestHeldTailStreamsAsync): a peer that holds the same tail and not the genesis
                // answers by serving that tail again. Left exactly as it is rather than stored
                // over, because the held document carries this node's own ask bookkeeping
                // (CatchUpAttempts, LastCatchUpAskedAt, CatchUpGivenUp) and its original HeldAt,
                // and Store on the same id is an upsert: overwriting it reset the attempt count to
                // zero on every answer, so the three-attempt stop never engaged for the one shape
                // it exists for and the stream was asked about again every cooldown for as long as
                // the node ran (independent pre-PR review, cycle 1, both lenses, high). Nothing in
                // the row is worth refreshing anyway — the origin event id is the identity, so the
                // wire record is the same event either way.
                HeldReplicatedEventRecord? alreadyHeld =
                    await session.LoadAsync<HeldReplicatedEventRecord>(record.OriginEventId, cancellationToken);
                if (alreadyHeld is not null)
                {
                    logger?.LogDebug(
                        "Replicated event {OriginEventId} targets stream {StreamId}, whose genesis is still "
                        + "missing, and is already held here since {HeldAt} after {Attempts} ask(s) — kept as "
                        + "held rather than re-held", record.OriginEventId, effectiveStreamId, alreadyHeld.HeldAt,
                        alreadyHeld.CatchUpAttempts);
                    return 0;
                }

                logger?.LogWarning(
                    "Replicated event {OriginEventId} (origin {OriginNodeId} sequence {OriginSequence}) of type "
                    + "{EventType} from sender {SenderNodeId} targets stream {StreamId}, whose own genesis this "
                    + "node has never received — held rather than starting a headless document; applied, in "
                    + "order, the moment a later envelope carries the genesis",
                    record.OriginEventId, record.OriginNodeId, record.OriginSequence, record.EventTypeName,
                    senderNodeId, effectiveStreamId);
                session.Store(new HeldReplicatedEventRecord
                {
                    Id = record.OriginEventId,
                    StreamId = effectiveStreamId,
                    ProjectId = projectId,
                    SenderNodeId = senderNodeId,
                    SenderFingerprint = senderFingerprint,
                    OriginProjectKey = originProjectKey,
                    RecordJson = EventReplicationCodec.EncodeRecord(record),
                    OriginSequence = record.OriginSequence,
                    OriginNodeId = record.OriginNodeId,
                    HeldAt = now,
                });
                await session.SaveChangesAsync(cancellationToken);
                return 0;
            }
        }

        // A second genesis for a stream that already has one, most reachably from
        // RunRecordReconstructed: CloseoutEngine.TasksWithMissingRunRecordsAsync is deliberately
        // fleet-wide rather than node-scoped (its own doc), so two nodes can each independently
        // decide the identical run id needs reconstructing — one legitimately, because that run's
        // own RunDispatched genuinely never existed, the other only because RunDispatched (or an
        // earlier reconstruction) simply had not replicated here yet when this node's own sweep
        // ran. Both mint a genesis-shaped event under their own origin, so the ordinary per-origin
        // ordering check below never catches it: this receiver has no prior sequence recorded for
        // that SECOND origin on this stream at all. Refused outright rather than appended, because
        // Marten only ever appends — landing a second genesis mid-stream would silently overwrite
        // RunAggregate.Apply(RunRecordReconstructed)'s own NodeId, OwnerId, DispatchedAt,
        // PullRequestUrl and PullRequestNumber and reset State back to Dispatched on a run that may
        // already be Completed here (independent pre-PR review, cycle 1, adversarial lens, medium).
        // This node's own already-applied genesis is authoritative; nothing about a second one ever
        // resolves by holding it for later, so it is discarded rather than held.
        if (streamExists && AggregateGenesisEventTypes.IsGenesis(eventType))
        {
            logger?.LogWarning(
                "Replicated event {OriginEventId} (origin {OriginNodeId} sequence {OriginSequence}) of type "
                + "{EventType} from sender {SenderNodeId} is a second genesis for stream {StreamId}, which "
                + "already exists here — discarded rather than appended into the middle of it",
                record.OriginEventId, record.OriginNodeId, record.OriginSequence, record.EventTypeName,
                senderNodeId, effectiveStreamId);
            session.Store(new ReplicatedEventRecord
            {
                Id = record.OriginEventId,
                StreamId = effectiveStreamId,
                ProjectId = projectId,
                AppliedAt = now,
                Applied = false,
            });
            await session.SaveChangesAsync(cancellationToken);
            return 0;
        }

        // An event is only ever APPENDED to a local stream — Marten has no way to put one before
        // what is already there — so a record that belongs earlier than an event this stream
        // already holds from the same origin cannot be applied at all: doing it anyway replays the
        // stream out of order, and TaskAggregate.Apply(TaskAdded) running last resets State and
        // clears the acceptance criteria, so a published or finished task reads as newly queued
        // (independent pre-PR review, cycle 4, adversarial lens, high). The shape that reaches
        // here is a node holding only a stream's post-switch-on TAIL — the ordinary flush ships
        // nothing older — which then pulls that stream's pre-switch-on head under the new
        // explicit-ask rule (task a56cf16e, Decisions Log #235). Refusing is the honest outcome:
        // the head stays where a peer holds it, the tail keeps reading correctly, and h9k task
        // pull says so up front rather than letting a human queue a pull that would corrupt the
        // stream. Compared per ORIGIN node, since two origins' own sequences say nothing about
        // each other's order.
        //
        // Not applied to an event that merges onto this node's own local Project stream
        // (mergesOntoLocalProjectStream). Its stream id is this receiver's own, so the sender's
        // sequences there are compared across two delivery paths, the ordinary post-switch-on
        // outbox and a catch-up answer that lifts the switch-on exclusion, and the head that the
        // answer serves after the tail would be refused on every reconcile with nothing able to
        // repair it. Those events are order-tolerant instead: ProjectDetailsProjection and
        // ProjectSettingsHistory resolve them by each event's own stamp, whatever order they sit in.
        Dictionary<Guid, long>? originHighWater = null;
        long highestHeld = 0;
        if (!mergesOntoLocalProjectStream)
        {
            originHighWater =
                await OriginHighWaterAsync(session, effectiveStreamId, streamExists, originHighWaterByStream, cancellationToken);
        }

        if (originHighWater is not null
            && originHighWater.TryGetValue(record.OriginNodeId, out highestHeld)
            && record.OriginSequence < highestHeld)
        {
            logger?.LogWarning(
                "Replicated event {OriginEventId} (origin {OriginNodeId} sequence {OriginSequence}) belongs before "
                + "origin sequence {HighestHeld}, which stream {StreamId} already holds — refused rather than "
                + "appended out of order",
                record.OriginEventId, record.OriginNodeId, record.OriginSequence, highestHeld, effectiveStreamId);
            return 0;
        }

        StreamAction action = streamExists
            ? session.Events.Append(effectiveStreamId, data)
            : session.Events.StartStream(effectiveStreamId, data);

        IEvent appended = action.Events[^1];
        appended.SetHeader(ReplicationEventHeaders.OriginNodeId, record.OriginNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginOwnerRootFingerprint, record.OriginOwnerRootFingerprint);
        appended.SetHeader(ReplicationEventHeaders.OriginEventId, record.OriginEventId.ToString());
        appended.SetHeader(ReplicationEventHeaders.OriginSequence, record.OriginSequence.ToString(CultureInfo.InvariantCulture));
        appended.SetHeader(ReplicationEventHeaders.OriginProjectId, record.OriginProjectId.ToString());
        if (originProjectKey is not null)
        {
            appended.SetHeader(ReplicationEventHeaders.OriginProjectKey, originProjectKey);
        }

        appended.SetHeader(ReplicationEventHeaders.ReceivedFromNodeId, senderNodeId.ToString());
        appended.SetHeader(ReplicationEventHeaders.ReceivedAt, now.ToString("O"));

        session.Store(new ReplicatedEventRecord
        {
            Id = record.OriginEventId,
            StreamId = effectiveStreamId,
            ProjectId = projectId,
            AppliedAt = now,
        });

        // TaskCreatorRootRecord's own doc: staged once, the moment a task's own genesis (TaskAdded)
        // starts its stream fresh here, whether this delivery is direct or relayed — ClaimedOriginNodeId
        // is recorded either way so a later direct delivery from that exact node id can still back-fill
        // CreatorRootFingerprint (independent pre-PR review, cycle 2, adversarial lens, high: the
        // earlier version of this block recorded nothing at all for a relayed genesis, so a task whose
        // genesis only ever arrived through catch-up — the ordinary shape a bootstrap or stream repair
        // takes — could never have its creator root resolved, holding every pre-assignment act against
        // it forever). CreatorRootFingerprint itself is set now only from a DIRECT delivery — this
        // node's own transport verified senderFingerprint against senderNodeId specifically, which is
        // worth nothing about the true author the moment a relay (senderNodeId != OriginNodeId) is what
        // actually delivered it. Staged in this same save so a poison-event rollback below takes it
        // down with everything else this record staged, never leaving an orphaned creator root for a
        // stream that never actually started.
        if (!streamExists && eventType == typeof(TaskAdded))
        {
            SenderResolution? genesisSender = senderNodeId == record.OriginNodeId
                ? ResolveSender(trustChain, senderFingerprint, senderNodeId)
                : null;
            session.Store(new TaskCreatorRootRecord
            {
                Id = effectiveStreamId,
                ProjectId = projectId,
                ClaimedOriginNodeId = record.OriginNodeId,
                CreatorRootFingerprint = genesisSender?.RootFingerprint ?? string.Empty,
            });
        }

        // idea 202383dc, M2b: the coarse "since" bound a future gap-fill events-request for this
        // origin is built from — never regressed, and refreshed lazily from the persisted value the
        // first time this origin is seen this read (this method's own doc on originProgressThisRead).
        // Advanced ONLY from a record read directly off its own origin's outbox (senderNodeId ==
        // record.OriginNodeId): EventOriginProgress's own doc asserts "greater than this is always a
        // safe superset of what is genuinely missing, never a subset", which held under the ordinary
        // outbox (an origin's own events only ever arrive in that origin's own sequence order) but
        // does not hold for a record FORWARDED by a catch-up answer — a peer can hold a high origin
        // sequence while genuinely missing a lower range from that same origin, and advancing this
        // node's own progress from that forwarded high-water mark would make a later gap-fill request
        // start past events this node was never actually sent (independent pre-PR review, cycle 1,
        // conformance and adversarial lenses, medium).
        //
        // Resolved and staged here, ahead of the save below, rather than after it: everything this
        // record stages — the append, the headers, the ReplicatedEventRecord, and this — has to
        // land in the SAME SaveChangesAsync call the catch below can eject as one unit. Staging it
        // after a successful save, in a session shared across every record this read processes,
        // would leave it sitting uncommitted until a LATER record's own failure ejects the whole
        // session's pending changes — taking this already-decided update down with it even though
        // its own record never failed.
        long? originProgressKnownHighest = null;
        long? originProgressAdvancedTo = null;
        if (senderNodeId == record.OriginNodeId)
        {
            if (!originProgressThisRead.TryGetValue(record.OriginNodeId, out long knownHighest))
            {
                EventOriginProgress? persisted = await session.LoadAsync<EventOriginProgress>(
                    EventReplicationStreamId.ForOriginProgress(projectId, record.OriginNodeId), cancellationToken);
                knownHighest = persisted?.HighestOriginSequenceApplied ?? 0;
            }

            originProgressKnownHighest = knownHighest;
            if (record.OriginSequence > knownHighest)
            {
                originProgressAdvancedTo = record.OriginSequence;
                session.Store(new EventOriginProgress
                {
                    Id = EventReplicationStreamId.ForOriginProgress(projectId, record.OriginNodeId),
                    ProjectId = projectId,
                    OriginNodeId = record.OriginNodeId,
                    HighestOriginSequenceApplied = record.OriginSequence,
                });
            }
        }

        // Flushed here, per record, rather than left pending for the read's one final
        // SaveChangesAsync at the bottom of ReadFromAsync: an inline projection this event's own
        // type feeds (IdeaDetailsProjection, say) runs synchronously inside THIS save, in the same
        // transaction as the append, and a defect in it throws JasperFx.Events.Daemon's own
        // ApplyEventException and rolls the whole transaction back — the append included. Saving
        // per record, rather than batching every record this read has queued into one save at the
        // end, is what keeps that rollback scoped to this one record instead of every record in
        // the read, known-good ones included.
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ApplyEventException exception)
        {
            // Nothing staged above actually landed — Marten rolled it all back with the rest of
            // this save's own transaction. Ejecting this record's own pending changes and
            // recording a bare ReplicatedEventRecord in a fresh save is what keeps this exact
            // origin event from being retried, and failing the same way, forever: a single poison
            // event must never freeze this sender's whole replication stream (idea 202383dc's own
            // worst case), whichever projection actually threw.
            logger?.LogError(exception,
                "Replicated event {OriginEventId} (origin {OriginNodeId} sequence {OriginSequence}) from "
                + "sender {SenderNodeId} failed to apply its own projection — skipped and recorded so it "
                + "is never retried",
                record.OriginEventId, record.OriginNodeId, record.OriginSequence, senderNodeId);
            session.EjectAllPendingChanges();
            if (!streamExists)
            {
                // This record would have started the stream — nothing else this node holds for it
                // yet. Remembered in-memory too, not only in the ReplicatedEventRecord stored
                // below, so a later record THIS SAME READ that targets the identical,
                // still-nonexistent stream is refused without paying for the persisted query above
                // a second time (streamsThatFailedToStartThisRead's own doc).
                streamsThatFailedToStartThisRead.Add(effectiveStreamId);
            }

            session.Store(new ReplicatedEventRecord
            {
                Id = record.OriginEventId,
                StreamId = effectiveStreamId,
                ProjectId = projectId,
                AppliedAt = now,
                Applied = false,
            });
            await session.SaveChangesAsync(cancellationToken);
            return 0;
        }

        streamsStartedThisRead.Add(effectiveStreamId);
        if (originHighWater is not null && record.OriginSequence > highestHeld)
        {
            originHighWater[record.OriginNodeId] = record.OriginSequence;
        }

        if (originProgressKnownHighest is { } knownHighestValue)
        {
            originProgressThisRead[record.OriginNodeId] = originProgressAdvancedTo ?? knownHighestValue;
        }

        // This call just started the stream fresh — the one moment a tail this node held earlier
        // (this record's own genesis simply had not arrived yet) could finally complete. NOT
        // replayed here, though: the one realistic way a held tail's own genesis arrives is inside a
        // `h9k task pull`/`h9k project pull` answer, which carries the whole stream in origin order
        // BEHIND this same record in this identical read — an ordinary flush never re-ships a
        // pre-switch-on genesis on its own (task a56cf16e). Replaying the held tail immediately
        // would push the per-origin high-water mark past that answer's own still-to-come middle,
        // and OriginHighWaterAsync's own guard above would refuse every one of those records as
        // "belongs before the tail", scrambling the stream and losing its middle for good
        // (independent pre-PR review, cycle 1, both lenses, high). Recorded here instead, for
        // ReadFromAsync's own deferred replay once this whole read's batches have landed in their
        // own order — the held copies then dedupe as no-ops against whichever of them this same
        // answer already redelivered.
        if (!streamExists)
        {
            streamsAwaitingHeldTailReplayThisRead.Add(effectiveStreamId);
        }

        return 1;
    }

    /// <summary>
    /// <see cref="EvaluateGatedEvent"/>'s own three outcomes for a project-settings-shaped event.
    /// <see cref="DroppedAndRefusedPermanently"/> and <see cref="DroppedWithoutRecording"/> are both
    /// "dropped" from the caller's own point of view — neither ever applies the event — but only the
    /// first also burns the origin event id so a retry can never apply it either; see
    /// <see cref="EvaluateGatedEvent"/>'s own doc for which sender shape earns which.
    /// </summary>
    internal enum GatedEventVerdict
    {
        Allowed,
        DroppedAndRefusedPermanently,
        DroppedWithoutRecording,
    }

    /// <summary>
    /// The pure verdict behind <see cref="ApplyAsync"/>'s own gate (idea 6be68ee2, trust-ledger
    /// findings 1 and 6): whether <paramref name="eventType"/> is even gated at all
    /// (<see cref="ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/> — a project
    /// settings change, a member vouch or removal record, a prompt addendum, or a run skill), and if
    /// so, whether <paramref name="senderFingerprint"/> is currently vouched, for
    /// <paramref name="senderNodeId"/> specifically, into some Owner-role project member's own
    /// chain — never a Member-role sender's, and never merely "vouched for some other node the same
    /// owner happens to have". Sender resolution (a fingerprint the transport read already verified
    /// against the sender's own node file) plus the event's own type is the whole of what decides
    /// apply versus drop; nothing here reads a store, so no test of it needs Docker. A null
    /// fingerprint (the transport somehow resolved none) is read as no key at all, refused rather
    /// than treated as "nothing to check" — the honest reading of an absent fact for a gate whose
    /// whole purpose is refusing anything it cannot positively vouch for.
    /// <para>
    /// A refused verdict burns the origin event id (<see cref="GatedEventVerdict.DroppedAndRefusedPermanently"/>)
    /// only when <paramref name="originNodeId"/> equals <paramref name="senderNodeId"/> — a sender
    /// claiming to be this event's own author, directly. When they differ, this sender is merely
    /// forwarding somebody else's record (the ordinary shape a catch-up answer takes), and burning it
    /// here would refuse the identical origin event id for good even though the true origin — an
    /// owner this gate would allow — may still send or forward it through a different, allowed
    /// sender; that shape returns <see cref="GatedEventVerdict.DroppedWithoutRecording"/> instead, so
    /// this exact delivery is skipped without ever standing in the way of a later one.
    /// </para>
    /// </summary>
    internal static GatedEventVerdict EvaluateGatedEvent(
        TrustChain trustChain, string? senderFingerprint, Guid senderNodeId, Guid originNodeId, Type eventType)
    {
        if (!ProjectStreamReplicationRules.IsProjectAggregateStreamEvent(eventType))
        {
            return GatedEventVerdict.Allowed;
        }

        SenderResolution? sender = ResolveSender(trustChain, senderFingerprint, senderNodeId);
        if (sender is { Role: MembershipRole.Owner })
        {
            return GatedEventVerdict.Allowed;
        }

        return originNodeId == senderNodeId
            ? GatedEventVerdict.DroppedAndRefusedPermanently
            : GatedEventVerdict.DroppedWithoutRecording;
    }

    /// <summary>
    /// The pure verdict behind the stream-ownership guard's one admitted cross-project append: an
    /// event on an idea stream that still resolves here to a different project than the one this
    /// read is scoped to, which is what an idea moved between projects (<see cref="IdeaAssignedToProject"/>)
    /// looks like until the move itself applies. <paramref name="sourceProjectChain"/> is the
    /// project the idea resolves to now, by that project's OWN ledger. Allowed only when
    /// <paramref name="senderFingerprint"/> is vouched, for <paramref name="senderNodeId"/>
    /// specifically, by a current member of the source project, whatever that member's role: an
    /// owner who belongs to both projects is exactly who runs <c>h9k idea assign</c> across them, and
    /// could send the identical event through the source project's own outbox anyway, while a member
    /// of the destination alone must never be able to pull another project's idea into it or write
    /// into it. A null chain (no chain reader, no source project to read, or an idea with no project
    /// at all) vouches nobody. A refusal burns the origin event id only when the sender is that
    /// record's own claimed origin, on the identical terms <see cref="EvaluateGatedEvent"/> uses, so
    /// a forwarded copy never stands in the way of a later delivery by a sender the source project
    /// does vouch for.
    /// </summary>
    internal static GatedEventVerdict EvaluateCrossProjectIdeaEvent(
        TrustChain? sourceProjectChain, string? senderFingerprint, Guid senderNodeId, Guid originNodeId)
    {
        bool allowed = senderFingerprint is not null
            && sourceProjectChain is not null
            && sourceProjectChain.IsAllowedSigner(senderFingerprint, senderNodeId);
        if (allowed)
        {
            return GatedEventVerdict.Allowed;
        }

        return originNodeId == senderNodeId
            ? GatedEventVerdict.DroppedAndRefusedPermanently
            : GatedEventVerdict.DroppedWithoutRecording;
    }

    /// <summary>
    /// <paramref name="sourceProjectId"/>'s own ledger trust chain, read fresh through
    /// <c>chainReader</c> from that project's own repository: an idea moved between projects is rare
    /// enough that one ledger read per event it carries costs little next to trusting a stale copy.
    /// Null when there is no chain reader, no source project, or no project here by that id.
    /// </summary>
    private async Task<TrustChain?> ComputeSourceProjectChainAsync(
        IQuerySession session, Guid? sourceProjectId, CancellationToken cancellationToken)
    {
        if (chainReader is null || sourceProjectId is not { } projectId)
        {
            return null;
        }

        ProjectDetails? sourceProject = await session.LoadAsync<ProjectDetails>(projectId, cancellationToken);
        return sourceProject is null || string.IsNullOrWhiteSpace(sourceProject.RepositoryPath)
            ? null
            : await chainReader.ComputeAsync(sourceProject.RepositoryPath, cancellationToken);
    }

    /// <summary>
    /// How long a matched <see cref="EventCatchUpRequest"/> keeps entitling a forwarded record after
    /// it was minted (<see cref="EventCatchUpRequest.SentAt"/>) — independent pre-PR review, cycle 1,
    /// conformance lens, medium: none of a request's own closing marks (<see cref="EventCatchUpRequest.AnsweredAt"/>,
    /// <see cref="EventCatchUpRequest.SupersededAt"/>, <see cref="EventCatchUpRequest.Exhausted"/>)
    /// is ever pruned, and a broadcast request's own id travels in the clear on this node's own
    /// project-audience outbox (a landed task naming an absent dependency, a human's own
    /// <c>h9k task pull</c>) for as long as this node keeps that history — reusing it, weeks later,
    /// to smuggle an unrelated forged record past <see cref="IsForwardedRecordAdmitted"/> would
    /// otherwise cost an attacker nothing but reading this node's own past outbox. Seven days is
    /// generously past any legitimate cascade's own per-candidate timeout
    /// (<c>DaemonOptions.EventCatchUpRequestTimeout</c>, minutes) or re-mint cooldown
    /// (<see cref="Hall9k.Connectors.Replication.TaskDependencyCatchUp.ReMintCooldown"/>, hours) —
    /// a genuine late answer inside it is ordinary network delay, never a request stale enough to be
    /// worth bounding out.
    /// </summary>
    internal static readonly TimeSpan ForwardedRecordAdmissionWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// idea 6be68ee2, trust-ledger findings 4 and 7: whether <paramref name="record"/> may be applied
    /// at all, coming from <paramref name="senderNodeId"/>. A record whose own claimed
    /// <see cref="EventReplicationCodec.ReplicatedEventRecord.OriginNodeId"/> equals the sender is
    /// native to that sender's own outbox and always admitted — this method's only real work is a
    /// FORWARDED record, one claiming an origin other than whoever delivered it. A teammate may
    /// forward another node's own record only inside an answer to a catch-up request THIS node
    /// itself minted, never on its own say-so, only when it is a sender that specific request
    /// actually entitles to answer, and only when the record itself falls inside what that request
    /// actually asked for — independent pre-PR review, cycle 1, conformance lens, medium: a stale
    /// request id read once off this node's own past outbox otherwise entitled ANY forwarded record,
    /// on ANY stream, from ANY origin, for as long as this node ever remembered minting it, which is
    /// forever:
    /// <list type="bullet">
    /// <item><see cref="EventCatchUpRequest.ForOriginNodeId"/> set (a gap-fill): the record's own
    /// origin must be that exact origin, and its own sequence must be above
    /// <see cref="EventCatchUpRequest.SinceOriginSequence"/> — the identical bound
    /// <see cref="EventCatchUpResponder.AnswerAsync"/>'s own <c>isMatch</c> filters an answer to.</item>
    /// <item><see cref="EventCatchUpRequest.ForStreamId"/> set (the ledger-record adoption path,
    /// <c>h9k task pull</c>, or a dependency ask): the record's own stream must be the requested
    /// stream or one of its own run streams — <paramref name="requestedStreamIds"/>, resolved by the
    /// caller from <see cref="EventCatchUpResponder.ResolveRequestedStreamIdsForAdmissionAsync"/>,
    /// the identical set that method itself would have answered from.</item>
    /// <item>Neither set (a bootstrap or a whole-project pull): nothing narrows the record's own
    /// content beyond entitlement below, since neither shape ever named anything narrower.</item>
    /// </list>
    /// Whichever of those applies, entitlement is then checked the same way as before:
    /// <list type="bullet">
    /// <item>A cascade ask (<see cref="EventCatchUpRequest.Candidates"/> non-empty — a gap-fill or a
    /// bootstrap): the sender is one of the ranked candidates, at ANY index, not only the one
    /// currently outstanding — a candidate the cascade already moved past may still answer late.</item>
    /// <item>A fleet reconcile (<see cref="EventCatchUpRequest.ToNodeId"/> set, candidates always
    /// empty): the sender is exactly that one peer.</item>
    /// <item>A broadcast (task pull, adoption, or a whole-project pull — candidates empty, no
    /// <c>ToNodeId</c>): any sender this project's own trust chain currently vouches, since a
    /// broadcast names no single peer up front and any project member may legitimately answer it.</item>
    /// </list>
    /// <paramref name="matchedRequest"/> is looked up by the caller from the envelope's own About
    /// field, in ANY state (answered, superseded, or exhausted all count — a split answer's later
    /// batches arrive after the first batch already closed the request) — null when About named no
    /// request this node ever minted, including a pre-a56cf16e sender's answer with no About at all,
    /// which never admits a foreign-origin record. <paramref name="now"/> bounds every match to
    /// <see cref="ForwardedRecordAdmissionWindow"/> after <see cref="EventCatchUpRequest.SentAt"/>,
    /// whatever else matches: the one check nothing about the request's own shape or state can lift.
    /// Otherwise pure: everything else it reads is already-resolved, in-memory data, so no test of it
    /// needs Docker.
    /// </summary>
    internal static bool IsForwardedRecordAdmitted(
        EventReplicationCodec.ReplicatedEventRecord record, Guid senderNodeId, EventCatchUpRequest? matchedRequest,
        TrustChain trustChain, string? senderFingerprint, IReadOnlyCollection<Guid> requestedStreamIds,
        DateTimeOffset now)
    {
        if (record.OriginNodeId == senderNodeId)
        {
            return true;
        }

        if (matchedRequest is null || now - matchedRequest.SentAt > ForwardedRecordAdmissionWindow)
        {
            return false;
        }

        if (matchedRequest.ForOriginNodeId is { } forOriginNodeId)
        {
            if (record.OriginNodeId != forOriginNodeId || record.OriginSequence <= matchedRequest.SinceOriginSequence)
            {
                return false;
            }
        }
        else if (matchedRequest.ForStreamId is not null && !requestedStreamIds.Contains(record.StreamId))
        {
            return false;
        }

        if (matchedRequest.Candidates.Count > 0)
        {
            return matchedRequest.Candidates.Contains(senderNodeId);
        }

        if (matchedRequest.ToNodeId is { } toNodeId)
        {
            return senderNodeId == toNodeId;
        }

        return senderFingerprint is not null && trustChain.IsAllowedSigner(senderFingerprint, senderNodeId);
    }

    /// <summary>
    /// A verified sender fingerprint (idea 6be68ee2, trust-ledger findings 1, 5, and 6), resolved
    /// once against the whole project trust chain — <see cref="RootFingerprint"/> and
    /// <see cref="Role"/> are the sender's own root's; <see cref="FleetNodeIds"/> is that root's
    /// whole fleet, needed to judge a task's own advisory node placement
    /// (<see cref="EvaluateTaskActVerdict"/>'s own <see cref="TaskAssigned"/> case).
    /// </summary>
    internal sealed record SenderResolution(string RootFingerprint, MembershipRole Role, IReadOnlySet<Guid> FleetNodeIds);

    /// <summary>
    /// The one place a sender's verified key fingerprint is resolved against
    /// <paramref name="trustChain"/>'s own current project membership (idea 6be68ee2, trust-ledger
    /// finding 5 — extending finding 1's own owner-only resolver to name ANY current member's role,
    /// not only an owner's): the first project member whose own chain currently vouches
    /// <paramref name="senderFingerprint"/> specifically for <paramref name="senderNodeId"/>
    /// (<see cref="TrustedOwner.ContainsForNode"/> — never merely some other node the same root
    /// happens to have vouched). Null when no current project member's chain does, which reads as
    /// "not a project member at all" everywhere this is consulted — never as "nothing to check".
    /// </summary>
    internal static SenderResolution? ResolveSender(TrustChain trustChain, string? senderFingerprint, Guid senderNodeId)
    {
        if (senderFingerprint is null)
        {
            return null;
        }

        // An Owner-role match wins over a Member-role match found earlier in Members order, rather
        // than the first vouching chain found winning outright: a node's key can legitimately show
        // up in more than one root's own chain — a Member root added before a second owner, say,
        // that later signs a vouch for that same node's key under its own owners/ tree — and the
        // gate this resolves for reads a Member-role result as unauthorized to do what an Owner may.
        // Resolving to whichever chain happened to list the node first silently demoted that node
        // to Member for every conditional or owner-only act it sent, and refused it permanently the
        // moment it did (independent pre-PR review, cycle 4, conformance lens, medium).
        string senderNodeIdText = senderNodeId.ToString();
        SenderResolution? firstMatch = null;
        foreach (ProjectMember member in trustChain.Members)
        {
            if (!trustChain.OwnerChains.TryGetValue(member.RootFingerprint, out TrustedOwner? owner)
                || !owner.ContainsForNode(senderFingerprint, senderNodeIdText))
            {
                continue;
            }

            SenderResolution resolution = new(member.RootFingerprint, member.Role, new HashSet<Guid>(owner.FleetNodeIds()));
            if (member.Role == MembershipRole.Owner)
            {
                return resolution;
            }

            firstMatch ??= resolution;
        }

        return firstMatch;
    }

    /// <summary>
    /// <see cref="EvaluateTaskActVerdict"/>'s own four outcomes for a Task or Run act (idea
    /// 6be68ee2, trust-ledger finding 5). <see cref="Held"/> is unique to this gate — the sibling
    /// project-settings gate (<see cref="GatedEventVerdict"/>) never has a fact to wait on, since
    /// its six event types always merge onto a stream that already exists — but a task's own
    /// assignment or holder, or a run's own task, genuinely may not have replicated here yet.
    /// </summary>
    internal enum TaskActVerdict
    {
        Allowed,
        Held,
        DroppedAndRefusedPermanently,
        DroppedWithoutRecording,
    }

    /// <summary>
    /// The <see cref="TaskActClassification.Conditional"/> members that fire at least as readily
    /// on a task with no recorded assignment or holder at all as on one that already has both —
    /// see <see cref="EvaluateTaskActVerdict"/>'s own <c>PreAssignmentCapableConditionalTypes</c>
    /// case for why a null pair means something different for these than it does for the general
    /// conditional case (<see cref="TaskUnassigned"/> and its siblings) or for
    /// <see cref="TaskClaimed"/>/<see cref="TaskHolderReleased"/>'s own special cases. A null pair
    /// here never means "no root to protect": it means the task is still its own CREATOR's
    /// (<see cref="TaskCreatorRootRecord"/>), and every member of this set is judged against that
    /// root rather than blanket-allowed (independent pre-PR review, cycle 1, both lenses, high —
    /// the earlier version of this set allowed any sender, resolved or not, to revise, scope,
    /// pre-approve, publish, or return to draft an owner's own unassigned task).
    /// <see cref="TaskAbandoned"/> belongs here for the identical reason: a member's own abandon of
    /// their own never-assigned draft is exactly as legitimate as their own publish of it.
    /// </summary>
    private static readonly IReadOnlySet<Type> PreAssignmentCapableConditionalTypes = new HashSet<Type>
    {
        typeof(TaskPublished),
        typeof(TaskReturnedToDraft),
        typeof(TaskPreApprovedSet),
        typeof(TaskScopeSet),
        typeof(TaskPrivacySet),
        typeof(TaskRevised),
        typeof(TaskAbandoned),
    };

    /// <summary>
    /// The pure verdict behind the Task/Run act gate (idea 6be68ee2, trust-ledger finding 5): a
    /// plain <see cref="TaskActClassification.MemberSafe"/> act (every one but
    /// <see cref="TaskAssigned"/>'s own special rule) always applies, exactly as it did before this
    /// gate existed — it needs no sender resolution at all, so a chain this read cannot currently
    /// resolve the sender against never stops it. Every other classification needs a resolved
    /// sender: an owner-role sender's act always applies; a non-owner (member-role) sender's is
    /// judged by <paramref name="classification"/> against <paramref name="task"/>'s own CURRENT
    /// state —
    /// <see cref="TaskAggregate.AssignedOwnerFingerprint"/> and
    /// <see cref="TaskAggregate.HolderOwnerRootFingerprint"/>, resolved by the caller from the task's
    /// own aggregate stream at apply time, never a projection. <paramref name="task"/> is null only
    /// when the caller could not resolve the task at all (a Run act whose own referenced task has
    /// never replicated here) — read as <see cref="TaskActVerdict.Held"/>, the identical answer a
    /// null <see cref="TaskAggregate.AssignedOwnerFingerprint"/>/<c>HolderOwnerRootFingerprint"</c>
    /// pair already gets for a task this node HAS seen: neither state is distinguishable, from this
    /// node's own knowledge alone, from the authorizing fact simply not having arrived yet
    /// (<c>OwnerRootFingerprintResolver</c> returning null for an owner with no local
    /// <c>OwnerDetails</c> row is the identical "treated as unassigned" reading, never a fallback to
    /// comparing raw owner Guids across nodes — idea 20723ef8's own closed hole).
    /// <para>
    /// A definite mismatch — the task IS known to be assigned to, or held by, some OTHER root —
    /// drops rather than holds: nothing further can arrive to change a fact this node already has.
    /// <see cref="TaskAssigned"/> carries its own rule even though its coarse bucket is
    /// <see cref="TaskActClassification.MemberSafe"/> (idea f72138e1: a member may self-assign an
    /// owner's published, unassigned task, exactly as <c>h9k task start</c> does locally, but never
    /// reassign a task already assigned to, or held by, some other root) — every one of its own
    /// failure branches is a definite mismatch the sender's own event data or this node's own trust
    /// chain already answers, so it is the one <see cref="TaskActClassification.MemberSafe"/> shape
    /// that can also drop, and it never holds. <see cref="TaskClaimed"/> checks assignment alone
    /// (the fact it is itself about to set is the holder, which does not exist yet);
    /// <see cref="TaskHolderReleased"/> checks the current holder alone, per its own doc ("only
    /// from the current holder").
    /// </para>
    /// <para>
    /// <paramref name="creatorRootFingerprint"/> is <see cref="TaskCreatorRootRecord.CreatorRootFingerprint"/>
    /// for <paramref name="task"/>'s own id, resolved by the caller — the "root to protect" a
    /// <see cref="PreAssignmentCapableConditionalTypes"/> member is judged against when
    /// <paramref name="task"/> has no assignment or holder yet, since a null pair there never means
    /// nothing is at stake: it means the task is still its own creator's. Null when this node never
    /// verified who that creator was (<see cref="TaskCreatorRootRecord"/>'s own doc on why a
    /// forwarded genesis leaves it unset) — read as "not yet known" and held, the identical
    /// treatment every other not-yet-arrived fact gets in this method, never as "no creator" and
    /// never blanket-allowed.
    /// </para>
    /// </summary>
    internal static TaskActVerdict EvaluateTaskActVerdict(
        TaskActClassification classification, Type eventType, object eventData, TaskAggregate? task,
        SenderResolution? sender, Guid originNodeId, Guid senderNodeId, string? creatorRootFingerprint = null)
    {
        // Plain MemberSafe — every entry in that bucket except TaskAssigned's own special rule —
        // was never gated at all before this PR, and stays that way here, native or forwarded alike
        // (independent pre-PR review, cycle 8, terminal lap): nothing about the sender's own work
        // (adding, completing, or reporting on a task or run this same node is running) or a plain
        // observation changes because a trust chain this read happened to compute does or does not
        // currently resolve the sender to a known project member. Checked before sender resolution
        // even matters, so a chain this node cannot currently fully resolve (or TrustChain.Empty, the
        // degenerate case with no chain data at all) never halts the ordinary run of task and run
        // lifecycle events every install already relied on. A FORWARDED delivery is not narrowed out
        // of this shortcut: by the time a record reaches this method, IsForwardedRecordAdmitted has
        // already refused it unless the relay is speaking inside a catch-up answer this node itself
        // minted for that exact origin and stream, so a forger stamping an arbitrary origin onto a
        // plain observation is refused there, before this gate ever runs — narrowing this shortcut to
        // native deliveries only would instead hold every forwarded TaskCompleted and run event a
        // legitimate relay served, until the true origin re-sent it directly, undercutting the
        // catch-up ask the held-act queue relies on to clear.
        if (classification == TaskActClassification.MemberSafe && eventType != typeof(TaskAssigned))
        {
            return TaskActVerdict.Allowed;
        }

        if (sender is null)
        {
            return Refuse(originNodeId, senderNodeId);
        }

        if (sender.Role == MembershipRole.Owner)
        {
            return TaskActVerdict.Allowed;
        }

        switch (classification)
        {
            case TaskActClassification.OwnerOnly:
                return Refuse(originNodeId, senderNodeId);

            case TaskActClassification.MemberSafe when eventType == typeof(TaskAssigned):
            {
                if (task is null)
                {
                    return TaskActVerdict.Held;
                }

                TaskAssigned assigned = (TaskAssigned)eventData;
                // A holder is a separate lock from assignment (TaskHolderTakenOver's own Apply
                // clears AssignedOwnerFingerprint to null the moment it sets one), so a null
                // assignment fingerprint alone never means "free to reassign" — it also means
                // "currently held by someone else" whenever a holder is recorded (independent
                // pre-PR review, cycle 1, conformance lens, medium: the earlier version of this
                // check let a forged TaskAssigned with no placement hijack an owner-held task the
                // moment TaskHolderTakenOver had cleared its assignment).
                bool freeToReassign = (task.AssignedOwnerFingerprint is null
                        || task.AssignedOwnerFingerprint == sender.RootFingerprint)
                    && (task.HolderOwnerRootFingerprint is null
                        || task.HolderOwnerRootFingerprint == sender.RootFingerprint);
                bool targetsOwnRoot = assigned.AssignedOwnerRootFingerprint == sender.RootFingerprint;
                bool placementInFleet = assigned.PlacedOnNodeId is not { HasValue: true, Value: { } placedNodeId }
                    || sender.FleetNodeIds.Contains(placedNodeId);

                return freeToReassign && targetsOwnRoot && placementInFleet
                    ? TaskActVerdict.Allowed
                    : Refuse(originNodeId, senderNodeId);
            }

            case TaskActClassification.MemberSafe:
                return TaskActVerdict.Allowed;

            case TaskActClassification.Conditional when eventType == typeof(TaskClaimed):
            {
                if (task is null)
                {
                    return TaskActVerdict.Held;
                }

                if (task.AssignedOwnerFingerprint == sender.RootFingerprint)
                {
                    return TaskActVerdict.Allowed;
                }

                return task.AssignedOwnerFingerprint is null
                    ? TaskActVerdict.Held
                    : Refuse(originNodeId, senderNodeId);
            }

            case TaskActClassification.Conditional when eventType == typeof(TaskHolderReleased):
            {
                if (task is null)
                {
                    return TaskActVerdict.Held;
                }

                if (task.HolderOwnerRootFingerprint == sender.RootFingerprint)
                {
                    return TaskActVerdict.Allowed;
                }

                return task.HolderOwnerRootFingerprint is null
                    ? TaskActVerdict.Held
                    : Refuse(originNodeId, senderNodeId);
            }

            case TaskActClassification.Conditional when PreAssignmentCapableConditionalTypes.Contains(eventType):
                // TaskPublished and TaskReturnedToDraft only ever fire on an unassigned task by
                // construction (Draft and Published are both pre-assignment states);
                // TaskPreApprovedSet, TaskScopeSet, and TaskPrivacySet are explicitly settable "on
                // any live non-terminal task, a draft included" (their own docs); TaskRevised is
                // Draft-only except for the queue-priority carve-out (AutoPrReviewEngine's own
                // queue-first revise); TaskAbandoned covers a member's own never-assigned draft.
                // task is null only defensively (every one of these targets its own Task stream
                // directly, which this gate already required to exist). A null assignment/holder
                // pair is judged against creatorRootFingerprint rather than blanket-allowed
                // (PreAssignmentCapableConditionalTypes' own doc): unknown, it holds, exactly like
                // every other not-yet-arrived fact this method defers on; known, it is the sender's
                // own root or it is not, and there is nothing further to wait for either way.
                return task switch
                {
                    null => TaskActVerdict.Held,
                    _ when task.AssignedOwnerFingerprint == sender.RootFingerprint
                        || task.HolderOwnerRootFingerprint == sender.RootFingerprint => TaskActVerdict.Allowed,
                    { AssignedOwnerFingerprint: null, HolderOwnerRootFingerprint: null } => creatorRootFingerprint switch
                    {
                        null => TaskActVerdict.Held,
                        _ when creatorRootFingerprint == sender.RootFingerprint => TaskActVerdict.Allowed,
                        _ => Refuse(originNodeId, senderNodeId),
                    },
                    _ => Refuse(originNodeId, senderNodeId),
                };

            case TaskActClassification.Conditional:
            {
                if (task is null)
                {
                    return TaskActVerdict.Held;
                }

                if (task.AssignedOwnerFingerprint == sender.RootFingerprint
                    || task.HolderOwnerRootFingerprint == sender.RootFingerprint)
                {
                    return TaskActVerdict.Allowed;
                }

                return task.AssignedOwnerFingerprint is null && task.HolderOwnerRootFingerprint is null
                    ? TaskActVerdict.Held
                    : Refuse(originNodeId, senderNodeId);
            }

            default:
                throw new InvalidOperationException($"Unhandled task act classification {classification}.");
        }

        static TaskActVerdict Refuse(Guid originNodeId, Guid senderNodeId) =>
            originNodeId == senderNodeId
                ? TaskActVerdict.DroppedAndRefusedPermanently
                : TaskActVerdict.DroppedWithoutRecording;
    }

    /// <summary>
    /// The <see cref="TaskCreatorRootRecord.CreatorRootFingerprint"/> to judge a
    /// <see cref="PreAssignmentCapableConditionalTypes"/> act against, resolved fresh every call
    /// rather than trusted from a stale value a caller might be holding: <paramref name="taskId"/>'s
    /// own record can go from unset to known between one held act and the next re-check.
    /// <para>
    /// No <see cref="TaskCreatorRootRecord"/> at all means this task's own genesis never went
    /// through <see cref="ApplyAsync"/> — it was created natively, right here, so it never crossed
    /// the wire and its own domain data needs no transport verification: this falls back to
    /// <paramref name="task"/>'s own <see cref="TaskAggregate.AddedByOwnerId"/>, resolved through
    /// <see cref="OwnerRootFingerprintResolver"/>, which is exactly what every OTHER door onto a
    /// task's owner identity already trusts for a locally-authored fact. Guarded by the genesis
    /// event's own <see cref="ReplicationEventHeaders.OriginNodeId"/> header being absent, so a
    /// stream that DID replicate in before this record existed (an upgrade gap, never a live one
    /// going forward) is read as "not yet known" rather than accidentally trusting this node's own
    /// receiving-side stamp on someone else's fact.
    /// </para>
    /// <para>
    /// A record that DOES exist but still carries an empty <see cref="TaskCreatorRootRecord.CreatorRootFingerprint"/>
    /// (<see cref="TaskCreatorRootRecord"/>'s own doc on why a relayed genesis leaves it that way) is
    /// backfilled here the first time this node receives a later Task act DIRECTLY
    /// (<paramref name="senderNodeId"/> == <paramref name="recordOriginNodeId"/>, the one shape this
    /// node's own transport has verified) FROM the exact node id the genesis claimed
    /// (<see cref="TaskCreatorRootRecord.ClaimedOriginNodeId"/>) — the identical proof the genesis
    /// itself would have carried had that node shipped it directly instead of a relay answering
    /// first (independent pre-PR review, cycle 2, adversarial lens, high: the earlier version of
    /// this record could never be completed once any relay had answered even once, holding every
    /// pre-assignment act against that task until its own 24-hour expiry, forever).
    /// </para>
    /// </summary>
    private static async Task<string?> ResolveCreatorRootFingerprintAsync(
        IDocumentSession session, TaskAggregate? task, Guid taskId, Guid recordOriginNodeId, Guid senderNodeId,
        SenderResolution? sender, CancellationToken cancellationToken)
    {
        TaskCreatorRootRecord? creatorRoot = await session.LoadAsync<TaskCreatorRootRecord>(taskId, cancellationToken);
        if (creatorRoot is null)
        {
            if (task is null)
            {
                return null;
            }

            IReadOnlyList<IEvent> taskEvents = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
            IEvent? genesis = taskEvents.Count > 0 ? taskEvents[0] : null;
            if (genesis is null || genesis.GetHeader(ReplicationEventHeaders.OriginNodeId) is not null)
            {
                return null;
            }

            string? ownerRootFingerprint =
                genesis.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) as string;
            return string.IsNullOrEmpty(ownerRootFingerprint)
                ? await OwnerRootFingerprintResolver.ResolveAsync(session, task.AddedByOwnerId, cancellationToken)
                : ownerRootFingerprint;
        }

        if (!string.IsNullOrEmpty(creatorRoot.CreatorRootFingerprint))
        {
            return creatorRoot.CreatorRootFingerprint;
        }

        if (sender is null
            || senderNodeId != recordOriginNodeId
            || recordOriginNodeId != creatorRoot.ClaimedOriginNodeId)
        {
            return null;
        }

        creatorRoot.CreatorRootFingerprint = sender.RootFingerprint;
        session.Store(creatorRoot);
        return sender.RootFingerprint;
    }

    /// <summary>The task a Task or Run act's own conditional verdict is judged against, and its id
    /// either way — <see cref="TaskAggregate"/> is null only when this node cannot resolve it at
    /// all (a Run act whose referenced task has never replicated here), never when the task simply
    /// has no opinion yet (an unassigned, unheld task is a real, non-null aggregate).</summary>
    private readonly record struct TaskActTargetResolution(Guid TaskId, TaskAggregate? Task);

    /// <summary>
    /// Resolves <paramref name="effectiveStreamId"/>'s own task, at apply time, directly from its
    /// event stream (never <c>TaskListItem</c>, which each record saves on its own and so may lag
    /// behind the stream this gate must judge against — see this class's own doc on why every
    /// record here is saved on its own). A Task-namespaced act's effective stream IS the task; a
    /// Run-namespaced act's is the run, whose own <see cref="RunAggregate.TaskId"/> names the task
    /// this gate actually judges it by (idea 6be68ee2, trust-ledger finding 5: "a run act is judged
    /// by its task's assignment").
    /// </summary>
    private static async Task<TaskActTargetResolution> ResolveTaskActTargetAsync(
        IQuerySession session, Type eventType, Guid effectiveStreamId, CancellationToken cancellationToken)
    {
        bool isRunAct = eventType.Namespace is not null
            && eventType.Namespace.StartsWith("Hall9k.Domain.Features.Run.Events", StringComparison.Ordinal);
        if (!isRunAct)
        {
            TaskAggregate? task =
                await session.Events.AggregateStreamAsync<TaskAggregate>(effectiveStreamId, token: cancellationToken);
            return new TaskActTargetResolution(effectiveStreamId, task);
        }

        // The record's own effective stream already exists (this gate only ever runs once
        // streamExists is true), and a Run's own genesis (RunDispatched) always carries TaskId, so
        // the run itself is never null here.
        RunAggregate run =
            (await session.Events.AggregateStreamAsync<RunAggregate>(effectiveStreamId, token: cancellationToken))!;
        TaskAggregate? referencedTask =
            await session.Events.AggregateStreamAsync<TaskAggregate>(run.TaskId, token: cancellationToken);
        return new TaskActTargetResolution(run.TaskId, referencedTask);
    }

    /// <summary>
    /// Queues <paramref name="record"/> in <see cref="HeldTaskActRecord"/>, upsert-avoiding exactly
    /// as <see cref="HeldReplicatedEventRecord"/>'s own doc describes: a row already there for this
    /// exact origin event id keeps its original <see cref="HeldTaskActRecord.HeldAt"/> (what the
    /// 24-hour expiry measures from) rather than being stored over, whether this call arrives
    /// because a peer redelivered the identical record or because <see cref="ReCheckHeldTaskActsAsync"/>
    /// re-evaluated it and found it still held. <paramref name="taskActCatchUpAskTaskIdsThisRead"/> is
    /// null for a record queued only because an EARLIER same-origin record already holds this stream
    /// (nothing new to ask about — the earlier one's own hold already asked); non-null, and added to
    /// on first creation only, for the record whose own verdict is what created the hold.
    /// <paramref name="projectId"/> is this RECEIVER's own local project id, stored on the row rather
    /// than <c>record.OriginProjectId</c> (independent pre-PR review, cycle 1, both lenses, high): the
    /// origin id is the SENDER's own per-install coordinate, which never matches this node's own
    /// <paramref name="projectId"/> across two real installs, and every reader of this table —
    /// <see cref="ApplyAsync"/>'s own "earlier held for this origin" check and
    /// <see cref="ReCheckHeldTaskActsAsync"/> alike — filters and re-applies by this node's own id.
    /// <para>
    /// An existing row's sender IS updated, though, when this call's own <paramref name="senderNodeId"/>
    /// is the record's true origin delivering directly (<c>senderNodeId == record.OriginNodeId</c>) and
    /// the stored row still cites some other, weaker sender — a relay's earlier forwarding of the
    /// identical origin event, held under the relay's own node id because that was the only sender
    /// known when it queued. Left un-updated, <see cref="ReCheckHeldTaskActsAsync"/> re-judges this
    /// record only once every held record ahead of it in the same origin/stream queue has cleared, and
    /// does so against the STORED sender, never the origin: a stale relay sender that was never
    /// authorized for this task keeps re-evaluating to <see cref="TaskActVerdict.DroppedWithoutRecording"/>
    /// forever, even though the true origin already proved its own legitimacy by delivering this exact
    /// record directly — reaching the queue-losing outcome
    /// <see cref="ApplyAsync"/>'s own stale-hold cleanup (just ahead of this method's own call sites)
    /// exists to prevent, for a second or later queued act whose own direct redelivery races ahead of
    /// the first's (independent pre-PR review, cycle 6, conformance lens, high). The reverse direction —
    /// overwriting an already-direct sender with a later relay's weaker one — is deliberately never done:
    /// a relay is never more authoritative than the origin it is relaying for.
    /// </para>
    /// </summary>
    private static async Task HoldTaskActAsync(
        IDocumentSession session, EventReplicationCodec.ReplicatedEventRecord record, Guid streamId, Guid taskId,
        Guid projectId, Guid senderNodeId, string? senderFingerprint, string? originProjectKey, DateTimeOffset now,
        HashSet<Guid>? taskActCatchUpAskTaskIdsThisRead, CancellationToken cancellationToken)
    {
        HeldTaskActRecord? existing = await session.LoadAsync<HeldTaskActRecord>(record.OriginEventId, cancellationToken);
        if (existing is not null)
        {
            if (senderNodeId == record.OriginNodeId && existing.SenderNodeId != record.OriginNodeId)
            {
                existing.SenderNodeId = senderNodeId;
                existing.SenderFingerprint = senderFingerprint;
                existing.OriginProjectKey = originProjectKey;
                session.Store(existing);
                await session.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        session.Store(new HeldTaskActRecord
        {
            Id = record.OriginEventId,
            StreamId = streamId,
            TaskId = taskId,
            ProjectId = projectId,
            SenderNodeId = senderNodeId,
            SenderFingerprint = senderFingerprint,
            OriginProjectKey = originProjectKey,
            RecordJson = EventReplicationCodec.EncodeRecord(record),
            OriginSequence = record.OriginSequence,
            OriginNodeId = record.OriginNodeId,
            HeldAt = now,
        });
        await session.SaveChangesAsync(cancellationToken);
        taskActCatchUpAskTaskIdsThisRead?.Add(taskId);
    }

    private void LogTaskActDropped(
        EventReplicationCodec.ReplicatedEventRecord record, Guid senderNodeId, string? senderFingerprint, Guid taskId,
        bool permanent) =>
        logger?.LogWarning(
            "Replicated task/run act {EventType} (origin {OriginEventId}) from sender {SenderNodeId} (fingerprint "
            + "{SenderFingerprint}) targets task {TaskId}, which is not this sender's to act on — dropped{Permanent}",
            record.EventTypeName, record.OriginEventId, senderNodeId, senderFingerprint ?? "(none)", taskId,
            permanent ? " and refused permanently" : "");

    /// <summary>
    /// Re-checks every <see cref="HeldTaskActRecord"/> for <paramref name="projectId"/> after a read
    /// that applied anything for it, whichever sender's read it was (idea 6be68ee2, trust-ledger
    /// finding 5): the fact a hold is waiting on may have just replicated from a completely
    /// different sender than the one whose act is held. Grouped by (stream, origin) — one hold
    /// queue per pair — and only ever the EARLIEST-sequence record in a queue is re-judged; the rest
    /// stay put until it clears, in origin-sequence order, exactly as <see cref="ApplyAsync"/>'s own
    /// gate defers them in the first place. A queue whose earliest record has sat held for over 24
    /// hours is dropped whole, with one log line naming the act — an owner's assignment delayed past
    /// that costs the member that claim and its own tail, acceptable only because
    /// <see cref="HoldTaskActAsync"/>'s own caller already asked a peer for the missing fact.
    /// </summary>
    private async Task<int> ReCheckHeldTaskActsAsync(
        IDocumentSession session, Guid projectId, TrustChain trustChain, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HeldTaskActRecord> allHeld = await session.Query<HeldTaskActRecord>()
            .Where(held => held.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        if (allHeld.Count == 0)
        {
            return 0;
        }

        int applied = 0;
        foreach (IGrouping<(Guid StreamId, Guid OriginNodeId), HeldTaskActRecord> group in allHeld
            .GroupBy(held => (held.StreamId, held.OriginNodeId)))
        {
            HeldTaskActRecord head = group.OrderBy(held => held.OriginSequence).First();
            if (now - head.HeldAt > MaxTaskActHoldAge)
            {
                EventReplicationCodec.ReplicatedEventRecord? decodedHead = EventReplicationCodec.DecodeRecord(head.RecordJson);
                int queuedCount = group.Count();
                foreach (HeldTaskActRecord expired in group)
                {
                    session.Delete<HeldTaskActRecord>(expired.Id);
                }

                logger?.LogWarning(
                    "Held task/run act {EventType} (origin {OriginEventId}) targeting task {TaskId} on stream "
                    + "{StreamId} from origin {OriginNodeId} sat unresolved for over 24 hours — dropped, taking "
                    + "{QueuedCount} record(s) queued behind it with it",
                    decodedHead?.EventTypeName ?? "(undecodable)", head.Id, head.TaskId, head.StreamId,
                    head.OriginNodeId, queuedCount);
                await session.SaveChangesAsync(cancellationToken);
                continue;
            }

            while (true)
            {
                HeldTaskActRecord? current = await session.Query<HeldTaskActRecord>()
                    .Where(held => held.ProjectId == projectId && held.StreamId == group.Key.StreamId
                        && held.OriginNodeId == group.Key.OriginNodeId)
                    .OrderBy(held => held.OriginSequence)
                    .FirstOrDefaultAsync(cancellationToken);
                if (current is null)
                {
                    break;
                }

                EventReplicationCodec.ReplicatedEventRecord? decoded = EventReplicationCodec.DecodeRecord(current.RecordJson);
                Type? eventType = decoded is null ? null : ReplicationEventTypeCatalog.Resolve(decoded.EventTypeName);
                TaskActClassification? classification = eventType is null ? null : TaskActClassificationRegistry.TryClassificationOf(eventType);
                object? data = decoded is null || eventType is null
                    ? null
                    : DecodeTaskActEventData(decoded, eventType, current.ProjectId);
                if (decoded is null || eventType is null || classification is null || data is null)
                {
                    logger?.LogError(
                        "Held task/run act {Id} for stream {StreamId} would not decode or resolve to a known, "
                        + "still-classified type — dropped rather than retried forever", current.Id, current.StreamId);
                    session.Delete<HeldTaskActRecord>(current.Id);
                    await session.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // Judged fresh, not inferred from ApplyAsync's own side effects: this call's own
                // verdict decides what happens next, so it must be known BEFORE anything is deleted
                // or applied, never guessed at afterward from whether a row happens to remain.
                // Plain MemberSafe (bar TaskAssigned) and an owner-role sender are both always
                // Allowed without ever consulting the task or the creator root — skipped here the
                // identical way ApplyAsync's own gate skips them, rather than paying for the same
                // aggregate reads only to discard them (independent pre-PR review, cycle 4, both
                // lenses, medium — the class sweep off that same finding), native or forwarded alike
                // (independent pre-PR review, cycle 8, terminal lap): a held row can only ever hold a
                // record that already cleared IsForwardedRecordAdmitted before reaching this queue
                // (queued here for sitting behind an earlier held act from the same origin, never for
                // its own classification verdict, since a plain MemberSafe act is never itself Held),
                // so there is no unproven relay left to re-judge by origin at this point.
                bool alwaysAllowed = classification.Value == TaskActClassification.MemberSafe && eventType != typeof(TaskAssigned);
                SenderResolution? sender = alwaysAllowed
                    ? null
                    : ResolveSender(trustChain, current.SenderFingerprint, current.SenderNodeId);
                TaskActVerdict verdict = TaskActVerdict.Allowed;
                if (!alwaysAllowed && sender is not { Role: MembershipRole.Owner })
                {
                    TaskActTargetResolution target =
                        await ResolveTaskActTargetAsync(session, eventType, current.StreamId, cancellationToken);
                    string? creatorRootFingerprint = await ResolveCreatorRootFingerprintAsync(
                        session, target.Task, target.TaskId, current.OriginNodeId, current.SenderNodeId, sender,
                        cancellationToken);
                    verdict = EvaluateTaskActVerdict(
                        classification.Value, eventType, data, target.Task, sender, current.OriginNodeId,
                        current.SenderNodeId, creatorRootFingerprint);
                }

                if (verdict is TaskActVerdict.Held or TaskActVerdict.DroppedWithoutRecording)
                {
                    // Still held (the fact this record itself needs remains unknown), or held
                    // because this exact relay still is not this act's own authorized sender — in
                    // neither case has anything actually resolved, so the row stays exactly where it
                    // is and the rest of this queue stays put behind it, in order, untouched. Deleting
                    // it here and draining the queue behind it — the earlier shape of this branch —
                    // would let the next-queued record's own append push the per-origin high-water
                    // mark past this one, permanently refusing the true origin's own direct delivery
                    // of it as out of order the moment it finally arrives (independent pre-PR review,
                    // cycle 4, conformance lens, high).
                    break;
                }

                if (verdict == TaskActVerdict.DroppedAndRefusedPermanently)
                {
                    session.Delete<HeldTaskActRecord>(current.Id);
                    LogTaskActDropped(decoded, current.SenderNodeId, current.SenderFingerprint, current.TaskId, permanent: true);
                    session.Store(new ReplicatedEventRecord
                    {
                        Id = decoded.OriginEventId,
                        StreamId = current.StreamId,
                        ProjectId = current.ProjectId,
                        AppliedAt = now,
                        Applied = false,
                    });
                    await session.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // Allowed. The hold row is deleted only AFTER ApplyAsync's own save(s) below have
                // already landed, whatever their outcome — staging the delete BEFORE calling
                // ApplyAsync, and committing it in its own earlier save, would lose the record for
                // good if this call then failed to ever reach ApplyAsync's own save: a dropped
                // connection, a cancelled token, or the daemon stopping between the two (independent
                // pre-PR review, cycle 4, adversarial lens, medium). It would also lose the delete
                // itself to EjectAllPendingChanges on the poison-event path if staged beforehand and
                // left pending — the identical reason ApplyHeldTailAsync's own doc gives for deleting
                // its sibling table's row only after ApplyAsync returns. A stale row left behind by a
                // crash between ApplyAsync's own save and this one is harmless either way: the next
                // re-check decodes the identical record, ApplyAsync's own dedupe check finds it
                // already applied (or already permanently refused) and returns 0, and this delete
                // finally lands, exactly as ApplyHeldTailAsync's own held records already tolerate.
                applied += await ApplyAsync(
                    session, decoded, current.SenderNodeId, current.ProjectId, current.OriginProjectKey,
                    [], [], [], [], [], [], [], [], [], trustChain, current.SenderFingerprint, now, cancellationToken);
                session.Delete<HeldTaskActRecord>(current.Id);
                await session.SaveChangesAsync(cancellationToken);
            }
        }

        return applied;
    }

    /// <summary>The identical decode-rewrite-deserialize path <see cref="ApplyAsync"/> applies
    /// inline, factored out for <see cref="ReCheckHeldTaskActsAsync"/>'s own re-judging of an
    /// already-held record, which needs the deserialized event itself (<see cref="TaskAssigned"/>'s
    /// own fields) before it knows whether to delete or replay the held row.</summary>
    private static object? DecodeTaskActEventData(
        EventReplicationCodec.ReplicatedEventRecord record, Type eventType, Guid projectId)
    {
        JsonNode? dataNode;
        try
        {
            dataNode = JsonNode.Parse(record.EventDataJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dataNode is JsonObject dataObject)
        {
            RewriteProjectIdField(dataObject, projectId);
        }

        try
        {
            return dataNode?.Deserialize(eventType, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly TimeSpan MaxTaskActHoldAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Replays the held tail of a stream that already exists here, outside any read of a sender's
    /// own outbox — the one shape <see cref="ReadFromAsync"/>'s own deferred replay can miss. That
    /// replay is driven by an in-memory set of the streams a genesis started THIS read, so a read
    /// that starts a stream and then throws on a later record (a transient Npgsql failure, say)
    /// loses the set: the retry finds the genesis already applied, never names the stream again,
    /// and the held records outlive the stream indefinitely, asked about by the daemon's own
    /// held-tail sweep forever and counted as a tail-only stream by <c>h9k status</c> for good
    /// (independent pre-PR review, cycle 1, adversarial lens, medium). That sweep
    /// (<see cref="EventCatchUpCoordinator.RequestHeldTailStreamsAsync"/>) is what finds them, in
    /// <see cref="HeldTailSweepResult.StreamsToReplay"/>, and this is what clears them.
    /// <para>
    /// Every parameter the in-read replay threads through is this read's own uncommitted state,
    /// which a standalone call simply has none of: the stream exists in committed form, so an
    /// empty started-this-read set reads the identical answer from the store.
    /// </para>
    /// <para>
    /// <paramref name="projectId"/>, <paramref name="myNodeId"/>, and
    /// <paramref name="myOwnerFingerprint"/> exist for the identical reason <see cref="ReadFromAsync"/>
    /// takes them: a Task/Run act this replay itself holds (idea 6be68ee2, trust-ledger finding 5 —
    /// the tail this method drains can carry one exactly as readily as an ordinary read can) still
    /// needs a broadcast stream ask for its own task, or it would sit waiting the full 24 hours with
    /// no ask ever sent, unlike every other path into this gate.
    /// </para>
    /// </summary>
    /// <returns>How many of the held records actually applied.</returns>
    public async Task<int> ReplayHeldTailAsync(
        IDocumentSession session, Guid projectId, Guid streamId, Guid myNodeId, string myOwnerFingerprint,
        DateTimeOffset now, TrustChain trustChain, CancellationToken cancellationToken)
    {
        HashSet<Guid> streamsStartedThisRead = [];
        HashSet<Guid> streamsThatFailedToStartThisRead = [];
        HashSet<Guid> originEventIdsAppliedThisRead = [];
        Dictionary<Guid, long> originProgressThisRead = [];
        Dictionary<Guid, Dictionary<Guid, long>> originHighWaterByStream = [];
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead = [];
        HashSet<Guid> gatedDropOriginNodeIdsThisRead = [];
        HashSet<Guid> gatedDropStreamIdsThisRead = [];
        HashSet<Guid> taskActCatchUpAskTaskIdsThisRead = [];
        int applied = await ApplyHeldTailAsync(
            session, streamId, streamsStartedThisRead, streamsThatFailedToStartThisRead,
            originEventIdsAppliedThisRead, originProgressThisRead, originHighWaterByStream,
            streamsAwaitingHeldTailReplayThisRead, gatedDropOriginNodeIdsThisRead, gatedDropStreamIdsThisRead,
            taskActCatchUpAskTaskIdsThisRead, trustChain, now, cancellationToken);

        if (taskActCatchUpAskTaskIdsThisRead.Count > 0)
        {
            EventCatchUpCoordinator coordinator = new();
            foreach (Guid taskId in taskActCatchUpAskTaskIdsThisRead)
            {
                await coordinator.RequestStreamBroadcastAsync(
                    session, projectId, taskId, myNodeId, myOwnerFingerprint, now, cancellationToken,
                    again: false, reMintCooldown: TaskActHoldCatchUpCooldown);
            }
        }

        return applied;
    }

    /// <summary>
    /// Replays every <see cref="HeldReplicatedEventRecord"/> waiting on <paramref name="streamId"/>'s
    /// own genesis, oldest first, called by <see cref="ReadFromAsync"/> only once this whole read's
    /// own batches have already landed — never inline from <see cref="ApplyAsync"/> the moment the
    /// genesis itself starts the stream, which would run in the middle of the very same read that,
    /// realistically, also carries that stream's own middle behind the genesis (that method's own
    /// doc explains why). By the time this runs, <paramref name="streamsStartedThisRead"/> already
    /// names the stream as existing, so every held record simply appends through
    /// <see cref="ApplyAsync"/> itself, rather than duplicating its append/header/dedupe logic here —
    /// a held record whose own origin event id this same read's batches already redelivered in order
    /// finds its <see cref="ReplicatedEventRecord"/> dedupe row already stored and replays as a
    /// no-op. Deletes each held document only once its own replay attempt has actually landed (or
    /// permanently failed, the ordinary poison-event outcome <see cref="ApplyAsync"/> already
    /// handles), so a crash between two held records loses nothing: whichever ones already
    /// committed are gone from this table, and whichever did not are found here again next time.
    /// <paramref name="originEventIdsAppliedThisRead"/> has this record's own origin id from when it
    /// was first held — removed before replay, since that set means "already resolved this read",
    /// which was never true for a record that only ever got as far as being held.
    /// </summary>
    private async Task<int> ApplyHeldTailAsync(
        IDocumentSession session, Guid streamId, HashSet<Guid> streamsStartedThisRead,
        HashSet<Guid> streamsThatFailedToStartThisRead, HashSet<Guid> originEventIdsAppliedThisRead,
        Dictionary<Guid, long> originProgressThisRead, Dictionary<Guid, Dictionary<Guid, long>> originHighWaterByStream,
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead, HashSet<Guid> gatedDropOriginNodeIdsThisRead,
        HashSet<Guid> gatedDropStreamIdsThisRead, HashSet<Guid> taskActCatchUpAskTaskIdsThisRead,
        TrustChain trustChain, DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<HeldReplicatedEventRecord> held = await session.Query<HeldReplicatedEventRecord>()
            .Where(candidate => candidate.StreamId == streamId)
            .ToListAsync(cancellationToken);
        if (held.Count == 0)
        {
            return 0;
        }

        int applied = 0;
        foreach (HeldReplicatedEventRecord heldRecord in held
            .OrderBy(candidate => candidate.OriginSequence)
            .ThenBy(candidate => candidate.HeldAt))
        {
            EventReplicationCodec.ReplicatedEventRecord? decoded = EventReplicationCodec.DecodeRecord(heldRecord.RecordJson);
            if (decoded is not null)
            {
                originEventIdsAppliedThisRead.Remove(decoded.OriginEventId);
                applied += await ApplyAsync(
                    session, decoded, heldRecord.SenderNodeId, heldRecord.ProjectId, heldRecord.OriginProjectKey,
                    streamsStartedThisRead, streamsThatFailedToStartThisRead, originEventIdsAppliedThisRead,
                    originProgressThisRead, originHighWaterByStream, streamsAwaitingHeldTailReplayThisRead,
                    gatedDropOriginNodeIdsThisRead, gatedDropStreamIdsThisRead, taskActCatchUpAskTaskIdsThisRead,
                    trustChain, heldRecord.SenderFingerprint, now, cancellationToken);
            }
            else
            {
                logger?.LogError(
                    "Held replicated record {HeldRecordId} for stream {StreamId} would not decode — dropped "
                    + "rather than retried forever", heldRecord.Id, streamId);
            }

            // Deleted only after ApplyAsync's own save(s) above have already landed, whatever their
            // outcome: staging the delete BEFORE calling ApplyAsync would lose it to
            // EjectAllPendingChanges on the poison-event path, leaving this row orphaned forever.
            session.Delete<HeldReplicatedEventRecord>(heldRecord.Id);
            await session.SaveChangesAsync(cancellationToken);
        }

        return applied;
    }

    /// <summary>
    /// Whether <paramref name="streamId"/>'s own genesis event — the one at stream version 1 — is
    /// <see cref="ProjectRegistered"/>, the only fact that tells a Project stream this node itself
    /// registered (<c>ProjectAddCommand</c>) apart from a phantom row Marten happily creates for a
    /// teammate's foreign lifecycle event with no <c>Create</c> handler of its own to match
    /// (<see cref="ProjectDetailsProjection"/> only defines one for <see cref="ProjectRegistered"/>;
    /// every other event on an unseen stream still gets a document via the parameterless constructor
    /// and <c>Apply</c>). Fetches only that first event — <c>version: 1</c> — never the whole stream,
    /// since a real project's own stream can run to thousands of events by the time this guard asks.
    /// </summary>
    private static async Task<bool> IsGenesisRegisteredProjectStreamAsync(
        IQuerySession session, Guid streamId, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> genesis =
            await session.Events.FetchStreamAsync(streamId, version: 1, token: cancellationToken);
        return genesis is [{ EventType: var eventType }] && eventType == typeof(ProjectRegistered);
    }

    /// <summary>
    /// Which <see cref="ReplicationStreamFamily"/> <paramref name="eventType"/> itself belongs to —
    /// the stream-ownership guard's own mismatched-family check
    /// (<c>refuseAsMismatchedFamilyOntoExistingStream</c>) needs this to compare against
    /// <see cref="ReplicationOwnership.Family"/> without a second document lookup. Every type that
    /// ever reaches this check already passed the earlier <c>EventScopeRegistry.ClassificationOf</c>
    /// gate (<c>ProjectScoped</c>), so its own namespace is always one of exactly these seven feature
    /// roots — <see cref="ReplicationStreamFamily.Unknown"/> is kept as the fallback anyway, rather
    /// than throwing, so an event type this mapping has not been taught about yet is simply never
    /// refused on this ground, the same "fail open on the enhancement, never regress the guard's own
    /// prior coverage" choice <see cref="ReplicationOwnership.Family"/>'s own doc makes.
    /// </summary>
    private static ReplicationStreamFamily FamilyOf(Type eventType) => eventType.Namespace switch
    {
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Tasks", StringComparison.Ordinal) => ReplicationStreamFamily.Task,
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Idea", StringComparison.Ordinal) => ReplicationStreamFamily.Idea,
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Epic", StringComparison.Ordinal) => ReplicationStreamFamily.Epic,
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Run", StringComparison.Ordinal) => ReplicationStreamFamily.Run,
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Decision", StringComparison.Ordinal) => ReplicationStreamFamily.Decision,
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Learning", StringComparison.Ordinal) => ReplicationStreamFamily.Learning,
        { } ns when ns.StartsWith("Hall9k.Domain.Features.Project", StringComparison.Ordinal) => ReplicationStreamFamily.Project,
        _ => ReplicationStreamFamily.Unknown,
    };

    /// <summary>
    /// The highest origin sequence <paramref name="streamId"/> already holds from each origin node,
    /// read from the applied copies' own <see cref="ReplicationEventHeaders.OriginNodeId"/>/
    /// <see cref="ReplicationEventHeaders.OriginSequence"/> headers. Read from the store once per
    /// stream per read, BEFORE anything is appended to that stream in this read — a
    /// <c>LightweightSession</c>'s own <c>FetchStreamAsync</c> only ever sees committed rows, the
    /// identical uncommitted-visibility gap <c>streamsStartedThisRead</c> exists to close — then
    /// kept current in <paramref name="originHighWaterByStream"/> as records land. Empty, with no
    /// query at all, for a stream this node does not hold yet, which is the whole of a bootstrap
    /// answer and most of a pull's: the per-stream query is paid only for streams already here.
    /// <para>
    /// An event this node produced NATIVELY carries no origin headers and is deliberately not
    /// counted: its origin is this node, and <c>EventCatchUpResponder</c> never hands a node its
    /// own history back, so no incoming record can ever be ordered against one.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<Guid, long>> OriginHighWaterAsync(
        IQuerySession session, Guid streamId, bool streamExists,
        Dictionary<Guid, Dictionary<Guid, long>> originHighWaterByStream, CancellationToken cancellationToken)
    {
        if (originHighWaterByStream.TryGetValue(streamId, out Dictionary<Guid, long>? known))
        {
            return known;
        }

        Dictionary<Guid, long> highWater = [];
        originHighWaterByStream[streamId] = highWater;
        if (!streamExists)
        {
            return highWater;
        }

        IReadOnlyList<IEvent> held = await session.Events.FetchStreamAsync(streamId, token: cancellationToken);
        foreach (IEvent candidate in held)
        {
            if (candidate.GetHeader(ReplicationEventHeaders.OriginNodeId) is not string originNodeIdText
                || !Guid.TryParse(originNodeIdText, out Guid originNodeId)
                || candidate.GetHeader(ReplicationEventHeaders.OriginSequence) is not string originSequenceText
                || !long.TryParse(originSequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long originSequence))
            {
                continue;
            }

            if (!highWater.TryGetValue(originNodeId, out long current) || originSequence > current)
            {
                highWater[originNodeId] = originSequence;
            }
        }

        return highWater;
    }

    /// <summary>Rewrites the top-level <c>projectId</c> property (case-insensitive, matching
    /// whatever casing this build's own JSON options produce) to <paramref name="localProjectId"/>
    /// when the payload carries one — a Task, Idea, or Epic event's own project reference. Leaves
    /// every other field alone, including an "Id" that happens to equal the sender's own project id
    /// on a Project-aggregate event (<see cref="ProjectStreamReplicationRules.IsProjectAggregateStreamEvent"/>
    /// already rewrites that event's own STREAM id instead, and nothing reads that field back).
    /// <para>
    /// A <c>scopeId</c> under a <c>scope</c> of <c>Project</c> is the same per-install coordinate
    /// wearing a different name, and is rewritten too: <c>DecisionRecorded</c> and
    /// <c>LearningRecorded</c> (idea d805fd8b, piece 1) carry the project there rather than in a
    /// <c>projectId</c> field. Without this a replicated decision landed on the receiver holding
    /// the SENDER's project id, which made it unreachable through every project-keyed surface
    /// there is — <c>h9k decide list --project</c>, <c>ReplicationProjectResolver</c> (so the
    /// receiver never forwarded it on to a third member), and <c>ProjectPurgeEngine</c>'s own
    /// scope-id sweep (independent pre-PR review, cycle 1, adversarial lens). Keyed on the
    /// sibling <c>scope</c> value rather than on the field name alone, because <c>Owner</c> is
    /// the other scope that field carries and an owner id must never be overwritten with a
    /// project's.
    /// </para></summary>
    private static void RewriteProjectIdField(JsonObject dataObject, Guid localProjectId)
    {
        RewriteField(dataObject, "projectId", localProjectId);

        if (Property(dataObject, "scope") is string scopeKey
            && dataObject[scopeKey] is JsonValue scope
            && scope.TryGetValue(out string? scopeValue)
            && scopeValue == KnowledgeScope.Project.Value)
        {
            RewriteField(dataObject, "scopeId", localProjectId);
        }
    }

    private static void RewriteField(JsonObject dataObject, string name, Guid localProjectId)
    {
        if (Property(dataObject, name) is string matchingKey)
        {
            dataObject[matchingKey] = JsonValue.Create(localProjectId);
        }
    }

    /// <summary>The payload's own key for <paramref name="name"/>, whatever casing this build's
    /// JSON options produced, or null when it carries no such property.</summary>
    private static string? Property(JsonObject dataObject, string name) => dataObject
        .FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
        .Key;

    /// <summary>This project's own ledger-derived key: the live trust chain's own value when a
    /// caller actually computed one this tick (every production sweep does, via
    /// <c>MessageSweepEngine.ProbeAndReadAsync</c>), falling back to this install's own local
    /// mirror (<see cref="ProjectDetails.ProjectKey"/>) only when it did not. Null when neither
    /// source has one yet, in which case a mismatch can never be judged and nothing carrying a
    /// project key is refused on that basis alone; the identical resolution
    /// <c>MessageInbox.ReadFromAsync</c>'s own <c>ResolveLocalProjectKeyAsync</c> applies.</summary>
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
}
