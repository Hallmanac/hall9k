using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Infrastructure.Persistence;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events;
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
/// leave the aggregate reading as it did at its creation.
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
public sealed class EventReplicationInbox(IMessageTransport transport, ILogger<EventReplicationInbox>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EventReplicationReadResult> ReadFromAsync(
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
                SenderIgnored = true,
                IgnoredReason = read.NotVouchedReason ?? "no node file vouches for this sender's outbox",
                IgnoredForProjectKeyMismatch = false,
                IgnoredAt = now,
            });
            await session.SaveChangesAsync(cancellationToken);
            return new EventReplicationReadResult(SenderIgnored: true, EventsApplied: 0);
        }

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
        // EventReplicationInboxCursor.SenderIgnored regardless of which reason set it). A null
        // envelope key, or one that is not shaped like a 26-character ULID (a pre-ruling envelope
        // still carrying the retired owner-fingerprint value), is read as "no opinion" and never
        // refused on that basis alone (MessageEnvelopeV1.ProjectKey's own doc).
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
            if (envelope.ProjectKey is { Length: 26 } candidateKey
                && await IsProjectKeyMismatchAsync(session, projectId, candidateKey, localProjectKey, cancellationToken))
            {
                projectKeyMismatch = true;
                projectKeyMismatchReason =
                    $"events envelope {raw.Seq} carries project key {candidateKey}, which does not match "
                    + "this project's own ledger-derived key, refused rather than applied";
                logger?.LogWarning(
                    "Sender {SenderNodeId}'s events envelope {Seq} carries a project key that does not match "
                    + "this project's own ledger-derived key, refused", senderNodeId, raw.Seq);
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

            int appliedBeforeThisEnvelope = applied;
            foreach (EventReplicationCodec.ReplicatedEventRecord record in batch)
            {
                streamIdsAnsweredThisRead.Add(record.StreamId);
                applied += await ApplyAsync(
                    session, record, senderNodeId, projectId, envelope.ProjectKey, streamsStartedThisRead,
                    streamsThatFailedToStartThisRead, originEventIdsAppliedThisRead, originProgressThisRead,
                    originHighWaterByStream, streamsAwaitingHeldTailReplayThisRead, now, cancellationToken);
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
                streamsAwaitingHeldTailReplayThisRead, now, cancellationToken);
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
        }

        return new EventReplicationReadResult(SenderIgnored: senderIgnored, applied, read.StalledAtSeq ?? read.PrunedBelowSeq);
    }

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
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead, DateTimeOffset now,
        CancellationToken cancellationToken)
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
        Guid effectiveStreamId = ProjectStreamReplicationRules.IsProjectAggregateStreamEvent(eventType)
            ? projectId
            : record.StreamId;

        bool streamExists = streamsStartedThisRead.Contains(effectiveStreamId)
            || await session.Events.FetchStreamStateAsync(effectiveStreamId, cancellationToken) is not null;

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
            bool genesisRequired = !ProjectStreamReplicationRules.IsProjectAggregateStreamEvent(eventType)
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
        Dictionary<Guid, long> originHighWater =
            await OriginHighWaterAsync(session, effectiveStreamId, streamExists, originHighWaterByStream, cancellationToken);
        if (originHighWater.TryGetValue(record.OriginNodeId, out long highestHeld) && record.OriginSequence < highestHeld)
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
        if (record.OriginSequence > highestHeld)
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
    /// </summary>
    /// <returns>How many of the held records actually applied.</returns>
    public Task<int> ReplayHeldTailAsync(
        IDocumentSession session, Guid streamId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        HashSet<Guid> streamsStartedThisRead = [];
        HashSet<Guid> streamsThatFailedToStartThisRead = [];
        HashSet<Guid> originEventIdsAppliedThisRead = [];
        Dictionary<Guid, long> originProgressThisRead = [];
        Dictionary<Guid, Dictionary<Guid, long>> originHighWaterByStream = [];
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead = [];
        return ApplyHeldTailAsync(
            session, streamId, streamsStartedThisRead, streamsThatFailedToStartThisRead,
            originEventIdsAppliedThisRead, originProgressThisRead, originHighWaterByStream,
            streamsAwaitingHeldTailReplayThisRead, now, cancellationToken);
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
        HashSet<Guid> streamsAwaitingHeldTailReplayThisRead, DateTimeOffset now, CancellationToken cancellationToken)
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
                    originProgressThisRead, originHighWaterByStream, streamsAwaitingHeldTailReplayThisRead, now,
                    cancellationToken);
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
    /// already rewrites that event's own STREAM id instead, and nothing reads that field back).</summary>
    private static void RewriteProjectIdField(JsonObject dataObject, Guid localProjectId)
    {
        string? matchingKey = dataObject
            .FirstOrDefault(property => string.Equals(property.Key, "projectId", StringComparison.OrdinalIgnoreCase))
            .Key;
        if (matchingKey is not null)
        {
            dataObject[matchingKey] = JsonValue.Create(localProjectId);
        }
    }

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

    /// <summary>Whether a genuinely 26-character <paramref name="candidateKey"/> fails to name this
    /// project: a direct mismatch against <paramref name="localProjectKey"/> when this install
    /// already knows it, or (the only case that needs a lookup at all, since a known key already
    /// answers the question directly) a hit against some OTHER local project's own recorded key
    /// when it does not, so a project too new or too far behind to have read its own key back yet
    /// still refuses an envelope this node can already prove belongs elsewhere. The identical check
    /// <c>MessageInbox.ReadFromAsync</c>'s own <c>IsProjectKeyMismatchAsync</c> applies.</summary>
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
