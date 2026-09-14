using Hall9k.Connectors.Messaging;
using Hall9k.Daemon.Execution;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;
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
    MessageNodeIdentityResolver identityResolver,
    LaunchHoldEngine launchHold,
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
            project = allProjects
                .Where(candidate => !candidate.IsArchived && candidate.RepositoryPath.IsNotBlank())
                .OrderBy(candidate => candidate.Id)
                .FirstOrDefault();
        }

        if (project is null)
        {
            return new MessageSweepResult(ActiveCadence: false, JustPushed: false);
        }

        bool justPushed = await FlushAsync(project, nodeId, identity, now, cancellationToken);
        await ProbeAndReadAsync(project, nodeId, identity, now, cancellationToken);
        await SquashAsync(project, nodeId, identity, now, cancellationToken);

        await using IDocumentSession finalSession = store.LightweightSession();
        bool activeCadence = await HasUnflushedOrUnreadAsync(finalSession, nodeId, cancellationToken)
            || await launchHold.CurrentHoldAsync(nodeId, cancellationToken) is not null;
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

        foreach (MessageOutboxTip tip in SendersToRead(tips, nodeId, project.RepositoryPath, _lastKnownTips))
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
                await inbox.ReadFromAsync(
                    session, project.RepositoryPath, tip.SenderNodeId, nodeId, identity.OwnerRootFingerprint, now,
                    cancellationToken: cancellationToken);
                _lastKnownTips[(project.RepositoryPath, tip.SenderNodeId)] = tip.Tip;
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

    private static Task<bool> HasUnflushedOrUnreadAsync(
        IDocumentSession session, Guid nodeId, CancellationToken cancellationToken) =>
        session.Query<MessageDetails>()
            .Where(message =>
                (message.FromNodeId == nodeId && message.SentAt == null)
                || (message.ReceivedAt != null && message.HandledAt == null))
            .AnyAsync(cancellationToken);
}
