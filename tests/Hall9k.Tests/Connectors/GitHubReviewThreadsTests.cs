using FluentAssertions;
using Hall9k.Connectors.WorkItems;
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
            "reviewRequests":{"nodes":[
              {"requestedReviewer":{"__typename":"User","login":"brian"}},
              {"requestedReviewer":{"__typename":"Team","slug":"platform"}}]}
            """));

        conversation.ReReviewRequestedOf("Brian").Should().BeTrue();
        conversation.ReReviewRequestedOf("platform").Should().BeFalse(
            "a team is recorded by its prefixed slug, which cannot collide with a personal login");
        conversation.OutstandingReviewerLogins.Should().Contain("team:platform");
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
