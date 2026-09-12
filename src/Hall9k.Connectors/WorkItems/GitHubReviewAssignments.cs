using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;

namespace Hall9k.Connectors.WorkItems;

/// <summary>One open pull request the search below found, as gh reported it.</summary>
public sealed record ReviewRequestedPullRequest(int Number, string Url, string Title, string? Body);

/// <summary>
/// The login <c>gh</c> is authenticated as right now, or why it could not be read —
/// <see cref="GitHubReviewAssignments.ReadCurrentLoginAsync"/>'s answer. Exactly one of
/// <see cref="Login"/> and <see cref="Error"/> is ever set. <see cref="AuthenticationRefusal"/>
/// tells the one failure whose remedy is a login (<c>gh auth login</c>) apart from every other,
/// whose remedy is the tool or the machine.
/// </summary>
public sealed record GitHubLoginRead(string? Login, string? Error, bool AuthenticationRefusal);

/// <summary>
/// Who most recently changed a login's reviewer-request state on a pull request, and when —
/// honestly null in either field when the timeline read could not attribute it (AGENTS.md, never
/// guess at unobserved facts). <see cref="RequestedAt"/> is GitHub's own event timestamp, not
/// this install's poll time — the caller decides which one to record as the observation moment.
/// <see cref="Found"/> is the fact <see cref="Login"/>/<see cref="RequestedAt"/> alone cannot
/// carry (independent pre-PR review, cycle 1, adversarial lens): whether a matching timeline
/// event of the requested <see cref="ReviewTimelineEventKind"/> was actually observed at all,
/// decoupled from whether its actor or timestamp could also be read. A caller deciding whether a
/// withdrawal genuinely happened needs this, not the attribution — a removal event with no
/// readable actor is still a removal, and a gh failure or a genuinely absent event must never be
/// read as one.
/// </summary>
public sealed record ReviewRequestActor(bool Found, string? Login, DateTimeOffset? RequestedAt);

/// <summary>
/// One comment <see cref="GitHubReviewAssignments.FindMentionCommentsAsync"/> found mentioning the
/// install's own login on a pull request (idea 2f079bcd, auto-pr-review's second trigger) — an
/// issue comment, a review-comment thread reply, or a review's own top-level body, whichever
/// carried the <c>@login</c> text. <see cref="AuthorLogin"/> is what the caller filters the
/// install's own comments out with (a comment the install itself wrote never counts), and
/// <see cref="CommentId"/> is the dedupe key a later sweep tick compares against so the same
/// comment never fires twice.
/// </summary>
public sealed record PullRequestMentionComment(
    string CommentId, string AuthorLogin, string Body, string Url, DateTimeOffset CreatedAt);

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
    public async Task<string?> CurrentLoginAsync(string workingDirectory, CancellationToken cancellationToken) =>
        (await ReadCurrentLoginAsync(workingDirectory, cancellationToken)).Login;

    /// <summary>
    /// The same read, with the failure kept instead of swallowed — what a
    /// <c>tracker-assignee</c> claim gate needs (idea 64c75e43), since a gate that fails closed
    /// has to say <em>why</em> it could not read this install's own identity and what ends the
    /// hold. <see cref="CurrentLoginAsync"/> is this method with the reason dropped, which is
    /// right for a poll whose only move is to try again next tick.
    /// <para>
    /// <see cref="GitHubLoginRead.AuthenticationRefusal"/> is true only when gh actually said the
    /// account was the problem, matched on the same two strings
    /// <c>GitHubWorkItemProvider.Explain</c> matches: a gh that will not start at all, or a
    /// proxy asking for its own credentials, is somebody else's failure and must not send the
    /// reader to re-authenticate a login nothing refused.
    /// </para>
    /// </summary>
    public async Task<GitHubLoginRead> ReadCurrentLoginAsync(
        string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner("gh", ["api", "user", "-q", ".login"], workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new GitHubLoginRead(
                null,
                $"gh could not be run from {workingDirectory} to read the login it is authenticated as: "
                + $"{RelayedText.OneLine(exception.Message)}",
                AuthenticationRefusal: false);
        }

        string login = result.StandardOutput.Trim();
        if (result.ExitCode == 0 && login.Length > 0)
        {
            return new GitHubLoginRead(login, null, AuthenticationRefusal: false);
        }

        // gh's stderr on the way into a sentence a terminal prints and a one-line status row
        // frames, so it goes through RelayedText first — exactly what every other gh-stderr path
        // in this feature does (GitHubWorkItemProvider.Explain, ExplainCreate). The commonest
        // failure here is the unauthenticated one, whose message is several lines of its own, and
        // an error carrying those newlines verbatim breaks the row that quotes it (independent
        // pre-PR review, cycle 1, adversarial lens). Folding costs the matching below nothing:
        // the words are all still there, on one line.
        string reported = RelayedText.OneLine(result.StandardError).Trim();
        return new GitHubLoginRead(
            null,
            result.ExitCode == 0
                ? $"gh api user exited successfully from {workingDirectory} but printed no login, so this "
                  + "install's own GitHub identity is unknown."
                : $"gh api user exited {result.ExitCode} from {workingDirectory}: {reported}",
            AuthenticationRefusal: reported.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
                || reported.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every open pull request in <paramref name="repository"/> (<c>owner/repo</c>) currently
    /// review-requesting <paramref name="login"/>, per GitHub's own search index — read fresh
    /// every call, never diffed against a prior snapshot here. Throws on a <c>gh</c> failure
    /// rather than returning an empty list, so a project this cannot reach counts as a failed
    /// inspection for the poll's own backoff rather than reading as "nothing is assigned here."
    /// <para>
    /// <c>--limit</c> is explicit rather than left at gh's own default of 30 (independent pre-PR
    /// review, cycle 1, conformance lens): a repository with more standing requests than the
    /// default page silently truncates both this read and <c>ConcludeWithdrawnAsync</c>'s own
    /// comparison against it, which reads a truncated candidate as recalled and abandons a task
    /// nobody withdrew. 500 is far beyond any reviewer queue a real install carries — a login
    /// with that many simultaneous requests has a different problem than this poll can solve —
    /// so a real truncation should never happen in practice; it is chosen explicitly rather than
    /// left to gh's own default precisely so it never becomes an invisible one again.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ReviewRequestedPullRequest>> ListReviewRequestedAsync(
        string repository, string login, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            [
                "pr", "list", "--repo", repository, "--state", "open", "--limit", "500",
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

    // first: 100 on every leg — a generous cap in the TimelineQuery's own spirit (that field's own
    // doc explains the choice): a pull request carrying more comments, review threads, or reviews
    // than this has left the range this feature reads, and reading past it silently risks missing
    // the very mention this poll exists to find rather than the oldest one. Nested per-thread
    // comments are capped lower (last: 20) because a thread with more replies than that is rare
    // and the query's own cost is threads times comments.
    private const string MentionQuery =
        """
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              comments(last: 100) {
                nodes { id author { login } body url createdAt }
              }
              reviewThreads(last: 100) {
                nodes {
                  comments(last: 20) {
                    nodes { id author { login } body url createdAt }
                  }
                }
              }
              reviews(last: 100) {
                nodes { id author { login } body url submittedAt }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Every open pull request in <paramref name="repository"/> currently mentioning
    /// <paramref name="login"/> anywhere GitHub's own <c>mentions:</c> search qualifier looks — a
    /// direct <c>@login</c> mention, never a team handle, which is a different qualifier the
    /// search never matches (idea 2f079bcd, decision 1). Read fresh every call, exactly like
    /// <see cref="ListReviewRequestedAsync"/>, and for the identical reason: this is the
    /// discovery half, never a diffed snapshot.
    /// </summary>
    public async Task<IReadOnlyList<ReviewRequestedPullRequest>> ListMentionedAsync(
        string repository, string login, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            [
                "pr", "list", "--repo", repository, "--state", "open", "--limit", "500",
                "--search", $"mentions:{login}", "--json", "number,url,title,body",
            ],
            workingDirectory, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh pr list --repo {repository} --search \"mentions:{login}\" exited "
                + $"{result.ExitCode}: {result.StandardError.Trim()}");
        }

        return ParseReviewRequested(result.StandardOutput);
    }

    /// <summary>
    /// Every comment on one pull request whose text actually names <paramref name="login"/> as an
    /// <c>@mention</c> — issue comments, review-comment thread replies, and a review's own
    /// top-level body, all three read in one query and merged, oldest first. The
    /// <c>mentions:</c> search above says a pull request carries one somewhere; this is what finds
    /// which comment it actually is, since the search itself names no comment id.
    /// <para>
    /// A comment authored by <paramref name="login"/> itself is excluded here, at the source,
    /// rather than left to the caller: the install's own comments never count as a trigger (idea
    /// 2f079bcd, decision 2), and a caller that forgot the filter would otherwise see its own
    /// replies as fresh mentions forever.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PullRequestMentionComment>> FindMentionCommentsAsync(
        string owner, string name, int number, string login, string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            [
                "api", "graphql",
                "-f", $"query={MentionQuery}",
                "-f", $"owner={owner}",
                "-f", $"name={name}",
                "-F", $"number={number}",
            ],
            workingDirectory, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh api graphql (mentions on {owner}/{name}#{number}) exited {result.ExitCode}: "
                + $"{result.StandardError.Trim()}");
        }

        return ParseMentionComments(result.StandardOutput, login);
    }

    /// <summary>Split from the gh call so the mapping is testable against recorded gh output.</summary>
    internal static IReadOnlyList<PullRequestMentionComment> ParseMentionComments(string json, string login)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("repository", out JsonElement repository)
            || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("pullRequest", out JsonElement pullRequest)
            || pullRequest.ValueKind != JsonValueKind.Object)
        {
            // "pullRequest": null — a stale number in a repository that has moved or renamed.
            // Honestly nothing found rather than a guess (AGENTS.md).
            return [];
        }

        List<PullRequestMentionComment> found = [];
        AddMatching(pullRequest, "comments", found, login);
        if (pullRequest.TryGetProperty("reviewThreads", out JsonElement threads)
            && threads.TryGetProperty("nodes", out JsonElement threadNodes)
            && threadNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement thread in threadNodes.EnumerateArray())
            {
                AddMatching(thread, "comments", found, login);
            }
        }

        AddMatching(pullRequest, "reviews", found, login, timestampProperty: "submittedAt");

        return [.. found.OrderBy(comment => comment.CreatedAt)];
    }

    /// <summary>
    /// One <c>{ nodes: [...] }</c> collection's own mentioning comments, appended to
    /// <paramref name="found"/> — the shared walk <see cref="ParseMentionComments"/> runs over
    /// issue comments, each review thread's own replies, and review bodies alike, since all three
    /// share the identical <c>id</c>/<c>author</c>/<c>body</c>/<c>url</c> shape and differ only in
    /// which timestamp field GitHub names it (a review's own <c>submittedAt</c> rather than every
    /// comment shape's <c>createdAt</c>).
    /// </summary>
    private static void AddMatching(
        JsonElement parent, string collectionProperty, List<PullRequestMentionComment> found, string login,
        string timestampProperty = "createdAt")
    {
        if (!parent.TryGetProperty(collectionProperty, out JsonElement collection)
            || !collection.TryGetProperty("nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // Bounded on the far side only: a GitHub username is [A-Za-z0-9-], so "@brian" must not be
        // read as a mention of "brian" when the text actually says "@brianhall99" or "@brian-2" —
        // a plain Contains check matches both as a false positive (independent pre-PR review,
        // cycle 1, adversarial lens). The near side needs no boundary of its own: GitHub reads
        // "@login" as a mention regardless of what character precedes the "@" (mid-word "cc@login"
        // included), so anchoring there would silently drop genuine mentions instead.
        Regex mentionPattern = new(
            $@"@{Regex.Escape(login)}(?![A-Za-z0-9-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            string? id = ReadString(node, "id");
            string? body = ReadString(node, "body");
            string? authorLogin = node.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object
                ? ReadString(author, "login")
                : null;
            if (id.IsBlank() || body.IsBlank() || authorLogin.IsBlank()
                || string.Equals(authorLogin, login, StringComparison.OrdinalIgnoreCase)
                || !mentionPattern.IsMatch(body))
            {
                continue;
            }

            string url = ReadString(node, "url") ?? string.Empty;
            DateTimeOffset createdAt =
                ReadString(node, timestampProperty) is { } stamp
                && DateTimeOffset.TryParse(
                    stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            found.Add(new PullRequestMentionComment(id, authorLogin, body, url, createdAt));
        }
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
            return new ReviewRequestActor(Found: false, null, null);
        }

        if (result.ExitCode != 0)
        {
            return new ReviewRequestActor(Found: false, null, null);
        }

        try
        {
            return ParseMostRecentRequestActor(result.StandardOutput, login, kind);
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ReviewRequestActor(Found: false, null, null);
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
            return new ReviewRequestActor(Found: false, null, null);
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
            return new ReviewRequestActor(Found: true, actorLogin, requestedAt);
        }

        return new ReviewRequestActor(Found: false, null, null);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
