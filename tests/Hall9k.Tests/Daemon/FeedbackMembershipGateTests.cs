using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Run;
using Hall9k.Domain.Features.Run.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Events;
using Hall9k.Domain.Features.Tasks.Handlers;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The membership gate on review feedback, decided over one poll's observation with no database, no
/// git and no GitHub (task: a changes-requested review or an unresolved review thread from someone
/// outside the project's trust-chain roster never dispatches a build lap; it raises a needs-you item,
/// and only the owner's explicit go dispatches a fix lap for it). Every case here is a pure function
/// call, and the multi-poll ones fold the events the gate asked for back into a real
/// <see cref="TaskAggregate"/> so the second poll reads exactly what the first one recorded.
/// </summary>
public sealed class FeedbackMembershipGateTests
{
    private const string PullRequest = "https://github.com/x/y/pull/7";
    private const string Head = "head-1";
    private const long MemberId = 1001;
    private const long SecondMemberId = 1002;
    private const long StrangerId = 2001;
    private const long CopilotId = 9001;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyCollection<long> Roster = [MemberId, SecondMemberId];

    private static readonly IReadOnlyDictionary<string, NonMemberFeedbackItem> NoItems =
        new Dictionary<string, NonMemberFeedbackItem>();

    private static PullRequestReviewer Human(string login, long? accountId) =>
        new(login, ReviewerKind.Human, AccountId: accountId);

    private static readonly PullRequestReviewer Member = Human("member", MemberId);
    private static readonly PullRequestReviewer Stranger = Human("stranger", StrangerId);
    private static readonly PullRequestReviewer Copilot =
        new("copilot-pull-request-reviewer", ReviewerKind.Bot, AccountId: CopilotId);

    private static string ReviewUrl(int number) => $"{PullRequest}#pullrequestreview-{number}";

    private static ChangesRequestedReview Review(string login, long? accountId, int number) =>
        new(login, ReviewUrl(number), Now.AddMinutes(-5),
            [new ChangesRequestedFinding($"{login}'s finding on review {number}.", "src/Limiter.cs:42", $"PRRT_{number}")],
            accountId);

    private static PullRequestSnapshot Public(params ChangesRequestedReview[] onHead) =>
        Quiet() with { RepositoryIsPrivate = false, HeadCommit = Head, ChangesRequestedReviews = onHead };

    private static PullRequestSnapshot Quiet() => new(
        IsMerged: false, IsClosed: false, MergedAt: null, ClosedAt: null,
        FailingChecks: [], HasPendingChecks: false, UnresolvedReviewThreadCount: 0,
        UnresolvedHumanThreadCount: 0, Reviewers: [], ErroredReview: null,
        CopilotReviewState: ExternalReviewState.None, CopilotReviewThreadCount: 0);

    /// <summary>A snapshot whose unresolved threads are exactly <paramref name="threads"/>, as the inspector would shape it.</summary>
    private static PullRequestSnapshot WithThreads(PullRequestSnapshot snapshot, params UnresolvedReviewThread[] threads)
    {
        UnresolvedReviewThread[] human = [.. threads.Where(thread => thread.Opener?.Author is not { IsHuman: false })];
        return snapshot with
        {
            UnresolvedReviewThreadCount = threads.Length,
            UnresolvedReviewThreadIds = [.. threads.Select(thread => thread.ThreadId)],
            UnresolvedHumanThreadCount = human.Length,
            UnresolvedHumanThreadIds = [.. human.Select(thread => thread.ThreadId)],
            UnresolvedHumanThreadDetails =
                [.. human.Select(thread => new UnresolvedHumanThread(
                    thread.ThreadId, thread.Opener?.Author?.Login ?? "ghost", thread.Url, "an opening comment"))],
            UnresolvedThreadDetails = threads,
        };
    }

    private static UnresolvedReviewThread Thread(string id, params (string CommentId, PullRequestReviewer? Author)[] comments) =>
        new(id, $"{PullRequest}#discussion_{id}",
            [.. comments.Select(comment => new UnresolvedThreadComment(comment.CommentId, comment.Author))]);

    private static FeedbackGateOutcome Decide(
        PullRequestSnapshot snapshot,
        IReadOnlyDictionary<string, NonMemberFeedbackItem>? items = null,
        IReadOnlyCollection<long>? roster = null,
        bool? explicitSetting = null,
        bool rosterUnknown = false) =>
        FeedbackMembershipGate.Decide(snapshot, explicitSetting, rosterUnknown ? null : roster ?? Roster, items ?? NoItems);

    /// <summary>
    /// A finished task with its pull request open, the state closeout watches it in, after folding
    /// <paramref name="events"/> in.
    /// </summary>
    private static TaskAggregate TaskAfter(params object[] events)
    {
        Guid ownerId = DomainId.New();
        TaskAdded added = TaskDecider.Add(
            DomainId.New(), DomainId.New(), "Do the thing", ["it is done"], TaskType.Chore, null, null, null, Now, ownerId);
        (TaskAggregate task, _) = TaskSeed.Start(added, ownerId, Now);
        task.Apply(TaskDecider.Claim(task, DomainId.New(), ownerId, DomainId.New(), Now));
        task.Apply(TaskDecider.Complete(task, task.CurrentRunId!.Value, PullRequest, Now));
        foreach (object @event in events)
        {
            switch (@event)
            {
                case NonMemberFeedbackHeld held:
                    task.Apply(held);
                    break;
                case NonMemberFeedbackAdmitted admitted:
                    task.Apply(admitted);
                    break;
                case NonMemberFeedbackDeclined declined:
                    task.Apply(declined);
                    break;
                case NonMemberFeedbackWithdrawn withdrawn:
                    task.Apply(withdrawn);
                    break;
                default:
                    throw new InvalidOperationException($"No fold for {@event.GetType().Name}.");
            }
        }

        return task;
    }

    private static object[] HeldEvents(FeedbackGateOutcome outcome) =>
        [.. outcome.Raise.Select(held => new NonMemberFeedbackHeld(
            Guid.Empty, held.Key, held.Kind, held.Reviewer, held.Url, Now, held.Review))];

    [Fact]
    public void A_members_review_on_the_head_keeps_todays_flow_stamped_as_a_members()
    {
        FeedbackGateOutcome outcome = Decide(Public(Review("member", MemberId, 1)));

        ChangesRequestedReview carried = outcome.Snapshot.ChangesRequested.Should().ContainSingle().Subject;
        carried.AdmittedAs.Should().Be(FeedbackAdmission.Member, "the prompt can only check that an admission was recorded");
        outcome.Raise.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeFalse();
    }

    [Fact]
    public void A_non_members_review_dispatches_nothing_and_raises_exactly_one_item_naming_it()
    {
        FeedbackGateOutcome outcome = Decide(Public(Review("stranger", StrangerId, 2)));

        outcome.Snapshot.ChangesRequested.Should().BeEmpty("no lap is dispatched for a stranger's review");
        HeldFeedback held = outcome.Raise.Should().ContainSingle().Subject;
        held.Kind.Should().Be(NonMemberFeedbackKind.Review);
        held.Key.Should().Be(ReviewUrl(2));
        held.Reviewer.Should().Be("stranger");
        held.Url.Should().Be(ReviewUrl(2));
        outcome.HoldsMerge.Should().BeTrue("a pre-approved task does not merge past an undecided item");
    }

    [Fact]
    public void A_non_members_review_is_held_even_when_the_head_has_moved_on_from_it()
    {
        PullRequestSnapshot snapshot = Quiet() with
        {
            RepositoryIsPrivate = false,
            HeadCommit = "head-2",
            ChangesRequestedReviews = [],
            StandingChangesRequestedReviews = [Review("stranger", StrangerId, 3)],
        };

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Raise.Should().ContainSingle(held => held.Key == ReviewUrl(3));
        outcome.Snapshot.ChangesRequested.Should().BeEmpty();
    }

    [Fact]
    public void A_stored_review_with_no_account_id_is_a_non_members()
    {
        // The shape every review recorded before the gate shipped has: a login and nothing to match.
        FeedbackGateOutcome outcome = Decide(Public(Review("member", accountId: null, 4)));

        outcome.Snapshot.ChangesRequested.Should().BeEmpty();
        outcome.Raise.Should().ContainSingle(held => held.Key == ReviewUrl(4));
    }

    [Fact]
    public void A_review_whose_author_the_provider_reports_as_deleted_is_a_non_members()
    {
        ChangesRequestedReview ghost = new("ghost", ReviewUrl(5), Now, [new ChangesRequestedFinding("gone")], null);

        FeedbackGateOutcome outcome = Decide(Public(ghost));

        outcome.Raise.Should().ContainSingle(held => held.Reviewer == "ghost");
    }

    [Fact]
    public void The_same_non_members_review_observed_on_two_polls_raises_one_item()
    {
        PullRequestSnapshot snapshot = Public(Review("stranger", StrangerId, 6));
        FeedbackGateOutcome first = Decide(snapshot);
        first.Raise.Should().HaveCount(1);

        TaskAggregate task = TaskAfter(HeldEvents(first));
        FeedbackGateOutcome second = Decide(snapshot, task.NonMemberFeedback);

        second.Raise.Should().BeEmpty("the key is already held, however many polls see it");
        second.Withdraw.Should().BeEmpty();
        second.HoldsMerge.Should().BeTrue("the item is still undecided");
        task.NonMemberFeedback.Values.Should().ContainSingle(item => item.Status == NonMemberFeedbackStatus.Undecided);
    }

    [Fact]
    public void A_mixed_poll_runs_the_members_lap_with_only_member_feedback_and_still_raises_the_strangers_item()
    {
        PullRequestSnapshot snapshot = Public(
            Review("member", MemberId, 7), Review("stranger", StrangerId, 8));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Snapshot.ChangesRequested.Should().ContainSingle(review => review.ReviewUrl == ReviewUrl(7));
        outcome.Raise.Should().ContainSingle(held => held.Key == ReviewUrl(8),
            "the item is raised in this same poll, before the member's push can move the head and lose it");
    }

    [Fact]
    public void A_declined_review_dispatches_nothing_and_no_longer_holds_the_merge()
    {
        PullRequestSnapshot snapshot = Public(Review("stranger", StrangerId, 9));
        TaskAggregate task = TaskAfter(
            HeldEvents(Decide(snapshot)).Concat([new NonMemberFeedbackDeclined(Guid.Empty, ReviewUrl(9), Now, Guid.Empty)]).ToArray());

        FeedbackGateOutcome outcome = Decide(snapshot, task.NonMemberFeedback);

        outcome.Snapshot.ChangesRequested.Should().BeEmpty();
        outcome.Raise.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeFalse("a declined review releases the hold");
    }

    [Fact]
    public void The_owners_go_dispatches_exactly_one_lap_carrying_that_review_even_after_the_head_moved()
    {
        ChangesRequestedReview strangers = Review("stranger", StrangerId, 10);
        PullRequestSnapshot onHead = Public(strangers);
        TaskAggregate task = TaskAfter(HeldEvents(Decide(onHead)));
        NonMemberFeedbackAdmitted go = TaskDecider.AdmitNonMemberFeedback(task, ReviewUrl(10), Now, DomainId.New());
        task.Apply(go);

        // The head moved before the go landed: the review is no longer on it, but still stands.
        PullRequestSnapshot moved = Quiet() with
        {
            RepositoryIsPrivate = false,
            HeadCommit = "head-2",
            StandingChangesRequestedReviews = [strangers],
        };
        FeedbackGateOutcome dispatching = Decide(moved, task.NonMemberFeedback);

        ChangesRequestedReview carried = dispatching.Snapshot.ChangesRequested.Should().ContainSingle().Subject;
        carried.ReviewUrl.Should().Be(ReviewUrl(10));
        carried.AdmittedAs.Should().Be(FeedbackAdmission.OwnerApproved);
        carried.Findings.Should().ContainSingle(finding => finding.Body.Contains("stranger's finding", StringComparison.Ordinal));
        dispatching.HoldsMerge.Should().BeFalse();

        // The lap's reopen carries it, which is what marks it dispatched ...
        TaskReopenedFold(task, carried);

        // ... and the next poll, head still moved, dispatches nothing more for it.
        FeedbackGateOutcome afterwards = Decide(moved, task.NonMemberFeedback);
        afterwards.Snapshot.ChangesRequested.Should().BeEmpty("the go buys one lap, not one per poll");
        afterwards.Raise.Should().BeEmpty();
    }

    [Fact]
    public void An_admitted_review_still_on_the_head_flows_through_the_ordinary_path_with_its_live_text()
    {
        ChangesRequestedReview strangers = Review("stranger", StrangerId, 11);
        PullRequestSnapshot snapshot = Public(strangers);
        TaskAggregate task = TaskAfter(HeldEvents(Decide(snapshot)));
        task.Apply(TaskDecider.AdmitNonMemberFeedback(task, ReviewUrl(11), Now, DomainId.New()));

        FeedbackGateOutcome outcome = Decide(snapshot, task.NonMemberFeedback);

        outcome.Snapshot.ChangesRequested.Should().ContainSingle(review => review.AdmittedAs == FeedbackAdmission.OwnerApproved);
    }

    [Fact]
    public void A_review_withdrawn_on_github_clears_its_item_and_a_reappearing_one_is_raised_afresh()
    {
        PullRequestSnapshot snapshot = Public(Review("stranger", StrangerId, 12));
        TaskAggregate task = TaskAfter(HeldEvents(Decide(snapshot)));

        FeedbackGateOutcome gone = Decide(Public(), task.NonMemberFeedback);
        gone.Withdraw.Should().Equal(ReviewUrl(12));
        gone.HoldsMerge.Should().BeFalse();

        task = TaskAfter(HeldEvents(Decide(snapshot))
            .Concat([new NonMemberFeedbackWithdrawn(Guid.Empty, ReviewUrl(12), Now)]).ToArray());
        task.NonMemberFeedback[ReviewUrl(12)].Status.Should().Be(NonMemberFeedbackStatus.Withdrawn);
        Decide(snapshot, task.NonMemberFeedback).Raise.Should().ContainSingle(
            "a thread or review that comes back after it went away is new to the owner");
    }

    [Fact]
    public void A_thread_a_non_member_opened_buys_no_lap_and_raises_one_item()
    {
        PullRequestSnapshot snapshot = WithThreads(
            Public(), Thread("PRRT_s", ("PRRC_s1", Stranger)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Snapshot.UnresolvedReviewThreadCount.Should().Be(0, "the thread is withheld from every lap decision");
        outcome.Snapshot.ThreadIds.Should().BeEmpty();
        outcome.Snapshot.HumanThreadIds.Should().BeEmpty("it does not count as human engagement either");
        outcome.Snapshot.HumanThreads.Should().BeEmpty();
        outcome.Snapshot.FeedbackReadScope!.WithheldThreadIds.Should().Equal("PRRT_s");
        outcome.Snapshot.FeedbackReadScope!.Threads.Should().BeEmpty();
        HeldFeedback held = outcome.Raise.Should().ContainSingle().Subject;
        held.Kind.Should().Be(NonMemberFeedbackKind.Thread);
        held.Key.Should().Be("PRRT_s");
        held.Reviewer.Should().Be("stranger");
        held.Review.Should().BeNull("a thread's text is never stored on its item");
        outcome.HoldsMerge.Should().BeTrue();
    }

    [Fact]
    public void A_non_members_thread_does_not_lift_the_per_obstruction_cap_but_a_members_does()
    {
        TaskAggregate task = TaskAfter();
        RunDetails run = new();
        PullRequestSnapshot strangersThread = Decide(WithThreads(Public(), Thread("PRRT_s", ("PRRC_s1", Stranger)))).Snapshot;
        PullRequestSnapshot membersThread = Decide(WithThreads(Public(), Thread("PRRT_m", ("PRRC_m1", Member)))).Snapshot;

        CloseoutEngine.HasHumanEngagement(task, run, strangersThread, out _).Should().BeFalse(
            "a stranger's new thread is not cause to spend another lap past the cap");
        CloseoutEngine.HasHumanEngagement(task, run, membersThread, out string reason).Should().BeTrue();
        reason.Should().Contain("1 new review thread");
    }

    [Fact]
    public void A_non_members_reply_inside_a_members_thread_is_left_unread_without_raising_anything()
    {
        PullRequestSnapshot snapshot = WithThreads(
            Public(), Thread("PRRT_m", ("PRRC_m1", Member), ("PRRC_x2", Stranger), ("PRRC_m3", Member)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Snapshot.ThreadIds.Should().Equal("PRRT_m");
        ReadableReviewThread readable = outcome.Snapshot.FeedbackReadScope!.Threads.Should().ContainSingle().Subject;
        readable.OpenedBy.Should().Be(FeedbackAdmission.Member);
        readable.CommentIds.Should().Equal("PRRC_m1", "PRRC_m3");
        readable.CommentIds.Should().NotContain("PRRC_x2", "the stranger's reply is never named to a session");
        outcome.Raise.Should().BeEmpty("a reply raises no item; it is simply never read");
        outcome.HoldsMerge.Should().BeFalse();
    }

    [Fact]
    public void A_non_members_reply_inside_a_bots_thread_is_left_unread_too()
    {
        PullRequestSnapshot snapshot = WithThreads(
            Public(), Thread("PRRT_c", ("PRRC_c1", Copilot), ("PRRC_x2", Stranger), ("PRRC_m3", Member)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Snapshot.FeedbackReadScope!.Threads.Should().ContainSingle()
            .Which.CommentIds.Should().Equal("PRRC_c1", "PRRC_m3");
    }

    [Fact]
    public void A_scope_names_at_most_a_bounded_number_of_comments_in_all_and_never_the_ones_past_it()
    {
        (string, PullRequestReviewer?)[] busy =
            [.. Enumerable.Range(0, FeedbackMembershipGate.MaxReadableCommentIds + 100)
                .Select(index => ($"PRRC_{index}", (PullRequestReviewer?)Member))];
        PullRequestSnapshot snapshot = WithThreads(
            Public(), Thread("PRRT_busy", busy), Thread("PRRT_late", ("PRRC_late", Member)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        IReadOnlyList<ReadableReviewThread> threads = outcome.Snapshot.FeedbackReadScope!.Threads;
        threads.Sum(thread => thread.CommentIds.Count).Should().Be(FeedbackMembershipGate.MaxReadableCommentIds);
        threads.Single(thread => thread.ThreadId == "PRRT_late").CommentIds.Should().BeEmpty(
            "a comment the scope does not name is a comment no session may read");
    }

    [Fact]
    public void A_copilot_thread_keeps_the_automated_flow_unchanged()
    {
        PullRequestSnapshot snapshot = WithThreads(Public(), Thread("PRRT_c", ("PRRC_c1", Copilot)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Snapshot.ThreadIds.Should().Equal("PRRT_c");
        outcome.Snapshot.UnresolvedReviewThreadCount.Should().Be(1);
        outcome.Snapshot.HumanThreadIds.Should().BeEmpty("a bot's thread was never a human's");
        outcome.Snapshot.FeedbackReadScope!.Threads.Should().ContainSingle()
            .Which.OpenedBy.Should().Be(FeedbackAdmission.Bot);
        outcome.Raise.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeFalse();
    }

    [Fact]
    public void A_thread_whose_opener_is_a_deleted_account_is_a_non_members()
    {
        PullRequestSnapshot snapshot = WithThreads(Public(), Thread("PRRT_g", ("PRRC_g1", null)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Raise.Should().ContainSingle(held => held.Key == "PRRT_g" && held.Reviewer == "ghost");
        outcome.Snapshot.ThreadIds.Should().BeEmpty();
    }

    [Fact]
    public void A_human_thread_the_read_could_not_attribute_to_an_account_is_a_non_members()
    {
        // A read that carries the thread ids and the opener's login but no authorship, as every
        // snapshot built before the gate existed does.
        PullRequestSnapshot snapshot = Public() with
        {
            UnresolvedReviewThreadCount = 1,
            UnresolvedHumanThreadCount = 1,
            UnresolvedReviewThreadIds = ["PRRT_old"],
            UnresolvedHumanThreadIds = ["PRRT_old"],
            UnresolvedHumanThreadDetails = [new UnresolvedHumanThread("PRRT_old", "someone", "https://x/old", "text")],
        };

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Raise.Should().ContainSingle(held => held.Key == "PRRT_old" && held.Reviewer == "someone");
        outcome.Snapshot.HumanThreadIds.Should().BeEmpty();
    }

    [Fact]
    public void A_thread_the_owner_approved_is_read_and_dispatched_like_a_members_with_only_the_openers_own_comments()
    {
        PullRequestSnapshot snapshot = WithThreads(
            Public(), Thread("PRRT_s", ("PRRC_s1", Stranger), ("PRRC_t2", Human("another", 3001)), ("PRRC_s3", Stranger)));
        TaskAggregate task = TaskAfter(HeldEvents(Decide(snapshot)));
        task.Apply(TaskDecider.AdmitNonMemberFeedback(task, "PRRT_s", Now, DomainId.New()));

        FeedbackGateOutcome outcome = Decide(snapshot, task.NonMemberFeedback);

        outcome.Snapshot.ThreadIds.Should().Equal("PRRT_s");
        outcome.Snapshot.HumanThreadIds.Should().Equal(["PRRT_s"], "an approved thread is cause for a lap");
        ReadableReviewThread readable = outcome.Snapshot.FeedbackReadScope!.Threads.Should().ContainSingle().Subject;
        readable.OpenedBy.Should().Be(FeedbackAdmission.OwnerApproved);
        readable.CommentIds.Should().Equal(
            ["PRRC_s1", "PRRC_s3"], "the go was for this person's thread, not for a third party's reply in it");
        outcome.Raise.Should().BeEmpty();
    }

    [Fact]
    public void A_declined_thread_stays_withheld_and_keeps_holding_the_merge()
    {
        PullRequestSnapshot snapshot = WithThreads(Public(), Thread("PRRT_s", ("PRRC_s1", Stranger)));
        TaskAggregate task = TaskAfter(HeldEvents(Decide(snapshot)));
        task.Apply(TaskDecider.DeclineNonMemberFeedback(task, "PRRT_s", Now, DomainId.New()));

        FeedbackGateOutcome outcome = Decide(snapshot, task.NonMemberFeedback);

        outcome.Snapshot.ThreadIds.Should().BeEmpty();
        outcome.Raise.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeTrue("a declined thread stays unresolved, as any unresolved thread does");
    }

    [Fact]
    public void A_resolved_thread_withdraws_its_item_unless_the_read_was_cut_off_at_the_page_cap()
    {
        PullRequestSnapshot snapshot = WithThreads(Public(), Thread("PRRT_s", ("PRRC_s1", Stranger)));
        TaskAggregate task = TaskAfter(HeldEvents(Decide(snapshot)));
        PullRequestSnapshot resolved = Public();

        Decide(resolved, task.NonMemberFeedback).Withdraw.Should().Equal("PRRT_s");
        Decide(resolved with { ReviewThreadsTruncated = true }, task.NonMemberFeedback).Withdraw.Should().BeEmpty(
            "past the provider's page cap a thread reads as absent when it is merely unseen");
    }

    [Fact]
    public void When_the_roster_is_not_computed_yet_human_feedback_is_neither_dispatched_nor_raised_and_the_merge_is_held()
    {
        PullRequestSnapshot snapshot = WithThreads(
            Public(Review("member", MemberId, 13)),
            Thread("PRRT_h", ("PRRC_h1", Member)),
            Thread("PRRT_c", ("PRRC_c1", Copilot)));

        FeedbackGateOutcome outcome = Decide(snapshot, rosterUnknown: true);

        outcome.Snapshot.ChangesRequested.Should().BeEmpty("a poll with no roster dispatches no review lap");
        outcome.Raise.Should().BeEmpty("and raises no needs-you item either: it asks again on the next poll");
        outcome.Withdraw.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeTrue();
        outcome.WaitingOnRoster.Should().BeTrue("no item explains this hold, so the engine says it in the log");
        outcome.Snapshot.ThreadIds.Should().Equal(["PRRT_c"], "only the bot's thread, with no human in it, proceeds");
    }

    [Fact]
    public void When_the_roster_is_not_computed_yet_a_poll_with_no_human_feedback_does_not_hold_the_merge()
    {
        FeedbackGateOutcome outcome = Decide(WithThreads(Public(), Thread("PRRT_c", ("PRRC_c1", Copilot))), rosterUnknown: true);

        outcome.HoldsMerge.Should().BeFalse("nothing needs classifying, so nothing waits for the roster");
        outcome.WaitingOnRoster.Should().BeFalse();
        outcome.Snapshot.ThreadIds.Should().Equal("PRRT_c");
    }

    [Fact]
    public void A_deleted_account_with_no_roster_is_undetermined_not_a_stranger_yet()
    {
        FeedbackGateOutcome outcome = Decide(
            WithThreads(Public(), Thread("PRRT_g", ("PRRC_g1", null))), rosterUnknown: true);

        outcome.Raise.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeTrue();
    }

    [Fact]
    public void A_private_repository_runs_with_the_gate_off_and_every_review_keeps_its_flow()
    {
        PullRequestSnapshot snapshot = WithThreads(
            Public(Review("stranger", StrangerId, 14)) with { RepositoryIsPrivate = true },
            Thread("PRRT_s", ("PRRC_s1", Stranger)));

        FeedbackGateOutcome outcome = Decide(snapshot);

        outcome.Snapshot.ChangesRequested.Should().ContainSingle(review => review.ReviewUrl == ReviewUrl(14));
        outcome.Snapshot.ThreadIds.Should().Equal("PRRT_s");
        outcome.Raise.Should().BeEmpty();
        outcome.HoldsMerge.Should().BeFalse();
        outcome.Snapshot.FeedbackReadScope!.Unrestricted.Should().BeTrue();
    }

    [Fact]
    public void An_unreported_visibility_reads_as_public_so_the_gate_fails_closed()
    {
        PullRequestSnapshot snapshot = Public(Review("stranger", StrangerId, 15)) with { RepositoryIsPrivate = null };

        Decide(snapshot).Raise.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void The_explicit_project_setting_overrides_what_visibility_says(bool isPrivate, bool setting, bool gateHolds)
    {
        PullRequestSnapshot snapshot = Public(Review("stranger", StrangerId, 16)) with { RepositoryIsPrivate = isPrivate };

        FeedbackGateOutcome outcome = Decide(snapshot, explicitSetting: setting);

        (outcome.Raise.Count == 1).Should().Be(gateHolds);
        (outcome.Snapshot.ChangesRequested.Count == 0).Should().Be(gateHolds);
    }

    [Fact]
    public void Turning_the_gate_off_clears_an_item_raised_while_it_was_on()
    {
        PullRequestSnapshot snapshot = Public(Review("stranger", StrangerId, 17));
        TaskAggregate task = TaskAfter(HeldEvents(Decide(snapshot)));

        FeedbackGateOutcome off = Decide(snapshot, task.NonMemberFeedback, explicitSetting: false);

        off.Withdraw.Should().Equal(ReviewUrl(17));
        off.Snapshot.ChangesRequested.Should().ContainSingle("with the gate off the review flows as it always did");
    }

    /// <summary>The closeout dispatch a lap that carries <paramref name="carried"/> records: a reopen of the task.</summary>
    private static void TaskReopenedFold(TaskAggregate task, ChangesRequestedReview carried) =>
        task.Apply(TaskDecider.Reopen(
            task, task.CurrentRunId!.Value, "task/abc12345-do-the-thing", "A human requested changes.",
            FollowUpKind.ReviewRequestedChanges, automatic: true, Now, DomainId.New(),
            changesRequestedReviews: [carried]));
}
