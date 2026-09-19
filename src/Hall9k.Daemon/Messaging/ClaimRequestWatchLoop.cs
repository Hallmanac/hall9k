using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// The cooperative take's own reaction loop (idea 202383dc, item 5, "a member can ask a holder for
/// a task"): polls this node's own received-but-unhandled messages of all three envelope kinds
/// <see cref="MessageKind.MechanicalKindValues"/> names on the ordinary sweep cadence
/// (<see cref="DaemonOptions.PollInterval"/>) — its own separate hosted service rather than a step
/// folded into <see cref="MessageSweepEngine"/>, the same reasoning
/// <see cref="Execution.TakeoverWatchLoop"/>'s own doc gives for keeping a reactive concern that is
/// not itself message transport apart from the transport sweep. No doorbell: nothing on this node
/// observes "a claim request just arrived" any sooner than the next poll.
/// <para>
/// A <see cref="MessageKind.ClaimRequest"/> is handed to
/// <see cref="ClaimRequestEngine.ReceiveRequestAsync"/> — the holder's own node deciding whether to
/// grant, refuse, or park. A <see cref="MessageKind.ClaimGranted"/> or
/// <see cref="MessageKind.ClaimRefused"/> reply carries nothing this node does not already have:
/// the requester's own answer arrives through the replicated <c>TaskHolderReleased</c> or
/// <c>TaskTakeRefused</c> event on the task's own stream, which is what <c>h9k task show</c> and
/// <c>h9k status</c> actually read (<see cref="ClaimEnvelopeCodec"/>'s own doc) — so this loop's
/// only job for a reply is to decode it far enough to log a malformed one, then mark it handled
/// rather than leave it received-and-invisible forever (independent pre-PR review, cycle 1,
/// adversarial lens: before this, nothing on the receiving node ever read or handled either reply
/// kind at all).
/// </para>
/// </summary>
public sealed class ClaimRequestWatchLoop(
    IDocumentStore store,
    NodeContext node,
    MessageNodeIdentityResolver identityResolver,
    ILedger ledger,
    IOptions<DaemonOptions> options,
    ILogger<ClaimRequestWatchLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await node.WaitForInitializationAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Claim-request sweep failed; will check again next tick");
            }

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        Guid nodeId = node.NodeId;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using IDocumentSession lookupSession = store.LightweightSession();
        MessageNodeIdentity? identity = await identityResolver.ResolveAsync(lookupSession, nodeId, node.OwnerId, cancellationToken);
        if (identity is null)
        {
            // No owner root fingerprint yet (h9k project join never ran) — the same "nothing to do
            // yet" MessageSweepEngine's own identical check reads.
            return;
        }

        IReadOnlyList<string> mechanicalKinds = MessageKind.MechanicalKindValues;
        IReadOnlyList<MessageDetails> pending = await lookupSession.Query<MessageDetails>()
            .Where(message => mechanicalKinds.Contains(message.Kind)
                && message.ReceivedAt != null && message.HandledAt == null)
            .ToListAsync(cancellationToken);

        foreach (MessageDetails message in pending)
        {
            try
            {
                await ReactAsync(message, nodeId, identity, now, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception, "Reacting to claim request {MessageId} failed; will retry next sweep", message.Id);
            }
        }
    }

    private async Task ReactAsync(
        MessageDetails message, Guid nodeId, MessageNodeIdentity identity, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (message.Kind == MessageKind.ClaimGranted.Value || message.Kind == MessageKind.ClaimRefused.Value)
        {
            await ReactToReplyAsync(message, now, cancellationToken);
            return;
        }

        ClaimEnvelopeCodec.ClaimRequestRecord? request = message.Body is null
            ? null
            : ClaimEnvelopeCodec.TryDecodeRequest(message.Body);
        if (request is null)
        {
            logger.LogWarning("Claim request {MessageId} could not be decoded — marked handled without acting", message.Id);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        await using IDocumentSession session = store.LightweightSession();
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(message.ProjectId, cancellationToken);
        if (project is null)
        {
            logger.LogWarning(
                "Claim request {MessageId} names project {ProjectId}, which this node does not know — "
                + "marked handled without acting", message.Id, message.ProjectId);
            await MarkHandledAsync(message, now, cancellationToken);
            return;
        }

        TrackerAssignmentTake take = new(new ProjectScopedGitHubRunner(store).Runner);
        try
        {
            await ClaimRequestEngine.ReceiveRequestAsync(
                store, session, project, request.TaskId, request, ledger, identity.Committer, identity.SigningKey,
                take, nodeId, identity.OwnerRootFingerprint, now, cancellationToken);
        }
        catch (DomainConflictException exception)
        {
            // A business-rule refusal marks the message handled rather than leaving it to retry
            // forever (independent pre-PR review, cycle 1, both lenses): the stale/misdirected
            // guard at the top of ReceiveRequestAsync and the state guard TaskDecider.GrantTake now
            // runs before any external write (ClaimRequestEngine.GrantAsync) are both refusals no
            // later sweep will ever decide differently, and re-processing them on every poll would
            // also re-append TaskTakeRequested each time. Even the one case in this family that IS
            // an external-system failure — the ledger holder could not be released for the grant —
            // is safe to mark handled the same way: TaskTakeRequested already landed on the task's
            // own stream by the time GrantAsync runs, so the request stays visible on h9k status
            // and h9k task show either way, and the holder's own human still has every ordinary
            // door onto it (h9k task grant/refuse, or the requester's own --force once it times
            // out) — marking this message handled only stops THIS automatic retry loop, not those.
            // An actual transient failure below ReceiveRequestAsync (a database hiccup) is never a
            // DomainConflictException, so it still falls through to SweepOnceAsync's own catch and
            // retries next tick, unmarked.
            logger.LogWarning(
                exception, "Claim request {MessageId} refused; marked handled — retrying would not change "
                + "the outcome", message.Id);
        }

        await MarkHandledAsync(message, now, cancellationToken);
    }

    /// <summary>
    /// A <see cref="MessageKind.ClaimGranted"/> or <see cref="MessageKind.ClaimRefused"/> reply —
    /// see this class's own doc for why nothing here needs to act on one beyond decoding it far
    /// enough to log a malformed body, then marking it handled so it does not sit
    /// received-and-unhandled forever.
    /// </summary>
    private async Task ReactToReplyAsync(MessageDetails message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool decoded = message.Body is not null && (message.Kind == MessageKind.ClaimGranted.Value
            ? ClaimEnvelopeCodec.TryDecodeGranted(message.Body) is not null
            : ClaimEnvelopeCodec.TryDecodeRefused(message.Body) is not null);
        if (!decoded)
        {
            logger.LogWarning("Claim reply {MessageId} ({Kind}) could not be decoded", message.Id, message.Kind);
        }

        await MarkHandledAsync(message, now, cancellationToken);
    }

    private async Task MarkHandledAsync(MessageDetails message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();
        StreamState? fence = await session.Events.FetchStreamStateAsync(message.Id, cancellationToken);
        if (fence is null)
        {
            return;
        }

        MessageAggregate? aggregate = await session.Events.AggregateStreamAsync<MessageAggregate>(
            message.Id, version: fence.Version, token: cancellationToken);
        if (aggregate is null || aggregate.HandledAt is not null)
        {
            return;
        }

        session.Events.Append(message.Id, expectedVersion: fence.Version + 1, MessageDecider.Handle(aggregate, now));
        await session.SaveChangesAsync(cancellationToken);
    }
}
