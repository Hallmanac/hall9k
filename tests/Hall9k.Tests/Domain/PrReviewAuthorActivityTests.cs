using FluentAssertions;
using Hall9k.Domain.Features.Tasks;
using Xunit;

namespace Hall9k.Tests.Domain;

/// <summary>
/// The comparison that decides whether a posted review has actually been answered (task: a
/// pr-review task stays open while the pull request's review threads are unresolved) — and the one
/// line every surface says it in. Pure, so the whole decision is checkable without Docker or a
/// GitHub account, which is the point of it being its own type.
/// </summary>
public sealed class PrReviewAuthorActivityTests
{
    /// <summary>
    /// One thread's watermark. <paramref name="replies"/> is what the reviewer did NOT write —
    /// everything after their own last comment in the thread — so a thread carrying only their own
    /// review comment is <c>Thread("t1", 0)</c>.
    /// </summary>
    private static PrReviewThreadWatermark Thread(string id, int replies, bool resolved = false) =>
        new(id, replies, resolved);

    /// <summary>
    /// The window the first cut lost entirely (independent pre-PR review, cycle 1, both lenses):
    /// the reviewer posts, the author answers a minute later, and the FIRST poll is the one that
    /// sees it. Suppressing that first look absorbed the answer into the baseline and told nobody,
    /// ever — not the board, and not the scoped lap whose anchor is that same first look. There is
    /// nothing to suppress, because a reviewer's own comments are never replies to them.
    /// </summary>
    [Fact]
    public void The_first_look_counts_the_replies_already_waiting_on_the_reviewer()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [],
            after: [Thread("t1", 3), Thread("t2", 1)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 1, commitsAfter: 1,
            reReviewBefore: false, reReviewAfter: false);

        activity.Any.Should().BeTrue(
            "a reply that beat the first poll to the pull request is exactly the silent miss this "
            + "whole watch exists to prevent");
        activity.ReplyCount.Should().Be(4);
        activity.ThreadsWithReplies.Should().Be(2);
    }

    [Fact]
    public void The_first_look_of_an_unanswered_review_says_nothing_happened()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [],
            after: [Thread("t1", 0), Thread("t2", 0)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 1, commitsAfter: 1,
            reReviewBefore: false, reReviewAfter: false);

        activity.Any.Should().BeFalse(
            "the reviewer's own comments are what those threads hold, and reporting their own review "
            + "back to them as an answer is the self-wake a count of every comment would cause");
        activity.ReplyCount.Should().Be(0);
    }

    /// <summary>
    /// The half the first look must NOT suppress (self-review, round one): the head the review was
    /// posted against is recorded when the wait begins, so a push landing in the minutes between
    /// the review and the first poll is genuinely observable — and swallowing it into the baseline
    /// instead is exactly the silent miss this whole watch exists to prevent.
    /// </summary>
    [Fact]
    public void The_first_look_still_notices_a_push_that_landed_before_it()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [],
            after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: "bbb",
            commitsBefore: null, commitsAfter: 7,
            reReviewBefore: false, reReviewAfter: false);

        activity.HeadMoved.Should().BeTrue();
        activity.Any.Should().BeTrue("a push between the review and the first poll is real and unreported otherwise");
        activity.ReplyCount.Should().Be(0, "nothing was said in the threads themselves");
    }

    [Fact]
    public void A_reply_in_a_known_thread_counts_the_comments_and_the_threads()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0), Thread("t2", 0)],
            after: [Thread("t1", 2), Thread("t2", 1)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 3, commitsAfter: 3,
            reReviewBefore: false, reReviewAfter: false);

        activity.ReplyCount.Should().Be(3);
        activity.ThreadsWithReplies.Should().Be(2);
        activity.HeadMoved.Should().BeFalse();
        activity.NewCommitCount.Should().BeNull("nothing was pushed, so there is no count to state");
    }

    /// <summary>
    /// The reviewer answering in their own thread moves the boundary the reply count is taken
    /// from, so the count goes DOWN. That is news about them and not about anybody answering them,
    /// and it must never read as negative activity — or as activity at all.
    /// </summary>
    [Fact]
    public void The_reviewer_answering_in_their_own_thread_is_not_a_reply_to_them()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 2)],
            after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 3, commitsAfter: 3,
            reReviewBefore: false, reReviewAfter: false);

        activity.Any.Should().BeFalse();
        activity.ReplyCount.Should().Be(0);
    }

    [Fact]
    public void A_thread_that_vanished_is_an_absence_rather_than_a_reply()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 2), Thread("t2", 1)],
            after: [Thread("t1", 2)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 3, commitsAfter: 3,
            reReviewBefore: false, reReviewAfter: false);

        activity.Any.Should().BeFalse();
    }

    [Fact]
    public void A_moved_head_with_both_counts_reports_how_many_commits_arrived()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0)],
            after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: "bbb",
            commitsBefore: 3, commitsAfter: 5,
            reReviewBefore: false, reReviewAfter: false);

        activity.HeadMoved.Should().BeTrue();
        activity.NewCommitCount.Should().Be(2);
        activity.Describe("acme/widgets", 42, openThreadCount: 1).Should().Contain("2 new commits");
    }

    [Fact]
    public void A_force_push_that_dropped_commits_is_a_push_with_no_count_rather_than_a_negative_one()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0)],
            after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: "bbb",
            commitsBefore: 9, commitsAfter: 4,
            reReviewBefore: false, reReviewAfter: false);

        activity.HeadMoved.Should().BeTrue("the head moved, which is the observation");
        activity.NewCommitCount.Should().BeNull("its size cannot honestly be called a number of new commits");
        activity.Describe("acme/widgets", 42, openThreadCount: 1).Should().Contain("the count is not readable");
    }

    [Fact]
    public void An_unobserved_commit_count_costs_the_number_and_not_the_observation()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0)],
            after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: "bbb",
            commitsBefore: null, commitsAfter: 5,
            reReviewBefore: false, reReviewAfter: false);

        activity.HeadMoved.Should().BeTrue();
        activity.NewCommitCount.Should().BeNull();
    }

    [Fact]
    public void An_unobserved_head_on_either_side_is_not_read_as_a_push()
    {
        PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0)], after: [Thread("t1", 0)],
            headBefore: null, headAfter: "bbb", commitsBefore: 1, commitsAfter: 2,
            reReviewBefore: false, reReviewAfter: false)
            .HeadMoved.Should().BeFalse("an unknown starting head cannot be compared against anything");

        PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0)], after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: null, commitsBefore: 1, commitsAfter: 2,
            reReviewBefore: false, reReviewAfter: false)
            .HeadMoved.Should().BeFalse("neither can an unknown current one");
    }

    /// <summary>
    /// The line names what was observed and no more: a comment the reviewer did not write arrived
    /// in a thread of theirs. Who wrote it is not in these counts — the pull request's author
    /// ordinarily, a teammate or a bot just as legitimately — so the line does not say
    /// (AGENTS.md: never guess at unobserved facts; independent pre-PR review, cycle 1,
    /// conformance lens).
    /// </summary>
    [Fact]
    public void The_line_names_the_pull_request_what_changed_and_what_is_still_open()
    {
        string line = new PrReviewAuthorActivity(
                ReplyCount: 5, ThreadsWithReplies: 5, NewCommitCount: 3, HeadMoved: true,
                ReReviewNewlyRequested: false)
            .Describe("AgelessRx/arx-platform", 2023, openThreadCount: 5);

        line.Should().Be(
            "AgelessRx/arx-platform#2023 moved since your review: 5 replies in 5 threads and "
            + "3 new commits — 5 of your threads are still unresolved.");
        line.Should().NotContain(
            "author", "nothing in these counts says who wrote the replies, so the line never claims to");
    }

    [Fact]
    public void The_line_says_so_when_every_thread_was_resolved_on_the_way_past()
    {
        string line = new PrReviewAuthorActivity(
                ReplyCount: 1, ThreadsWithReplies: 1, NewCommitCount: null, HeadMoved: false,
                ReReviewNewlyRequested: false)
            .Describe("acme/widgets", 42, openThreadCount: 0);

        line.Should().Contain("1 reply in 1 thread")
            .And.Contain("every thread you opened is resolved now");
    }

    /// <summary>
    /// The standoff the first cut left (independent pre-PR review, cycle 1, adversarial lens): the
    /// author resolves every one of the reviewer's threads themselves, says nothing, pushes
    /// nothing, and clicks re-request review. That is the one case where the author has explicitly
    /// asked the reviewer to come back, and it read as "waiting on its author" — each of them
    /// waiting on the other until a human happened to read the phase line.
    /// </summary>
    [Fact]
    public void A_re_review_request_alone_is_an_explicit_ask_of_the_reviewer_and_wakes_them()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0, resolved: false)],
            after: [Thread("t1", 0, resolved: true)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 3, commitsAfter: 3,
            reReviewBefore: false, reReviewAfter: true);

        activity.Any.Should().BeTrue(
            "an author who resolves the threads themselves and re-requests review has said 'your "
            + "turn' as plainly as any reply");
        activity.ReReviewNewlyRequested.Should().BeTrue();
        activity.ReplyCount.Should().Be(0, "nobody wrote a word");
        activity.HeadMoved.Should().BeFalse("nobody pushed anything");
        activity.Describe("acme/widgets", 42, openThreadCount: 0).Should().Be(
            "acme/widgets#42 moved since your review: a re-review requested of you — every thread "
            + "you opened is resolved now.");
    }

    /// <summary>
    /// Only the transition wakes them. A request the previous observation already recorded has
    /// been baselined, so a pull request whose author asked a week ago and has gone quiet since
    /// does not re-announce itself every few minutes — the same dedup rule the reply counts live
    /// by. What the standing request still does is hold the wait open, which the follow-through
    /// sweep reads off the observation rather than from here.
    /// </summary>
    [Fact]
    public void A_re_review_request_that_was_already_standing_is_not_news_again()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0, resolved: true)],
            after: [Thread("t1", 0, resolved: true)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 3, commitsAfter: 3,
            reReviewBefore: true, reReviewAfter: true);

        activity.ReReviewNewlyRequested.Should().BeFalse();
        activity.Any.Should().BeFalse("waking the reviewer once for one ask is the whole rule");
    }

    /// <summary>
    /// A request WITHDRAWN is not an ask either — the author removed the reviewer from the
    /// requested list, which is news about the pull request but nothing the reviewer is being
    /// asked to do.
    /// </summary>
    [Fact]
    public void A_re_review_request_that_was_withdrawn_is_not_an_ask()
    {
        PrReviewAuthorActivity activity = PrReviewAuthorActivity.Between(
            before: [Thread("t1", 0)],
            after: [Thread("t1", 0)],
            headBefore: "aaa", headAfter: "aaa",
            commitsBefore: 3, commitsAfter: 3,
            reReviewBefore: true, reReviewAfter: false);

        activity.ReReviewNewlyRequested.Should().BeFalse();
        activity.Any.Should().BeFalse();
    }

    /// <summary>
    /// All three at once — the ordinary re-review round: the author answers the threads, pushes
    /// the fixes, and asks the reviewer back. One line, three clauses, and no string of "and"s.
    /// </summary>
    [Fact]
    public void The_line_reads_as_a_list_when_replies_a_push_and_a_re_review_request_arrive_together()
    {
        string line = new PrReviewAuthorActivity(
                ReplyCount: 2, ThreadsWithReplies: 2, NewCommitCount: 4, HeadMoved: true,
                ReReviewNewlyRequested: true)
            .Describe("acme/widgets", 42, openThreadCount: 2);

        line.Should().Be(
            "acme/widgets#42 moved since your review: 2 replies in 2 threads, 4 new commits and "
            + "a re-review requested of you — 2 of your threads are still unresolved.");
    }
}
