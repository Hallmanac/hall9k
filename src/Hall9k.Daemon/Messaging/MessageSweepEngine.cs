using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.Trust;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Node;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>One sweep's own outcome, for the loop's own cadence decision: whether the fast
/// interval applies for the NEXT tick, and whether this sweep itself pushed something — the
/// signal for the loop's own "immediate probe on the tick after this node's own push".</summary>
public sealed record MessageSweepResult(bool ActiveCadence, bool JustPushed);

/// <summary>
/// One tick of the message sweep (idea 202383dc, M1b): flush this node's own queued envelopes,
/// probe for moved outboxes and read the ones that moved, then squash this node's own outbox by
/// age. Scoped to a single project's own repository — the first non-archived registered project
/// with a repository — not every registered project: the message domain (seq allocation, the
/// per-sender cursor, <c>SentAt</c>) is keyed by sender node alone, with no project scoping at
/// all, so sweeping more than one project's repository from the same node would let the first
/// project's flush mark every queued envelope sent before a second project ever saw them. Fixing
/// that needs project-scoped stream ids in the message feature itself (M1a's own schema), which is
/// out of this task's scope; this sweep instead serves the one-project shape idea 202383dc was
/// actually walked against (Brian's own two nodes, one shared project).
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

    /// <summary>Whether this process has already logged the ambiguous-project warning <see
    /// cref="SweepOnceAsync"/> gives once this node has more than one eligible project — in-memory
    /// and per-process by design, the same as <see cref="_lastKnownTips"/>: a restart re-warns
    /// once, which is cheap and never lossy, rather than spamming it every tick forever.</summary>
    private bool _loggedAmbiguousProjectChoice;

    public async Task<MessageSweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MessageNodeIdentity? identity;
        ProjectDetails? project;
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
            List<ProjectDetails> eligibleProjects = [.. allProjects
                .Where(candidate => !candidate.IsArchived && candidate.RepositoryPath.IsNotBlank())
                .OrderBy(candidate => candidate.Id)];
            project = eligibleProjects.FirstOrDefault();

            // The one-project scope itself (this class's own doc comment) is out of this sweep's
            // control; picking silently among two or more is what independent pre-PR review, cycle
            // 1, conformance lens flagged: whichever one this sweep does NOT pick may have no node
            // file for this node at all, so every reader on that project ignores this node's own
            // outbox with no error anywhere. Logged once per process, never per tick, so an operator
            // troubleshooting undelivered messages has a lead without the log filling up with a
            // warning this sweep repeats forever for a node whose registration never changes.
            if (eligibleProjects.Count > 1 && !_loggedAmbiguousProjectChoice)
            {
                _loggedAmbiguousProjectChoice = true;
                logger.LogWarning(
                    "This node has {Count} eligible projects for the message sweep, which only ever sweeps one "
                    + "(idea 202383dc, M1b's documented one-project scope): {Chosen} was picked, by lowest project "
                    + "id. If this node's own node.yaml was written into a different project ({OtherProjects}), no "
                    + "other node reading that project's ledger can vouch for it, and this node's messages will "
                    + "never arrive. Run h9k project join against {ChosenAgain} to make sure this node's node file "
                    + "actually lives there.",
                    eligibleProjects.Count, eligibleProjects[0].Name,
                    string.Join(", ", eligibleProjects.Skip(1).Select(candidate => candidate.Name)), eligibleProjects[0].Name);
            }
        }

        if (project is null)
        {
            return new MessageSweepResult(ActiveCadence: false, JustPushed: false);
        }

        bool justPushed = await FlushAsync(project, nodeId, identity, now, cancellationToken);
        await ProbeAndReadAsync(project, nodeId, identity, now, cancellationToken);
        await SquashAsync(project, nodeId, identity, now, cancellationToken);

        await using IDocumentSession finalSession = store.LightweightSession();
        bool hasUnflushedOrUnread = await HasUnflushedOrUnreadAsync(finalSession, nodeId, cancellationToken);
        bool hasActiveRun = await HasActiveRunAsync(finalSession, nodeId, cancellationToken);
        bool activeCadence = ComputeActiveCadence(hasUnflushedOrUnread, hasActiveRun);
        return new MessageSweepResult(activeCadence, justPushed);
    }

    private async Task<bool> FlushAsync(
        ProjectDetails project, Guid nodeId, MessageNodeIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            MessageFlushResult flush = await outbox.FlushAsync(
                session, project.RepositoryPath, nodeId, identity.Committer, identity.SigningKey, now, cancellationToken);
            return flush.EnvelopesFlushed > 0;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Message flush failed for project {ProjectId}; will retry next sweep", project.Id);
            return false;
        }
    }

    private async Task ProbeAndReadAsync(
        ProjectDetails project, Guid nodeId, MessageNodeIdentity identity, DateTimeOffset now,
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

        // Computed once for the whole sweep, never per sender: every sender's own read otherwise
        // repeated the full ledger chain walk (an ls-remote, a fetch per owner ref plus the members
        // ref, and a signature check per candidate key), even though nothing about the chain changes
        // between reads in the same tick (independent pre-PR review, cycle 1, conformance lens, low).
        TrustChain? trustChain = null;
        try
        {
            trustChain = await chainReader.ComputeAsync(project.RepositoryPath, cancellationToken);
        }
        catch (Exception exception)
        {
            // Not fatal to this sweep: each per-sender read below falls back to computing its own
            // chain fresh when none is supplied, the identical behavior this sweep always had before
            // this cache existed.
            logger.LogWarning(
                exception, "Precomputing the trust chain failed for project {ProjectId}; each sender read "
                + "this sweep will compute its own instead", project.Id);
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
                    session, project.RepositoryPath, tip.SenderNodeId, nodeId, identity.OwnerRootFingerprint, now,
                    trustChain: trustChain, cancellationToken: cancellationToken);

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
        ProjectDetails project, Guid nodeId, MessageNodeIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await using IDocumentSession session = store.LightweightSession();
            await outbox.SquashAsync(
                session, project.RepositoryPath, nodeId, options.Value.MessageRetention, identity.Committer,
                identity.SigningKey, now, cancellationToken);
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

    private static Task<bool> HasUnflushedOrUnreadAsync(
        IDocumentSession session, Guid nodeId, CancellationToken cancellationToken) =>
        session.Query<MessageDetails>()
            .Where(message =>
                (message.FromNodeId == nodeId && message.SentAt == null)
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
