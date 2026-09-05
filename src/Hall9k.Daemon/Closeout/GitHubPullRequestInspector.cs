using System.Diagnostics;
using System.Text.Json;
using Hall9k.Domain.Features.Run;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Reads a pull request's closeout signals through gh: state/merge via
/// gh pr view --json, unresolved review threads from every reviewer plus each reviewer's
/// latest review via the GraphQL API (the REST surface has no thread-resolution data),
/// and re-requests a review via the REST review-request endpoint. Runs in the project's
/// repository so gh resolves the repo from the origin remote.
/// <para>
/// What the API cannot show it: a review still in the PENDING state hides its comments
/// entirely, so a reviewer part-way through a draft review reads here as silence. That is
/// correct — nothing has been said until Submit review — but it is why a pull request can
/// look quiet while feedback is being written (Decisions Log #62).
/// </para>
/// </summary>
public sealed class GitHubPullRequestInspector : IPullRequestInspector
{
    // Genuine failures only. CANCELLED (usually a superseding push's concurrency group)
    // and ACTION_REQUIRED (a workflow awaiting human approval) are deliberately neither
    // failing nor pending: a fix run cannot fix either, so dispatching one would burn
    // the bounded automatic budget on a non-failure. They resolve on their own or leave
    // the PR honestly sitting AwaitingReview for a human.
    private static readonly string[] FailingCheckRunConclusions =
        ["FAILURE", "TIMED_OUT", "STARTUP_FAILURE"];

    private static readonly string[] FailingStatusContextStates = ["FAILURE", "ERROR"];

    // The errored-review matching rule, deliberately conservative (match Copilot's own
    // failure notice, never arbitrary review text): the review is authored by Copilot
    // (the login rule below), it is that reviewer's LATEST review (latestReviews is
    // per-reviewer latest, so a successful re-review supersedes an errored one
    // structurally), and its body contains this marker. Observed instance (PR #6,
    // 2026-08-17, GitHub partial outage): "Copilot encountered an error and was unable to
    // review this pull request. You can try again by re-requesting a review."
    //
    // Deliberately still Copilot-specific while thread counting is not: a human review
    // cannot error, and matching "unable to review" in a person's prose would read an
    // opinion as an outage.
    private const string ErroredReviewBodyMarker = "unable to review";

    // first: 100 is a deliberate cap, not missing pagination: threads past it read as
    // quiet, so a monster PR simply waits for a human instead of dispatching follow-ups
    // from an incomplete picture. A PR carrying 100+ review threads has left the range
    // this automation is for. reviewRequests gets a smaller cap for the same reason,
    // sized to what a closeout-relevant pull request actually carries rather than to a
    // theoretical maximum. pageInfo.hasNextPage on reviewThreads is what lets a caller tell
    // "every thread read as resolved" apart from "only the first 100 were read, and they
    // happen to be resolved" — the latter still safely waits for a human on the ordinary
    // follow-up path (nothing here merges on its own), but a pre-approved task's own merge
    // gate has no human left to fall back on, so it needs the distinction this cap's own
    // comment above did not previously expose (independent pre-PR review, cycle 1,
    // adversarial finding).
    private const string ReviewsQuery =
        """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              author { login __typename }
              headRefOid
              mergeable
              reviewDecision
              reviewThreads(first: 100) {
                nodes { id isResolved comments(first: 1) { nodes { author { login __typename } pullRequestReview { id } } } }
                pageInfo { hasNextPage }
              }
              reviewRequests(first: 20) {
                nodes { requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Team { slug } } }
              }
              timelineItems(last: 20, itemTypes: [REVIEW_REQUESTED_EVENT]) {
                nodes {
                  ... on ReviewRequestedEvent {
                    actor { login __typename }
                    requestedReviewer { __typename ... on User { login } ... on Bot { login } }
                  }
                }
              }
              latestReviews(first: 100) {
                nodes { id author { login __typename } body url commit { oid } }
              }
            }
          }
        }
        """;

    public async Task<PullRequestSnapshot> InspectAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
    {
        string viewJson = await RunGhAsync(
            repositoryPath,
            ["pr", "view", pullRequestNumber.ToString(), "--json", "state,mergedAt,closedAt,statusCheckRollup,baseRefName"],
            cancellationToken);

        using JsonDocument view = JsonDocument.Parse(viewJson);
        string state = view.RootElement.GetProperty("state").GetString() ?? "";
        DateTimeOffset? mergedAt = ReadTimestamp(view.RootElement, "mergedAt");
        DateTimeOffset? closedAt = ReadTimestamp(view.RootElement, "closedAt");
        string? baseRefName = view.RootElement.TryGetProperty("baseRefName", out JsonElement baseRefElement)
            && baseRefElement.ValueKind == JsonValueKind.String
                ? baseRefElement.GetString()
                : null;
        (IReadOnlyList<string> failing, bool pending, bool checksObserved) = ReadChecks(view.RootElement);

        // Reviews matter only while the PR is open; skip the second call otherwise.
        ReviewObservation reviews = state == "OPEN"
            ? await InspectReviewsAsync(repositoryPath, pullRequestUrl, pullRequestNumber, cancellationToken)
            : ReviewObservation.None;

        return new PullRequestSnapshot(
            IsMerged: state == "MERGED",
            IsClosed: state == "CLOSED",
            MergedAt: mergedAt,
            ClosedAt: closedAt,
            FailingChecks: failing,
            HasPendingChecks: pending,
            HasObservedChecks: checksObserved,
            UnresolvedReviewThreadCount: reviews.UnresolvedThreads,
            UnresolvedHumanThreadCount: reviews.UnresolvedHumanThreads,
            Reviewers: reviews.Reviewers,
            ErroredReview: reviews.ErroredReview,
            CopilotReviewState: reviews.CopilotReviewState,
            CopilotReviewThreadCount: reviews.CopilotReviewThreadCount,
            HeadCommit: reviews.HeadCommit,
            UnresolvedReviewThreadIds: reviews.UnresolvedThreadIds,
            UnresolvedHumanThreadIds: reviews.UnresolvedHumanThreadIds,
            PendingReviewRequestLogins: reviews.PendingReviewRequestLogins,
            IsConflicting: reviews.IsConflicting,
            BaseRefName: baseRefName,
            ReviewDecision: reviews.ReviewDecision,
            OutstandingReviewerLogins: reviews.OutstandingReviewerLogins,
            ReviewThreadsTruncated: reviews.ReviewThreadsTruncated);
    }

    /// <summary>
    /// The one-call read behind <see cref="PullRequestStateSnapshot"/>: state, mergedAt and
    /// closedAt only, no statusCheckRollup and no review GraphQL call — the orphan sweep's
    /// single caller ignores checks and reviews entirely, so gathering them here would spend
    /// a remote read this method's only caller has no use for.
    /// </summary>
    public async Task<PullRequestStateSnapshot> InspectStateAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
    {
        string viewJson = await RunGhAsync(
            repositoryPath,
            ["pr", "view", pullRequestNumber.ToString(), "--json", "state,mergedAt,closedAt"],
            cancellationToken);

        using JsonDocument view = JsonDocument.Parse(viewJson);
        string state = view.RootElement.GetProperty("state").GetString() ?? "";
        return new PullRequestStateSnapshot(
            IsMerged: state == "MERGED",
            IsClosed: state == "CLOSED",
            MergedAt: ReadTimestamp(view.RootElement, "mergedAt"),
            ClosedAt: ReadTimestamp(view.RootElement, "closedAt"));
    }

    /// <summary>
    /// Re-requests through the REST review-request endpoint. GraphQL reports the bare
    /// bot login (copilot-pull-request-reviewer); the endpoint addresses app accounts by
    /// the [bot]-suffixed form — docs.github.com: request copilot-pull-request-reviewer[bot]
    /// as a reviewer. Verified against the origin incident's own successful re-request
    /// (PR #6 timeline, 2026-08-17). A human login is sent exactly as reported: suffixing
    /// one would address an account that does not exist.
    /// </summary>
    public async Task RerequestReviewAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, PullRequestReviewer reviewer,
        CancellationToken cancellationToken)
    {
        (string owner, string name) = ParseOwnerAndRepository(pullRequestUrl);
        string login = reviewer.IsBot && !reviewer.Login.EndsWith("[bot]", StringComparison.Ordinal)
            ? $"{reviewer.Login}[bot]"
            : reviewer.Login;
        await RunGhAsync(
            repositoryPath,
            ["api", "--method", "POST",
                $"repos/{owner}/{name}/pulls/{pullRequestNumber}/requested_reviewers",
                "-f", $"reviewers[]={login}"],
            cancellationToken);
    }

    /// <summary>
    /// Rebase-merges through <c>gh pr merge --rebase</c> (design ruling 8: linear history, never a
    /// squash or a plain merge commit) — never <c>--delete-branch</c>, since the platform's own
    /// closeout cleanup (<c>IWorktreeManager.DeleteBranchEverywhereAsync</c>) owns removing the
    /// branch once the merge is observed, exactly as it does for an operator's own by-hand merge.
    /// <paramref name="expectedHeadCommit"/> is passed as <c>--match-head-commit</c> when known, so
    /// GitHub itself refuses the merge rather than this call ever landing a commit the sweep never
    /// actually inspected. Throws on any failure — a stale head mismatch, a re-evaluated required
    /// check, a transient API error — which the caller (<c>CloseoutEngine</c>) treats as one unit
    /// spent against the pre-approved task's own mechanical-resolution budget.
    /// </summary>
    public async Task MergeAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, string? expectedHeadCommit,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["pr", "merge", pullRequestNumber.ToString(), "--rebase"];
        if (expectedHeadCommit.IsNotBlank())
        {
            arguments.Add("--match-head-commit");
            arguments.Add(expectedHeadCommit);
        }

        await RunGhAsync(repositoryPath, arguments, cancellationToken);
    }

    /// <summary>What one GraphQL call saw about a pull request's reviews.</summary>
    internal sealed record ReviewObservation(
        int UnresolvedThreads,
        int UnresolvedHumanThreads,
        IReadOnlyList<PullRequestReviewer> Reviewers,
        ErroredReview? ErroredReview,
        string? HeadCommit,
        IReadOnlyList<string> UnresolvedThreadIds,
        IReadOnlyList<string> UnresolvedHumanThreadIds,
        IReadOnlyList<string> PendingReviewRequestLogins,
        ExternalReviewState CopilotReviewState,
        int CopilotReviewThreadCount,
        bool IsConflicting = false,
        string? ReviewDecision = null,
        IReadOnlyList<string>? OutstandingReviewerLogins = null,
        bool ReviewThreadsTruncated = false)
    {
        public static readonly ReviewObservation None = new(0, 0, [], null, null, [], [], [], ExternalReviewState.None, 0);
    }

    private static async Task<ReviewObservation> InspectReviewsAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
    {
        (string owner, string name) = ParseOwnerAndRepository(pullRequestUrl);
        string json = await RunGhAsync(
            repositoryPath,
            ["api", "graphql",
                "-f", $"query={ReviewsQuery}",
                "-f", $"owner={owner}",
                "-f", $"name={name}",
                "-F", $"number={pullRequestNumber}"],
            cancellationToken);

        return ParseReviews(json);
    }

    /// <summary>
    /// The reading half of the review inspection, split from the gh call so the classification
    /// rules — who counts as a person, who can be asked for a review, which reviews still
    /// predate the head — are testable against provider payloads rather than only in
    /// production. Internal for exactly that; nothing else calls it.
    /// </summary>
    internal static ReviewObservation ParseReviews(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement pullRequest = document.RootElement
            .GetProperty("data").GetProperty("repository").GetProperty("pullRequest");

        string? headCommit = ReadHeadCommit(pullRequest);
        (ExternalReviewState copilotReviewState, string? reportedCopilotReviewId) =
            ReadCopilotReviewState(pullRequest, headCommit);

        // Every unresolved thread counts, whoever started it (Decisions Log #62). The
        // starter is read only to tell a human's thread from a bot's, because the two get
        // different care in the follow-up — never to decide whether feedback exists. The id
        // sets feed two different uses (Decisions Log #80, backlog 45): the full set is the
        // closeout budget's mechanical obstruction key, and the human subset is what a later
        // poll diffs to recognize a newly opened human thread.
        JsonElement reviewThreads = pullRequest.GetProperty("reviewThreads");
        bool reviewThreadsTruncated = reviewThreads.TryGetProperty("pageInfo", out JsonElement threadsPageInfo)
            && threadsPageInfo.TryGetProperty("hasNextPage", out JsonElement hasNextPage)
            && hasNextPage.ValueKind == JsonValueKind.True;

        List<string> threadIds = [];
        List<string> humanThreadIds = [];
        int copilotThreadCount = 0;
        foreach (JsonElement thread in reviewThreads.GetProperty("nodes").EnumerateArray())
        {
            // Read once and reused below: the resolved-or-not count needs it for the "landed
            // (or stale) with its comment-thread count" phase text, so it is read ahead of the
            // unresolved-only filtering the rest of this loop applies. Scoped to the review
            // that is actually reported (Decisions Log #89, independent pre-PR review cycle 6)
            // — Copilot's login alone is not enough, since a stale review superseded by a fresh
            // countersign left threads too, and those are not what the reported review left.
            PullRequestReviewer? starter = ThreadStarter(thread);
            if (reportedCopilotReviewId is not null
                && starter is not null && IsCopilotLogin(starter.Login)
                && ThreadReviewId(thread) == reportedCopilotReviewId)
            {
                copilotThreadCount++;
            }

            if (thread.GetProperty("isResolved").GetBoolean())
            {
                continue;
            }

            // GitHub types a review thread's id as a non-null ID; a null here means a
            // malformed payload. Skipping it rather than coalescing to "" keeps a genuinely
            // missing id from fabricating a key that collapses every such thread onto the
            // same obstruction identity and human-engagement diff — a malformed payload
            // should undercount, never corrupt the mechanical key.
            string? id = thread.GetProperty("id").GetString();
            if (id is null)
            {
                continue;
            }

            threadIds.Add(id);
            if (starter is { IsHuman: true })
            {
                humanThreadIds.Add(id);
            }
        }

        return new ReviewObservation(
            threadIds.Count,
            humanThreadIds.Count,
            ReadReviewers(pullRequest),
            FindErroredCopilotReview(pullRequest),
            headCommit,
            threadIds,
            humanThreadIds,
            ReadPendingReviewRequestLogins(pullRequest),
            copilotReviewState,
            copilotThreadCount,
            IsConflicting: ReadMergeable(pullRequest) == "CONFLICTING",
            ReviewDecision: ReadReviewDecision(pullRequest),
            OutstandingReviewerLogins: ReadOutstandingReviewerLogins(pullRequest),
            ReviewThreadsTruncated: reviewThreadsTruncated);
    }

    /// <summary>GitHub's own branch-protection-aware verdict, or null when the repository has no rule requiring one.</summary>
    private static string? ReadReviewDecision(JsonElement pullRequest) =>
        pullRequest.TryGetProperty("reviewDecision", out JsonElement decision) && decision.ValueKind == JsonValueKind.String
            ? decision.GetString()
            : null;

    /// <summary>
    /// Every requested reviewer's login, raw and unfiltered — Copilot included, whoever asked for
    /// it (task: a task can be published pre-approved). Deliberately not
    /// <see cref="ReadPendingReviewRequestLogins"/>, which excludes a bot's own automatically
    /// recreated request unless a human is shown to have re-asked for it: a pre-approved merge
    /// gate needs "is anyone still asked to look" regardless of who asked.
    /// <para>
    /// A requested TEAM reviewer carries no <c>login</c> — GitHub exposes a team by
    /// <c>slug</c>/<c>name</c> instead — so it is recorded as <c>team:&lt;slug&gt;</c> rather than
    /// dropped the way <see cref="ReadPendingReviewRequestLogins"/>'s own human-engagement filter
    /// deliberately drops one (Decisions Log #80, backlog 45). Dropping it here instead would tell
    /// this gate no reviewer is outstanding while a team's review request genuinely still is,
    /// which is the "never guess at an unobserved fact" rule inverted: an unrecorded reviewer read
    /// as absent rather than as unknown, with an automatic merge as the consequence rather than
    /// only a display gap (independent pre-PR review, cycle 1, both lenses).
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ReadOutstandingReviewerLogins(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("reviewRequests", out JsonElement requests))
        {
            return [];
        }

        List<string> logins = [];
        foreach (JsonElement request in requests.GetProperty("nodes").EnumerateArray())
        {
            if (!request.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                || reviewer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (reviewer.TryGetProperty("login", out JsonElement login)
                && login.ValueKind == JsonValueKind.String
                && login.GetString() is { } loginValue)
            {
                logins.Add(loginValue);
            }
            else if (reviewer.TryGetProperty("slug", out JsonElement slug)
                && slug.ValueKind == JsonValueKind.String
                && slug.GetString() is { } slugValue)
            {
                logins.Add($"team:{slugValue}");
            }
        }

        // Sorted (and de-duplicated) before it ever reaches a caller: GitHub's own
        // reviewRequests ordering is not guaranteed stable sweep to sweep, and this list feeds
        // an equality comparison (CloseoutEngine.RecordExternalReviewObservationAsync) that would
        // otherwise append a fresh ExternalReviewObserved event on harmless reordering alone
        // (independent pre-PR review, cycle 2, both lenses).
        return [.. logins.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether Copilot's review has landed, is requested but not yet submitted, or the pull
    /// request carries no Copilot review activity at all (origin: PR #50 sat Delivered for 23
    /// minutes with a landed Copilot review nobody had read before the merge). An errored
    /// review (<see cref="FindErroredCopilotReview"/>) does not count as landed: a review that
    /// could not read the diff produced no verdict, so the phase this state ultimately writes
    /// must not tell a reader Copilot has weighed in when it has not. Nor does it fall through to
    /// <see cref="ExternalReviewState.None"/>: an errored review is review activity that was
    /// observed and simply produced no verdict, so it reports
    /// <see cref="ExternalReviewState.Unknown"/> instead — the same claim-no-absence reasoning
    /// as the uncomparable-commit case below, and the mirror of the mistake it already corrects
    /// (independent pre-PR review, cycle 1: this arm used to skip the review with no record at
    /// all, which read as "no review activity exists" when Copilot had, in fact, errored). A
    /// review left on a commit
    /// other than <paramref name="headCommit"/> does not count as landed either (origin: a
    /// countersign re-request after a fix push recreates Copilot's review request while its
    /// earlier review of the pre-fix commit still sits in <c>latestReviews</c> — the same
    /// staleness <see cref="ReadReviewers"/>/<see cref="ReadReviewedCommit"/> already track for
    /// the countersign), so a stale review is kept as a candidate exactly as if Copilot had not
    /// reviewed yet.
    /// <para>
    /// Whether Copilot currently has a pending request (<see cref="IsCopilotReviewRequestPending"/>)
    /// is checked first, before anything about <c>latestReviews</c> is read at all: a currently
    /// outstanding request means "awaiting Copilot review" regardless of what an earlier review
    /// already said, landed or otherwise, about an earlier — or even the same — commit. Checking
    /// it only after a landed-review match let that match short-circuit the method before the
    /// request was ever read, so a human re-requesting Copilot's review through GitHub's UI with no
    /// new push (leaving the earlier landed review sitting in <c>latestReviews</c> at the same head)
    /// stayed reported as <see cref="ExternalReviewState.Landed"/> forever (independent pre-PR
    /// review, cycle 1, conformance finding). That check reads <c>reviewRequests</c> raw,
    /// deliberately not through <see cref="ReadPendingReviewRequestLogins"/> — that method excludes
    /// a bot's own request unless a human is shown to have re-asked for it (the human-engagement
    /// signal this question is not). Whether Copilot is currently outstanding at all, however it
    /// got that way, is exactly what "awaiting Copilot review" needs to say.
    /// </para>
    /// <para>
    /// A non-errored Copilot review that does not match <paramref name="headCommit"/> is kept as
    /// a stale candidate rather than discarded: if the loop finds no landed review and no request
    /// is pending, that stale review is what gets reported (<see cref="ExternalReviewState.Stale"/>)
    /// instead of the state falling all the way through to <see cref="ExternalReviewState.None"/>,
    /// which would tell a reader Copilot never looked at all (independent pre-PR review, cycle 6).
    /// A review that could not be compared on either side reports
    /// <see cref="ExternalReviewState.Unknown"/> for the same reason: unclassifiable evidence is
    /// not absence (cycle 9).
    /// </para>
    /// <para>
    /// Also returns the reported review's own GraphQL id (the landed one, or the stale one when
    /// that is what gets reported), or null when nothing was recorded — what
    /// <see cref="ParseReviews"/> scopes <c>CopilotReviewThreadCount</c> against, so a thread a
    /// now-superseded review left does not inflate the count of a fresh, clean countersign
    /// (Decisions Log #89).
    /// </para>
    /// </summary>
    private static (ExternalReviewState State, string? ReviewId) ReadCopilotReviewState(
        JsonElement pullRequest, string? headCommit)
    {
        // Checked first, and unconditionally: a currently pending request means Copilot is
        // outstanding right now, regardless of whatever an earlier review already said about
        // an earlier (or even the same) commit. Checking this only after the latestReviews loop
        // let a Landed review at the current head short-circuit the whole method before this
        // request was ever read, so a human re-requesting Copilot's review (through GitHub's UI,
        // no new push) on a commit Copilot had already reviewed stayed reported as Landed forever
        // — the exact "no outstanding requested reviewer" gate this discriminator exists to serve
        // then merged past a review request that was genuinely still open (independent pre-PR
        // review, cycle 1, conformance finding).
        if (IsCopilotReviewRequestPending(pullRequest))
        {
            return (ExternalReviewState.RequestedPending, null);
        }

        string? staleReviewId = null;
        bool unclassifiedReviewSeen = false;
        if (pullRequest.TryGetProperty("latestReviews", out JsonElement latest))
        {
            foreach (JsonElement review in latest.GetProperty("nodes").EnumerateArray())
            {
                if (ReadActor(review) is not { } reviewer || !IsCopilotLogin(reviewer.Login))
                {
                    continue;
                }

                string body = review.GetProperty("body").GetString() ?? "";
                if (body.Contains(ErroredReviewBodyMarker, StringComparison.OrdinalIgnoreCase))
                {
                    // An errored review is review activity that happened and produced no verdict —
                    // it must not fall through to None below, the same claim-no-absence reasoning
                    // as the uncomparable-commit case just below (independent pre-PR review, cycle 1).
                    unclassifiedReviewSeen = true;
                    continue;
                }

                string? reviewId = review.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
                string? reviewedCommit = ReadReviewedCommit(review);

                // Both sides have to be actually observed before this review can be compared at
                // all: a null reviewedCommit already means "cannot tell" per ReadReviewedCommit's
                // own contract, and a null headCommit means the provider did not report a head
                // either. string.Equals(null, null) is true, so comparing unconditionally turned
                // two unobserved values into a positive Landed claim on evidence nobody read, and
                // a null reviewedCommit against a real head into a positive Stale claim the same
                // way (independent pre-PR review, cycle 7). Neither claim is made when either
                // side is unobserved; the review is left unclassified for this pass instead.
                bool commitObserved = reviewedCommit is not null && headCommit is not null;
                if (commitObserved && string.Equals(reviewedCommit, headCommit, StringComparison.OrdinalIgnoreCase))
                {
                    return (ExternalReviewState.Landed, reviewId);
                }

                if (commitObserved)
                {
                    staleReviewId = reviewId;
                }
                else
                {
                    // A Copilot review is present but could not be compared (its commit, or the
                    // head, was unobserved). That must not fall through to None below — None is
                    // a positive claim that no review activity exists, and this pass has read
                    // evidence it could not classify (independent pre-PR review, cycle 9).
                    unclassifiedReviewSeen = true;
                }
            }
        }

        return staleReviewId is not null
            ? (ExternalReviewState.Stale, staleReviewId)
            : unclassifiedReviewSeen
                ? (ExternalReviewState.Unknown, null)
                : (ExternalReviewState.None, null);
    }

    /// <summary>
    /// Whether Copilot currently has an outstanding review request, read from
    /// <c>reviewRequests</c> raw — deliberately not through <see cref="ReadPendingReviewRequestLogins"/>,
    /// which excludes a bot's own automatically recreated request unless a human is shown to have
    /// re-asked for it. <see cref="ReadCopilotReviewState"/> needs "is Copilot currently asked to
    /// look", however that request got there, checked before anything else it reports.
    /// </summary>
    private static bool IsCopilotReviewRequestPending(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("reviewRequests", out JsonElement requests))
        {
            return false;
        }

        foreach (JsonElement request in requests.GetProperty("nodes").EnumerateArray())
        {
            if (request.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                && reviewer.ValueKind == JsonValueKind.Object
                && reviewer.TryGetProperty("login", out JsonElement login)
                && IsCopilotLogin(login.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// GitHub's own three-state read of whether this pull request can be merged as-is
    /// (<c>MERGEABLE | CONFLICTING | UNKNOWN</c>) — <c>UNKNOWN</c> means the provider has not
    /// finished computing it yet (a very recent push), and is read here as "not observed as
    /// conflicting", never as a guess either way; the next sweep asks again.
    /// </summary>
    private static string? ReadMergeable(JsonElement pullRequest) =>
        pullRequest.TryGetProperty("mergeable", out JsonElement mergeable) && mergeable.ValueKind == JsonValueKind.String
            ? mergeable.GetString()
            : null;

    /// <summary>
    /// Who currently has a pending review request — the second human-engagement signal
    /// (Decisions Log #80, backlog 45): a login that was not pending as of the task's last
    /// automatic decision, and that the platform did not itself just request
    /// (CloseoutEngine.HasHumanEngagement also compares against RunDetails.RequestedReviewerLogins),
    /// is a human re-requesting a review through GitHub's own UI. Team requests carry no
    /// login the review-request REST endpoint or this comparison can use, so they are left
    /// out rather than guessed at.
    /// <para>
    /// A bot-typed reviewer (Copilot, or the unified app surfacing as User — the known-login
    /// fallback) is excluded UNLESS <see cref="ReadLastReviewRequesters"/> shows the most
    /// recent request for that reviewer was made by a human actor. <c>reviewRequests</c>
    /// alone reports only who is currently requested, never who asked, so a request GitHub's
    /// own automation recreated — Copilot's "review new commits automatically" setting
    /// re-requests it on every push nobody asked for — would be indistinguishable from a
    /// human deliberately re-requesting Copilot (the origin incident this whole signal exists
    /// for, PR 26, 2026-08-22) without the requester's identity. When the timeline carries no
    /// matching event — a malformed payload, or the cap on the query truncated it — the bot
    /// reviewer stays excluded, the same conservative default as before this discriminator
    /// existed.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ReadPendingReviewRequestLogins(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("reviewRequests", out JsonElement requests))
        {
            return [];
        }

        Dictionary<string, PullRequestReviewer?> lastRequesterByReviewer = ReadLastReviewRequesters(pullRequest);

        List<string> logins = [];
        foreach (JsonElement request in requests.GetProperty("nodes").EnumerateArray())
        {
            if (!request.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                || reviewer.ValueKind != JsonValueKind.Object
                || !reviewer.TryGetProperty("login", out JsonElement login)
                || login.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? reviewerLogin = login.GetString();
            string typeName = reviewer.TryGetProperty("__typename", out JsonElement type) ? type.GetString() ?? "" : "";
            bool isBotReviewer = typeName == "Bot" || IsCopilotLogin(reviewerLogin);
            if (isBotReviewer)
            {
                bool requestedByHuman = reviewerLogin is not null
                    && lastRequesterByReviewer.TryGetValue(reviewerLogin, out PullRequestReviewer? requester)
                    && requester is { IsHuman: true };
                if (!requestedByHuman)
                {
                    continue;
                }
            }

            logins.Add(reviewerLogin ?? "");
        }

        return logins;
    }

    /// <summary>
    /// Who most recently asked for each still-pending reviewer, read from the review-request
    /// timeline rather than <c>reviewRequests</c> itself, which carries no requester. Keyed by
    /// the requested reviewer's login; <c>timelineItems(last:)</c> returns events oldest-first,
    /// so iterating in order and overwriting on each match leaves the most recent ask per
    /// reviewer, which is all <see cref="ReadPendingReviewRequestLogins"/> needs to tell a
    /// human's re-request apart from GitHub's own automation recreating the same request.
    /// </summary>
    private static Dictionary<string, PullRequestReviewer?> ReadLastReviewRequesters(JsonElement pullRequest)
    {
        Dictionary<string, PullRequestReviewer?> lastRequesterByReviewer = new(StringComparer.OrdinalIgnoreCase);
        if (!pullRequest.TryGetProperty("timelineItems", out JsonElement timeline))
        {
            return lastRequesterByReviewer;
        }

        foreach (JsonElement item in timeline.GetProperty("nodes").EnumerateArray())
        {
            if (!item.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                || reviewer.ValueKind != JsonValueKind.Object
                || !reviewer.TryGetProperty("login", out JsonElement login)
                || login.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? reviewerLogin = login.GetString();
            if (reviewerLogin is null)
            {
                continue;
            }

            lastRequesterByReviewer[reviewerLogin] = ReadActor(item, "actor");
        }

        return lastRequesterByReviewer;
    }

    /// <summary>
    /// The accounts whose latest review sits on this pull request, minus its own author and
    /// minus anyone the provider will not accept a request for. GitHub refuses a review
    /// request addressed to the author, and the author is exactly who a self-note thread was
    /// written by, so leaving them in would fail the whole re-request call on a pull request
    /// that had human self-review; a mannequin is refused for its own reason (nobody claimed
    /// it), and it is dropped here rather than at the call site so no caller has to remember.
    /// <para>
    /// Each reviewer carries the commit its latest review was left on, which is what lets the
    /// countersign skip a reviewer who has already seen the head (Decisions Log #62) instead
    /// of resetting a fresh approval to pending and spending a pass to learn nothing.
    /// </para>
    /// </summary>
    private static IReadOnlyList<PullRequestReviewer> ReadReviewers(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("latestReviews", out JsonElement latest))
        {
            return [];
        }

        string? author = ReadActor(pullRequest)?.Login;
        List<PullRequestReviewer> reviewers = [];
        foreach (JsonElement review in latest.GetProperty("nodes").EnumerateArray())
        {
            if (ReadActor(review) is not { IsRequestable: true } reviewer
                || string.Equals(reviewer.Login, author, StringComparison.OrdinalIgnoreCase)
                || reviewers.Any(seen => string.Equals(seen.Login, reviewer.Login, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            reviewers.Add(reviewer with { LastReviewedCommit = ReadReviewedCommit(review) });
        }

        return reviewers;
    }

    /// <summary>
    /// The commit a review was left on, null when the provider did not report one. Null is
    /// read downstream as "cannot tell", never as "up to date": an unobserved commit must not
    /// silently retire a reviewer the fixes were pushed for.
    /// </summary>
    private static string? ReadReviewedCommit(JsonElement review) =>
        review.TryGetProperty("commit", out JsonElement commit) && commit.ValueKind == JsonValueKind.Object
            ? commit.GetProperty("oid").GetString()
            : null;

    /// <summary>The pull request's current head, which the reviews above are compared against.</summary>
    private static string? ReadHeadCommit(JsonElement pullRequest) =>
        pullRequest.TryGetProperty("headRefOid", out JsonElement head)
            ? head.GetString()
            : null;

    private static ErroredReview? FindErroredCopilotReview(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("latestReviews", out JsonElement latest))
        {
            return null;
        }

        foreach (JsonElement review in latest.GetProperty("nodes").EnumerateArray())
        {
            if (ReadActor(review) is not { } reviewer || !IsCopilotLogin(reviewer.Login))
            {
                continue;
            }

            string body = review.GetProperty("body").GetString() ?? "";
            if (body.Contains(ErroredReviewBodyMarker, StringComparison.OrdinalIgnoreCase))
            {
                return new ErroredReview(reviewer.Login, review.GetProperty("url").GetString() ?? "");
            }
        }

        return null;
    }

    /// <summary>
    /// Who opened the thread — the discriminator the whole self-review story rests on.
    /// Agents never START review threads; they only reply within existing ones, so the
    /// first comment's author is always a reviewer, including when that author is the pull
    /// request's own human owner leaving themselves a note (AGENTS.md records the
    /// invariant, and what breaks if agents ever gain their own thread-opening voice).
    /// </summary>
    private static PullRequestReviewer? ThreadStarter(JsonElement thread)
    {
        foreach (JsonElement comment in thread.GetProperty("comments").GetProperty("nodes").EnumerateArray())
        {
            return ReadActor(comment);
        }

        return null;
    }

    /// <summary>
    /// The GraphQL review id the thread's first comment belongs to, or null when the comment
    /// carries none (a standalone pull-request comment, never part of a review). What
    /// <see cref="ParseReviews"/> compares against the currently-landed review's own id to scope
    /// <c>CopilotReviewThreadCount</c> to that review specifically, rather than to every thread
    /// Copilot has ever opened across the pull request's history (Decisions Log #89).
    /// </summary>
    private static string? ThreadReviewId(JsonElement thread)
    {
        foreach (JsonElement comment in thread.GetProperty("comments").GetProperty("nodes").EnumerateArray())
        {
            return comment.TryGetProperty("pullRequestReview", out JsonElement review) && review.ValueKind == JsonValueKind.Object
                ? review.GetProperty("id").GetString()
                : null;
        }

        return null;
    }

    // Copilot's reviewer authors under a small set of known app logins: GraphQL reports
    // the bare form (copilot-pull-request-reviewer), REST the [bot]-suffixed form, and
    // the unified Copilot app surfaces as plain Copilot. Exact match after stripping the
    // suffix — a collaborator whose login merely contains "copilot" is not the reviewer
    // bot, and misclassifying one would hold the run at ReviewPending and spend the
    // automatic closeout budget re-requesting reviews from an account that cannot answer.
    private static readonly string[] CopilotLogins = ["copilot", "copilot-pull-request-reviewer"];

    /// <summary>Internal so <see cref="PullRequestSnapshot.HasOutstandingHumanReviewer"/> can share the identical classification.</summary>
    internal static bool IsCopilotLogin(string? login)
    {
        if (login is null)
        {
            return false;
        }

        string bare = login.EndsWith("[bot]", StringComparison.Ordinal)
            ? login[..^"[bot]".Length]
            : login;
        return CopilotLogins.Contains(bare, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The author of a comment, review, or pull request, with the provider's own actor type
    /// deciding bot versus human — GraphQL types an app account as Bot, which is a fact
    /// rather than a naming convention. The known Copilot logins are an extra yes rather
    /// than the rule, because the unified Copilot app has surfaced under both actor types
    /// and misreading it as a person would spend the careful-with-humans path on a bot.
    /// A deleted account serializes author as null, and null is nobody: unattributable
    /// authorship is recorded as absent rather than guessed at either way.
    /// <para>
    /// <paramref name="propertyName"/> defaults to "author" (comments, reviews, the pull
    /// request itself); a timeline event's actor field is named "actor" instead, and the
    /// same actor-typing logic applies to it unchanged.
    /// </para>
    /// </summary>
    private static PullRequestReviewer? ReadActor(JsonElement authored, string propertyName = "author")
    {
        if (!authored.TryGetProperty(propertyName, out JsonElement author) || author.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? login = author.GetProperty("login").GetString();
        if (login.IsBlank())
        {
            return null;
        }

        string typeName = author.TryGetProperty("__typename", out JsonElement type) ? type.GetString() ?? "" : "";
        return new PullRequestReviewer(login, ReadKind(typeName, login));
    }

    /// <summary>
    /// The provider's actor type mapped to the three kinds that behave differently here. Only
    /// Bot and Mannequin are named: every other actor type GitHub reports for an author is a
    /// person (User today, EnterpriseUserAccount in an enterprise tenant), so the default is
    /// Human deliberately — an unfamiliar type must not silently vanish from the human thread
    /// count, which is what tells a follow-up that somebody is waiting on an answer.
    /// </summary>
    private static ReviewerKind ReadKind(string typeName, string login) => typeName switch
    {
        "Bot" => ReviewerKind.Bot,
        "Mannequin" => ReviewerKind.Mannequin,
        _ => IsCopilotLogin(login) ? ReviewerKind.Bot : ReviewerKind.Human,
    };

    /// <summary>
    /// Reads the rollup, plus whether GitHub has actually reported any check at all
    /// (<paramref name="root"/>'s own <c>statusCheckRollup</c> array non-empty). The two are not
    /// the same fact: a rollup GitHub has not yet populated (a workflow run object typically takes
    /// only seconds to appear, but can take longer under Actions queue congestion, or right after a
    /// mechanical rebase's own force-push re-triggers CI) reads identically to a repository with no
    /// CI configured at all — both come back as an empty array — yet only the second one is really
    /// "green" (independent pre-PR review, cycle 1, adversarial finding: a pre-approved task could
    /// merge past a workflow that simply had not registered yet). Observed is the caller's signal
    /// to wait out a bounded settle window before trusting silence as absence.
    /// </summary>
    private static (IReadOnlyList<string> Failing, bool Pending, bool Observed) ReadChecks(JsonElement root)
    {
        if (!root.TryGetProperty("statusCheckRollup", out JsonElement rollup) || rollup.ValueKind != JsonValueKind.Array)
        {
            return ([], false, false);
        }

        bool observed = rollup.GetArrayLength() > 0;
        List<string> failing = [];
        bool pending = false;
        foreach (JsonElement check in rollup.EnumerateArray())
        {
            string typename = check.TryGetProperty("__typename", out JsonElement t) ? t.GetString() ?? "" : "";
            if (typename == "StatusContext")
            {
                string contextState = check.GetProperty("state").GetString() ?? "";
                if (FailingStatusContextStates.Contains(contextState))
                {
                    failing.Add(check.GetProperty("context").GetString() ?? "unnamed status");
                }
                else if (contextState is "PENDING" or "EXPECTED")
                {
                    pending = true;
                }
            }
            else
            {
                string status = check.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
                if (status != "COMPLETED")
                {
                    pending = true;
                    continue;
                }

                string conclusion = check.TryGetProperty("conclusion", out JsonElement c) ? c.GetString() ?? "" : "";
                if (FailingCheckRunConclusions.Contains(conclusion))
                {
                    failing.Add(check.TryGetProperty("name", out JsonElement n)
                        ? n.GetString() ?? "unnamed check"
                        : "unnamed check");
                }
            }
        }

        return (failing, pending, observed);
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out DateTimeOffset parsed)
            ? parsed
            : null;

    /// <summary>PR URLs are https://github.com/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;.</summary>
    private static (string Owner, string Repository) ParseOwnerAndRepository(string pullRequestUrl)
    {
        string[] segments = new Uri(pullRequestUrl).AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2
            ? (segments[0], segments[1])
            : throw new InvalidOperationException($"Cannot parse owner/repository from {pullRequestUrl}.");
    }

    private static async Task<string> RunGhAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return process.ExitCode == 0
            ? await standardOutput
            : throw new InvalidOperationException(
                $"gh {string.Join(' ', arguments)} exited {process.ExitCode}: {(await standardError).Trim()}");
    }
}
