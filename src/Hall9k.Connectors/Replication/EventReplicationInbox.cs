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
using Marten;
using Marten.Events;
using Microsoft.Extensions.Logging;

namespace Hall9k.Connectors.Replication;

/// <summary>One sweep's own outcome reading one sender's <c>events</c>-kind envelopes for one
/// project. <see cref="StalledAtSeq"/> is <see cref="TransportReadResult.StalledAtSeq"/> passed
/// straight through — a numeric gap in this sender's own outbox this call could not even inspect
/// (idea 202383dc, M2b, task 9408d525: "a node that finds a gap in a sender's sequence... asks a
/// peer"), the trigger <c>Hall9k.Connectors.Replication.EventCatchUpCoordinator.RequestGapFillAsync</c>
/// is built for.</summary>
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
/// </summary>
public sealed class EventReplicationInbox(IMessageTransport transport, ILogger<EventReplicationInbox>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EventReplicationReadResult> ReadFromAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid senderNodeId,
        Guid projectId,
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

            foreach (EventReplicationCodec.ReplicatedEventRecord record in batch)
            {
                streamIdsAnsweredThisRead.Add(record.StreamId);
                if (await ApplyAsync(
                    session, record, senderNodeId, projectId, envelope.ProjectKey, streamsStartedThisRead,
                    originEventIdsAppliedThisRead, originProgressThisRead, now, cancellationToken))
                {
                    applied++;
                }
            }
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
                .Where(request => request.ProjectId == projectId && request.AnsweredAt == null && !request.Exhausted)
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
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        return new EventReplicationReadResult(SenderIgnored: senderIgnored, applied, read.StalledAtSeq);
    }

    /// <summary>Returns false, applying nothing, when this origin event id is already stored — the
    /// idempotency a re-delivered batch relies on — or already applied earlier in this identical
    /// read, uncommitted (<paramref name="originEventIdsAppliedThisRead"/>): the stored-row check
    /// alone only ever sees committed rows, so a second copy of the same origin event later in the
    /// same read would otherwise find nothing yet and apply a duplicate.</summary>
    private async Task<bool> ApplyAsync(
        IDocumentSession session, EventReplicationCodec.ReplicatedEventRecord record, Guid senderNodeId, Guid projectId,
        string? originProjectKey, HashSet<Guid> streamsStartedThisRead, HashSet<Guid> originEventIdsAppliedThisRead,
        Dictionary<Guid, long> originProgressThisRead, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!originEventIdsAppliedThisRead.Add(record.OriginEventId))
        {
            return false;
        }

        if (await session.LoadAsync<ReplicatedEventRecord>(record.OriginEventId, cancellationToken) is not null)
        {
            return false;
        }

        Type? eventType = ReplicationEventTypeCatalog.Resolve(record.EventTypeName);
        if (eventType is null)
        {
            logger?.LogWarning(
                "Replicated event of type {EventType} (origin {OriginEventId}) is not a type this build "
                + "knows — skipped", record.EventTypeName, record.OriginEventId);
            return false;
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
            return false;
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
            return false;
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
            return false;
        }

        if (data is null)
        {
            return false;
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
        StreamAction action = streamExists
            ? session.Events.Append(effectiveStreamId, data)
            : session.Events.StartStream(effectiveStreamId, data);
        streamsStartedThisRead.Add(effectiveStreamId);

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
        if (senderNodeId == record.OriginNodeId)
        {
            if (!originProgressThisRead.TryGetValue(record.OriginNodeId, out long knownHighest))
            {
                EventOriginProgress? persisted = await session.LoadAsync<EventOriginProgress>(
                    EventReplicationStreamId.ForOriginProgress(projectId, record.OriginNodeId), cancellationToken);
                knownHighest = persisted?.HighestOriginSequenceApplied ?? 0;
            }

            if (record.OriginSequence > knownHighest)
            {
                originProgressThisRead[record.OriginNodeId] = record.OriginSequence;
                session.Store(new EventOriginProgress
                {
                    Id = EventReplicationStreamId.ForOriginProgress(projectId, record.OriginNodeId),
                    ProjectId = projectId,
                    OriginNodeId = record.OriginNodeId,
                    HighestOriginSequenceApplied = record.OriginSequence,
                });
            }
            else
            {
                originProgressThisRead[record.OriginNodeId] = knownHighest;
            }
        }

        return true;
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
