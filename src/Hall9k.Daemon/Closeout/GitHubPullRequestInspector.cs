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
    //
    // timelineItems reads both halves of the review-request story — the ask and the un-ask — so
    // the net "who has ever been asked to review this" the after-human-review merge gate needs
    // (task: the people a pull request is waiting on are named, and pre-approval gains a mode that
    // waits for human review) can drop a reviewer whose request a human took back. Two itemTypes
    // now share the cap, so it doubles to 50, and __typename becomes load-bearing: both event
    // shapes carry a requestedReviewer, and reading a removal as an ask would invert the answer
    // (as would reading a removal's actor as a human re-requesting, which is what
    // ReadLastReviewRequesters would have done unfiltered).
    //
    // standingReviews is an alias onto the reviews connection, filtered to the three verdict
    // states, and it exists because latestReviews CANNOT answer "did this person approve": it is
    // the latest review per author of ANY type, so a reviewer who approves the head and then
    // answers a question with a comment-only review has that comment as their latest, while
    // GitHub keeps their approval standing and goes on reporting reviewDecision: APPROVED. Reading
    // the verdict off latestReviews would hold the after-human-review merge forever on a reviewer
    // GitHub's own reviewers panel shows as having approved (independent pre-PR review, cycle 1,
    // adversarial finding). DISMISSED is selected alongside the other two precisely so a dismissal
    // still clears the approval it dismissed; COMMENTED and PENDING are excluded because neither
    // is a verdict and a later one must not supersede a standing verdict. last: 50 is a cap on the
    // same terms as every other read here, and its truncation direction is safe: an approval old
    // enough to fall out of it reads as absent, which holds the merge rather than granting it.
    //
    // comments(first: 50) reads a thread WHOLE rather than only its opening comment, because a
    // re-reviewing human's second changes-requested review is most often written as replies
    // inside the threads their first one opened ("still not fixed here"). Such a reply belongs to
    // the new review while the thread's first comment still belongs to the superseded one, so a
    // read that stopped at the first comment dropped exactly the comments the fix lap exists to
    // answer and handed it a review with zero findings (independent pre-PR review, cycle 1,
    // conformance finding). Who STARTED the thread is still read from the first node alone
    // (ThreadStarter, ThreadReviewId) — that invariant is unchanged. 50 is the same species of
    // cap as the 100 above: a single thread carrying 50+ comments has left the range this
    // automation is for. Deliberately first: 50 and not last: 50, even though GitHub returns a
    // thread oldest-first and so it is the NEWEST reply a 50+ comment thread would lose: the
    // thread's OPENER is what the starter and review-id reads above are, and every closeout
    // count keyed on them would misattribute a thread whose first comment fell off the page.
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
                nodes { id isResolved comments(first: 50) { nodes { author { login __typename } pullRequestReview { id } body path line originalLine } } }
                pageInfo { hasNextPage }
              }
              reviewRequests(first: 20) {
                nodes { requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Team { slug } } }
              }
              timelineItems(last: 50, itemTypes: [REVIEW_REQUESTED_EVENT, REVIEW_REQUEST_REMOVED_EVENT]) {
                nodes {
                  __typename
                  ... on ReviewRequestedEvent {
                    actor { login __typename }
                    requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Team { slug } }
                  }
                  ... on ReviewRequestRemovedEvent {
                    requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Team { slug } }
                  }
                }
              }
              latestReviews(first: 100) {
                nodes { id author { login __typename } body url state submittedAt commit { oid } }
              }
              standingReviews: reviews(last: 50, states: [APPROVED, CHANGES_REQUESTED, DISMISSED]) {
                nodes { author { login __typename } state commit { oid } }
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
            ReviewThreadsTruncated: reviews.ReviewThreadsTruncated,
            RequestedHumanReviewerLogins: reviews.RequestedHumanReviewerLogins,
            ChangesRequestedReviews: reviews.ChangesRequestedReviews);
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

    public async Task RetargetAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, string baseBranch,
        CancellationToken cancellationToken) =>
        await RunGhAsync(
            repositoryPath, ["pr", "edit", pullRequestNumber.ToString(), "--base", baseBranch], cancellationToken);

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
        bool ReviewThreadsTruncated = false,
        IReadOnlyList<string>? RequestedHumanReviewerLogins = null,
        IReadOnlyList<ChangesRequestedReview>? ChangesRequestedReviews = null)
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
            ReviewThreadsTruncated: reviewThreadsTruncated,
            RequestedHumanReviewerLogins: ReadRequestedHumanReviewerLogins(pullRequest),
            ChangesRequestedReviews: ReadChangesRequestedReviews(pullRequest, headCommit));
    }

    /// <summary>
    /// Every human reviewer whose LATEST review requested changes on the head this snapshot just
    /// read (task: a changes-requested pull-request review from a human becomes a fix lap), with
    /// the review's own body and each of its inline comments as findings.
    /// <para>
    /// Four filters, each earning its place. The author's own account is dropped for the reason
    /// <see cref="ReadReviewers"/> drops it (a review request addressed there is refused, and the
    /// lap re-requests). A BOT's changes-requested review is dropped because Copilot's findings
    /// stay on the automated thread path they have always been on — the whole point of this lap is
    /// that a person is owed a person's answer (Brian's ruling, 2026-09-06 12:15). Anything other
    /// than <c>CHANGES_REQUESTED</c> is dropped, which is what leaves a comment-only review to the
    /// existing <c>FollowUpKind.ReviewFeedback</c> path unchanged. And a review whose commit does
    /// not equal the head is dropped — including one the provider reported NO commit for, which
    /// reads as "cannot tell", never as "on the head": claiming a fix lap for a review that may
    /// answer a superseded push would dispatch against feedback nobody can place, while leaving it
    /// alone costs nothing, since its unresolved threads are still on the thread path.
    /// </para>
    /// <para>
    /// <c>latestReviews</c> is per-reviewer latest, so a reviewer who requested changes and later
    /// approved is structurally gone from here without needing a rule of its own — the same
    /// property the errored-review match already relies on.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ChangesRequestedReview> ReadChangesRequestedReviews(
        JsonElement pullRequest, string? headCommit)
    {
        if (headCommit is null || !pullRequest.TryGetProperty("latestReviews", out JsonElement latest))
        {
            return [];
        }

        string? author = ReadActor(pullRequest)?.Login;
        List<ChangesRequestedReview> reviews = [];
        foreach (JsonElement review in latest.GetProperty("nodes").EnumerateArray())
        {
            if (ReadActor(review) is not { IsHuman: true } reviewer
                || string.Equals(reviewer.Login, author, StringComparison.OrdinalIgnoreCase)
                || ReadReviewState(review) != "CHANGES_REQUESTED"
                // Compared the way ReadCopilotReviewState compares the same two facts, rather
                // than case-sensitively: one shape, one comparison. A null reviewedCommit is
                // "cannot tell" per ReadReviewedCommit's contract and is dropped by this, which is
                // the intended read — headCommit is non-null by the guard above, so the two are
                // never both unobserved here the way they can be there.
                || !string.Equals(ReadReviewedCommit(review), headCommit, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<ChangesRequestedFinding> findings = [];
            string body = review.GetProperty("body").GetString() ?? "";
            if (body.IsNotBlank())
            {
                // No location and no thread: GitHub makes a review's body unthreadable, which is
                // exactly why an answer to it can only ever be a top-level pull-request comment.
                findings.Add(new ChangesRequestedFinding(body.Trim()));
            }

            if (review.GetProperty("id").GetString() is { } reviewId)
            {
                findings.AddRange(ReadInlineFindings(pullRequest, reviewId));
            }

            reviews.Add(new ChangesRequestedReview(
                reviewer.Login,
                review.GetProperty("url").GetString() ?? "",
                ReadTimestamp(review, "submittedAt"),
                findings));
        }

        return reviews;
    }

    /// <summary>
    /// One review's inline comments, read from the review THREADS rather than from the review's
    /// own comment list — which is what supplies the thread id a reply would land inside, since
    /// nothing here ever starts a thread of its own (AGENTS.md). Resolved threads are included
    /// deliberately: a reviewer who requested changes and whose comment an earlier lap already
    /// resolved has still not had their verdict answered, and dropping it would hand the fix lap a
    /// review with findings missing from it.
    /// <para>
    /// Every comment in every thread is considered, not just each thread's opening one, and the
    /// membership test is the comment's OWN <c>pullRequestReview</c> id. That is what admits the
    /// shape a re-reviewing human writes most: a second changes-requested review left as replies
    /// inside the threads the first one opened, where the reply belongs to the new review and the
    /// thread's first comment still belongs to the superseded one. Scoping by the thread's opener
    /// instead dropped every such comment and handed the fix lap a review with nothing in it
    /// (independent pre-PR review, cycle 1, conformance finding). The reply target is still the
    /// thread, so nothing here starts one; and an agent's own earlier reply in the thread is
    /// excluded structurally, since it belongs to a different review id than the human's.
    /// </para>
    /// </summary>
    private static IEnumerable<ChangesRequestedFinding> ReadInlineFindings(JsonElement pullRequest, string reviewId)
    {
        foreach (JsonElement thread in pullRequest.GetProperty("reviewThreads").GetProperty("nodes").EnumerateArray())
        {
            string? threadId = thread.GetProperty("id").GetString();
            foreach (JsonElement comment in thread.GetProperty("comments").GetProperty("nodes").EnumerateArray())
            {
                if (CommentReviewId(comment) != reviewId)
                {
                    continue;
                }

                string body = comment.GetProperty("body").GetString() ?? "";
                if (body.IsBlank())
                {
                    continue;
                }

                yield return new ChangesRequestedFinding(
                    body.Trim(), ReadCommentLocation(comment), threadId);
            }
        }
    }

    /// <summary>
    /// Where a review comment points, in the same `path/to/file.cs:123` form every platform review
    /// finding states. <c>line</c> is null on a comment GitHub considers outdated, where
    /// <c>originalLine</c> still names the line it was written against; with neither, the path
    /// alone is what was actually observed and no line is invented for it. Null when even the path
    /// is absent.
    /// </summary>
    private static string? ReadCommentLocation(JsonElement comment)
    {
        string? path = comment.TryGetProperty("path", out JsonElement pathElement)
            && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
        if (path.IsBlank())
        {
            return null;
        }

        int? line = ReadCommentLine(comment, "line") ?? ReadCommentLine(comment, "originalLine");
        return line is { } stated ? $"{path}:{stated}" : path;
    }

    private static int? ReadCommentLine(JsonElement comment, string property) =>
        comment.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    /// <summary>The thread's first comment — the reviewer's own, per the thread-starter invariant.</summary>
    private static JsonElement? ThreadFirstComment(JsonElement thread)
    {
        foreach (JsonElement comment in thread.GetProperty("comments").GetProperty("nodes").EnumerateArray())
        {
            return comment;
        }

        return null;
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
                logins.Add($"{PullRequestSnapshot.TeamReviewerPrefix}{slugValue}");
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
            // The timeline now also carries REVIEW_REQUEST_REMOVED_EVENT, which has both a
            // requestedReviewer and an actor of its own: without this filter a human WITHDRAWING a
            // request would read here as a human MAKING one, which is precisely the inverted
            // human-engagement signal ReadPendingReviewRequestLogins exists to get right.
            if (TimelineItemType(item) != "ReviewRequestedEvent"
                || !item.TryGetProperty("requestedReviewer", out JsonElement reviewer)
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

    /// <summary>Which timeline event a node is, read from GraphQL's own <c>__typename</c> rather than inferred from its shape.</summary>
    private static string? TimelineItemType(JsonElement item) =>
        item.TryGetProperty("__typename", out JsonElement typeName) && typeName.ValueKind == JsonValueKind.String
            ? typeName.GetString()
            : null;

    /// <summary>
    /// Every human (or team) reviewer this pull request has an outstanding-or-answered review
    /// request for, net of withdrawals — what
    /// <see cref="PullRequestSnapshot.HumanReviewersEverRequested"/> unions with the
    /// currently-outstanding list (task: the people a pull request is waiting on are named, and
    /// pre-approval gains a mode that waits for human review).
    /// <para>
    /// <c>timelineItems(last:)</c> returns oldest-first, so walking in order and letting each
    /// event overwrite the previous verdict for that reviewer leaves the most recent ask-or-un-ask
    /// per login. A reviewer who simply answered leaves no removal event at all — GitHub retires
    /// their pending request silently — so they stay in this set and the merge gate goes on to ask
    /// what their verdict was. One a human un-asked drops out, which is what lets the gate report
    /// "no human reviewer has been requested" again rather than waiting on somebody nobody is
    /// waiting on.
    /// </para>
    /// <para>
    /// Copilot is filtered out here, on exactly the same classification every other surface uses
    /// (<see cref="IsCopilotLogin"/>), so its own request — including the one GitHub's
    /// review-new-commits-automatically setting recreates on every push — is classified exactly as
    /// it always was: handled through <see cref="ExternalReviewState"/> and its bounded settle
    /// window, never as the human review this gate waits for. A requested TEAM is recorded
    /// <c>team:&lt;slug&gt;</c>, matching <see cref="ReadOutstandingReviewerLogins"/>, because a
    /// team's request is a request for a person's review even though the slug itself can never
    /// carry one.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ReadRequestedHumanReviewerLogins(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("timelineItems", out JsonElement timeline))
        {
            return [];
        }

        Dictionary<string, bool> requestedByReviewer = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in timeline.GetProperty("nodes").EnumerateArray())
        {
            string? itemType = TimelineItemType(item);
            if (itemType is not ("ReviewRequestedEvent" or "ReviewRequestRemovedEvent"))
            {
                continue;
            }

            if (!item.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                || reviewer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? recordAs = null;
            if (reviewer.TryGetProperty("login", out JsonElement login)
                && login.ValueKind == JsonValueKind.String
                && login.GetString() is { } loginValue)
            {
                recordAs = IsCopilotLogin(loginValue) ? null : loginValue;
            }
            else if (reviewer.TryGetProperty("slug", out JsonElement slug)
                && slug.ValueKind == JsonValueKind.String
                && slug.GetString() is { } slugValue)
            {
                recordAs = $"{PullRequestSnapshot.TeamReviewerPrefix}{slugValue}";
            }

            if (recordAs is not null)
            {
                requestedByReviewer[recordAs] = itemType == "ReviewRequestedEvent";
            }
        }

        // Sorted and de-duplicated before it reaches a caller, the same reason
        // ReadOutstandingReviewerLogins sorts: this list feeds an equality comparison
        // (CloseoutEngine.RecordExternalReviewObservationAsync) that would otherwise append a
        // fresh event on harmless reordering alone.
        return
        [
            .. requestedByReviewer
                .Where(entry => entry.Value)
                .Select(entry => entry.Key)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
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
    /// <para>
    /// Their STANDING verdict, and the commit that verdict sits on, come from a different read
    /// (<see cref="ReadStandingVerdicts"/>) for a reason the two fields' names carry: the latest
    /// review answers "has this person seen the head", and only a verdict-only read answers "did
    /// this person approve it". The two diverge the moment a reviewer approves and then comments.
    /// </para>
    /// </summary>
    private static IReadOnlyList<PullRequestReviewer> ReadReviewers(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("latestReviews", out JsonElement latest))
        {
            return [];
        }

        string? author = ReadActor(pullRequest)?.Login;
        Dictionary<string, StandingVerdict> standing = ReadStandingVerdicts(pullRequest);
        List<PullRequestReviewer> reviewers = [];
        foreach (JsonElement review in latest.GetProperty("nodes").EnumerateArray())
        {
            if (ReadActor(review) is not { IsRequestable: true } reviewer
                || string.Equals(reviewer.Login, author, StringComparison.OrdinalIgnoreCase)
                || reviewers.Any(seen => string.Equals(seen.Login, reviewer.Login, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            StandingVerdict? verdict = standing.TryGetValue(reviewer.Login, out StandingVerdict found)
                ? found
                : null;
            reviewers.Add(reviewer with
            {
                LastReviewedCommit = ReadReviewedCommit(review),
                StandingReviewState = verdict?.State,
                StandingReviewCommit = verdict?.Commit,
            });
        }

        return reviewers;
    }

    /// <summary>One reviewer's most recent verdict, and the commit it was left on.</summary>
    private readonly record struct StandingVerdict(string State, string? Commit);

    /// <summary>
    /// Each account's most recent VERDICT — approval, changes-requested, or a dismissal that
    /// cleared one — read from the verdict-filtered <c>standingReviews</c> alias rather than from
    /// <c>latestReviews</c>, which is per-author latest of any type and so loses a standing
    /// approval behind the commenting review that followed it (independent pre-PR review, cycle 1,
    /// adversarial finding).
    /// <para>
    /// The connection returns oldest-first, so walking in order and letting each verdict overwrite
    /// the previous one for that account leaves the standing verdict — including a DISMISSED that
    /// retires an earlier approval, which is why that state is selected alongside the other two.
    /// An account with no verdict at all is simply absent, which reads downstream as "did not
    /// approve" and never as approval.
    /// </para>
    /// <para>
    /// Absent entirely (a payload written before this alias existed, or a fixture that never sets
    /// it) yields an empty map on the same terms: no verdict observed, so nobody has approved.
    /// </para>
    /// </summary>
    private static Dictionary<string, StandingVerdict> ReadStandingVerdicts(JsonElement pullRequest)
    {
        Dictionary<string, StandingVerdict> verdictByLogin = new(StringComparer.OrdinalIgnoreCase);
        if (!pullRequest.TryGetProperty("standingReviews", out JsonElement standing)
            || standing.ValueKind != JsonValueKind.Object
            || !standing.TryGetProperty("nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return verdictByLogin;
        }

        foreach (JsonElement review in nodes.EnumerateArray())
        {
            if (ReadActor(review) is not { } reviewer || ReadReviewState(review) is not { } state)
            {
                continue;
            }

            verdictByLogin[reviewer.Login] = new StandingVerdict(state, ReadReviewedCommit(review));
        }

        return verdictByLogin;
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

    /// <summary>
    /// GitHub's own verdict word for a review — APPROVED, CHANGES_REQUESTED, DISMISSED among the
    /// states <c>standingReviews</c> selects — null when the provider did not report one. Null is
    /// read downstream as "did not approve", never as approval, the same claim-no-absence reading
    /// <see cref="ReadReviewedCommit"/> already gets.
    /// </summary>
    private static string? ReadReviewState(JsonElement review) =>
        review.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String
            ? state.GetString()
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
    private static PullRequestReviewer? ThreadStarter(JsonElement thread) =>
        ThreadFirstComment(thread) is { } comment ? ReadActor(comment) : null;

    /// <summary>
    /// The GraphQL review id the thread's first comment belongs to, or null when the comment
    /// carries none (a standalone pull-request comment, never part of a review). What
    /// <see cref="ParseReviews"/> compares against the currently-landed review's own id to scope
    /// <c>CopilotReviewThreadCount</c> to that review specifically, rather than to every thread
    /// Copilot has ever opened across the pull request's history (Decisions Log #89). Deliberately
    /// the OPENER's review and not any reply's: what this answers is which review left the thread.
    /// </summary>
    private static string? ThreadReviewId(JsonElement thread) =>
        ThreadFirstComment(thread) is { } comment ? CommentReviewId(comment) : null;

    /// <summary>
    /// The GraphQL review one comment itself belongs to, or null when it belongs to none (a
    /// standalone pull-request comment). What scopes a review's findings to its own comments
    /// wherever in a thread they sit (<see cref="ReadInlineFindings"/>).
    /// </summary>
    private static string? CommentReviewId(JsonElement comment) =>
        comment.TryGetProperty("pullRequestReview", out JsonElement review) && review.ValueKind == JsonValueKind.Object
            ? review.GetProperty("id").GetString()
            : null;

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
