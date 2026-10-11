using Hall9k.Domain.Features.Tasks.Events;
using JasperFx.Events;
using Marten;

namespace Hall9k.Domain.Features.Tasks.Queries;

/// <summary>
/// The comments a pr-review task's mention follow-up dispatch can be pinned to by its own claim, rather than
/// by <c>TaskAggregate.LatestMention*</c>, which every observed mention overwrites, including one that earned
/// no follow-up. <see cref="Claimed"/> is the mention a mention follow-up claim was taken for: the
/// <c>AutoPrReviewEngine.AttachMentionAsync</c> claim appends the mention event and the claim in one append, so
/// the claim is the sentinel (<c>Guid.Empty</c>) non-interactive one that directly follows a non-minting
/// mention event, and the latest such claim is the one a pending follow-up belongs to.
/// <see cref="Minting"/> is the mention that minted an answer-only task, whose first dispatch answers it.
/// <see cref="Only"/> is the one comment the stream ever observed, null when it observed none or several: with
/// a single candidate there is nothing for a later mention to be mistaken for, which is the only reason a
/// stream with no identifiable claim (one an older node wrote) can still be answered. Each is null when the
/// stream holds no such event.
/// </summary>
public sealed record MentionFollowUpClaimAnchors(
    PullRequestReviewMentionObserved? Claimed, PullRequestReviewMentionObserved? Minting,
    PullRequestReviewMentionObserved? Only);

public static class MentionFollowUpClaimResolver
{
    public static async Task<MentionFollowUpClaimAnchors> ResolveAsync(
        IQuerySession session, Guid taskId, CancellationToken cancellationToken)
    {
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(taskId, token: cancellationToken);
        return Resolve([.. events.Select(@event => @event.Data)]);
    }

    public static MentionFollowUpClaimAnchors Resolve(IReadOnlyList<object> events)
    {
        PullRequestReviewMentionObserved? claimed = null;
        PullRequestReviewMentionObserved? minting = null;
        PullRequestReviewMentionObserved? only = null;
        bool severalComments = false;
        for (int index = 0; index < events.Count; index++)
        {
            if (events[index] is PullRequestReviewMentionObserved seen)
            {
                severalComments |= only is not null && only.CommentId != seen.CommentId;
                only = seen;
            }

            switch (events[index])
            {
                case PullRequestReviewMentionObserved { MintedTask: true, AnswerOnly: true } mint:
                    minting ??= mint;
                    break;
                case TaskClaimed { NodeId: var nodeId, InteractiveMode: false }
                    when nodeId == Guid.Empty && index > 0
                        && events[index - 1] is PullRequestReviewMentionObserved { MintedTask: false } observed:
                    claimed = observed;
                    break;
            }
        }

        return new MentionFollowUpClaimAnchors(claimed, minting, severalComments ? null : only);
    }
}
