using System.Diagnostics;
using System.Text.Json;

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
    // this automation is for.
    private const string ReviewsQuery =
        """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              author { login __typename }
              headRefOid
              reviewThreads(first: 100) {
                nodes { isResolved comments(first: 1) { nodes { author { login __typename } } } }
              }
              latestReviews(first: 100) {
                nodes { author { login __typename } body url commit { oid } }
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
            ["pr", "view", pullRequestNumber.ToString(), "--json", "state,mergedAt,closedAt,statusCheckRollup"],
            cancellationToken);

        using JsonDocument view = JsonDocument.Parse(viewJson);
        string state = view.RootElement.GetProperty("state").GetString() ?? "";
        DateTimeOffset? mergedAt = ReadTimestamp(view.RootElement, "mergedAt");
        DateTimeOffset? closedAt = ReadTimestamp(view.RootElement, "closedAt");
        (IReadOnlyList<string> failing, bool pending) = ReadChecks(view.RootElement);

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
            UnresolvedReviewThreadCount: reviews.UnresolvedThreads,
            UnresolvedHumanThreadCount: reviews.UnresolvedHumanThreads,
            Reviewers: reviews.Reviewers,
            ErroredReview: reviews.ErroredReview,
            HeadCommit: reviews.HeadCommit);
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

    /// <summary>What one GraphQL call saw about a pull request's reviews.</summary>
    internal sealed record ReviewObservation(
        int UnresolvedThreads,
        int UnresolvedHumanThreads,
        IReadOnlyList<PullRequestReviewer> Reviewers,
        ErroredReview? ErroredReview,
        string? HeadCommit)
    {
        public static readonly ReviewObservation None = new(0, 0, [], null, null);
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

        // Every unresolved thread counts, whoever started it (Decisions Log #62). The
        // starter is read only to tell a human's thread from a bot's, because the two get
        // different care in the follow-up — never to decide whether feedback exists.
        int unresolved = 0;
        int unresolvedHuman = 0;
        foreach (JsonElement thread in pullRequest.GetProperty("reviewThreads").GetProperty("nodes").EnumerateArray())
        {
            if (thread.GetProperty("isResolved").GetBoolean())
            {
                continue;
            }

            unresolved++;
            if (ThreadStarter(thread) is { IsHuman: true })
            {
                unresolvedHuman++;
            }
        }

        return new ReviewObservation(
            unresolved,
            unresolvedHuman,
            ReadReviewers(pullRequest),
            FindErroredCopilotReview(pullRequest),
            ReadHeadCommit(pullRequest));
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

    // Copilot's reviewer authors under a small set of known app logins: GraphQL reports
    // the bare form (copilot-pull-request-reviewer), REST the [bot]-suffixed form, and
    // the unified Copilot app surfaces as plain Copilot. Exact match after stripping the
    // suffix — a collaborator whose login merely contains "copilot" is not the reviewer
    // bot, and misclassifying one would hold the run at ReviewPending and spend the
    // automatic closeout budget re-requesting reviews from an account that cannot answer.
    private static readonly string[] CopilotLogins = ["copilot", "copilot-pull-request-reviewer"];

    private static bool IsCopilotLogin(string? login)
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
    /// </summary>
    private static PullRequestReviewer? ReadActor(JsonElement authored)
    {
        if (!authored.TryGetProperty("author", out JsonElement author) || author.ValueKind != JsonValueKind.Object)
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

    private static (IReadOnlyList<string> Failing, bool Pending) ReadChecks(JsonElement root)
    {
        if (!root.TryGetProperty("statusCheckRollup", out JsonElement rollup) || rollup.ValueKind != JsonValueKind.Array)
        {
            return ([], false);
        }

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

        return (failing, pending);
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
