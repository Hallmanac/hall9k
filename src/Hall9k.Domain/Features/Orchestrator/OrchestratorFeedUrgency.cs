using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Run.Events;
using Hall9k.Domain.Features.Tasks.Events;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Which feed items are urgent enough for the feed courier (idea 89471598, piece 3) to dispatch
/// at once, regardless of its own batching wait — Brian's own words from the walk: "a park, a
/// dispute, daemon trouble, or a message from a person dispatches at once" — and, since it is
/// the reviewer's turn the moment it happens, a reply to a review the owner left or a re-review
/// asked of them. A narrower set than
/// <see cref="OrchestratorFeedLevel.Actionable"/>, deliberately: that band's own "gate and run
/// failures" and "a merge that stays failed" entries are retried automatically and do not need a
/// human paged the moment they land, while every type named here is a stop with nobody but a
/// human able to move it forward.
/// <para>
/// Every type on this table is already inside the actionable band's own floor, which every
/// project's own <c>--orchestrator-feed</c> level admits regardless of its own choice — so an
/// urgent item is never invisible to a project reading at a narrower level than the one that
/// would otherwise show it. Read at select time, in <see cref="OrchestratorFeedSelection"/>,
/// rather than re-derived by the courier from a second scan of the same events, so the two can
/// never disagree about which item forced the wait.
/// </para>
/// </summary>
public static class OrchestratorFeedUrgency
{
    private static readonly IReadOnlyCollection<Type> UrgentTypes =
    [
        // Parks and disputes: the same set OrchestratorFeedInterest's own "somebody is owed
        // something" section opens with.
        typeof(ReviewParked),
        typeof(CloseoutParked),
        typeof(ReviewDisagreementParked),
        typeof(HumanThreadReplyParked),
        typeof(ReviewThreadReplyRefused),
        typeof(ReviewFindingRouted),
        typeof(QuestionAsked),

        // Daemon trouble: the machinery, not the work, went wrong.
        typeof(RunSessionErrorRetried),
        typeof(RunUncommittedWorkRecoveryAttempted),
        typeof(RunUnattendedExitFlagged),
        typeof(RunRecordReconstructed),
        typeof(RunLaunchHeld),
    ];

    /// <summary>
    /// Whether this admitted feed candidate is urgent. <see cref="MessageReceived"/> is not on
    /// the fixed type table above because its own urgency is payload-gated the identical way
    /// <see cref="OrchestratorFeedInterest.Admits(object)"/> already gates its admission: every
    /// message the feed itself ever shows at all is already a person's or another node's
    /// window's, so a candidate that reaches this method as one is urgent unconditionally.
    /// <para>
    /// <see cref="PullRequestReviewAuthorResponded"/> is payload-gated too: a reply in the
    /// reviewer's threads or a newly requested re-review is someone asking for the reviewer's turn
    /// and is urgent, while a wake that is only new commits is still an item but waits for the
    /// courier's batch, so a push followed two minutes later by its replies is not two pages.
    /// </para>
    /// </summary>
    public static bool IsUrgent(Type eventType, object eventData) => eventData switch
    {
        MessageReceived => true,
        PullRequestReviewAuthorResponded responded => responded.ReplyCount > 0 || responded.ReReviewNewlyRequested,
        _ => UrgentTypes.Contains(eventType),
    };
}
