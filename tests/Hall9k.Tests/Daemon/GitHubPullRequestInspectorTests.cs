using FluentAssertions;
using Hall9k.Daemon.Closeout;
using Hall9k.Domain.Features.Run;
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
        string reviewRequests = "", string timelineItems = "", string? mergeable = null) =>
        ("{'data':{'repository':{'pullRequest':{"
            + $"'author':{author},'headRefOid':'{headOid}',"
            + (mergeable is null ? "" : $"'mergeable':'{mergeable}',")
            + $"'reviewThreads':{{'nodes':[{threads}]}},"
            + $"'reviewRequests':{{'nodes':[{reviewRequests}]}},"
            + $"'timelineItems':{{'nodes':[{timelineItems}]}},"
            + $"'latestReviews':{{'nodes':[{reviews}]}}"
            + "}}}}").Replace('\'', '"');

    private static string Thread(bool resolved, string author, string id = "thread-1") =>
        $"{{'id':'{id}',"
            + $"'isResolved':{(resolved ? "true" : "false")},'comments':{{'nodes':[{{'author':{author}}}]}}}}";

    private static string ThreadWithNullId(bool resolved, string author) =>
        "{'id':null,"
            + $"'isResolved':{(resolved ? "true" : "false")},'comments':{{'nodes':[{{'author':{author}}}]}}}}";

    private static string Actor(string login, string typeName) =>
        $"{{'login':'{login}','__typename':'{typeName}'}}";

    private static string Review(string author, string oid, string body = "") =>
        $"{{'author':{author},'body':'{body}','url':'https://x/y/pull/7#r1','commit':{{'oid':'{oid}'}}}}";

    private static string RequestedReviewer(string login, string typeName) =>
        $"{{'requestedReviewer':{{'__typename':'{typeName}','login':'{login}'}}}}";

    private static string ReviewRequestedEvent(string actorLogin, string actorTypeName, string reviewerLogin, string reviewerTypeName) =>
        "{'actor':" + Actor(actorLogin, actorTypeName)
            + $",'requestedReviewer':{{'__typename':'{reviewerTypeName}','login':'{reviewerLogin}'}}}}";

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
    /// GitHub types a review thread's id as a non-null ID; a null here is a malformed payload.
    /// Skipping it rather than coalescing to "" keeps a genuinely missing id from fabricating a
    /// key that would collapse every such thread onto the same obstruction identity and
    /// human-engagement diff (PR #37 review).
    /// </summary>
    [Fact]
    public void A_thread_with_no_id_is_skipped_rather_than_given_a_fabricated_key()
    {
        string json = Payload(
            Actor("hallmanac", "User"),
            "cafe1",
            string.Join(",",
                ThreadWithNullId(resolved: false, Actor("teammate", "User")),
                Thread(resolved: false, Actor("teammate", "User"), "thread-real")),
            "");

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.UnresolvedThreads.Should().Be(
            1, "the malformed thread is skipped rather than counted under a fabricated key");
        observation.UnresolvedThreadIds.Should().Equal("thread-real");
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
    /// Pending review requests are read by requestable actor type only (Decisions Log #80,
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
                "{'requestedReviewer':{'__typename':'Team'}}"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.PendingReviewRequestLogins.Should().Equal("teammate");
    }

    /// <summary>
    /// A bot's own pending review request is not a human's re-request, whatever surfaced it —
    /// GitHub's "review new commits automatically" setting recreates Copilot's request on
    /// every push with nobody touching the UI. Excluded by __typename Bot, with the known
    /// Copilot logins as a fallback for the cases the unified Copilot app has surfaced under
    /// User instead (adversarial pre-PR review, 2026-08-24): granting a closeout lap for
    /// either would let the monitor's own follow-up push manufacture the human-engagement
    /// signal that grants its next lap past the per-obstruction cap.
    /// </summary>
    [Fact]
    public void Bot_pending_review_requests_are_excluded_even_when_typed_as_user()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            reviewRequests: string.Join(",",
                RequestedReviewer("copilot-pull-request-reviewer", "Bot"),
                RequestedReviewer("copilot-pull-request-reviewer", "User"),
                RequestedReviewer("teammate", "User")));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.PendingReviewRequestLogins.Should().Equal("teammate");
    }

    /// <summary>
    /// The blanket bot exclusion above would also swallow the one event it exists to catch:
    /// a human clicking "Re-request review" on Copilot through GitHub's own UI (origin
    /// incident, PR 26, 2026-08-22). <c>reviewRequests</c> alone cannot tell that apart from
    /// GitHub's automatic recreation, because it reports only who is currently requested,
    /// never who asked — the review-request timeline's actor is the discriminator
    /// (adversarial pre-PR review, cycle 2).
    /// </summary>
    [Fact]
    public void A_human_re_requesting_Copilot_through_the_timeline_is_observed()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            reviewRequests: RequestedReviewer("copilot-pull-request-reviewer", "Bot"),
            timelineItems: ReviewRequestedEvent("hallmanac", "User", "copilot-pull-request-reviewer", "Bot"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.PendingReviewRequestLogins.Should().Equal("copilot-pull-request-reviewer");
    }

    /// <summary>
    /// The same request, but the timeline's most recent ask was made by the Bot actor GitHub's
    /// "review new commits automatically" setting recreates it under — still excluded, the
    /// conservative default this discriminator falls back to whenever the requester cannot be
    /// shown to be human.
    /// </summary>
    [Fact]
    public void Copilot_re_requested_by_a_non_human_actor_stays_excluded()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            reviewRequests: RequestedReviewer("copilot-pull-request-reviewer", "Bot"),
            timelineItems: ReviewRequestedEvent("github-actions", "Bot", "copilot-pull-request-reviewer", "Bot"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.PendingReviewRequestLogins.Should().BeEmpty();
    }

    /// <summary>
    /// Only the LAST timeline event per reviewer matters: an earlier human ask superseded by a
    /// later automatic recreation (a subsequent push) must not read as the human's request
    /// still standing.
    /// </summary>
    [Fact]
    public void Only_the_most_recent_request_for_a_reviewer_is_consulted()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            reviewRequests: RequestedReviewer("copilot-pull-request-reviewer", "Bot"),
            timelineItems: string.Join(",",
                ReviewRequestedEvent("hallmanac", "User", "copilot-pull-request-reviewer", "Bot"),
                ReviewRequestedEvent("github-actions", "Bot", "copilot-pull-request-reviewer", "Bot")));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.PendingReviewRequestLogins.Should().BeEmpty("the automatic re-request is the more recent one");
    }

    /// <summary>
    /// GitHub's own mergeable read, observed exactly as reported (backlog 44) — never inferred
    /// from anything else the payload carries.
    /// </summary>
    [Fact]
    public void A_conflicting_mergeable_state_is_observed()
    {
        string json = Payload(Actor("hallmanac", "User"), "cafe1", "", "", mergeable: "CONFLICTING");

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.IsConflicting.Should().BeTrue();
    }

    [Fact]
    public void A_mergeable_state_is_not_read_as_conflicting()
    {
        string json = Payload(Actor("hallmanac", "User"), "cafe1", "", "", mergeable: "MERGEABLE");

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.IsConflicting.Should().BeFalse();
    }

    /// <summary>
    /// UNKNOWN means the provider has not finished computing it yet (a very recent push) — read
    /// as "not observed as conflicting" rather than guessed either way; the next sweep asks
    /// again. A payload predating this field (mergeable absent entirely) reads the same way.
    /// </summary>
    [Fact]
    public void An_unresolved_or_absent_mergeable_state_is_not_read_as_conflicting()
    {
        string unknown = Payload(Actor("hallmanac", "User"), "cafe1", "", "", mergeable: "UNKNOWN");
        string absent = Payload(Actor("hallmanac", "User"), "cafe1", "", "");

        GitHubPullRequestInspector.ParseReviews(unknown).IsConflicting.Should().BeFalse();
        GitHubPullRequestInspector.ParseReviews(absent).IsConflicting.Should().BeFalse();
    }

    /// <summary>
    /// The post-PR review watcher's own read (origin: PR #50 sat Delivered for 23 minutes with
    /// a landed Copilot review nobody had read before the merge): a real, non-errored Copilot
    /// review on the pull request is "landed", whatever else it also carries.
    /// </summary>
    [Fact]
    public void A_landed_Copilot_review_is_observed()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "",
            Review(Actor("copilot-pull-request-reviewer", "Bot"), "cafe1", "Looks good."));

        GitHubPullRequestInspector.ParseReviews(json).CopilotReviewState.Should().Be(ExternalReviewState.Landed);
    }

    /// <summary>
    /// Read raw, deliberately unlike <see cref="ReadPendingReviewRequestLogins"/>'s human-
    /// engagement filter: whether Copilot is currently outstanding at all — including GitHub's
    /// own "review new commits automatically" recreating the request — is what "awaiting
    /// Copilot review" needs to say, even though the same request does not count as a human's
    /// re-request for the closeout budget's own purposes.
    /// </summary>
    [Fact]
    public void A_pending_Copilot_review_request_is_observed_even_without_a_human_re_request()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "", "",
            reviewRequests: RequestedReviewer("copilot-pull-request-reviewer", "Bot"));

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.CopilotReviewState.Should().Be(ExternalReviewState.RequestedPending);
        observation.PendingReviewRequestLogins.Should().BeEmpty(
            "the human-engagement signal still excludes Copilot's own automatic re-request");
    }

    /// <summary>
    /// A Copilot review left on a commit that is no longer the head does not read as landed
    /// (origin: a countersign re-request after a fix push recreates Copilot's review request
    /// while its earlier review of the pre-fix commit still sits in <c>latestReviews</c> — the
    /// same staleness <see cref="ReadReviewedCommit"/> already tracks for the countersign, PR
    /// review, 2026-08-26). Without this check a stale review pinned the state to "landed" for
    /// the whole re-review window, masking that Copilot has not read the current head.
    /// </summary>
    [Fact]
    public void A_stale_Copilot_review_falls_through_to_the_pending_request()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe2", "",
            Review(Actor("copilot-pull-request-reviewer", "Bot"), "cafe1", "Looks good."),
            reviewRequests: RequestedReviewer("copilot-pull-request-reviewer", "Bot"));

        GitHubPullRequestInspector.ParseReviews(json).CopilotReviewState.Should().Be(
            ExternalReviewState.RequestedPending,
            "Copilot's review of the pre-fix commit does not speak for the current head");
    }

    /// <summary>
    /// A stale review with no pending request behind it is not outstanding either — nothing
    /// currently asks Copilot for anything, so this reads as no external review activity rather
    /// than a landed review that no longer applies.
    /// </summary>
    [Fact]
    public void A_stale_Copilot_review_with_no_pending_request_reads_as_none()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe2", "",
            Review(Actor("copilot-pull-request-reviewer", "Bot"), "cafe1", "Looks good."));

        GitHubPullRequestInspector.ParseReviews(json).CopilotReviewState.Should().Be(ExternalReviewState.None);
    }

    [Fact]
    public void No_Copilot_review_activity_reads_as_none()
    {
        string json = Payload(Actor("hallmanac", "User"), "cafe1", "", "");

        GitHubPullRequestInspector.ParseReviews(json).CopilotReviewState.Should().Be(ExternalReviewState.None);
    }

    /// <summary>
    /// An error placeholder (Decisions Log #62, origin: PR #6, 2026-08-17) produced no verdict,
    /// so it must not read as Copilot having weighed in — the same conservatism
    /// <see cref="FindErroredCopilotReview"/> already applies to the errored-review re-request.
    /// </summary>
    [Fact]
    public void An_errored_Copilot_review_does_not_read_as_landed()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1", "",
            Review(Actor("copilot-pull-request-reviewer", "Bot"), "cafe1",
                "Copilot encountered an error and was unable to review this pull request."));

        GitHubPullRequestInspector.ParseReviews(json).CopilotReviewState.Should().NotBe(
            ExternalReviewState.Landed, "a review that could not read the diff produced no verdict");
    }

    /// <summary>
    /// "Landed with its comment-thread count" names every thread the review opened, not only
    /// the ones still unresolved — distinct from <see cref="ReviewObservation.UnresolvedThreads"/>,
    /// which only ever renders once a finding has moved the run off AwaitingReview.
    /// </summary>
    [Fact]
    public void Copilot_thread_count_includes_resolved_threads_it_started()
    {
        string json = Payload(
            Actor("hallmanac", "User"), "cafe1",
            string.Join(",",
                Thread(resolved: true, Actor("copilot-pull-request-reviewer", "Bot"), "thread-1"),
                Thread(resolved: false, Actor("copilot-pull-request-reviewer", "Bot"), "thread-2"),
                Thread(resolved: false, Actor("teammate", "User"), "thread-3")),
            "");

        GitHubPullRequestInspector.ReviewObservation observation = GitHubPullRequestInspector.ParseReviews(json);

        observation.CopilotReviewThreadCount.Should().Be(2, "both Copilot threads count, resolved or not");
        observation.UnresolvedThreads.Should().Be(2, "one of Copilot's and the teammate's are still open");
    }
}
