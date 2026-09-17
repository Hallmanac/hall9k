using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Trust;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>One sweep's own outcome, for the loop's own cadence decision: whether the fast
/// interval applies for the NEXT tick, and whether this sweep itself pushed something — the
/// signal for the loop's own "immediate probe on the tick after this node's own push".</summary>
public sealed record MessageSweepResult(bool ActiveCadence, bool JustPushed);

/// <summary>
/// One tick of the message sweep (idea 202383dc, M1b; project-scoped M2): flush this node's own
/// queued envelopes, probe for moved outboxes and read the ones that moved, then squash this node's
/// own outbox by age — for EVERY eligible project this node is registered to (not archived, with a
/// repository — <see cref="ProjectEligibilityExtensions.IsEligibleForMessaging"/>), each one through
/// its own repository, never just the first one found. The message domain's own seq allocation, the
/// per-sender cursor, and <c>SentAt</c> are all scoped by local project id (M1a/M1b's own schema,
/// extended by M2), so a project's own flush, read, and squash never touch another project's pending
/// messages, cursors, or outbox ref — a failure in one project's own repository this tick never
/// blocks or corrupts any other project's own sweep.
/// </summary>
public sealed class MessageSweepEngine(
    IDocumentStore store,
    NodeContext node,
    MessageOutbox outbox,
    MessageInbox inbox,
    IMessageTransport transport,
    ILedgerChainReader chainReader,
    MessageNodeIdentityResolver identityResolver,
    IOptions<DaemonOptions> options,
    ILogger<MessageSweepEngine> logger)
{
    /// <summary>Every sender outbox's tip as of this node's last probe, so a sweep that finds an
    /// unmoved tip skips reading it entirely. In-memory and per-process by design: a restart just
    /// re-reads every sender once, which is cheap and correct, never lossy.</summary>
    private readonly Dictionary<(string RepositoryPath, Guid SenderNodeId), string> _lastKnownTips = [];

    public async Task<MessageSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MessageNodeIdentity? identity;
        List<ProjectDetails> eligibleProjects;
        bool hasAlreadySentLegacyHistory;
        await using (IDocumentSession lookupSession = store.LightweightSession())
        {
            identity = await identityResolver.ResolveAsync(lookupSession, nodeId, node.OwnerId, cancellationToken);
            if (identity is null)
            {
                // No owner root fingerprint yet (h9k project join never ran) — nothing to stamp an
                // envelope with, so there is nothing this sweep can send or receive as.
                return new MessageSweepResult(ActiveCadence: false, JustPushed: false);
            }

            IReadOnlyList<ProjectDetails> allProjects =
                await lookupSession.Query<ProjectDetails>().ToListAsync(cancellationToken);
            eligibleProjects = [.. allProjects
                .Where(candidate => candidate.IsEligibleForMessaging())
                .OrderBy(candidate => candidate.Id)];

            if (eligibleProjects.Count == 0)
            {
                return new MessageSweepResult(ActiveCadence: false, JustPushed: false);
            }

            // Whether ANY legacy envelope from before idea 202383dc's M2 shipped has already landed
            // on the wire, under whichever project the old, single-project sweep physically wrote it
            // to (see the doc below on hasAlreadySentLegacyHistory's own use).
            hasAlreadySentLegacyHistory = await lookupSession.Query<MessageDetails>()
                .Where(message => message.FromNodeId == nodeId && message.ProjectId == Guid.Empty && message.SentAt != null)
                .AnyAsync(cancellationToken);
        }

        // The one project a still-pending message queued before idea 202383dc's M2 shipped (still
        // carrying Guid.Empty as its own ProjectId, SentAt still null) is adopted into, the first
        // time this sweep actually flushes it — in principle the lowest eligible project id
        // (LegacyMessageAdoption's own rule, the identical one the old, single-project sweep always
        // picked by), so nothing already queued before this change is lost. When no legacy envelope
        // has ever actually landed on the wire yet (hasAlreadySentLegacyHistory false — nothing
        // physically holds pre-M2 history anywhere), whichever eligible project's own flush is the
        // first to actually include one of those still-pending legacy envelopes in its batch claims
        // the adoption instead of that fixed choice: if the lowest-id project's own trust chain read
        // or genesis check keeps failing, pinning adoption to it forever would leave every legacy
        // message pending forever too, even though a healthier eligible project sits right behind it
        // in the loop (independent pre-PR review, cycle 1, both lenses, medium — "the old sweep
        // flushed without needing either"), and redirecting costs nothing since there is no existing
        // ref content anywhere else it could ever collide with or gap against.
        //
        // But once even one legacy envelope HAS already landed — physically, permanently, in
        // whatever project's ref the old sweep used — that project is the only one this backlog may
        // ever adopt into: the still-pending envelopes share that same node-wide, pre-M2 seq space
        // (idea 202383dc, M1a's own global-per-node counter, before M2 ever scoped it per project),
        // so flushing them through a DIFFERENT project would leave that different project's own ref
        // permanently missing every seq below the batch's lowest one — an unfillable gap
        // GitLedgerMessageTransport.ReadSinceAsync's own gap-stop rule stalls every reader on forever
        // — while the project that actually holds the earlier history keeps no record it was ever
        // reserved, so its own next NextSeqAsync-allocated send silently reuses and overwrites one of
        // those already-landed paths instead (independent pre-PR review, cycle 6, conformance and
        // adversarial lenses, medium and high). No fallback can make that split safe, so this case
        // never redirects: adoption stays pinned to the lowest-id eligible project, the one the old
        // sweep actually used, even on a tick where that project's own trust chain read or genesis
        // check fails — the backlog simply stays pending and is retried next sweep, the same as any
        // other flush failure, rather than risking either an overwrite or a permanent gap.
        Guid lowestEligibleProjectId = eligibleProjects[0].Id;
        bool legacyAlreadyClaimedThisTick = false;

        bool anyJustPushed = false;
        foreach (ProjectDetails project in eligibleProjects)
        {
            TrustChain trustChain;
            try
            {
                trustChain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Computing the trust chain failed for project {ProjectId}; this project's sweep "
                    + "is skipped this tick and retried next sweep", project.Id);
                continue;
            }

            await PersistUnverifiedWritesAsync(project, trustChain, now, cancellationToken);

            // The project's own ledger-derived wire key (idea 202383dc, M2) — never a local project
            // id, which differs per install for the identical shared project. Null only when this
            // project's own ledger has never had a member written into it (h9k project join never
            // ran there): nothing meaningful to flush or read as yet, so this project's sweep is
            // skipped rather than stamping envelopes with a key that would never match what any
            // other node on the same repository eventually computes.
            if (trustChain.GenesisRootFingerprint is not { } projectKey)
            {
                logger.LogWarning(
                    "Project {ProjectId}'s own ledger has no genesis yet (h9k project join never ran there); "
                    + "this project's sweep is skipped this tick and retried next sweep", project.Id);
                continue;
            }

            bool adoptUnassigned = !legacyAlreadyClaimedThisTick
                && (!hasAlreadySentLegacyHistory || project.Id == lowestEligibleProjectId);
            legacyAlreadyClaimedThisTick = true;
            bool justPushed = await FlushAsync(project, nodeId, identity, projectKey, adoptUnassigned, now, cancellationToken);
            anyJustPushed |= justPushed;

            await ProbeAndReadAsync(project, nodeId, identity, trustChain, now, cancellationToken);
            await SquashAsync(project, nodeId, identity, projectKey, now, cancellationToken);
        }

        await using IDocumentSession finalSession = store.LightweightSession();
        IReadOnlyList<Guid> eligibleProjectIds = [.. eligibleProjects.Select(project => project.Id)];
        bool hasUnflushedOrUnread = await HasUnflushedOrUnreadAsync(finalSession, nodeId, eligibleProjectIds, cancellationToken);
        bool hasActiveRun = await HasActiveRunAsync(finalSession, nodeId, cancellationToken);
        bool activeCadence = ComputeActiveCadence(hasUnflushedOrUnread, hasActiveRun);
        return new MessageSweepResult(activeCadence, anyJustPushed);
    }

    private async Task<bool> FlushAsync(
        ProjectDetails project, Guid nodeId, MessageNodeIdentity identity, string projectKey, bool adoptUnassigned,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            MessageFlushResult flush = await outbox.FlushAsync(
                session, project.RepositoryPath, nodeId, project.Id, projectKey, adoptUnassigned, identity.Committer,
                identity.SigningKey, now, cancellationToken);

            // Only reached once this exact flush call is known to have actually succeeded AND
            // actually adopted something — a chain read that succeeded but a push that then failed
            // must never pin this project as the permanent legacy adopter
            // (LegacyMessageAdoption.AssignAsync's own doc), and neither must a flush that succeeded
            // but never touched a still-Guid.Empty envelope at all: an eligible project with nothing
            // pending but its own, already-project-scoped mail still reaches this line with
            // adoptUnassigned true on whichever tick it happens to flush first, and pinning it as
            // the legacy adopter would permanently misdirect NextSeqAsync's and ReadFromAsync's own
            // legacy fold onto a project that never actually held any legacy content — nothing ties
            // that choice to the repository that physically holds this node's already-sent legacy
            // envelopes, which is what MessageFlushResult.AdoptedLegacyBacklog guards against
            // (independent pre-PR review, cycle 4, adversarial lens, high). An already-sent legacy
            // envelope (SentAt already set before this flush ever ran) is never in this batch either
            // way — pending only ever holds SentAt-null rows — so this correctly leaves the static
            // lowest-id guess standing for that history instead of handing it to whichever healthy
            // project happened to flush its own unrelated mail first, which is what the old
            // unconditional-on-adoptUnassigned check let happen.
            if (flush.AdoptedLegacyBacklog)
            {
                await LegacyMessageAdoption.AssignAsync(session, project.Id, now, cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }

            return flush.EnvelopesFlushed > 0;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Message flush failed for project {ProjectId}; will retry next sweep", project.Id);
            return false;
        }
    }

    private async Task ProbeAndReadAsync(
        ProjectDetails project, Guid nodeId, MessageNodeIdentity identity, TrustChain trustChain, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MessageOutboxTip> tips;
        try
        {
            tips = await transport.ProbeAsync(project.RepositoryPath, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Message probe failed for project {ProjectId}; will retry next sweep", project.Id);
            return;
        }

        IReadOnlyList<MessageOutboxTip> toRead = SendersToRead(tips, nodeId, project.RepositoryPath, _lastKnownTips);
        if (toRead.Count == 0)
        {
            return;
        }

        foreach (MessageOutboxTip tip in toRead)
        {
            try
            {
                // Its own session per sender: MessageInbox.ReadFromAsync is only self-contained
                // (calls its own SaveChangesAsync) on the path that reaches the end of its own
                // method — a mid-loop failure (a dropped connection between two envelopes in the
                // same read) can leave earlier-in-that-call appends still pending, unsaved, on
                // whatever session it was handed. Sharing one session across every sender in this
                // loop would let a LATER sender's own successful SaveChangesAsync silently flush
                // that stale partial state alongside its own — a sender this sweep just logged as
                // failed would still land some of its envelopes. A session scoped to one sender's
                // own read call means a failure here throws its session away, pending appends and
                // all, and costs this sender nothing but a retry next sweep.
                await using IDocumentSession session = store.LightweightSession();
                MessageInboxSweepResult read = await inbox.ReadFromAsync(
                    session, project.RepositoryPath, tip.SenderNodeId, project.Id, nodeId, identity.OwnerRootFingerprint,
                    now, trustChain: trustChain, cancellationToken: cancellationToken);

                // Never recorded on a read that came back not vouched or stalled: neither one
                // actually looked at the sender's content, so caching the tip here would make this
                // sweep skip the sender on every later tick until it pushes again or this process
                // restarts, even though a plain re-read next sweep would succeed — the sender
                // joining the project after already sending, or a transient git failure, are both
                // read.StalledAtSeq's own doc promises a retry for (independent pre-PR review,
                // cycle 1, both lenses).
                if (read is { SenderNotVouched: false, StalledAtSeq: null })
                {
                    _lastKnownTips[(project.RepositoryPath, tip.SenderNodeId)] = tip.Tip;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Reading sender {SenderNodeId}'s outbox failed for project {ProjectId}; will retry next sweep",
                    tip.SenderNodeId, project.Id);
            }
        }
    }

    /// <summary>How stale <see cref="UnverifiedLedgerWriteAggregate.LastSeenAt"/> is allowed to get
    /// before an otherwise-unchanged sighting still refreshes it — long enough that a project with
    /// one standing bad write appends on the order of tens of events a year, not thousands a day,
    /// while <c>h9k status</c> still shows a recent-enough "last seen" for an operator to trust it
    /// reflects the current sweep, not a stale one from hours ago.</summary>
    private static readonly TimeSpan UnverifiedWriteRefreshAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Persists every writer this sweep's own trust chain read found it could not verify
    /// (<see cref="TrustChain.UnverifiedWrites"/>) as a standing, per-project record
    /// (<see cref="UnverifiedLedgerWriteDetails"/>) — the acceptance criterion "the writer is
    /// named in h9k status" (idea 202383dc, T1 criterion 3) is settled by that pane reading this
    /// record, never by a live ledger walk from h9k status itself.
    /// <para>
    /// Folded by <see cref="UnverifiedLedgerWriteStreamId.For"/> into one standing fact per
    /// (kind, identifier, root) rather than one row per sweep tick that observed it, two ways:
    /// first, <paramref name="trustChain"/> itself can name the identical writer more than once in
    /// a single tick (a revoked node pushing a correction over its own earlier bad commit, say),
    /// collapsed here to the last-observed one <em>before</em> the store is ever touched — an
    /// earlier version called <c>AggregateStreamAsync</c> then <c>StartStream</c> per element with
    /// no such de-duplication, so two entries sharing a stream id in the same tick called
    /// <c>StartStream</c> twice for the same id in one session and <c>SaveChangesAsync</c> failed
    /// the whole batch, silently dropping every writer that tick was supposed to persist, including
    /// the distinct ones (independent pre-PR review, cycle 1, adversarial lens, medium). Second,
    /// nothing is appended at all when the sighting has not meaningfully changed — the offending
    /// ledger commit never goes away on its own, so a project with one bad write would otherwise
    /// grow this stream by one identical event every tick, forever, with <c>AggregateStreamAsync</c>
    /// replaying the whole thing again on every single one of them (independent pre-PR review,
    /// cycle 1, both lenses, medium). A changed <c>Reason</c> (a different offending commit now)
    /// or a <c>LastSeenAt</c> older than <see cref="UnverifiedWriteRefreshAge"/> still refreshes it.
    /// </para>
    /// <para>
    /// Also the only place a standing record ever clears: a writer this project has a live,
    /// unresolved record for but that this sweep's own trust chain read no longer names among
    /// <see cref="TrustChain.UnverifiedWrites"/> gets a <see cref="UnverifiedLedgerWriteResolved"/>
    /// appended to its own stream, the identical "cleared on the read that stops naming it" rule
    /// the envelope half of this same criterion already applies
    /// (<see cref="Hall9k.Domain.Features.Message.InboxSenderVouched"/>) — without this, a resolved
    /// writer (the offending node re-vouched, or a later commit correcting the bad write) would
    /// stay in <c>h9k status</c> forever with no way to clear (independent pre-PR review, cycle 3,
    /// conformance lens, medium).
    /// </para>
    /// </summary>
    private async Task PersistUnverifiedWritesAsync(
        ProjectDetails project, TrustChain trustChain, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Dictionary<Guid, UnverifiedLedgerWrite> distinctWrites = [];
        foreach (UnverifiedLedgerWrite write in trustChain.UnverifiedWrites)
        {
            Guid streamId = UnverifiedLedgerWriteStreamId.For(project.Id, write.Kind, write.Identifier, write.RootFingerprint);
            distinctWrites[streamId] = write;
        }

        try
        {
            await using IDocumentSession session = store.LightweightSession();

            IReadOnlyList<UnverifiedLedgerWriteDetails> standing = await session.Query<UnverifiedLedgerWriteDetails>()
                .Where(details => details.ProjectId == project.Id && !details.Resolved)
                .ToListAsync(cancellationToken);
            foreach (UnverifiedLedgerWriteDetails record in standing)
            {
                if (!distinctWrites.ContainsKey(record.Id))
                {
                    session.Events.Append(record.Id, UnverifiedLedgerWriteDecider.Resolve(now));
                }
            }

            foreach ((Guid streamId, UnverifiedLedgerWrite write) in distinctWrites)
            {
                UnverifiedLedgerWriteAggregate? existing = await session.Events
                    .AggregateStreamAsync<UnverifiedLedgerWriteAggregate>(streamId, token: cancellationToken);
                if (existing is not null
                    && !existing.Resolved
                    && existing.Reason == write.Reason
                    && now - existing.LastSeenAt < UnverifiedWriteRefreshAge)
                {
                    continue;
                }

                UnverifiedLedgerWriteObserved observed = UnverifiedLedgerWriteDecider.Observe(
                    project.Id, write.Kind, write.Identifier, write.RootFingerprint, write.Reason, now);
                if (existing is null)
                {
                    session.Events.StartStream<UnverifiedLedgerWriteAggregate>(streamId, observed);
                }
                else
                {
                    session.Events.Append(streamId, observed);
                }
            }

            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception, "Persisting this sweep's unverifiable ledger writers failed for project {ProjectId}; "
                + "will retry next sweep", project.Id);
        }
    }

    /// <summary>
    /// Which of the probe's own tips are actually worth a <see cref="MessageInbox.ReadFromAsync"/>
    /// call: never this node's own outbox (a "project"-addressed broadcast is not something the
    /// sender itself needs to mark unread and handle), and never a sender whose tip is identical to
    /// what the last sweep already saw — the probe's whole point, one <c>ls-remote</c> deciding
    /// which senders are worth a real fetch at all. Pure and side-effect-free so it is unit-testable
    /// without a document store, a transport, or a sweep — <see cref="_lastKnownTips"/> is passed in
    /// rather than read directly, and the caller is the one that actually records a new tip once its
    /// own read succeeds, never this method.
    /// </summary>
    internal static IReadOnlyList<MessageOutboxTip> SendersToRead(
        IReadOnlyList<MessageOutboxTip> tips, Guid myNodeId, string repositoryPath,
        IReadOnlyDictionary<(string RepositoryPath, Guid SenderNodeId), string> lastKnownTips) =>
        [.. tips.Where(tip =>
            tip.SenderNodeId != myNodeId
            && (!lastKnownTips.TryGetValue((repositoryPath, tip.SenderNodeId), out string? knownTip)
                || knownTip != tip.Tip))];

    private async Task SquashAsync(
        ProjectDetails project, Guid nodeId, MessageNodeIdentity identity, string projectKey, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            await outbox.SquashAsync(
                session, project.RepositoryPath, nodeId, project.Id, projectKey, options.Value.MessageRetention,
                identity.Committer, identity.SigningKey, now, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Message squash failed for project {ProjectId}; will retry next sweep", project.Id);
        }
    }

    /// <summary>
    /// Whether the loop's NEXT tick should use the fast cadence: this node has something
    /// unflushed or unread, or this node holds or is running work right now.
    /// <paramref name="hasActiveRun"/> is <see cref="HasActiveRunAsync"/>'s own result — this node's
    /// own <see cref="RunState.IsLive"/> runs — rather than <see cref="NodeDetails.LaunchHoldActive"/>:
    /// that flag names a node stuck failing to launch sessions at all (a failure state), which is
    /// neither "holds work" nor "running" the ruled criterion actually names, and mapping the two
    /// together left a node busy with live runs but nothing unflushed or unread reading idle, while a
    /// node stuck launch-held read active (independent pre-PR review, cycle 1, both lenses). A true
    /// holder-lock reading of "holds work" is not built yet (noted elsewhere); this is the honest
    /// proxy available today, the same one <c>DispatchEngine.MeasureLoadAsync</c> already reads for
    /// this node's own live slots. Pure and side-effect-free, the same reason <see cref="SendersToRead"/>
    /// is its own static method: unit-testable without a document store.
    /// </summary>
    internal static bool ComputeActiveCadence(bool hasUnflushedOrUnread, bool hasActiveRun) =>
        hasUnflushedOrUnread || hasActiveRun;

    /// <summary>Driven by whether ANY of this node's eligible projects has something unflushed or
    /// unread (idea 202383dc, M2), not just the one this tick happened to look at last — but an
    /// unflushed message pending for a project this node is NOT currently eligible to sweep through
    /// (archived, or dropped its repository) is excluded from the unflushed half: the sweep can never
    /// actually flush it, so counting it here would pin this node on the faster active cadence
    /// forever, even though nothing this node can do moves that message (independent pre-PR review,
    /// cycle 4, conformance lens, low — <c>h9k message send --project</c>'s own explicit-project path
    /// lets a message queue for an ineligible project). A still-Guid.Empty legacy message is kept in
    /// regardless of <paramref name="eligibleProjectIds"/>: it always has somewhere eligible left to
    /// land, or this method is never reached at all (<see cref="SweepOnceAsync"/> returns early the
    /// moment <c>eligibleProjects</c> is empty). The unread half is left unscoped, since
    /// <see cref="MessageInbox.ReadFromAsync"/> only ever stores a received message under an eligible
    /// project's own id in the first place.</summary>
    private static Task<bool> HasUnflushedOrUnreadAsync(
        IDocumentSession session, Guid nodeId, IReadOnlyList<Guid> eligibleProjectIds, CancellationToken cancellationToken) =>
        session.Query<MessageDetails>()
            .Where(message =>
                (message.FromNodeId == nodeId && message.SentAt == null
                    && (message.ProjectId == Guid.Empty || eligibleProjectIds.Contains(message.ProjectId)))
                || (message.ReceivedAt != null && message.HandledAt == null))
            .AnyAsync(cancellationToken);

    /// <summary>Whether this node currently has any run in a live state (mirrors
    /// <c>DispatchEngine.MeasureLoadAsync</c>'s own per-node live-slot read) — the proxy for "this
    /// node holds or is running work" <see cref="ComputeActiveCadence"/>'s own doc explains.</summary>
    private static Task<bool> HasActiveRunAsync(
        IQuerySession session, Guid nodeId, CancellationToken cancellationToken) =>
        session.Query<RunListItem>()
            .Where(run => run.NodeId == nodeId)
            .Where(run => run.MatchesSql(
                "d.data ->> 'state' in (?, ?, ?, ?)",
                RunState.Dispatched.Value, RunState.Running.Value, RunState.Verifying.Value, RunState.UnderReview.Value))
            .AnyAsync(cancellationToken);
}
