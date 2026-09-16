using Hall9k.Connectors.Ledger;
using Hall9k.Domain.Features.Message;
using Marten;

namespace Hall9k.Connectors.Messaging;

/// <summary>How many envelopes one <see cref="MessageOutbox.FlushAsync"/> call actually landed —
/// zero means there was nothing queued to flush, so the caller pushed no commit at all.</summary>
public sealed record MessageFlushResult(int EnvelopesFlushed);

/// <summary>How many envelopes <see cref="MessageOutbox.SquashAsync"/> kept — a squash never
/// touches the local event store, only the outbox ref's own content, so there is nothing to
/// report beyond the count that survived.</summary>
public sealed record MessageSquashResult(int EnvelopesKept);

/// <summary>
/// The sending half of the message seam (idea 202383dc, M1a; queue-then-flush split M1b):
/// <see cref="QueueAsync"/> allocates the next seq from this node's own store and records the
/// envelope as queued — no transport call, so it never waits on git or a network — and
/// <see cref="FlushAsync"/> is what the daemon's own sweep calls to actually land every envelope
/// queued since the last flush in one push. Both are self-contained — each calls
/// <see cref="IDocumentSession.SaveChangesAsync"/> itself — so <see cref="QueueAsync"/>'s own seq
/// allocation always reads a prior call's committed result, never an uncommitted one still sitting
/// in the same session.
/// </summary>
public sealed class MessageOutbox(IMessageTransport transport)
{
    /// <summary>
    /// The survivor seqs <see cref="SquashAsync"/> actually pushed last time it ran for a given
    /// outbox, so a later sweep whose own aged-out check still reads true — which it does forever
    /// once anything has ever aged out, since a squash never touches the local event store, only
    /// the transport's own copy (this class's own doc) — can tell "nothing new aged out since that
    /// push" from "something new aged out" and skip the push instead of force-pushing an identical
    /// orphan commit under a fresh timestamp every tick (independent pre-PR review, cycle 1, both
    /// lenses). The comparison below is deliberately one-directional — every seq that survived last
    /// time still surviving now, never whole-set equality — because an ordinary new send changes the
    /// survivor set too (one more seq now counts as "sent"): comparing full-set equality re-triggered
    /// a squash on every later flush once retention was first reached, since a freshly sent envelope
    /// is never equal to the empty or smaller set the last squash actually pushed, even though
    /// nothing has actually aged out that the last squash's own push does not already reflect
    /// (independent pre-PR review, cycle 1, adversarial lens). A push is only worth the ref rewrite
    /// when something that survived last time no longer does. In-memory and per-process, the same as
    /// <c>MessageSweepEngine._lastKnownTips</c>: a restart costs at most one redundant squash, never
    /// a forever-repeating one.
    /// </summary>
    private readonly Dictionary<(string RepositoryPath, Guid FromNodeId, Guid ProjectId), IReadOnlySet<long>> _lastSquashedSurvivorSeqs = [];

    /// <summary>
    /// Static, unlike every other method here: queueing never touches <see cref="IMessageTransport"/>
    /// at all, so a caller with no transport to hand — <c>h9k message send</c>, which never waits on
    /// git or a network — needs no <see cref="MessageOutbox"/> instance either.
    /// <paramref name="projectId"/> is this install's own local project id (idea 202383dc, M2) — never
    /// <see cref="Guid.Empty"/>, per <c>MessageDecider.Queue</c>'s own validation: every message
    /// queued going forward records the project it belongs to.
    /// </summary>
    public static async Task<MessageEnvelopeV1> QueueAsync(
        IDocumentSession session,
        Guid fromNodeId,
        Guid projectId,
        string fromOwnerFingerprint,
        MessageAudience to,
        string? about,
        MessageKind kind,
        string body,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long seq = await NextSeqAsync(session, fromNodeId, projectId, cancellationToken);
        MessageEnvelopeV1 envelope = new(seq, now, fromNodeId, fromOwnerFingerprint, to, about, kind, body);
        Guid streamId = MessageStreamId.ForMessage(fromNodeId, projectId, seq);

        session.Events.StartStream<MessageAggregate>(
            streamId, MessageDecider.Queue(fromNodeId, seq, projectId, fromOwnerFingerprint, to, about, kind, body, now));
        await session.SaveChangesAsync(cancellationToken);
        return envelope;
    }

    /// <summary>
    /// Every envelope this node has queued for <paramref name="projectId"/> but not yet landed
    /// (<c>SentAt is null</c> — true for a fresh queue and for one whose last flush attempt failed
    /// alike) goes into ONE transport call against <paramref name="repositoryPath"/>, which either
    /// lands all of them in a single commit or lands none: a push either reaches origin or it does
    /// not, and there is no partial-batch outcome to record. A failed push re-throws after marking
    /// every envelope in the batch failed, so the caller (the daemon's own sweep) can log it and
    /// simply try again next tick — nothing here retries on its own, and a failure flushing THIS
    /// project never touches any other project's own pending messages (idea 202383dc, M2: each
    /// project's own flush is scoped to that project's own query, session, and transport call).
    /// <para>
    /// <paramref name="projectKey"/> is stamped onto every envelope this call builds — the project's
    /// own ledger-derived wire key (<c>Hall9k.Connectors.Trust.TrustChain.GenesisRootFingerprint</c>),
    /// recomputed fresh by the caller every tick from the live ledger rather than persisted: the
    /// underlying genesis fact never changes once established, so re-deriving it costs nothing and a
    /// retried flush always stamps the identical value a first attempt would have.
    /// </para>
    /// <para>
    /// <paramref name="adoptUnassigned"/> is true only for the one project a sweep resolves as the
    /// legacy fallback (idea 202383dc, M2's migration rule: "the project the old, single-project
    /// sweep would have picked") — when true, this call also picks up every pending message still
    /// carrying <see cref="Guid.Empty"/> as its own <see cref="MessageDetails.ProjectId"/> (queued
    /// before M2 shipped) and flushes them alongside this project's own, stamping each one's real
    /// <see cref="MessageSent"/>/<see cref="MessageSendFailed"/> with <paramref name="projectId"/> —
    /// the first and only place that sentinel is ever resolved, so every later flush finds it already
    /// scoped like any other message and never re-adopts it.
    /// </para>
    /// </summary>
    public async Task<MessageFlushResult> FlushAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid fromNodeId,
        Guid projectId,
        string projectKey,
        bool adoptUnassigned,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MessageDetails> pending = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == fromNodeId && message.SentAt == null
                && (message.ProjectId == projectId || (adoptUnassigned && message.ProjectId == Guid.Empty)))
            .OrderBy(message => message.Seq)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return new MessageFlushResult(0);
        }

        List<TransportEnvelope> batch = [.. pending.Select(
            message => new TransportEnvelope(
                message.Seq, MessageEnvelopeCodec.Encode(ToEnvelope(message) with { ProjectKey = projectKey })))];

        try
        {
            await transport.FlushAsync(repositoryPath, fromNodeId, batch, committer, signingKey, cancellationToken);
        }
        catch (Exception exception) when (exception is LedgerPushRejectedException or InvalidOperationException)
        {
            // Only a message not already marked failed gets a fresh MessageSendFailed appended: an
            // outage that keeps this same batch failing tick after tick would otherwise duplicate
            // every envelope's full body onto its own stream once per sweep for as long as the
            // outage lasts — thousands of appends a day per message, replayed again by the eventual
            // successful flush's own Resend aggregation (independent pre-PR review, cycle 1,
            // adversarial lens). A message already SendFailed stays SendFailed either way; nothing
            // here needs a second event to say so again.
            foreach (MessageDetails message in pending.Where(message => !message.SendFailed))
            {
                session.Events.Append(
                    message.Id, MessageDecider.FailSend(ToEnvelope(message), projectId, exception.Message, now));
            }

            await session.SaveChangesAsync(cancellationToken);
            throw;
        }

        foreach (MessageDetails message in pending)
        {
            if (message.SendFailed)
            {
                MessageAggregate aggregate = await session.Events.AggregateStreamAsync<MessageAggregate>(
                    message.Id, token: cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Message {fromNodeId}/{message.Seq} has no stream to flush a resend onto.");
                session.Events.Append(message.Id, MessageDecider.Resend(aggregate, now));
            }
            else
            {
                session.Events.Append(message.Id, MessageDecider.Send(fromNodeId, message.Seq, projectId, now));
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        return new MessageFlushResult(pending.Count);
    }

    /// <summary>
    /// Squashes this node's own outbox to envelopes sent within <paramref name="retention"/>
    /// (idea 202383dc, M1b's retention rule) — reads only <paramref name="projectId"/>'s own
    /// <c>SentAt is not null</c> messages (idea 202383dc, M2: a message sent to a different project
    /// lives in a different repository's own ref entirely, and must never be counted here or
    /// force-pushed into THIS project's own outbox) — anything still pending a flush is not
    /// physically in the ref yet, so it is never a squash candidate at all — and rewrites the
    /// transport's own copy to hold exactly the survivors. Touches nothing in the local event store:
    /// a message's own history — sent, resent, received, handled — is a fact this node already
    /// recorded, and squashing the outbox never un-happens it, it only stops re-shipping old bytes
    /// over the wire.
    /// <para>
    /// The cutoff is measured against <see cref="MessageDetails.SentAt"/>, never
    /// <see cref="MessageDetails.QueuedAt"/>: a node that could not reach origin for longer than
    /// <paramref name="retention"/> still has every envelope land with a fresh <c>SentAt</c> the
    /// moment its next flush finally succeeds, so measuring from the much older queue time would
    /// force-remove an envelope moments after a reader's very first chance to fetch it
    /// (independent pre-PR review, cycle 1, adversarial lens).
    /// </para>
    /// <para>
    /// Calls <see cref="IMessageTransport.SquashAsync"/> only when at least one sent envelope has
    /// actually aged out: every survivor is already physically present in the transport's own
    /// copy, so a squash with nothing to drop would only force-push a fresh orphan commit under a
    /// new timestamp for no observable change — on a node's fast sweep cadence, that is thousands
    /// of pointless force-pushes a day, and it moves the outbox tip on every tick even for a node
    /// that has never sent anything, which defeats the daemon's own message sweep's unmoved-tip
    /// skip for every reader watching it (independent pre-PR review, cycle 1, both lenses).
    /// <see cref="MessageDetails"/>'s own <c>SentAt</c> never changes once a squash actually drops
    /// an envelope — a squash rewrites the transport's own copy only, never the local event store
    /// this query reads (this method's own doc, and <see cref="_lastSquashedSurvivorSeqs"/>'s) — so
    /// that first check alone stays true forever after the first real drop, and would otherwise
    /// force-push an identical orphan commit, unchanged survivors and all, on every later sweep for
    /// the rest of the node's life. Skipping again once nothing that survived the last real push has
    /// since aged out is what actually stops that (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    public async Task<MessageSquashResult> SquashAsync(
        IDocumentSession session,
        string repositoryPath,
        Guid fromNodeId,
        Guid projectId,
        TimeSpan retention,
        LedgerCommitter committer,
        LedgerSigningKey signingKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = now - retention;
        IReadOnlyList<MessageDetails> sent = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == fromNodeId && message.ProjectId == projectId && message.SentAt != null)
            .OrderBy(message => message.Seq)
            .ToListAsync(cancellationToken);

        if (!sent.Any(message => message.SentAt < cutoff))
        {
            return new MessageSquashResult(sent.Count);
        }

        List<MessageDetails> survivors = [.. sent.Where(message => message.SentAt >= cutoff)];
        HashSet<long> survivorSeqs = [.. survivors.Select(message => message.Seq)];
        (string RepositoryPath, Guid FromNodeId, Guid ProjectId) key = (repositoryPath, fromNodeId, projectId);
        if (_lastSquashedSurvivorSeqs.TryGetValue(key, out IReadOnlySet<long>? previousSurvivorSeqs)
            && previousSurvivorSeqs.IsSubsetOf(survivorSeqs))
        {
            return new MessageSquashResult(survivors.Count);
        }

        List<TransportEnvelope> batch = [.. survivors.Select(
            message => new TransportEnvelope(message.Seq, MessageEnvelopeCodec.Encode(ToEnvelope(message))))];
        await transport.SquashAsync(repositoryPath, fromNodeId, batch, committer, signingKey, cancellationToken);
        _lastSquashedSurvivorSeqs[key] = survivorSeqs;
        return new MessageSquashResult(survivors.Count);
    }

    /// <summary>
    /// Rebuilds the exact envelope <see cref="QueueAsync"/> originally queued, from
    /// <see cref="MessageDetails"/>'s own stored fields — every one of them is set by
    /// <see cref="MessageDecider.Queue"/>'s own <see cref="MessageQueued"/> before a candidate ever
    /// reaches <see cref="FlushAsync"/>'s pending query, so a null here means the store itself is
    /// broken, not a case this method should paper over.
    /// </summary>
    private static MessageEnvelopeV1 ToEnvelope(MessageDetails message) => new(
        message.Seq, message.QueuedAt, message.FromNodeId,
        message.FromOwnerFingerprint ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no FromOwnerFingerprint."),
        MessageAudience.Parse(message.To ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no To.")),
        message.About,
        MessageKind.Parse(message.Kind ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no Kind.")),
        message.Body ?? throw new InvalidOperationException(
            $"Message {message.FromNodeId}/{message.Seq} is pending flush but has no Body."));

    /// <summary>Seq is monotonic per node and per project (idea 202383dc, M2), from this node's own
    /// store — never from the ledger, never from any caller-supplied counter. Scoped by project so
    /// each project's own outbox ref, in its own repository, always starts contiguous at 1: a global
    /// counter shared across every project would leave gaps in any one project's own ref whenever a
    /// send interleaved with another project's, and the transport's own gap-stop rule
    /// (<c>GitLedgerMessageTransport.ReadSinceAsync</c>) would then stall a reader at the very first
    /// one forever.
    /// <para>
    /// Known, accepted limitation: this scoping deliberately excludes <see cref="Guid.Empty"/>-project
    /// messages (queued before M2 shipped) from the max it computes, even for the one project a
    /// sweep later adopts them into — considering them for every project would reintroduce exactly
    /// the stall above for any brand-new project's own first-ever message, which is the worse of the
    /// two failure modes. Whichever project ends up adopting legacy messages could in principle
    /// already hold ALREADY-SENT history under this same node from before M2 (seq 1..N, physically
    /// on that project's own wire ref) that this query cannot see, since only a pending message's own
    /// <see cref="MessageSendFailed"/>/<see cref="MessageSent"/> ever backfills a real project id —
    /// an already-sent legacy message never does (its own history stays <see cref="Guid.Empty"/>
    /// forever, per this task's own scope decision). A message newly queued for that same project
    /// could then be allocated a seq that collides with one already physically written to that
    /// project's own ref, and a batched flush would silently overwrite it. Accepted rather than
    /// solved here because this task's own acceptance criteria name continuity only for a message
    /// "queued before this change but not yet sent," never for one already sent, and no production
    /// deployment of this pre-M2 feature is known to have sent one; closing this fully would need
    /// either backfilling every historical message's project id (not only pending ones) or tracking
    /// each message's own repository path directly, both out of this task's own scope.
    /// </para>
    /// </summary>
    private static async Task<long> NextSeqAsync(IDocumentSession session, Guid fromNodeId, Guid projectId, CancellationToken cancellationToken)
    {
        MessageDetails? highest = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == fromNodeId && message.ProjectId == projectId)
            .OrderByDescending(message => message.Seq)
            .FirstOrDefaultAsync(cancellationToken);
        return (highest?.Seq ?? 0) + 1;
    }
}
