using Hall9k.Connectors.Ledger;
using Hall9k.Connectors.Messaging;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Marten.Events;
using Microsoft.Extensions.Options;

namespace Hall9k.Daemon.Messaging;

/// <summary>
/// The holder's own reaction to a received cooperative claim request (idea 202383dc, item 5, "a
/// member can ask a holder for a task"): polls this node's own received-but-unhandled
/// <see cref="MessageKind.ClaimRequest"/> messages on the ordinary sweep cadence
/// (<see cref="DaemonOptions.PollInterval"/>) and hands each to
/// <see cref="ClaimRequestEngine.ReceiveRequestAsync"/> — its own separate hosted service rather
/// than a step folded into <see cref="MessageSweepEngine"/>, the same reasoning
/// <see cref="Execution.TakeoverWatchLoop"/>'s own doc gives for keeping a reactive concern that is
/// not itself message transport apart from the transport sweep. No doorbell: nothing on this node
/// observes "a claim request just arrived" any sooner than the next poll.
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

        IReadOnlyList<MessageDetails> pending = await lookupSession.Query<MessageDetails>()
            .Where(message => message.Kind == MessageKind.ClaimRequest.Value
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
        await ClaimRequestEngine.ReceiveRequestAsync(
            store, session, project, request.TaskId, request, ledger, identity.Committer, identity.SigningKey, take,
            nodeId, identity.OwnerRootFingerprint, now, cancellationToken);

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
