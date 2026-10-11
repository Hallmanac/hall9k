using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Run;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// What the inspector reads for the membership gate (task: a changes-requested review or an
/// unresolved review thread from someone outside the project's trust-chain roster never dispatches a
/// build lap): every author's numeric account id, the repository's own visibility from the same call,
/// every standing changes-requested review whatever commit it sits on, and who wrote each comment in
/// each unresolved thread. Read against provider payloads, never GitHub.
/// </summary>
public sealed class GitHubPullRequestInspectorMembershipTests
{
    private const string ReviewUrl = "https://github.com/x/y/pull/7#pullrequestreview-42";

    private static string Payload(
        string standingReviews = "", string threads = "", string repositoryFields = "'isPrivate':false,", string head = "head-1") =>
        ("{'data':{'repository':{" + repositoryFields + "'pullRequest':{"
            + "'author':{'login':'owner','__typename':'User','databaseId':1},"
            + $"'headRefOid':'{head}',"
            + $"'reviewThreads':{{'nodes':[{threads}]}},"
            + "'reviewRequests':{'nodes':[]},'timelineItems':{'nodes':[]},'latestReviews':{'nodes':[]},"
            + $"'standingReviews':{{'nodes':[{standingReviews}]}}"
            + "}}}}").Replace('\'', '"');

    private static string Author(string login, string typeName, long? databaseId) =>
        $"{{'login':'{login}','__typename':'{typeName}'"
        + (databaseId is { } id ? $",'databaseId':{id}" : "")
        + "}";

    private static string ChangesRequested(
        string author, string oid = "head-1", string id = "PRR_42", string url = ReviewUrl, string body = "Please fix.") =>
        $"{{'id':'{id}','author':{author},'body':'{body}','url':'{url}','state':'CHANGES_REQUESTED',"
        + $"'submittedAt':'2026-10-11T12:00:00Z','commit':{{'oid':'{oid}'}}}}";

    private static string Thread(string id, params string[] comments) =>
        $"{{'id':'{id}','isResolved':false,'comments':{{'nodes':[{string.Join(",", comments)}]}}}}";

    private static string Comment(string id, string author, string url = "https://x/y/pull/7#discussion_r1") =>
        $"{{'id':'{id}','author':{author},'url':'{url}','body':'text'}}";

    [Fact]
    public void A_reviews_author_account_id_rides_the_review_for_the_gate_to_match()
    {
        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(
            Payload(standingReviews: ChangesRequested(Author("teammate", "User", 4242))));

        observation.ChangesRequestedReviews.Should().ContainSingle()
            .Which.ReviewerAccountId.Should().Be(4242);
    }

    [Fact]
    public void A_reviewer_the_provider_gave_no_id_for_reads_as_no_account_id_never_as_zero()
    {
        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(
            Payload(standingReviews: ChangesRequested(Author("teammate", "User", null))));

        observation.ChangesRequestedReviews.Should().ContainSingle()
            .Which.ReviewerAccountId.Should().BeNull();
    }

    [Fact]
    public void Every_standing_changes_requested_review_is_read_whatever_commit_it_sits_on()
    {
        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(
            Payload(standingReviews: ChangesRequested(Author("teammate", "User", 4242), oid: "an-older-head")));

        observation.ChangesRequestedReviews.Should().BeEmpty("the ordinary flow still reads only a review on the head");
        observation.StandingChangesRequestedReviews.Should().ContainSingle()
            .Which.ReviewUrl.Should().Be(ReviewUrl);
    }

    [Fact]
    public void A_changes_requested_review_by_a_deleted_account_is_read_as_a_ghost_with_no_account_id()
    {
        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(
            Payload(standingReviews: ChangesRequested("null")));

        observation.ChangesRequestedReviews.Should().BeEmpty(
            "nothing is waiting on a person who is gone, so the head-bound read never dispatched for one");
        ChangesRequestedReview ghost = observation.StandingChangesRequestedReviews.Should().ContainSingle().Subject;
        ghost.Reviewer.Should().Be("ghost");
        ghost.ReviewerAccountId.Should().BeNull();
    }

    [Fact]
    public void A_bots_changes_requested_review_is_still_never_one_of_these()
    {
        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(
            Payload(standingReviews: ChangesRequested(Author("copilot-pull-request-reviewer", "Bot", 9001))));

        observation.StandingChangesRequestedReviews.Should().BeEmpty();
    }

    [Fact]
    public void The_repositorys_visibility_comes_from_the_same_call()
    {
        GitHubPullRequestInspector.ParseReviews(Payload(repositoryFields: "'isPrivate':true,"))
            .RepositoryIsPrivate.Should().BeTrue();
        GitHubPullRequestInspector.ParseReviews(Payload(repositoryFields: "'isPrivate':false,"))
            .RepositoryIsPrivate.Should().BeFalse();
        GitHubPullRequestInspector.ParseReviews(Payload(repositoryFields: ""))
            .RepositoryIsPrivate.Should().BeNull("an unreported visibility is unknown, which the gate reads as public");
    }

    [Fact]
    public void Every_comment_in_an_unresolved_thread_is_attributed_to_an_account_the_opener_first()
    {
        string threads = Thread(
            "PRRT_1",
            Comment("PRRC_1", Author("member", "User", 1001)),
            Comment("PRRC_2", Author("stranger", "User", 2001)),
            Comment("PRRC_3", "null"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(Payload(threads: threads));

        UnresolvedReviewThread thread = observation.UnresolvedThreadDetails.Should().ContainSingle().Subject;
        thread.ThreadId.Should().Be("PRRT_1");
        thread.Opener!.Author!.AccountId.Should().Be(1001);
        thread.Comments.Select(comment => comment.Id).Should().Equal("PRRC_1", "PRRC_2", "PRRC_3");
        thread.Comments[1].Author!.AccountId.Should().Be(2001);
        thread.Comments[2].Author.Should().BeNull("a deleted account is nobody the roster can name");
    }

    [Fact]
    public void A_bot_threads_comments_are_typed_as_a_bots_even_with_an_id()
    {
        string threads = Thread("PRRT_c", Comment("PRRC_1", Author("copilot-pull-request-reviewer", "Bot", 9001)));

        UnresolvedReviewThread thread = GitHubPullRequestInspector.ParseReviews(Payload(threads: threads))
            .UnresolvedThreadDetails.Should().ContainSingle().Subject;

        thread.Opener!.Author!.IsBot.Should().BeTrue();
    }

    [Fact]
    public void A_comment_the_provider_gave_no_node_id_for_is_dropped_so_it_can_never_be_named_to_a_session()
    {
        string threads = Thread("PRRT_1", "{'author':" + Author("member", "User", 1001) + ",'body':'x'}");

        GitHubPullRequestInspector.ParseReviews(Payload(threads: threads))
            .UnresolvedThreadDetails.Should().ContainSingle().Which.Comments.Should().BeEmpty();
    }
}
