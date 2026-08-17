using System.Diagnostics;
using System.Text.Json;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Reads a pull request's closeout signals through gh: state/merge via
/// gh pr view --json, unresolved Copilot review threads and each reviewer's latest
/// review via the GraphQL API (the REST surface has no thread-resolution data), and
/// re-requests an errored Copilot review via the REST review-request endpoint. Runs in
/// the project's repository so gh resolves the repo from the origin remote.
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
    // (same login rule as thread attribution), it is that reviewer's LATEST review
    // (latestReviews is per-reviewer latest, so a successful re-review supersedes an
    // errored one structurally), and its body contains this marker. Observed instance
    // (PR #6, 2026-08-17, GitHub partial outage): "Copilot encountered an error and was
    // unable to review this pull request. You can try again by re-requesting a review."
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
              reviewThreads(first: 100) {
                nodes { isResolved comments(first: 1) { nodes { author { login } } } }
              }
              latestReviews(first: 100) {
                nodes { author { login } body url }
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
        (int unresolvedCopilotThreads, ErroredReview? erroredReview) = state == "OPEN"
            ? await InspectReviewsAsync(repositoryPath, pullRequestUrl, pullRequestNumber, cancellationToken)
            : (0, null);

        return new PullRequestSnapshot(
            IsMerged: state == "MERGED",
            IsClosed: state == "CLOSED",
            MergedAt: mergedAt,
            ClosedAt: closedAt,
            FailingChecks: failing,
            HasPendingChecks: pending,
            UnresolvedCopilotThreadCount: unresolvedCopilotThreads,
            ErroredCopilotReview: erroredReview);
    }

    /// <summary>
    /// Re-requests through the REST review-request endpoint. GraphQL reports the bare
    /// bot login (copilot-pull-request-reviewer); the endpoint addresses app accounts by
    /// the [bot]-suffixed form — docs.github.com: request copilot-pull-request-reviewer[bot]
    /// as a reviewer. Verified against the origin incident's own successful re-request
    /// (PR #6 timeline, 2026-08-17).
    /// </summary>
    public async Task RerequestReviewAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, string reviewer,
        CancellationToken cancellationToken)
    {
        (string owner, string name) = ParseOwnerAndRepository(pullRequestUrl);
        string login = reviewer.EndsWith("[bot]", StringComparison.Ordinal) ? reviewer : $"{reviewer}[bot]";
        await RunGhAsync(
            repositoryPath,
            ["api", "--method", "POST",
                $"repos/{owner}/{name}/pulls/{pullRequestNumber}/requested_reviewers",
                "-f", $"reviewers[]={login}"],
            cancellationToken);
    }

    private static async Task<(int UnresolvedCopilotThreads, ErroredReview? ErroredReview)> InspectReviewsAsync(
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

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement pullRequest = document.RootElement
            .GetProperty("data").GetProperty("repository").GetProperty("pullRequest");

        int unresolved = 0;
        foreach (JsonElement thread in pullRequest.GetProperty("reviewThreads").GetProperty("nodes").EnumerateArray())
        {
            if (!thread.GetProperty("isResolved").GetBoolean() && IsCopilotThread(thread))
            {
                unresolved++;
            }
        }

        return (unresolved, FindErroredCopilotReview(pullRequest));
    }

    private static ErroredReview? FindErroredCopilotReview(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("latestReviews", out JsonElement latest))
        {
            return null;
        }

        foreach (JsonElement review in latest.GetProperty("nodes").EnumerateArray())
        {
            string? login = ReadAuthorLogin(review);
            if (login is null || !IsCopilotLogin(login))
            {
                continue;
            }

            string body = review.GetProperty("body").GetString() ?? "";
            if (body.Contains(ErroredReviewBodyMarker, StringComparison.OrdinalIgnoreCase))
            {
                return new ErroredReview(login, review.GetProperty("url").GetString() ?? "");
            }
        }

        return null;
    }

    private static bool IsCopilotThread(JsonElement thread)
    {
        foreach (JsonElement comment in thread.GetProperty("comments").GetProperty("nodes").EnumerateArray())
        {
            return IsCopilotLogin(ReadAuthorLogin(comment));
        }

        return false;
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

    /// <summary>A deleted account serializes author as null; that is not Copilot.</summary>
    private static string? ReadAuthorLogin(JsonElement authored) =>
        authored.GetProperty("author").ValueKind == JsonValueKind.Object
            ? authored.GetProperty("author").GetProperty("login").GetString()
            : null;

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
