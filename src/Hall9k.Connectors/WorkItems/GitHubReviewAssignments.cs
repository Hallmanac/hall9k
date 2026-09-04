using System.Globalization;
using System.Text.Json;
using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.WorkItems;

/// <summary>One open pull request the search below found, as gh reported it.</summary>
public sealed record ReviewRequestedPullRequest(int Number, string Url, string Title, string? Body);

/// <summary>
/// Who most recently changed a login's reviewer-request state on a pull request, and when —
/// honestly null in either field when the timeline read could not attribute it (AGENTS.md, never
/// guess at unobserved facts). <see cref="RequestedAt"/> is GitHub's own event timestamp, not
/// this install's poll time — the caller decides which one to record as the observation moment.
/// </summary>
public sealed record ReviewRequestActor(string? Login, DateTimeOffset? RequestedAt);

/// <summary>
/// Which half of a login's reviewer-request history <see cref="GitHubReviewAssignments.FindMostRecentRequestActorAsync"/>
/// should walk the timeline for: <see cref="Requested"/> for who most recently asked for this
/// login as a reviewer (the assignment's own provenance), <see cref="Removed"/> for who most
/// recently withdrew that request (a recall's own provenance). The two are never the same
/// timeline entry — asking for the most recent match of either kind, as a single lookup once
/// did, attributes a request's own requester as its recaller whenever no removal exists at all
/// (independent pre-PR review, adversarial lens, cycle 1).
/// </summary>
public enum ReviewTimelineEventKind
{
    Requested,
    Removed,
}

/// <summary>
/// The discovery half of the auto-pr-review feature (idea e5e98a33): finding which open pull
/// requests currently request this install's own login as a reviewer, in a repository nothing
/// has adopted a task from yet — the one read nothing in this codebase already does, since every
/// existing GitHub read (<see cref="GitHubPullRequestProvider"/>, closeout's own
/// <c>GitHubPullRequestInspector</c>) starts from a pull request the platform already knows the
/// number of.
/// <para>
/// <see cref="CurrentLoginAsync"/> is read fresh through <c>gh</c> every time it is called,
/// deliberately never cached on a connection record: a stale login would silently stop matching
/// new assignments (or start matching someone else's) the moment the machine's <c>gh auth</c>
/// session changes, and a poll that goes quiet that way looks exactly like an idle queue.
/// </para>
/// </summary>
public sealed class GitHubReviewAssignments(ProcessRunner? runner = null)
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    // first: 20 is the same deliberate cap GitHubPullRequestInspector's own reviewRequests query
    // uses, for the same reason — a pull request with a reviewer-request history this long has
    // left the range this feature reads, and reading past it silently would risk missing the
    // most recent entry rather than the oldest.
    private const string TimelineQuery =
        """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              timelineItems(last: 20, itemTypes: [REVIEW_REQUESTED_EVENT, REVIEW_REQUEST_REMOVED_EVENT]) {
                nodes {
                  __typename
                  ... on ReviewRequestedEvent {
                    createdAt
                    actor { login }
                    requestedReviewer { __typename ... on User { login } ... on Bot { login } }
                  }
                  ... on ReviewRequestRemovedEvent {
                    createdAt
                    actor { login }
                    requestedReviewer { __typename ... on User { login } ... on Bot { login } }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The login <c>gh</c> is authenticated as right now — read back from GitHub at call time,
    /// never a configured or previously-observed name. Null when <c>gh</c> could not answer (not
    /// installed, not authenticated, offline): the poll treats that exactly like any other failed
    /// inspection rather than crashing the sweep.
    /// </summary>
    public async Task<string?> CurrentLoginAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner("gh", ["api", "user", "-q", ".login"], workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        string login = result.StandardOutput.Trim();
        return result.ExitCode == 0 && login.Length > 0 ? login : null;
    }

    /// <summary>
    /// Every open pull request in <paramref name="repository"/> (<c>owner/repo</c>) currently
    /// review-requesting <paramref name="login"/>, per GitHub's own search index — read fresh
    /// every call, never diffed against a prior snapshot here. Throws on a <c>gh</c> failure
    /// rather than returning an empty list, so a project this cannot reach counts as a failed
    /// inspection for the poll's own backoff rather than reading as "nothing is assigned here."
    /// </summary>
    public async Task<IReadOnlyList<ReviewRequestedPullRequest>> ListReviewRequestedAsync(
        string repository, string login, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            [
                "pr", "list", "--repo", repository, "--state", "open",
                "--search", $"review-requested:{login}", "--json", "number,url,title,body",
            ],
            workingDirectory, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh pr list --repo {repository} --search \"review-requested:{login}\" exited "
                + $"{result.ExitCode}: {result.StandardError.Trim()}");
        }

        return ParseReviewRequested(result.StandardOutput);
    }

    /// <summary>Split from the gh call so the mapping is testable against recorded gh output.</summary>
    internal static IReadOnlyList<ReviewRequestedPullRequest> ParseReviewRequested(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ReviewRequestedPullRequest> found = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("number", out JsonElement numberElement)
                || numberElement.ValueKind != JsonValueKind.Number
                || !numberElement.TryGetInt32(out int number))
            {
                continue;
            }

            string url = ReadString(item, "url") ?? string.Empty;
            string title = ReadString(item, "title") ?? string.Empty;
            string? body = ReadString(item, "body") is { Length: > 0 } bodyText ? bodyText : null;
            found.Add(new ReviewRequestedPullRequest(number, url, title, body));
        }

        return found;
    }

    /// <summary>
    /// Who most recently requested or withdrew <paramref name="login"/> as a reviewer on this
    /// pull request, and when GitHub recorded it — the provenance a newly observed assignment or
    /// recall is recorded against. <paramref name="kind"/> restricts the walk to that one half of
    /// the timeline: the most recent event of either kind, regardless of which, would attribute a
    /// request's own requester as its recaller whenever the pull request carries no removal event
    /// at all (independent pre-PR review, adversarial lens, cycle 1) — a caller recording who
    /// requested asks for <see cref="ReviewTimelineEventKind.Requested"/>, a caller recording who
    /// recalled asks for <see cref="ReviewTimelineEventKind.Removed"/>, and neither ever sees the
    /// other's kind of event. Best-effort: a <c>gh</c> failure or a timeline this method cannot
    /// read returns an actor whose fields are both null rather than throwing, since a missing
    /// "who" must never block recording the "what" and "when" the caller already knows.
    /// </summary>
    public async Task<ReviewRequestActor> FindMostRecentRequestActorAsync(
        string owner, string name, int number, string login, ReviewTimelineEventKind kind,
        string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner(
                "gh",
                [
                    "api", "graphql",
                    "-f", $"query={TimelineQuery}",
                    "-f", $"owner={owner}",
                    "-f", $"name={name}",
                    "-F", $"number={number}",
                ],
                workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReviewRequestActor(null, null);
        }

        if (result.ExitCode != 0)
        {
            return new ReviewRequestActor(null, null);
        }

        try
        {
            return ParseMostRecentRequestActor(result.StandardOutput, login, kind);
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ReviewRequestActor(null, null);
        }
    }

    /// <summary>Split from the gh call so the mapping is testable against recorded gh output.</summary>
    internal static ReviewRequestActor ParseMostRecentRequestActor(string json, string login, ReviewTimelineEventKind kind)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("repository", out JsonElement repository)
            || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("pullRequest", out JsonElement pullRequest)
            || pullRequest.ValueKind != JsonValueKind.Object
            || !pullRequest.TryGetProperty("timelineItems", out JsonElement timelineItems)
            || !timelineItems.TryGetProperty("nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            // A pull request GraphQL cannot resolve (a stale number in a repository that has
            // moved or renamed) returns "pullRequest": null rather than an error, with no
            // "timelineItems" to walk — honestly unattributed rather than a guess.
            return new ReviewRequestActor(null, null);
        }

        string expectedTypeName = kind == ReviewTimelineEventKind.Requested
            ? "ReviewRequestedEvent"
            : "ReviewRequestRemovedEvent";

        // timelineItems' own `last:` ordering is oldest-first, so the most recent match is found
        // walking backward from the end rather than taking nodes[0].
        for (int index = nodes.GetArrayLength() - 1; index >= 0; index--)
        {
            JsonElement node = nodes[index];
            if (ReadString(node, "__typename") != expectedTypeName
                || !node.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                || reviewer.ValueKind != JsonValueKind.Object
                || ReadString(reviewer, "login") is not { } reviewerLogin
                || !string.Equals(reviewerLogin, login, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? actorLogin = node.TryGetProperty("actor", out JsonElement actor) && actor.ValueKind == JsonValueKind.Object
                ? ReadString(actor, "login")
                : null;
            DateTimeOffset? requestedAt =
                ReadString(node, "createdAt") is { } stamp
                && DateTimeOffset.TryParse(
                    stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                    ? parsed
                    : null;
            return new ReviewRequestActor(actorLogin, requestedAt);
        }

        return new ReviewRequestActor(null, null);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
