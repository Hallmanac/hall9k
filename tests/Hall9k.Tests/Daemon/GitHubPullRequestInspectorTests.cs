using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The reading half of the closeout inspection, against provider payloads. Who counts as a
/// person, who can be asked for a review, and which reviews still predate the head are all
/// decisions made here from GraphQL's own actor types, and getting any of them wrong is
/// invisible in production until it inflates a count or loops a re-request (Decisions Log #62).
/// </summary>
public sealed class GitHubPullRequestInspectorTests
{
    // The payloads are written with single quotes and converted, which keeps a GraphQL
    // response readable in C# source; JSON has no apostrophes to lose to it.
    private static string Payload(
        string author, string headOid, string threads, string reviews,
        string comments = "", string reviewRequests = "") =>
        ("{'data':{'repository':{'pullRequest':{"
            + $"'author':{author},'headRefOid':'{headOid}',"
            + $"'reviewThreads':{{'nodes':[{threads}]}},"
            + $"'comments':{{'nodes':[{comments}]}},"
            + $"'reviewRequests':{{'nodes':[{reviewRequests}]}},"
            + $"'latestReviews':{{'nodes':[{reviews}]}}"
            + "}}}}").Replace('\'', '"');

    private static string Thread(bool resolved, string author, string id = "thread-1") =>
        $"{{'id':'{id}',"
            + $"'isResolved':{(resolved ? "true" : "false")},'comments':{{'nodes':[{{'author':{author}}}]}}}}";

    private static string Actor(string login, string typeName) =>
        $"{{'login':'{login}','__typename':'{typeName}'}}";

    private static string Review(string author, string oid, string body = "") =>
        $"{{'author':{author},'body':'{body}','url':'https://x/y/pull/7#r1','commit':{{'oid':'{oid}'}}}}";

    private static string Comment(string id, string author) =>
        $"{{'id':'{id}','author':{author}}}";

    private static string RequestedReviewer(string login, string typeName) =>
        $"{{'requestedReviewer':{{'__typename':'{typeName}','login':'{login}'}}}}";

    /// <summary>
    /// Every unresolved thread is feedback; only the ones a person started say somebody is
    /// waiting on an answer, which is what the follow-up's care rules key off.
    /// </summary>
    [Fact]
    public void Unresolved_threads_are_counted_whoever_started_them_and_people_are_counted_apart()
    {
        string json = Payload(
            Actor("hallmanac", "User"),
            "cafe1",
            string.Join(",",
                Thread(resolved: false, Actor("copilot-pull-request-reviewer", "Bot"), "thread-bot"),
                Thread(resolved: false, Actor("hallmanac", "User"), "thread-self-note"),
                Thread(resolved: true, Actor("teammate", "User"), "thread-resolved")),
            "");

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.UnresolvedThreads.Should().Be(2, "a resolved thread is answered; the rest are feedback");
        observation.UnresolvedHumanThreads.Should().Be(
            1, "the author's own thread is a self-note and counts as a person's (AGENTS.md invariant)");
        observation.UnresolvedThreadIds.Should().BeEquivalentTo(["thread-bot", "thread-self-note"]);
        observation.UnresolvedHumanThreadIds.Should().Equal("thread-self-note");
    }

    /// <summary>
    /// A mannequin is the placeholder GitHub leaves when an import never mapped an identity to
    /// an account. Nobody is behind one, so it is neither a person waiting on an answer nor an
    /// account a review can be requested from — counting it as either is what fed a re-request
    /// at an account the API rejects.
    /// </summary>
    [Fact]
    public void A_mannequin_is_neither_a_person_waiting_nor_an_account_that_can_be_asked()
    {
        string json = Payload(
            Actor("hallmanac", "User"),
            "cafe1",
            Thread(resolved: false, Actor("imported-reviewer", "Mannequin")),
            Review(Actor("imported-reviewer", "Mannequin"), "cafe0"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.UnresolvedThreads.Should().Be(1, "the thread is still unresolved feedback");
        observation.UnresolvedHumanThreads.Should().Be(0, "there is nobody behind a mannequin to wait");
        observation.Reviewers.Should().BeEmpty("the review-request endpoint refuses a mannequin");
    }

    /// <summary>
    /// The pull request's author is dropped because GitHub refuses a review request addressed
    /// to them, and each remaining reviewer carries the commit its latest review sits on — the
    /// comparison that keeps a countersign from asking someone who has already read the head.
    /// </summary>
    [Fact]
    public void Reviewers_exclude_the_author_and_carry_the_commit_each_review_sits_on()
    {
        string json = Payload(
            Actor("hallmanac", "User"),
            "cafe2",
            "",
            string.Join(",",
                Review(Actor("hallmanac", "User"), "cafe2"),
                Review(Actor("copilot-pull-request-reviewer", "Bot"), "cafe2"),
                Review(Actor("teammate", "User"), "cafe1")));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.HeadCommit.Should().Be("cafe2");
        observation.Reviewers.Select(reviewer => reviewer.Login).Should().Equal(
            "copilot-pull-request-reviewer", "teammate");
        observation.Reviewers[0].IsBot.Should().BeTrue("the actor type is the provider's answer, not the login string");
        observation.Reviewers[0].LastReviewedCommit.Should().Be("cafe2", "this reviewer has already read the head");
        observation.Reviewers[1].LastReviewedCommit.Should().Be("cafe1", "this one is still a commit behind");
    }

    /// <summary>
    /// A deleted account serializes as a null author. Unattributable authorship is recorded as
    /// absent rather than guessed at either way, so a ghost's thread counts as feedback but
    /// never as a person waiting, and a ghost is never asked for a review.
    /// </summary>
    [Fact]
    public void A_ghost_author_is_nobody_rather_than_a_person_or_a_bot()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", Thread(resolved: false, "null"), Review("null", "cafe0"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.UnresolvedThreads.Should().Be(1);
        observation.UnresolvedHumanThreads.Should().Be(0);
        observation.Reviewers.Should().BeEmpty();
    }

    /// <summary>
    /// A top-level pull-request comment is one of the three mechanical human-engagement
    /// signals the closeout budget grants a lap for (Decisions Log #77, backlog 45). Agents
    /// here only ever reply inside review threads, never open a bare comment, so only the
    /// human-authored ones are collected — checked by actor type, not assumed.
    /// </summary>
    [Fact]
    public void Only_human_authored_top_level_comments_are_collected()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            comments: string.Join(",",
                Comment("comment-human", Actor("hallmanac", "User")),
                Comment("comment-bot", Actor("some-bot", "Bot")),
                Comment("comment-ghost", "null")));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.HumanCommentIds.Should().Equal("comment-human");
    }

    /// <summary>
    /// Pending review requests are read by requestable actor type only (Decisions Log #77,
    /// backlog 45): a login lets a later poll compare against what was already known to spot
    /// a human's own re-request, and a team carries no such login to compare, so it is left
    /// out rather than guessed at.
    /// </summary>
    [Fact]
    public void Pending_review_requests_are_read_by_login_teams_excluded()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            reviewRequests: string.Join(",",
                RequestedReviewer("teammate", "User"),
                RequestedReviewer("copilot-pull-request-reviewer", "Bot"),
                "{'requestedReviewer':{'__typename':'Team'}}"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.PendingReviewRequestLogins.Should().Equal("teammate", "copilot-pull-request-reviewer");
    }
}
