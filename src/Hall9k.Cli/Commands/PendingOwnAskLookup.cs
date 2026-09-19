using Hall9k.Domain.Features.Message;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// This node's own outstanding cooperative asks (idea 202383dc, item 5) that the task's own local
/// replica does not yet know about — the case the take-timeout wording exists for hardest: a
/// holder that never received or processed the request at all, so <c>TaskTakeRequested</c> never
/// lands on the holder's own stream for replication to carry back here. <c>h9k task take</c>'s own
/// <c>RunCooperativeAsync</c> queues the request but appends nothing to the task's own stream
/// locally (only the holder's node does that, on receipt) — the one thing this node's own database
/// already has about its own ask is the <see cref="MessageDetails"/> row
/// <c>MessageOutbox.QueueAsync</c> writes the moment it queues, independent of any flush, reply, or
/// replication (independent pre-PR review, cycle 5, adversarial lens).
/// </summary>
internal static class PendingOwnAskLookup
{
    public sealed record PendingOwnAsk(Guid TaskId, DateTimeOffset RequestedAt, string Reason);

    /// <summary>
    /// Every task this node has asked a holder for and never saw an answer to — reads only this
    /// node's own outbox and inbox, so it costs nothing when this node has queued no cooperative
    /// ask at all.
    /// </summary>
    public static async Task<IReadOnlyList<PendingOwnAsk>> FindUnansweredAsync(
        IQuerySession session, Guid myNodeId, CancellationToken cancellationToken)
    {
        string requestKind = MessageKind.ClaimRequest.Value;
        IReadOnlyList<MessageDetails> ownRequests = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == myNodeId && message.Kind == requestKind && message.About != null)
            .ToListAsync(cancellationToken);
        if (ownRequests.Count == 0)
        {
            return [];
        }

        string grantedKind = MessageKind.ClaimGranted.Value;
        string refusedKind = MessageKind.ClaimRefused.Value;
        IReadOnlyList<MessageDetails> replies = await session.Query<MessageDetails>()
            .Where(message => message.ReceivedAt != null && message.About != null
                && (message.Kind == grantedKind || message.Kind == refusedKind))
            .ToListAsync(cancellationToken);
        Dictionary<string, DateTimeOffset> latestReplyAt = replies
            .GroupBy(message => message.About!)
            .ToDictionary(group => group.Key, group => group.Max(message => message.ReceivedAt!.Value));

        List<PendingOwnAsk> results = [];
        foreach (IGrouping<string, MessageDetails> group in ownRequests.GroupBy(message => message.About!))
        {
            if (!Guid.TryParse(group.Key, out Guid taskId))
            {
                continue;
            }

            MessageDetails latest = group.OrderByDescending(message => message.QueuedAt).First();
            if (latestReplyAt.TryGetValue(group.Key, out DateTimeOffset repliedAt) && repliedAt >= latest.QueuedAt)
            {
                // Answered — either granted (the ordinary TaskHolderReleased replication will
                // catch up on its own) or refused, and either way this node already knows via the
                // reply envelope, so the local-only fallback below would only ever repeat stale news.
                continue;
            }

            string reason = latest.Body is not null
                ? ClaimEnvelopeCodec.TryDecodeRequest(latest.Body)?.Reason ?? string.Empty
                : string.Empty;
            results.Add(new PendingOwnAsk(taskId, latest.QueuedAt, reason));
        }

        return results;
    }

    /// <summary>The single-task convenience wrapper <c>h9k task show</c> needs — see <see cref="FindUnansweredAsync"/>'s own doc.</summary>
    public static async Task<PendingOwnAsk?> FindUnansweredAsync(
        IQuerySession session, Guid taskId, Guid myNodeId, CancellationToken cancellationToken)
    {
        IReadOnlyList<PendingOwnAsk> asks = await FindUnansweredAsync(session, myNodeId, cancellationToken);
        return asks.FirstOrDefault(ask => ask.TaskId == taskId);
    }
}
