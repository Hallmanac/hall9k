using Hall9k.Daemon.AutoPrReview;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Tasks;

namespace Hall9k.Daemon.Closeout;

/// <summary>One piece of non-member feedback this poll saw for the first time, to be held for the owner.</summary>
/// <param name="Review">For a review: the review as observed. Null for a thread.</param>
/// <param name="CommentIds">For a thread: the ids of the comments in it as observed, which is all the owner's go can ever cover. Null for a review.</param>
internal sealed record HeldFeedback(
    string Key,
    NonMemberFeedbackKind Kind,
    string Reviewer,
    string Url,
    ChangesRequestedReview? Review,
    IReadOnlyList<string>? CommentIds = null);

/// <summary>
/// What the membership gate made of one poll's review feedback (task: a changes-requested review or an
/// unresolved review thread from someone outside the project's trust-chain roster never dispatches a
/// build lap).
/// </summary>
/// <param name="Snapshot">
/// The pull request as every later step of the poll should see it: the same observation with
/// non-member feedback taken out of the reviews and threads a lap could be dispatched for, and the
/// reviews that remain stamped with why a session may read them. Identical to the observed snapshot,
/// bar a read scope that says everything is readable, wherever the gate is off.
/// </param>
/// <param name="Raise">Feedback seen for the first time, each of which becomes exactly one needs-you item.</param>
/// <param name="Withdraw">Keys of items whose feedback is gone from GitHub, which no longer ask anything of the owner.</param>
/// <param name="HoldsMerge">
/// Whether a pre-approved merge must wait: an item still waits on the owner, a non-member's thread is
/// still unresolved, or the roster is not known and human feedback exists that cannot be classified.
/// </param>
/// <param name="WaitingOnRoster">
/// Whether human feedback is being held only because this node has not computed the roster, which is
/// the one hold no item explains, so the engine says it in the log.
/// </param>
internal sealed record FeedbackGateOutcome(
    PullRequestSnapshot Snapshot,
    IReadOnlyList<HeldFeedback> Raise,
    IReadOnlyList<string> Withdraw,
    bool HoldsMerge,
    bool WaitingOnRoster = false);

/// <summary>
/// The membership gate on review feedback (task: a changes-requested review or an unresolved review
/// thread from someone outside the project's trust-chain roster never dispatches a build lap; it
/// raises a needs-you item, and only the owner's explicit go dispatches a fix lap for it).
/// <para>
/// A pure function of one poll's observation, the project's explicit setting, the roster and the
/// items already raised, so every case it decides is a unit test with no database, no git and no
/// GitHub. It answers "is this author a member" the way <see cref="AutoPrReviewObservation.DecideMembershipGate"/>
/// does and for the same reasons: by numeric account id alone, never a login; a roster that has not
/// been computed (null) is unknown, not empty, and decides nothing in either direction; and an author
/// the provider reports as null (a deleted account) is a non-member.
/// </para>
/// <para>
/// Bots keep the automated thread path they have always been on: a thread a bot opened, Copilot's
/// included, is read as before. What is withheld from a session is a non-member's comment wherever
/// it sits: as the opener of a thread, which also holds the thread back from the lap altogether, or
/// as a reply inside a member's or a bot's thread, which is merely left out of what the lap is told it
/// may read.
/// </para>
/// </summary>
internal static class FeedbackMembershipGate
{
    /// <summary>The login recorded for an author the provider reports as null, as GitHub itself names a deleted account.</summary>
    private const string DeletedAccountLogin = "ghost";

    /// <summary>
    /// How many comment ids a poll's read scope names in all, so a pull request with a hundred busy
    /// threads cannot make the reopen event, which replicates across the fleet, or the prompt carrying
    /// it, a megabyte of ids. A comment not named is a comment no session may read, so cutting the list
    /// short only ever reads less.
    /// </summary>
    internal const int MaxReadableCommentIds = 500;

    private enum Who
    {
        Member,
        Bot,
        NonMember,

        /// <summary>A person, with the roster not yet computed: neither a member nor a stranger yet.</summary>
        Undetermined,
    }

    public static FeedbackGateOutcome Decide(
        PullRequestSnapshot snapshot,
        bool? explicitSetting,
        IReadOnlyCollection<long>? roster,
        IReadOnlyDictionary<string, NonMemberFeedbackItem> items)
    {
        if (!AutoPrReviewObservation.MembershipGateIsOn(snapshot.RepositoryIsPrivate, explicitSetting))
        {
            // Off: nothing is held, and an item raised while it was on no longer asks anything. Every
            // review keeps its flow, so each is stamped as readable: the prompt withholds the text of a
            // review with no admission, and an unstamped review here would send a lap its own fix with
            // the review it is meant to fix taken out.
            return new FeedbackGateOutcome(
                snapshot with
                {
                    ChangesRequestedReviews = StampedAsMembers(snapshot.ChangesRequested),
                    StandingChangesRequestedReviews = StampedAsMembers(snapshot.StandingChangesRequested),
                    FeedbackReadScope = ReviewReadScope.AllThreads,
                },
                [],
                [.. items.Values.Where(item => item.Status.NeedsOwner).Select(item => item.Key)],
                HoldsMerge: false);
        }

        List<HeldFeedback> raise = [];
        bool holdsMerge = false;

        IReadOnlyList<ChangesRequestedReview> admitted = DecideReviews(snapshot, roster, items, raise, ref holdsMerge);
        (IReadOnlyList<string> withheld, IReadOnlyList<ReadableReviewThread> readable) =
            DecideThreads(snapshot, roster, items, raise, ref holdsMerge);

        HashSet<string> withheldSet = [.. withheld];
        IReadOnlyList<string> threadIds = [.. snapshot.ThreadIds.Where(id => !withheldSet.Contains(id))];
        IReadOnlyList<string> humanThreadIds = [.. snapshot.HumanThreadIds.Where(id => !withheldSet.Contains(id))];

        PullRequestSnapshot effective = snapshot with
        {
            UnresolvedReviewThreadCount = snapshot.ThreadIds.Count > 0
                ? threadIds.Count
                : snapshot.UnresolvedReviewThreadCount,
            UnresolvedReviewThreadIds = threadIds,
            UnresolvedHumanThreadCount = snapshot.HumanThreadIds.Count > 0
                ? humanThreadIds.Count
                : snapshot.UnresolvedHumanThreadCount,
            UnresolvedHumanThreadIds = humanThreadIds,
            UnresolvedHumanThreadDetails =
                [.. snapshot.HumanThreads.Where(thread => !withheldSet.Contains(thread.ThreadId))],
            ChangesRequestedReviews = admitted,
            StandingChangesRequestedReviews = admitted,
            FeedbackReadScope = new ReviewReadScope(readable, withheld),
        };

        return new FeedbackGateOutcome(
            effective, DistinctByKey(raise), roster is null ? [] : WithdrawnKeys(snapshot, items), holdsMerge,
            WaitingOnRoster: roster is null && holdsMerge);
    }

    private static IReadOnlyList<ChangesRequestedReview> StampedAsMembers(IReadOnlyList<ChangesRequestedReview> reviews) =>
        [.. reviews.Select(review => review with { Admission = FeedbackAdmission.Member })];

    /// <summary>
    /// The reviews that may drive a lap: a member's, on the head as always, and a non-member's the owner
    /// approved. A non-member's review is raised once and otherwise holds the merge until decided,
    /// whether or not the head has moved since it was written, because it still stands on GitHub.
    /// </summary>
    private static IReadOnlyList<ChangesRequestedReview> DecideReviews(
        PullRequestSnapshot snapshot,
        IReadOnlyCollection<long>? roster,
        IReadOnlyDictionary<string, NonMemberFeedbackItem> items,
        List<HeldFeedback> raise,
        ref bool holdsMerge)
    {
        HashSet<string> onHead = [.. snapshot.ChangesRequested.Select(review => review.ReviewUrl)];
        IEnumerable<ChangesRequestedReview> everyStanding = snapshot.ChangesRequested
            .Concat(snapshot.StandingChangesRequested)
            .DistinctBy(review => review.ReviewUrl);

        List<ChangesRequestedReview> admitted = [];
        foreach (ChangesRequestedReview review in everyStanding)
        {
            bool isOnHead = onHead.Contains(review.ReviewUrl);
            switch (WhoIs(review.ReviewerAccountId, roster))
            {
                case Who.Undetermined:
                    holdsMerge = true;
                    break;
                case Who.Member:
                    if (isOnHead)
                    {
                        admitted.Add(review with { Admission = FeedbackAdmission.Member });
                    }

                    break;
                default:
                    if (!items.TryGetValue(review.ReviewUrl, out NonMemberFeedbackItem? item)
                        || item.Status == NonMemberFeedbackStatus.Withdrawn)
                    {
                        raise.Add(new HeldFeedback(
                            review.ReviewUrl, NonMemberFeedbackKind.Review, review.Reviewer, review.ReviewUrl, review));
                        holdsMerge = true;
                    }
                    else if (item.Status == NonMemberFeedbackStatus.Undecided)
                    {
                        holdsMerge = true;
                    }
                    else if (item.Status == NonMemberFeedbackStatus.Admitted)
                    {
                        // The go dispatches ONE lap, and it covers the review the owner was shown: the copy
                        // kept with the item, never a live read, so words the stranger edits in after the
                        // go are not carried. The live review stands in only for an item that kept none.
                        // Once the head has moved the copy can be carried only until a lap has carried it.
                        ChangesRequestedReview? carried = isOnHead
                            ? item.Review ?? review
                            : item.Dispatched ? null : item.Review;
                        if (carried is not null)
                        {
                            admitted.Add(carried with { Admission = FeedbackAdmission.OwnerApproved });
                        }
                    }

                    break;
            }
        }

        return admitted;
    }

    /// <summary>
    /// Which unresolved threads a lap may see at all (those a member, a bot or an approved stranger
    /// opened) and, inside each, which comments it may read. Everything else is withheld by id.
    /// </summary>
    private static (IReadOnlyList<string> Withheld, IReadOnlyList<ReadableReviewThread> Readable) DecideThreads(
        PullRequestSnapshot snapshot,
        IReadOnlyCollection<long>? roster,
        IReadOnlyDictionary<string, NonMemberFeedbackItem> items,
        List<HeldFeedback> raise,
        ref bool holdsMerge)
    {
        Dictionary<string, UnresolvedReviewThread> details = [];
        foreach (UnresolvedReviewThread detail in snapshot.ThreadDetails)
        {
            details.TryAdd(detail.ThreadId, detail);
        }

        HashSet<string> humanIds = [.. snapshot.HumanThreadIds];
        List<string> withheld = [];
        List<ReadableReviewThread> readable = [];

        foreach (string id in snapshot.ThreadIds)
        {
            if (!details.TryGetValue(id, out UnresolvedReviewThread? thread))
            {
                if (!humanIds.Contains(id))
                {
                    // No authorship and not a person's thread: as it has always been, a bot's.
                    continue;
                }

                // A person's thread this read cannot attribute to an account: a stranger until shown otherwise.
                UnresolvedHumanThread? human = snapshot.HumanThreads.FirstOrDefault(candidate => candidate.ThreadId == id);
                thread = new UnresolvedReviewThread(
                    id,
                    human?.Url ?? "",
                    human is null ? [] : [new UnresolvedThreadComment("", new PullRequestReviewer(human.Author, ReviewerKind.Human))]);
            }

            PullRequestReviewer? opener = thread.Opener?.Author;
            Who openerIs = WhoIs(opener, roster);
            string openerLogin = opener?.Login ?? DeletedAccountLogin;

            if (roster is null && thread.Comments.Any(comment => comment.Author is not { IsBot: true }))
            {
                // A person wrote in this thread and nobody can say whether they are a member yet.
                holdsMerge = true;
            }

            switch (openerIs)
            {
                case Who.Undetermined:
                    withheld.Add(id);
                    holdsMerge = true;
                    break;
                case Who.NonMember:
                    DecideStrangersThread(
                        id, thread, opener, openerLogin, roster, items, raise, withheld, readable, ref holdsMerge);
                    break;
                default:
                    readable.Add(new ReadableReviewThread(
                        id, openerLogin, openerIs == Who.Bot ? FeedbackAdmission.Bot : FeedbackAdmission.Member,
                        ReadableCommentIds(thread, roster, approvedAccountId: null, approvedCommentIds: null)));
                    break;
            }
        }

        return (withheld, CapNamedComments(readable));
    }

    /// <summary>
    /// The threads with their comment ids cut off, in order, once <see cref="MaxReadableCommentIds"/>
    /// have been named across the whole poll.
    /// </summary>
    private static IReadOnlyList<ReadableReviewThread> CapNamedComments(IReadOnlyList<ReadableReviewThread> threads)
    {
        int named = 0;
        List<ReadableReviewThread> capped = [];
        foreach (ReadableReviewThread thread in threads)
        {
            IReadOnlyList<string> kept = [.. thread.CommentIds.Take(Math.Max(0, MaxReadableCommentIds - named))];
            named += kept.Count;
            capped.Add(thread with { CommentIds = kept });
        }

        return capped;
    }

    private static void DecideStrangersThread(
        string threadId,
        UnresolvedReviewThread thread,
        PullRequestReviewer? opener,
        string openerLogin,
        IReadOnlyCollection<long>? roster,
        IReadOnlyDictionary<string, NonMemberFeedbackItem> items,
        List<HeldFeedback> raise,
        List<string> withheld,
        List<ReadableReviewThread> readable,
        ref bool holdsMerge)
    {
        if (items.TryGetValue(threadId, out NonMemberFeedbackItem? item)
            && item.Status == NonMemberFeedbackStatus.Admitted)
        {
            readable.Add(new ReadableReviewThread(
                threadId, openerLogin, FeedbackAdmission.OwnerApproved,
                ReadableCommentIds(thread, roster, approvedAccountId: opener?.AccountId, approvedCommentIds: item.CommentIds)));
            return;
        }

        withheld.Add(threadId);
        holdsMerge = true;
        if (item is null || item.Status == NonMemberFeedbackStatus.Withdrawn)
        {
            raise.Add(new HeldFeedback(
                threadId, NonMemberFeedbackKind.Thread, openerLogin, thread.Url, Review: null,
                CommentIds: [.. thread.Comments.Where(comment => comment.Id.Length > 0).Select(comment => comment.Id)]));
        }
    }

    /// <summary>
    /// The comments in a readable thread whose bodies a session may read: a bot's, a member's, and,
    /// in a thread the owner approved, the approved opener's own among those the owner was shown
    /// (<paramref name="approvedCommentIds"/>, held with the item). A comment the opener adds after the
    /// go was never approved, so it is left out, as are a deleted account's and every other stranger's,
    /// which is the whole of how a non-member's reply is never read. An approved thread whose item kept
    /// no ids approves none of the stranger's comments.
    /// </summary>
    private static IReadOnlyList<string> ReadableCommentIds(
        UnresolvedReviewThread thread,
        IReadOnlyCollection<long>? roster,
        long? approvedAccountId,
        IReadOnlyList<string>? approvedCommentIds) =>
        [.. thread.Comments
            .Where(comment => comment.Id.Length > 0
                && (WhoIs(comment.Author, roster) is Who.Bot or Who.Member
                    || (approvedAccountId is { } approved
                        && comment.Author?.AccountId == approved
                        && approvedCommentIds?.Contains(comment.Id) == true)))
            .Select(comment => comment.Id)];

    private static IReadOnlyList<string> WithdrawnKeys(
        PullRequestSnapshot snapshot, IReadOnlyDictionary<string, NonMemberFeedbackItem> items)
    {
        HashSet<string> present =
        [
            .. snapshot.StandingChangesRequested.Select(review => review.ReviewUrl),
            .. snapshot.ChangesRequested.Select(review => review.ReviewUrl),
            .. snapshot.ThreadIds,
            .. snapshot.HumanThreadIds,
            .. snapshot.ThreadDetails.Select(thread => thread.ThreadId),
        ];

        return
        [
            .. items.Values
                .Where(item => item.Status != NonMemberFeedbackStatus.Withdrawn
                    && !present.Contains(item.Key)
                    // Past the provider's own page cap a thread reads as absent when it is merely unseen.
                    && !(item.Kind == NonMemberFeedbackKind.Thread && snapshot.ReviewThreadsTruncated))
                .Select(item => item.Key),
        ];
    }

    private static IReadOnlyList<HeldFeedback> DistinctByKey(List<HeldFeedback> raise) =>
        [.. raise.DistinctBy(held => held.Key)];

    private static Who WhoIs(PullRequestReviewer? author, IReadOnlyCollection<long>? roster) =>
        author is { IsBot: true }
            ? Who.Bot
            : WhoIs(author?.AccountId, roster);

    /// <summary>
    /// A person's standing by account id, through the one function the review-request gate already
    /// asks (with the gate forced on, since the caller has decided it is): a roster that is not known
    /// is undetermined, an id outside it is a stranger, and no id at all is a stranger too.
    /// </summary>
    private static Who WhoIs(long? accountId, IReadOnlyCollection<long>? roster) =>
        AutoPrReviewObservation.DecideMembershipGate(
            isPrivate: false, explicitSetting: true, accountId, roster) switch
        {
            MembershipGateDecision.Run => Who.Member,
            MembershipGateDecision.Unknown => Who.Undetermined,
            _ => Who.NonMember,
        };
}
