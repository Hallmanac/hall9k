using Hall9k.Domain.Features.Invite;
using Hall9k.Domain.Features.Message;
using Marten;

namespace Hall9k.Cli.Commands;

/// <summary>
/// This node's own outstanding owner-act asks (idea 6be68ee2, companion 1bb803e1) that its own
/// invite sweep has not yet completed — a view copied from <see cref="PendingOwnAskLookup"/>: reads
/// only this node's own outbox and inbox, so it costs nothing when this node has queued no owner-act
/// request at all, and a reply already covering the latest request (by receive time) resolves it the
/// identical way <see cref="PendingOwnAskLookup"/>'s own reply-covers-request check does.
/// </summary>
internal static class OwnerActAskLookup
{
    public sealed record PendingOwnerAct(Guid InviteId, DateTimeOffset RequestedAt, string? Verdict, string? Reason);

    public static async Task<IReadOnlyList<PendingOwnerAct>> FindUnansweredAsync(
        IQuerySession session, Guid myNodeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string requestKind = MessageKind.OwnerActRequest.Value;
        IReadOnlyList<MessageDetails> ownRequests = await session.Query<MessageDetails>()
            .Where(message => message.FromNodeId == myNodeId && message.Kind == requestKind && message.About != null)
            .ToListAsync(cancellationToken);
        if (ownRequests.Count == 0)
        {
            return [];
        }

        string outcomeKind = MessageKind.OwnerActOutcome.Value;
        IReadOnlyList<MessageDetails> replies = await session.Query<MessageDetails>()
            .Where(message => message.ReceivedAt != null && message.About != null && message.Kind == outcomeKind)
            .ToListAsync(cancellationToken);
        Dictionary<string, MessageDetails> latestReplyByAbout = replies
            .GroupBy(message => message.About!)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(message => message.ReceivedAt).First());

        List<PendingOwnerAct> results = [];
        foreach (IGrouping<string, MessageDetails> group in ownRequests.GroupBy(message => message.About!))
        {
            if (!Guid.TryParse(group.Key, out Guid inviteId))
            {
                continue;
            }

            // A refused or expired answer is terminal for THIS request, but the invite itself may
            // since have expired on its own clock — nothing re-asks for it once that happens
            // (InviteSweepEngine's own outstanding query already drops it), so this view must not
            // keep warning about it forever either (independent pre-PR review, cycle 1, adversarial
            // lens, low). A re-mint gets its own fresh invite id and so its own fresh, unanswered
            // entry here; it is never this same row's concern.
            InviteDetails? invite = await session.LoadAsync<InviteDetails>(inviteId, cancellationToken);
            if (invite is null || invite.Spent || invite.ExpiresAt <= now)
            {
                continue;
            }

            MessageDetails latest = group.OrderByDescending(message => message.QueuedAt).First();
            OwnerActEnvelopeCodec.OwnerActOutcomeRecord? outcome =
                latestReplyByAbout.TryGetValue(group.Key, out MessageDetails? reply)
                    && reply.ReceivedAt >= latest.QueuedAt && reply.Body is not null
                    ? OwnerActEnvelopeCodec.TryDecodeOutcome(reply.Body)
                    : null;

            if (outcome is { Verdict: OwnerActEnvelopeCodec.OwnerActVerdict.Done })
            {
                // Fully resolved — the requester's own OwnerActRequestWatchLoop reaction already
                // wrote the spend once it saw this same reply, so InviteDetails.Spent already covers
                // it; nothing left for this view to say.
                continue;
            }

            results.Add(new PendingOwnerAct(inviteId, latest.QueuedAt, outcome?.Verdict, outcome?.Reason));
        }

        return results;
    }
}
