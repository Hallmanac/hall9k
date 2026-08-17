using System.Diagnostics;
using System.Text.Json;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Reads a pull request's closeout signals through gh: state/merge via
/// gh pr view --json, unresolved Copilot review threads via the GraphQL API (the REST
/// surface has no thread-resolution data). Runs in the project's repository so gh
/// resolves the repo from the origin remote.
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

    // first: 100 is a deliberate cap, not missing pagination: threads past it read as
    // quiet, so a monster PR simply waits for a human instead of dispatching follow-ups
    // from an incomplete picture. A PR carrying 100+ review threads has left the range
    // this automation is for.
    private const string ReviewThreadsQuery =
        """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              reviewThreads(first: 100) {
                nodes { isResolved comments(first: 1) { nodes { author { login } } } }
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

        // Review threads matter only while the PR is open; skip the second call otherwise.
        int unresolvedCopilotThreads = state == "OPEN"
            ? await CountUnresolvedCopilotThreadsAsync(repositoryPath, pullRequestUrl, pullRequestNumber, cancellationToken)
            : 0;

        return new PullRequestSnapshot(
            IsMerged: state == "MERGED",
            IsClosed: state == "CLOSED",
            MergedAt: mergedAt,
            ClosedAt: closedAt,
            FailingChecks: failing,
            HasPendingChecks: pending,
            UnresolvedCopilotThreadCount: unresolvedCopilotThreads);
    }

    private static async Task<int> CountUnresolvedCopilotThreadsAsync(
        string repositoryPath, string pullRequestUrl, int pullRequestNumber, CancellationToken cancellationToken)
    {
        (string owner, string name) = ParseOwnerAndRepository(pullRequestUrl);
        string json = await RunGhAsync(
            repositoryPath,
            ["api", "graphql",
                "-f", $"query={ReviewThreadsQuery}",
                "-f", $"owner={owner}",
                "-f", $"name={name}",
                "-F", $"number={pullRequestNumber}"],
            cancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement nodes = document.RootElement
            .GetProperty("data").GetProperty("repository").GetProperty("pullRequest")
            .GetProperty("reviewThreads").GetProperty("nodes");

        int unresolved = 0;
        foreach (JsonElement thread in nodes.EnumerateArray())
        {
            if (!thread.GetProperty("isResolved").GetBoolean() && IsCopilotThread(thread))
            {
                unresolved++;
            }
        }

        return unresolved;
    }

    private static bool IsCopilotThread(JsonElement thread)
    {
        foreach (JsonElement comment in thread.GetProperty("comments").GetProperty("nodes").EnumerateArray())
        {
            // A deleted account serializes author as null; that is not Copilot.
            string? login = comment.GetProperty("author").ValueKind == JsonValueKind.Object
                ? comment.GetProperty("author").GetProperty("login").GetString()
                : null;
            return login?.Contains("copilot", StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }

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
