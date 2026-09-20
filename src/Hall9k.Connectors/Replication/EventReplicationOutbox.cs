using Hall9k.Connectors.Messaging;
using Hall9k.Domain.Features.Idea;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Replication;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Infrastructure.Persistence;
using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten;

namespace Hall9k.Connectors.Replication;

/// <summary>How many envelopes one <see cref="EventReplicationOutbox.QueuePendingAsync"/> call
/// actually queued through <see cref="MessageOutbox.QueueAsync"/> — the daemon's own sweep flushes
/// them in the same tick, alongside whatever else this project has pending, through the ordinary
/// <see cref="MessageOutbox.FlushAsync"/> call it already makes.</summary>
public sealed record EventReplicationQueueResult(int EnvelopesQueued, int EventsQueued);

/// <summary>
/// The outbound half of event replication (idea 202383dc, M2a; scoped per item by idea 8c5993c5): on
/// this node's own current global event log, past this project's own durable
/// <see cref="EventReplicationOutboxPosition"/> (never before this node's own
/// <see cref="NodeAggregate.ReplicationSwitchOnSequence"/>, recorded here the first time this method
/// ever runs), reads every <see cref="EventScope.ProjectScoped"/> event that belongs to
/// <paramref name="projectId"/>, batches it into envelopes of kind <see cref="MessageKind.Events"/> —
/// capped at <see cref="MaxEventsPerEnvelope"/> events or <see cref="MaxBytesPerEnvelope"/> bytes
/// each — and queues each one through the ordinary <see cref="MessageOutbox.QueueAsync"/>, so the
/// daemon's existing per-project flush lands them in the same commit as any other pending mail. A
/// fact this node itself received by replication (carrying <see cref="ReplicationEventHeaders.OriginEventId"/>)
/// never re-enters this scan: only what this node itself produced travels, exactly as the acceptance
/// criterion says, and re-sending a teammate's own fact back at them under a fresh local event id and
/// this node's own origin stamp would otherwise echo forever, each hop minting a new duplicate on
/// both ends. A currently-private task or idea's own events are skipped, never blocking any other
/// stream's own events in the same scan — but the durable position this project's own next scan
/// resumes from never advances past the earliest one still owed to a teammate (<see
/// cref="EventReplicationOutboxPosition.PendingPrivateSequences"/>; the same gap-stop idiom
/// <c>GitLedgerMessageTransport.ReadSinceAsync</c> already uses for a numeric seq gap), so clearing
/// the flag later always finds it again rather than having silently skipped past it forever. A
/// separate, never-capped high-water mark
/// (<see cref="EventReplicationOutboxPosition.HighestQueuedSequence"/>) is what stops that resumed
/// rescan from re-queuing an already-sent event past the hold-back point a second time (independent
/// pre-PR review, cycle 3, both lenses: the position alone can only ever move backward while the
/// flag stays set, so every eligible event past it was otherwise re-batched, and re-pushed as a
/// brand-new envelope, on every single sweep for as long as the flag stood) — but the mark alone
/// cannot tell "already sent" apart from "still owed", once some other stream's own later event
/// pushed the mark past a held-back one; <see
/// cref="EventReplicationOutboxPosition.PendingPrivateSequences"/> makes that distinction, so a
/// rescan neither drops a held-back event forever nor pays a full reclassification-and-resolve pass
/// for every already-sent event it re-reads while anything nearby stays private (independent pre-PR
/// review, cycle 5, both lenses).
/// <para>
/// Idea 8c5993c5: an eligible event's own audience follows its resolved <see cref="ReplicationScope"/>
/// — <see cref="ReplicationScope.Team"/> addresses <see cref="MessageAudience.Project"/> exactly as
/// every event always has, and <see cref="ReplicationScope.Fleet"/> addresses
/// <see cref="MessageAudience.Owner"/> at the event's own origin owner root fingerprint, so it
/// reaches every one of that owner's own nodes and no one else's — the same addressing an invite
/// proof already uses. The two audiences batch and flush independently, since a single envelope
/// carries one audience. Whenever a scope-widening event (<see cref="IdeaScopeSet"/>,
/// <see cref="IdeaPrivacySet"/>, <see cref="TaskScopeSet"/>, <see cref="TaskPrivacySet"/>, or
/// <see cref="TaskPublished"/>) passes through this scan, its own stream is marked
/// (<see cref="EventReplicationOutboxPosition.PendingFullResendStreamIds"/>) for a one-time resend of
/// every earlier event on that stream still sitting below this scan's own starting position — the
/// only way a newly-wider audience (a teammate an idea was just shared with, say) ever receives
/// history this outbox already sent narrower and moved past. The resend is bounded to that starting
/// position exactly so it never re-queues what the ordinary forward scan in the SAME call already
/// queued afresh at the current scope.
/// </para>
/// </summary>
public sealed class EventReplicationOutbox(ReplicationProjectResolver ownership)
{
    public const int MaxEventsPerEnvelope = 200;
    public const int MaxBytesPerEnvelope = 250 * 1024;

    public async Task<EventReplicationQueueResult> QueuePendingAsync(
        IDocumentSession session,
        Guid nodeId,
        Guid projectId,
        string fromOwnerFingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long switchOnSequence = await EnsureSwitchedOnAsync(session, nodeId, now, cancellationToken);

        EventReplicationOutboxPosition? position =
            await session.LoadAsync<EventReplicationOutboxPosition>(projectId, cancellationToken);
        long sinceSequence = Math.Max(position?.LastFlushedGlobalSequence ?? 0, switchOnSequence);
        long highestQueuedSequence = position?.HighestQueuedSequence ?? 0;

        // Sequences this project has found still private and skipped, not yet actually queued —
        // every one of them is re-examined below regardless of where it sits relative to
        // highestQueuedSequence, which is what lets a formerly-private event be told apart from an
        // already-sent one once some other stream's own later event has pushed the mark past it
        // (independent pre-PR review, cycle 5, adversarial lens).
        HashSet<long> pendingPrivate = position?.PendingPrivateSequences is { Count: > 0 } savedPrivate ? [.. savedPrivate] : [];

        // Streams a scope-widening event marked for a one-time full-history resend at their own new,
        // wider scope (idea 8c5993c5) — never cleared until that resend actually runs, so a crash
        // between marking and resending finds it again rather than silently dropping it.
        HashSet<Guid> pendingFullResend = position?.PendingFullResendStreamIds is { Count: > 0 } savedResend ? [.. savedResend] : [];

        // A snapshot of the two fast-skip inputs exactly as loaded, before the forward scan below
        // mutates either one — what the resend pass (below) needs to tell "the forward scan already
        // handled this sequence in THIS SAME call" apart from "the forward scan's own fast skip
        // passed over it, and it is this resend's own gap to close". The LIVE, scan-mutated
        // variables cannot answer that: by the time the resend pass runs, every sequence the forward
        // scan actually reprocessed has already been removed from pendingPrivate (or has already
        // pushed highestQueuedSequence past it), making a freshly-queued sequence indistinguishable
        // from one the fast skip genuinely never touched — and resending it a second time here would
        // double-queue whatever the forward scan already sent this same call (independent pre-PR
        // review, cycle 1, adversarial lens, medium — the fix for the fast-skip gap otherwise
        // reintroduces the exact double-send the sinceSequence bound always existed to prevent).
        long highestQueuedSequenceAtScanStart = highestQueuedSequence;
        HashSet<long> pendingPrivateAtScanStart = [.. pendingPrivate];

        IReadOnlyList<IEvent> candidates = await session.Events.QueryAllRawEvents()
            .Where(e => e.Sequence > sinceSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0 && pendingFullResend.Count == 0)
        {
            return new EventReplicationQueueResult(0, 0);
        }

        int envelopesQueued = 0;
        int eventsQueued = 0;
        long lastIncludedSequence = sinceSequence;
        Dictionary<MessageAudience, PendingEnvelopeBatch> batches = [];

        // The earliest sequence sitting in any audience's own still-unflushed batch, excluding
        // whichever audience is about to be committed alongside this very snapshot — the floor
        // a position advance must never cross. Per-audience batches flush one at a time, each in
        // its own commit (MessageOutbox.QueueAsync calls SaveChangesAsync itself), so a snapshot
        // taken while flushing audience A must not claim credit for records still waiting,
        // uncommitted, in audience B's own open batch: a crash or a thrown exception between the
        // two commits would otherwise leave those records permanently unsent, since the next
        // sweep's own sinceSequence and fast-skip both read the (falsely advanced) position as
        // "already handled" (independent pre-PR review, cycle 1, both lenses, high — the single
        // shared position doc no longer commits atomically with a single shared batch once a scan
        // can produce more than one audience).
        long OpenBatchFloor(MessageAudience? excluding)
        {
            long floor = long.MaxValue;
            foreach ((MessageAudience audience, PendingEnvelopeBatch state) in batches)
            {
                if (excluding is not null && audience == excluding)
                {
                    continue;
                }

                if (state.Records.Count == 0 || state.FirstSequence is not { } first)
                {
                    continue;
                }

                floor = Math.Min(floor, first - 1);
            }

            return floor;
        }

        // Stores the position doc reflecting progress so far, immediately before the envelope that
        // progress belongs to — the identical "advance and queue land in one commit" idiom the
        // single-batch version of this method always used, generalized across as many audiences and
        // flush points (mid-scan overflow, end of scan, the resend pass) as one call now has.
        // <paramref name="flushing"/> names the one audience whose own batch is being committed in
        // the SAME transaction as this snapshot — safe to credit — so every OTHER audience's still-
        // open batch caps how far both fields below are allowed to advance.
        void StorePositionSnapshot(MessageAudience? flushing = null)
        {
            long floor = OpenBatchFloor(flushing);
            long safeLastIncluded = floor == long.MaxValue ? lastIncludedSequence : Math.Min(lastIncludedSequence, floor);
            long safeHighestQueued = floor == long.MaxValue ? highestQueuedSequence : Math.Min(highestQueuedSequence, floor);
            session.Store(new EventReplicationOutboxPosition
            {
                Id = projectId,
                LastFlushedGlobalSequence = CapAtHeldBackPosition(safeLastIncluded, pendingPrivate),
                HighestQueuedSequence = safeHighestQueued,
                PendingPrivateSequences = [.. pendingPrivate],
                PendingFullResendStreamIds = [.. pendingFullResend],
            });
        }

        async Task FlushAsync(MessageAudience audience)
        {
            if (!batches.TryGetValue(audience, out PendingEnvelopeBatch? state) || state.Records.Count == 0)
            {
                return;
            }

            StorePositionSnapshot(audience);
            await MessageOutbox.QueueAsync(
                session, nodeId, projectId, fromOwnerFingerprint, audience, about: null, MessageKind.Events,
                EventReplicationCodec.EncodeBatch(state.Records), now, cancellationToken);
            envelopesQueued++;
            eventsQueued += state.Records.Count;
            batches[audience] = new PendingEnvelopeBatch();
        }

        async Task AddAsync(
            MessageAudience audience, EventReplicationCodec.ReplicatedEventRecord record, string recordJson, long sequence)
        {
            if (!batches.TryGetValue(audience, out PendingEnvelopeBatch? state))
            {
                state = new PendingEnvelopeBatch();
                batches[audience] = state;
            }

            if (state.Records.Count >= MaxEventsPerEnvelope
                || (state.Records.Count > 0 && state.Bytes + recordJson.Length > MaxBytesPerEnvelope))
            {
                await FlushAsync(audience);
                state = batches[audience];
            }

            // The lowest sequence in this open batch — OpenBatchFloor's own read of how far a
            // snapshot taken while some OTHER audience flushes may safely advance.
            state.FirstSequence ??= sequence;
            state.Records.Add(record);
            state.Bytes += recordJson.Length;
        }

        foreach (IEvent candidate in candidates)
        {
            if (candidate.Sequence <= highestQueuedSequence && !pendingPrivate.Contains(candidate.Sequence))
            {
                // Already scanned in an earlier sweep and conclusively settled then — queued, or
                // found to be none of this project's business — with nothing left here that could
                // change that reading except its own scope, and a still-private candidate is exactly
                // what pendingPrivate tracks. Skipping the classification and ownership resolution
                // below for the rest is what keeps a long-held-back private draft from making every
                // sweep re-read and re-resolve the whole rest of the project's history past it
                // (independent pre-PR review, cycle 5, conformance lens, medium).
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            EventScope scope;
            try
            {
                scope = EventScopeRegistry.ClassificationOf(candidate.EventType);
            }
            catch (InvalidOperationException)
            {
                // Never classified — cannot decide whether this is replication's business at all.
                // Skipping it here is the fail-soft twin of EventScopeRegistryTests' own build-time
                // gate, which is what actually stops an unclassified type from shipping.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (scope != EventScope.ProjectScoped)
            {
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (candidate.GetHeader(ReplicationEventHeaders.OriginEventId) is not null)
            {
                // Already a fact this node received by replication, not one it produced itself —
                // sending it back out would echo a teammate's own event back at them under a fresh
                // local event id and this node's origin stamp, forever. Only what this node itself
                // wrote travels.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            ReplicationOwnership resolved = await ownership.ResolveAsync(session, candidate, cancellationToken);
            if (resolved.ProjectId != projectId)
            {
                // Belongs to no project this node knows, or a different one — never this project's
                // outbox's business either way, and never worth looking at again.
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (resolved.IsProjectStreamItself && ProjectStreamReplicationRules.IsProjectIdentityEvent(candidate.EventType))
            {
                // Only the Project aggregate's own identity event (ProjectRegistered) is excluded
                // here: it mints this install's own local project id (ProjectAddCommand), never
                // shared across installs, so a fact appended under it could never land on a
                // receiver's own Project stream, only create a phantom one under a foreign id. Never
                // worth looking at again, the same as any other never-this-project's-business skip.
                // Every OTHER project-scoped event on this same stream still travels like any other
                // candidate below — the receiving inbox rewrites the team-facing subset
                // (ProjectTeamSettingsChanged, MemberVouched, MemberRemoved) onto ITS OWN local
                // Project stream (ProjectStreamReplicationRules.IsProjectAggregateStreamEvent), but
                // keeps the sender's own foreign stream id for the per-install lifecycle events
                // (ProjectStreamReplicationRules.IsProjectLifecycleEvent) — those are this install's
                // own decision about its own local copy, never one a teammate's node may act on
                // (independent pre-PR review, cycle 3, conformance lens).
                lastIncludedSequence = candidate.Sequence;
                continue;
            }

            if (resolved.Scope == ReplicationScope.Private)
            {
                // Skip only this event, never the rest of the scan: a private task or idea must not
                // hold back every other stream's own events. Recorded (or kept recorded) as owed, so
                // a later sweep re-finds it by sequence rather than trusting highestQueuedSequence's
                // reading once the flag clears and some other stream has since pushed the mark past
                // it (the fast skip above, and the position cap below, both key off this set).
                pendingPrivate.Add(candidate.Sequence);
                continue;
            }

            // Reaching here with candidate.Sequence <= highestQueuedSequence is only possible for a
            // pendingPrivate member (the fast skip above is the only thing that could have let it
            // through) — never treated as "already sent" the way the high-water mark alone would
            // read it. It is owed, whichever sweep first held it back.
            EventReplicationCodec.ReplicatedEventRecord record = ToRecord(candidate, nodeId, fromOwnerFingerprint, projectId);
            string recordJson = System.Text.Json.JsonSerializer.Serialize(record);
            await AddAsync(
                AudienceFor(resolved.Scope, record.OriginOwnerRootFingerprint, fromOwnerFingerprint),
                record, recordJson, candidate.Sequence);

            if (IsScopeChangingEventType(candidate.EventType))
            {
                // idea 8c5993c5: this stream just moved to a scope at least this wide — everything
                // it holds below this scan's own starting position (sinceSequence), OR sitting in
                // the fast-skip gap between sinceSequence and highestQueuedSequence (a gap that
                // opens whenever some OTHER stream's held-back private event has kept sinceSequence
                // from advancing as far as this project's own high-water mark already has), never
                // reached whatever audience it is newly eligible for, and the resend pass below is
                // the one chance to catch both regions up (independent pre-PR review, cycle 1,
                // adversarial lens, medium: bounding the resend to sinceSequence alone left the gap
                // neither resent nor re-queued, since the ordinary forward scan fast-skips it too).
                pendingFullResend.Add(candidate.StreamId);
            }

            lastIncludedSequence = candidate.Sequence;
            // Math.Max, not a plain assignment: a pendingPrivate member being caught up here can
            // have a LOWER sequence than the mark already reached through other streams, and a plain
            // assignment would regress the mark backward — which would then make the fast skip above
            // wrongly stop trusting every already-sent event between the regressed mark and where it
            // used to be, reclassifying and re-queuing them as duplicates on this very sweep.
            highestQueuedSequence = Math.Max(highestQueuedSequence, candidate.Sequence);
            // Sent now — no longer owed.
            pendingPrivate.Remove(candidate.Sequence);
        }

        // Batches stay open across the resend pass below, rather than flushing here first: a
        // scope-changing candidate's own envelope and the history the resend pass sends to the
        // identical new audience belong in one envelope together whenever they both fit, not two.
        foreach (Guid streamId in pendingFullResend.ToList())
        {
            await ResendStreamHistoryAsync(
                session, nodeId, projectId, fromOwnerFingerprint, streamId, switchOnSequence, sinceSequence,
                highestQueuedSequenceAtScanStart, pendingPrivateAtScanStart, AddAsync, cancellationToken);
            // Handled either way: fully resent at whatever scope it currently reads, or found
            // private again by the time this ran (nothing to send right now) — a future
            // scope-widening event re-adds it if that ever changes.
            pendingFullResend.Remove(streamId);
        }

        foreach (MessageAudience audience in batches.Keys.ToList())
        {
            await FlushAsync(audience);
        }

        StorePositionSnapshot();
        await session.SaveChangesAsync(cancellationToken);

        return new EventReplicationQueueResult(envelopesQueued, eventsQueued);
    }

    /// <summary>Never lets a persisted position pass the earliest event still owed to a teammate,
    /// however far past it later, eligible events were actually queued and sent.</summary>
    private static long CapAtHeldBackPosition(long candidatePosition, HashSet<long> pendingPrivate) =>
        pendingPrivate.Count > 0 ? Math.Min(candidatePosition, pendingPrivate.Min() - 1) : candidatePosition;

    /// <summary>
    /// idea 8c5993c5: <see cref="ReplicationScope.Team"/> addresses the whole project, exactly as
    /// every event always has; <see cref="ReplicationScope.Fleet"/> addresses the event's own origin
    /// owner root — every one of that owner's own nodes, the addressing an invite proof already
    /// uses. Never called for <see cref="ReplicationScope.Private"/>, which never reaches here.
    /// <paramref name="fromOwnerFingerprint"/> is the fallback for
    /// <see cref="EventOriginStampingListener.UnclaimedOwnerRootFingerprint"/> — a candidate appended
    /// before this node's own owner had claimed a root at all, which that listener's own doc says to
    /// resolve rather than trust as a final answer; this node's own current fingerprint is the
    /// ordinary case such a candidate belongs to.
    /// </summary>
    private static MessageAudience AudienceFor(ReplicationScope scope, string originOwnerRootFingerprint, string fromOwnerFingerprint) =>
        scope == ReplicationScope.Team
            ? MessageAudience.Project
            : MessageAudience.Owner(
                string.IsNullOrWhiteSpace(originOwnerRootFingerprint) ? fromOwnerFingerprint : originOwnerRootFingerprint);

    /// <summary>
    /// Every event type whose own appearance means the stream it lives on just widened its
    /// replication scope (idea 8c5993c5) — the trigger for a one-time full-history resend of that
    /// stream, below. <see cref="TaskPublished"/> is included because publishing always sets team
    /// scope unconditionally (<see cref="Hall9k.Domain.Features.Tasks.TaskAggregate.Apply(TaskPublished)"/>),
    /// with no separate scope event of its own.
    /// </summary>
    private static bool IsScopeChangingEventType(Type eventType) =>
        eventType == typeof(IdeaScopeSet)
        || eventType == typeof(IdeaPrivacySet)
        || eventType == typeof(TaskScopeSet)
        || eventType == typeof(TaskPrivacySet)
        || eventType == typeof(TaskPublished);

    /// <summary>
    /// idea 8c5993c5: resends <paramref name="streamId"/>'s own history strictly between this node's
    /// own switch-on point and the wider of <paramref name="sinceSequence"/> (the ordinary forward
    /// scan's own starting position for THIS call) and <paramref name="highestQueuedSequenceAtScanStart"/>
    /// (this project's own high-water mark exactly as loaded, before the forward scan below advances
    /// it any further this same call). The two diverge exactly when some OTHER stream's own held-back
    /// private event has kept <paramref name="sinceSequence"/> from advancing as far as the mark
    /// already had — a gap the ordinary forward scan's own fast skip (candidate.Sequence &lt;=
    /// highestQueuedSequence) never re-examines, so this stream's own already-sent events sitting in
    /// that gap would otherwise reach neither the forward scan nor this resend, and a teammate newly
    /// let in would receive an incomplete history (independent pre-PR review, cycle 1, adversarial
    /// lens, medium). A candidate at or below <paramref name="sinceSequence"/> is always this resend's
    /// own to handle; one strictly above it is only this resend's to handle when the forward scan's
    /// fast skip would also have passed over it, judged by the pre-scan snapshot
    /// <paramref name="pendingPrivateAtScanStart"/> names the exception: a sequence recorded there
    /// was NOT fast-skipped and was instead freshly reclassified by the forward scan in this very
    /// call, which would double-queue it if resent here too — the LIVE, scan-mutated set cannot make
    /// this call, since every sequence the forward scan itself reprocesses is removed from it as that
    /// happens, making a sequence the forward scan just queued indistinguishable, by the time this
    /// resend pass runs, from one the fast skip genuinely never touched. Forwards own-or-replicated
    /// events alike, true origin preserved, the identical technique
    /// <see cref="EventCatchUpResponder"/> already uses to answer a catch-up request for one stream: a
    /// scope change run from a fleet sibling that only ever received this stream by replication must
    /// still be able to resend its full history, not just whatever it produced itself. Sends nothing,
    /// and leaves the caller to treat the stream as handled regardless, when the stream currently
    /// reads <see cref="ReplicationScope.Private"/> — it was toggled back before this ran, and a
    /// later scope-widening event re-marks it if that changes again.
    /// </summary>
    private async Task ResendStreamHistoryAsync(
        IDocumentSession session, Guid nodeId, Guid projectId, string fromOwnerFingerprint, Guid streamId,
        long switchOnSequence, long sinceSequence, long highestQueuedSequenceAtScanStart,
        HashSet<long> pendingPrivateAtScanStart,
        Func<MessageAudience, EventReplicationCodec.ReplicatedEventRecord, string, long, Task> addAsync,
        CancellationToken cancellationToken)
    {
        long upperBoundSequence = Math.Max(sinceSequence, highestQueuedSequenceAtScanStart);
        IReadOnlyList<IEvent> history = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamId == streamId && e.Sequence > switchOnSequence && e.Sequence <= upperBoundSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);

        foreach (IEvent candidate in history)
        {
            // Above sinceSequence, this candidate also sat in the forward scan's own candidate list
            // for THIS call. It is this resend's to handle only when the forward scan's fast skip
            // would have passed over it too, judged by the SAME inputs the fast skip itself used —
            // the state exactly as loaded at the top of this call, before the forward scan started
            // mutating either one. The live, already-mutated pendingPrivate cannot answer this: by
            // the time this resend pass runs, the forward scan has already removed every sequence it
            // itself reprocessed, making a sequence THIS SAME CALL just queued indistinguishable
            // from one the fast skip genuinely never touched — and resending it again here would
            // double-queue it.
            if (candidate.Sequence > sinceSequence && pendingPrivateAtScanStart.Contains(candidate.Sequence))
            {
                continue;
            }

            EventScope scope;
            try
            {
                scope = EventScopeRegistry.ClassificationOf(candidate.EventType);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (scope != EventScope.ProjectScoped)
            {
                continue;
            }

            ReplicationOwnership resolved = await ownership.ResolveAsync(session, candidate, cancellationToken);
            if (resolved.ProjectId != projectId)
            {
                continue;
            }

            if (resolved.IsProjectStreamItself && ProjectStreamReplicationRules.IsProjectIdentityEvent(candidate.EventType))
            {
                continue;
            }

            if (resolved.Scope == ReplicationScope.Private)
            {
                return;
            }

            EventReplicationCodec.ReplicatedEventRecord record =
                ToRecordPreservingOrigin(candidate, nodeId, fromOwnerFingerprint, projectId);
            string recordJson = System.Text.Json.JsonSerializer.Serialize(record);
            await addAsync(
                AudienceFor(resolved.Scope, record.OriginOwnerRootFingerprint, fromOwnerFingerprint),
                record, recordJson, candidate.Sequence);
        }
    }

    private static EventReplicationCodec.ReplicatedEventRecord ToRecord(
        IEvent candidate, Guid nodeId, string fromOwnerFingerprint, Guid projectId)
    {
        string originNodeIdText = candidate.GetHeader(EventOriginStampingListener.NodeIdHeader) as string ?? string.Empty;
        Guid originNodeId = Guid.TryParse(originNodeIdText, out Guid parsedNodeId) ? parsedNodeId : nodeId;
        // string.Empty (never null) is EventOriginStampingListener.UnclaimedOwnerRootFingerprint —
        // that listener's own doc says to resolve or fall back rather than trust it as a final
        // answer, so a blank header falls back exactly as a missing one would.
        string originHeaderValue = candidate.GetHeader(EventOriginStampingListener.OwnerRootFingerprintHeader) as string ?? string.Empty;
        string originOwnerRootFingerprint = string.IsNullOrWhiteSpace(originHeaderValue) ? fromOwnerFingerprint : originHeaderValue;

        return new EventReplicationCodec.ReplicatedEventRecord(
            candidate.StreamId,
            candidate.EventType.FullName ?? candidate.EventType.Name,
            System.Text.Json.JsonSerializer.Serialize(candidate.Data, candidate.EventType),
            candidate.Id,
            candidate.Sequence,
            originNodeId,
            originOwnerRootFingerprint,
            candidate.Timestamp,
            projectId);
    }

    /// <summary>The resend pass's own record builder — <see cref="ToRecord"/>'s twin, except it
    /// preserves an ALREADY-REPLICATED event's own true origin (<see cref="ReplicationEventOriginResolver"/>)
    /// rather than assuming every candidate is this node's own, since a resend may run on a fleet
    /// sibling that only ever received the stream by replication.</summary>
    private static EventReplicationCodec.ReplicatedEventRecord ToRecordPreservingOrigin(
        IEvent candidate, Guid nodeId, string fromOwnerFingerprint, Guid projectId)
    {
        (Guid originNodeId, string originOwnerRootFingerprint, Guid originEventId, long originSequence) =
            ReplicationEventOriginResolver.Resolve(candidate, nodeId, fromOwnerFingerprint);

        return new EventReplicationCodec.ReplicatedEventRecord(
            candidate.StreamId,
            candidate.EventType.FullName ?? candidate.EventType.Name,
            System.Text.Json.JsonSerializer.Serialize(candidate.Data, candidate.EventType),
            originEventId,
            originSequence,
            originNodeId,
            originOwnerRootFingerprint,
            candidate.Timestamp,
            projectId);
    }

    /// <summary>The first time this ever runs on this node, records the node's own current global
    /// sequence as its switch-on point (idea 202383dc: "the first time replication runs on a node it
    /// records that node's current global sequence as its switch-on point on the Node stream; events
    /// before it never travel"). Idempotent: a node that has already switched on just replays its
    /// recorded point back. Internal rather than private: <see cref="EventCatchUpResponder"/> calls
    /// this identical method to learn the same switch-on point before forwarding ANY held event to a
    /// catch-up requester — a pre-switch-on event is this node's own migration-era history, ruled to
    /// never travel (idea 202383dc; Brian's 2026-09-13 ruling), and a catch-up answer must honor that
    /// exclusion exactly as an ordinary outbox flush already does (independent pre-PR review, cycle 1,
    /// conformance lens, high).</summary>
    internal static async Task<long> EnsureSwitchedOnAsync(
        IDocumentSession session, Guid nodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NodeAggregate node = await session.Events.AggregateStreamAsync<NodeAggregate>(nodeId, token: cancellationToken)
            ?? throw new InvalidOperationException($"Node {nodeId} has no stream to switch replication on for.");

        if (node.ReplicationSwitchOnSequence is { } existing)
        {
            return existing;
        }

        IReadOnlyList<IEvent> latest = await session.Events.QueryAllRawEvents()
            .OrderByDescending(e => e.Sequence)
            .Take(1)
            .ToListAsync(cancellationToken);
        long currentGlobalSequence = latest.Count > 0 ? latest[0].Sequence : 0;

        session.Events.Append(nodeId, NodeDecider.SwitchOnReplication(node, currentGlobalSequence, now));
        await session.SaveChangesAsync(cancellationToken);
        return currentGlobalSequence;
    }

    private sealed class PendingEnvelopeBatch
    {
        public List<EventReplicationCodec.ReplicatedEventRecord> Records { get; } = [];
        public long Bytes { get; set; }
        /// <summary>The lowest global sequence added to this batch since it was last flushed — what caps how far a snapshot taken while another audience flushes may safely advance.</summary>
        public long? FirstSequence { get; set; }
    }
}
