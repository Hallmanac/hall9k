using FluentAssertions;
using Hall9k.Connectors.WorkItems;
using Hall9k.Tests.Fakes;
using Xunit;

namespace Hall9k.Tests.Connectors;

/// <summary>
/// The reading half of the review-conversation seam (task: a pr-review task stays open while the
/// pull request's review threads are unresolved), against provider payloads rather than only in
/// production: whose thread is whose, what an absent field is reported as, and what a capped page
/// says about itself.
/// </summary>
public sealed class GitHubReviewThreadsTests
{
    /// <summary>
    /// The GraphQL envelope every test's own pull-request body sits inside, so each test states
    /// only the fields it is about.
    /// </summary>
    private static string Payload(string pullRequestBody) =>
        "{\"data\":{\"repository\":{\"pullRequest\":{" + pullRequestBody + "}}}}";

    [Fact]
    public void A_threads_opener_is_who_it_belongs_to()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "commits":{"totalCount":4},
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"path":"src/One.cs","line":12,
               "comments":{"totalCount":2,"nodes":[
                 {"author":{"login":"brian"},"body":"this needs a look","createdAt":"2026-09-07T13:20:00Z"},
                 {"author":{"login":"Fanzoo"},"body":"fixed in 9a1","createdAt":"2026-09-08T12:06:00Z"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.IsOpen.Should().BeTrue();
        conversation.HeadSha.Should().Be("abc123");
        conversation.CommitCount.Should().Be(4);
        conversation.ThreadsTruncated.Should().BeFalse();
        conversation.ThreadsStartedBy("BRIAN").Should().HaveCount(
            1, "a login is matched case-insensitively, the way GitHub itself treats one");
        conversation.ThreadsStartedBy("Fanzoo").Should().BeEmpty(
            "answering a thread does not make it yours");
        ReviewThread thread = conversation.Threads.Single();
        thread.Location().Should().Be("src/One.cs:12");
        thread.CommentCount.Should().Be(2);
        thread.Comments.Last().Body.Should().Be("fixed in 9a1");
    }

    [Fact]
    public void A_capped_comment_page_still_reports_the_threads_real_size()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "commits":{"totalCount":1},
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"path":"src/One.cs","line":1,
               "comments":{"totalCount":140,"nodes":[
                 {"author":{"login":"brian"},"body":"one","createdAt":"2026-09-07T13:20:00Z"}]}}
            ],"pageInfo":{"hasNextPage":true}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Single().CommentCount.Should().Be(
            140, "the provider's own total, so a reply past the page cap still registers as movement");
        conversation.Threads.Single().Comments.Should().HaveCount(1, "only what fit is carried");
        conversation.ThreadsTruncated.Should().BeTrue("a count off a capped page is a floor, and says so");
    }

    [Fact]
    public void A_thread_with_no_total_count_degrades_to_what_was_actually_read()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,
               "comments":{"nodes":[
                 {"author":{"login":"brian"},"body":"one"},
                 {"author":{"login":"fanzoo"},"body":"two"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Single().CommentCount.Should().Be(
            2, "an undercount costs a poll a notification it would otherwise send, never a false one");
        conversation.CommitCount.Should().BeNull("a payload with no commit count reports none");
        conversation.Threads.Single().Location().Should().Be(
            "(no file reported)", "a thread with no path is not given a plausible-looking one");
    }

    [Fact]
    public void A_thread_with_no_id_is_skipped_rather_than_given_a_fabricated_key()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":null,"isResolved":false,"comments":{"totalCount":1,"nodes":[
                 {"author":{"login":"brian"},"body":"one"}]}},
              {"id":"T2","isResolved":true,"comments":{"totalCount":1,"nodes":[
                 {"author":{"login":"brian"},"body":"two"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Should().HaveCount(1);
        conversation.Threads.Single().Id.Should().Be(
            "T2", "a fabricated key would collapse every malformed thread onto one identity in the watermark");
    }

    [Fact]
    public void An_authorless_comment_carries_no_login_rather_than_somebody_elses()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"comments":{"totalCount":1,"nodes":[
                 {"author":null,"body":"from a deleted account"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Single().StartedByLogin.Should().BeNull();
        conversation.ThreadsStartedBy("brian").Should().BeEmpty(
            "an unknown opener belongs to nobody, so it holds nobody's review open");
    }

    [Fact]
    public void An_outstanding_request_is_matched_by_login_and_a_team_by_its_prefixed_slug()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[
              {"id":"R1","state":"COMMENTED","submittedAt":"2026-09-07T13:20:00Z","author":{"login":"brian"},"commit":{"oid":"abc"}}
            ],"pageInfo":{"hasPreviousPage":false}},
            "reviewRequests":{"nodes":[
              {"requestedReviewer":{"__typename":"User","login":"brian"}},
              {"requestedReviewer":{"__typename":"Team","slug":"platform"}}]}
            """));

        conversation.ReviewRequestedOf("Brian").Should().BeTrue();
        conversation.ReReviewRequestedOf("Brian").Should().BeTrue("brian has reviewed and a request stands again");
        conversation.ReReviewRequestedOf("platform").Should().BeFalse(
            "a team is recorded by its prefixed slug, which cannot collide with a personal login");
        conversation.OutstandingReviewerLogins.Should().Contain("team:platform");
    }

    [Fact]
    public void Reviews_are_folded_per_author_to_the_latest_and_recorded_in_full()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[
              {"id":"R2","state":"APPROVED","submittedAt":"2026-09-09T10:00:00Z","author":{"login":"Brian"},"commit":{"oid":"ccc"}},
              {"id":"R1","state":"CHANGES_REQUESTED","submittedAt":"2026-09-07T10:00:00Z","author":{"login":"brian"},"commit":{"oid":"aaa"}},
              {"id":"R3","state":"COMMENTED","submittedAt":"2026-09-08T10:00:00Z","author":null,"commit":{"oid":"bbb"}},
              {"id":"R4","state":"DISMISSED","submittedAt":"2026-09-08T11:00:00Z","author":{"login":"ryan"},"commit":null}
            ],"pageInfo":{"hasPreviousPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.ReviewsTruncated.Should().BeFalse();
        conversation.LatestReviewByLogin.Should().HaveCount(2, "a review with no author is skipped, not attributed");
        SubmittedReview brians = conversation.LatestReviewByLogin["BRIAN"];
        brians.Id.Should().Be("R2", "the latest by submittedAt wins whatever order the page lists them in, and logins fold case-insensitively");
        brians.State.Should().Be("APPROVED");
        brians.SubmittedAt.Should().Be(DateTimeOffset.Parse("2026-09-09T10:00:00Z"));
        brians.CommitOid.Should().Be("ccc");
        conversation.LatestReviewByLogin["ryan"].State.Should().Be("DISMISSED");
        conversation.LatestReviewByLogin["ryan"].CommitOid.Should().BeNull("an absent commit is recorded as unknown");
    }

    [Fact]
    public void A_pending_review_is_nobodys_submitted_review()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[
              {"id":"R1","state":"PENDING","submittedAt":null,"author":{"login":"brian"},"commit":{"oid":"aaa"}}
            ],"pageInfo":{"hasPreviousPage":false}},
            "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"User","login":"brian"}}]}
            """));

        conversation.LatestReviewByLogin.Should().BeEmpty();
        conversation.ReReviewRequestedOf("brian").Should().BeFalse("a draft is not a review, so the standing request is their first");
    }

    [Fact]
    public void A_truncated_review_page_counts_an_absent_requested_login_as_having_reviewed()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[
              {"id":"R9","state":"COMMENTED","submittedAt":"2026-09-09T10:00:00Z","author":{"login":"someone-else"},"commit":{"oid":"aaa"}}
            ],"pageInfo":{"hasPreviousPage":true}},
            "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"User","login":"brian"}}]}
            """));

        conversation.ReviewsTruncated.Should().BeTrue();
        conversation.ReReviewRequestedOf("brian").Should().BeTrue(
            "the missing review may be older than the page, and a false wake is cheaper than a missed re-review");
        conversation.ReReviewRequestedOf("nobody-asked").Should().BeFalse("no request stands on them");
    }

    [Fact]
    public void A_standing_request_with_no_review_is_a_first_request_not_a_re_review()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[],"pageInfo":{"hasPreviousPage":false}},
            "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"User","login":"brian"}}]}
            """));

        conversation.ReviewRequestedOf("brian").Should().BeTrue();
        conversation.ReReviewRequestedOf("brian").Should().BeFalse();
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("CHANGES_REQUESTED")]
    [InlineData("COMMENTED")]
    [InlineData("DISMISSED")]
    public void A_request_after_any_submitted_review_including_a_dismissed_one_is_a_re_review(string reviewState)
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[
              {"id":"R1","state":"STATE_HERE","submittedAt":"2026-09-07T10:00:00Z","author":{"login":"brian"},"commit":{"oid":"aaa"}}
            ],"pageInfo":{"hasPreviousPage":false}},
            "reviewRequests":{"nodes":[{"requestedReviewer":{"__typename":"User","login":"brian"}}]}
            """.Replace("STATE_HERE", reviewState, StringComparison.Ordinal)));

        conversation.ReReviewRequestedOf("brian").Should().BeTrue();
    }

    [Fact]
    public async Task The_read_asks_for_the_newest_hundred_non_pending_reviews()
    {
        RecordingProcessRunner runner = RecordingProcessRunner.Succeeding(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviews":{"nodes":[],"pageInfo":{"hasPreviousPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        await new GitHubReviewThreads(runner.Runner).ReadAsync("acme/widgets", 42, "/tmp", CancellationToken.None);

        string query = runner.Calls.Single().Arguments.Single(argument => argument.StartsWith("query=", StringComparison.Ordinal));
        query.Should().Contain("reviews(last: 100, states: [APPROVED, CHANGES_REQUESTED, COMMENTED, DISMISSED])")
            .And.Contain("hasPreviousPage")
            .And.NotContain("PENDING");
    }

    /// <summary>
    /// What "answered" is actually observed as (independent pre-PR review, cycle 1, both lenses):
    /// the comments after the reviewer's own last word in the thread. Their own comments are never
    /// replies to them — counting them reported a posted review back to its author as news, and
    /// turned a follow-up comment of the reviewer's own into a needs-you claiming somebody had
    /// answered.
    /// </summary>
    [Fact]
    public void A_threads_replies_are_the_comments_after_the_reviewers_own_last_word()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"comments":{"totalCount":4,"nodes":[
                 {"author":{"login":"brian"},"body":"this needs a look"},
                 {"author":{"login":"fanzoo"},"body":"fixed in 9a1"},
                 {"author":{"login":"BRIAN"},"body":"not quite — see the second call"},
                 {"author":{"login":"fanzoo"},"body":"now it is"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        ReviewThread thread = conversation.Threads.Single();
        thread.FirstReplyIndexFor("brian").Should().Be(
            3, "their own second comment is where what is waiting on them starts, matched case-insensitively");
        thread.ReplyCountFor("brian").Should().Be(1, "one comment landed after their last word");
        thread.ReplyCountFor("fanzoo").Should().Be(
            0, "everything in the thread is at or before the author's own last comment");
    }

    [Fact]
    public void A_thread_carrying_only_the_reviewers_own_comment_is_waiting_on_nobody()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"comments":{"totalCount":1,"nodes":[
                 {"author":{"login":"brian"},"body":"this needs a look"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Single().ReplyCountFor("brian").Should().Be(
            0, "a posted review must never read as its own answer");
    }

    /// <summary>
    /// The tail a capped comment page could not carry sits after every comment that WAS read, so
    /// it counts as replies. In the one case that overstates — the reviewer's own last comment
    /// being in the unread tail — the count is a ceiling, which costs a notification nobody needed
    /// rather than silently missing one.
    /// </summary>
    [Fact]
    public void The_comments_a_capped_page_could_not_carry_still_count_as_waiting()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"comments":{"totalCount":140,"nodes":[
                 {"author":{"login":"brian"},"body":"one"},
                 {"author":{"login":"fanzoo"},"body":"two"}]}}
            ],"pageInfo":{"hasNextPage":true}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Single().ReplyCountFor("brian").Should().Be(
            139, "1 read reply plus the 138 the page could not carry");
        conversation.Threads.Single().UnreadCommentCount.Should().Be(
            138,
            "and the size of that tail is named, so a reader showing the comments themselves can say what "
            + "it could not show instead of presenting a short read as a whole thread");
    }

    [Fact]
    public void An_authorless_comment_is_never_read_as_the_reviewers_own()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"OPEN","merged":false,"closed":false,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[
              {"id":"T1","isResolved":false,"comments":{"totalCount":2,"nodes":[
                 {"author":{"login":"brian"},"body":"this needs a look"},
                 {"author":null,"body":"from a deleted account"}]}}
            ],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.Threads.Single().ReplyCountFor("brian").Should().Be(
            1, "an unknown author is not given the reviewer's login, so the comment stays a reply to them");
    }

    [Fact]
    public void A_merged_pull_request_reports_itself_as_neither_open_nor_unmerged()
    {
        ReviewConversation conversation = GitHubReviewThreads.Parse(Payload("""
            "state":"MERGED","merged":true,"closed":true,"headRefOid":"abc123",
            "reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}},
            "reviewRequests":{"nodes":[]}
            """));

        conversation.IsOpen.Should().BeFalse();
        conversation.IsMerged.Should().BeTrue();
        conversation.IsClosed.Should().BeTrue();
    }
}
