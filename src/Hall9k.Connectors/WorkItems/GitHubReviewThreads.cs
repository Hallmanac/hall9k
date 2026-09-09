using System.Globalization;
using System.Text.Json;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One comment inside a review thread, as the provider reported it. <see cref="AuthorLogin"/> is
/// honestly null where the payload carried no author (a deleted account, a malformed node) rather
/// than defaulted to anyone (AGENTS.md: never guess at unobserved facts).
/// </summary>
public sealed record ReviewThreadComment(string? AuthorLogin, string Body, DateTimeOffset? CreatedAt);

/// <summary>
/// One review thread on a pull request, read whole: who opened it, whether it is resolved, where
/// it sits in the diff, and every comment on it the read could see.
/// <para>
/// <see cref="CommentCount"/> is the provider's own total rather than <c>Comments.Count</c>,
/// because the comment read is capped: a thread carrying more comments than the cap still reports
/// its real size here while <see cref="Comments"/> holds only what fits. The two are equal for
/// every thread inside the cap, which is every thread this automation is for.
/// </para>
/// </summary>
public sealed record ReviewThread(
    string Id,
    bool IsResolved,
    string? StartedByLogin,
    string? Path,
    int? Line,
    int CommentCount,
    IReadOnlyList<ReviewThreadComment> Comments)
{
    /// <summary>
    /// Where this thread sits, as a reviewer names one: <c>path:line</c>, the same form
    /// <c>h9k pr request-changes --finding</c> takes. Lives on the thread rather than in whichever
    /// surface happens to be printing it, so a scoped lap's packet and any later reader cannot
    /// word the same location two ways.
    /// <para>
    /// A thread whose path the provider did not report says so instead of naming a
    /// plausible-looking file (AGENTS.md, the never-guess rule).
    /// </para>
    /// </summary>
    public string Location() => (Path, Line) switch
    {
        ({ Length: > 0 } path, { } line) => $"{path}:{line}",
        ({ Length: > 0 } path, null) => path,
        _ => "(no file reported)",
    };

    /// <summary>
    /// How many of this thread's comments the read could not carry: the tail past the provider's
    /// own per-thread comment page cap, and always the NEWEST comments, because the page is read
    /// from the front. Zero for every thread inside the cap, which is every thread this automation
    /// is for.
    /// <para>
    /// Named here rather than recomputed at each call site because the two readers of a thread owe
    /// it opposite duties. <see cref="ReplyCountFor"/> counts this tail AS replies, so a reply
    /// landing past the cap still registers as movement; a reader that shows the comments
    /// themselves cannot show what it never read, and has to say the tail is missing instead. A
    /// packet that silently dropped it would be at its most wrong exactly where it hurts — the
    /// unread comments are the most recent ones (independent pre-PR review, cycle 2).
    /// </para>
    /// </summary>
    public int UnreadCommentCount => Math.Max(0, CommentCount - Comments.Count);

    /// <summary>
    /// Where the comments waiting on <paramref name="login"/> start: the index just past the last
    /// comment they wrote themselves, so everything from it onwards is what has been said to them
    /// since their own last word. A thread they have never commented in starts at 0.
    /// <para>
    /// The reviewer's own last comment rather than a timestamp, deliberately, and it is what both
    /// callers of this seam ask for (independent pre-PR review, cycle 1, both lenses): the
    /// follow-through poll counts replies from it, and the scoped lap
    /// (<c>h9k pr review --since-my-review</c>) skips from it, so the count the board shows and
    /// the comments the packet carries can never disagree. A timestamp comparison would need this
    /// platform's clock and the provider's to agree, and a skew of seconds either way would either
    /// invent a reply or lose one permanently — a loss being exactly the silent miss the whole
    /// watch exists to prevent.
    /// </para>
    /// <para>
    /// Matched case-insensitively, the way GitHub itself treats a login, and a comment with no
    /// author is never read as theirs (AGENTS.md: never guess at unobserved facts) — which can
    /// only move the boundary earlier, and an early boundary shows a comment twice rather than
    /// hiding one.
    /// </para>
    /// </summary>
    public int FirstReplyIndexFor(string login)
    {
        for (int index = Comments.Count - 1; index >= 0; index--)
        {
            if (Comments[index].AuthorLogin is { } author
                && string.Equals(author, login, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// How many comments in this thread are waiting on <paramref name="login"/>: everything after
    /// their own last comment in it, which is what "the author answered" is actually observed as.
    /// Their own comments are never counted — counting them would report a reviewer's own review
    /// back to them as an answer, and a follow-up comment of their own would flip the task to
    /// needs-you for an act nobody else took (independent pre-PR review, cycle 1, conformance lens).
    /// <para>
    /// The tail a capped comment page could not carry (<see cref="CommentCount"/> beyond
    /// <see cref="Comments"/>) is counted as replies, because everything in it sits after every
    /// comment that WAS read. In the one case that can overstate — a thread past the 100-comment
    /// cap where the reviewer's own last comment is in the unread tail — the count is a ceiling,
    /// which costs a notification the reviewer did not need rather than silently missing one.
    /// </para>
    /// </summary>
    public int ReplyCountFor(string login) =>
        Comments.Count - FirstReplyIndexFor(login) + UnreadCommentCount;
}

/// <summary>
/// What one look at a pull request's review conversation saw. Everything a posted review's
/// follow-through needs from the provider, in one read: whether the pull request is still open,
/// where its head is, how many commits it carries, every review thread, and who still owes a
/// review.
/// <para>
/// <see cref="CommitCount"/> and <see cref="HeadSha"/> are nullable because a payload that does
/// not carry them is a payload that does not carry them: a null costs a caller its
/// "commits pushed since" number and nothing else, where a zero would read as an observed fact.
/// <see cref="ThreadsTruncated"/> says the thread page itself was capped, so a caller counting
/// threads knows its count is a floor.
/// </para>
/// </summary>
public sealed record ReviewConversation(
    bool IsOpen,
    bool IsMerged,
    bool IsClosed,
    string? HeadSha,
    int? CommitCount,
    IReadOnlyList<ReviewThread> Threads,
    IReadOnlyList<string> OutstandingReviewerLogins,
    bool ThreadsTruncated)
{
    /// <summary>Every thread <paramref name="login"/> opened, case-insensitively — the only threads a follow-through of that login's review is ever about.</summary>
    public IReadOnlyList<ReviewThread> ThreadsStartedBy(string login) =>
    [
        .. Threads.Where(thread =>
            thread.StartedByLogin is not null
            && string.Equals(thread.StartedByLogin, login, StringComparison.OrdinalIgnoreCase)),
    ];

    /// <summary>Whether <paramref name="login"/> has a review request outstanding right now — the second reason a follow-through stays open.</summary>
    public bool ReReviewRequestedOf(string login) =>
        OutstandingReviewerLogins.Any(outstanding =>
            string.Equals(outstanding, login, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The seam onto a pull request's review conversation (task: a pr-review task stays open while the
/// pull request's review threads are unresolved), so the follow-through's decision table tests
/// against a scripted conversation rather than only against a live GitHub account.
/// <para>
/// It lives in Connectors, not in the daemon beside <c>IPullRequestInspector</c>, because two
/// processes need the identical read: the daemon's follow-through poll asks "has the author
/// answered", and <c>h9k pr review --since-my-review</c>, in the CLI, asks "what exactly did they
/// say" — and those two must never disagree about which threads belong to the reviewer.
/// </para>
/// </summary>
public interface IReviewConversationReader
{
    /// <summary>
    /// One read of <paramref name="repository"/>#<paramref name="number"/>, from
    /// <paramref name="workingDirectory"/> so it uses that repository's own <c>gh</c>
    /// authentication.
    /// </summary>
    Task<ReviewConversation> ReadAsync(
        string repository, int number, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>gh</c> implementation: one GraphQL call per look, deliberately, because the
/// follow-through poll runs on the closeout watcher's cadence for every waiting review this node
/// holds and a second round trip per task per tick is a cost with nothing to show for it.
/// <para>
/// Deliberately separate from the daemon's own <c>GitHubPullRequestInspector</c> rather than
/// folded into it. That inspector answers a different question — "may this pull request merge" —
/// and its snapshot is shaped around checks, reviewers, and merge gates; the threads it does read,
/// it reads as counts and id sets, which is exactly what a "did this conversation move" question
/// cannot use. Widening it would also drag a merge-gate read into the CLI, which cannot reference
/// the daemon at all.
/// </para>
/// </summary>
public sealed class GitHubReviewThreads(ProcessRunner? runner = null) : IReviewConversationReader
{
    /// <summary>
    /// The thread page cap. A hundred threads on one pull request is already past the range this
    /// automation is for, and <c>pageInfo.hasNextPage</c> is what lets a caller know its count is
    /// a floor rather than silently trusting a truncated page — the same discipline
    /// <c>GitHubPullRequestInspector</c>'s own reviewThreads read keeps.
    /// </summary>
    private const int ThreadPageSize = 100;

    /// <summary>
    /// The per-thread comment cap. <c>first</c> and not <c>last</c>, matching the closeout
    /// inspector's own choice and for its reason: the thread's OPENER is what decides whose
    /// thread it is, and a read that dropped the first comment would misattribute the thread
    /// entirely. <c>totalCount</c> beside it is what keeps a capped thread's own count honest, so
    /// a reply landing past the cap still registers as movement.
    /// </summary>
    private const int CommentPageSize = 100;

    private static readonly string ConversationQuery =
        $$"""
        query($owner: String!, $name: String!, $number: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              state
              merged
              closed
              headRefOid
              commits(first: 1) { totalCount }
              reviewThreads(first: {{ThreadPageSize}}) {
                nodes {
                  id
                  isResolved
                  path
                  line
                  originalLine
                  comments(first: {{CommentPageSize}}) {
                    totalCount
                    nodes { author { login } body createdAt }
                  }
                }
                pageInfo { hasNextPage }
              }
              reviewRequests(first: 20) {
                nodes { requestedReviewer { __typename ... on User { login } ... on Bot { login } ... on Team { slug } } }
              }
            }
          }
        }
        """;

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    public async Task<ReviewConversation> ReadAsync(
        string repository, int number, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!TrySplitOwnerAndName(repository, out string owner, out string name))
        {
            throw new DomainValidationException(
                $"'{RelayedText.OneLine(repository)}' is not an owner/repo pair, so there is no repository "
                + "to read a pull request's review threads from. A pr-review task's own reference always is "
                + "one; a value that is not needs a human look at the task's stream.");
        }

        ProcessResult result = await RunGhAsync(
            [
                "api", "graphql",
                "-f", $"query={ConversationQuery}",
                "-f", $"owner={owner}",
                "-f", $"name={name}",
                "-F", $"number={number.ToString(CultureInfo.InvariantCulture)}",
            ],
            workingDirectory,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new DomainValidationException(
                $"gh could not read {repository}#{number}'s review threads from {workingDirectory}: "
                + $"{RelayedText.OneLine(result.StandardError).Trim()}");
        }

        return Parse(result.StandardOutput);
    }

    /// <summary>
    /// The reading half, split from the <c>gh</c> call so the classification rules — whose thread
    /// is whose, what counts as truncated, how an absent count is reported — are testable against
    /// provider payloads rather than only in production. Internal for exactly that; nothing else
    /// calls it.
    /// </summary>
    internal static ReviewConversation Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement pullRequest = document.RootElement
            .GetProperty("data").GetProperty("repository").GetProperty("pullRequest");

        string? state = ReadString(pullRequest, "state");
        bool merged = ReadBool(pullRequest, "merged");
        bool closed = ReadBool(pullRequest, "closed");

        JsonElement reviewThreads = pullRequest.GetProperty("reviewThreads");
        bool truncated = reviewThreads.TryGetProperty("pageInfo", out JsonElement pageInfo)
            && pageInfo.TryGetProperty("hasNextPage", out JsonElement hasNextPage)
            && hasNextPage.ValueKind == JsonValueKind.True;

        List<ReviewThread> threads = [];
        foreach (JsonElement thread in reviewThreads.GetProperty("nodes").EnumerateArray())
        {
            // GitHub types a thread's id as a non-null ID, so a null here is a malformed payload.
            // Skipped rather than coalesced to "": a fabricated key would collapse every such
            // thread onto one identity in the watermark, and a malformed payload should undercount
            // rather than corrupt the comparison the whole watch turns on. Exactly the reading
            // GitHubPullRequestInspector.ParseReviews makes of the same field.
            if (ReadString(thread, "id") is not { } id)
            {
                continue;
            }

            List<ReviewThreadComment> comments = [];
            JsonElement commentsNode = thread.GetProperty("comments");
            foreach (JsonElement comment in commentsNode.GetProperty("nodes").EnumerateArray())
            {
                comments.Add(new ReviewThreadComment(
                    ReadActorLogin(comment),
                    ReadString(comment, "body") ?? string.Empty,
                    ReadTimestamp(comment, "createdAt")));
            }

            threads.Add(new ReviewThread(
                id,
                ReadBool(thread, "isResolved"),
                comments.Count > 0 ? comments[0].AuthorLogin : null,
                ReadString(thread, "path"),
                ReadInt(thread, "line") ?? ReadInt(thread, "originalLine"),
                // The provider's own total wherever it gave one, and the read's own count where it
                // did not — which can only undercount, and an undercount costs a poll a
                // notification it would otherwise send rather than producing a false one.
                ReadInt(commentsNode, "totalCount") ?? comments.Count,
                comments));
        }

        return new ReviewConversation(
            IsOpen: string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase),
            IsMerged: merged,
            IsClosed: closed,
            HeadSha: ReadString(pullRequest, "headRefOid"),
            CommitCount: pullRequest.TryGetProperty("commits", out JsonElement commits)
                ? ReadInt(commits, "totalCount")
                : null,
            Threads: threads,
            OutstandingReviewerLogins: ReadOutstandingReviewerLogins(pullRequest),
            ThreadsTruncated: truncated);
    }

    /// <summary>
    /// Every reviewer with a request outstanding right now, unfiltered — a bot's request included,
    /// because this list is only ever asked about one specific login and filtering would make the
    /// answer depend on a classification this read has no reason to make. A requested TEAM has no
    /// login, so it is recorded by slug under the same <c>team:</c> prefix the closeout inspector
    /// uses, which cannot collide with a personal login (GitHub logins cannot contain a colon).
    /// </summary>
    private static IReadOnlyList<string> ReadOutstandingReviewerLogins(JsonElement pullRequest)
    {
        if (!pullRequest.TryGetProperty("reviewRequests", out JsonElement requests)
            || !requests.TryGetProperty("nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> logins = [];
        foreach (JsonElement request in nodes.EnumerateArray())
        {
            if (!request.TryGetProperty("requestedReviewer", out JsonElement reviewer)
                || reviewer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (ReadString(reviewer, "login") is { } login)
            {
                logins.Add(login);
            }
            else if (ReadString(reviewer, "slug") is { } slug)
            {
                logins.Add($"team:{slug}");
            }
        }

        return logins;
    }

    private static bool TrySplitOwnerAndName(string repository, out string owner, out string name)
    {
        string[] parts = repository.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && parts[0].IsNotBlank() && parts[1].IsNotBlank())
        {
            owner = parts[0];
            name = parts[1];
            return true;
        }

        owner = string.Empty;
        name = string.Empty;
        return false;
    }

    private static string? ReadActorLogin(JsonElement authored) =>
        authored.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object
            ? ReadString(author, "login")
            : null;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property) =>
        ReadString(element, property) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;

    private async Task<ProcessResult> RunGhAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await runner("gh", arguments, workingDirectory, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw Directory.Exists(workingDirectory)
                ? new DomainValidationException(
                    "Could not run gh, the GitHub CLI a review's follow-through reads through: "
                    + $"{exception.Message}. Install it (https://cli.github.com) and sign in with "
                    + "'gh auth login', then try again.")
                : new DomainNotFoundException(
                    $"The directory gh would run in does not exist: {workingDirectory}. gh reads the pull "
                    + $"request from the directory it runs in. gh reported: {exception.Message}");
        }
        catch (ProcessOutputStuckException exception)
        {
            throw new DomainValidationException(
                $"{exception.Message} Run the same gh command by hand from {workingDirectory} to read the "
                + "error it could not print here, then try again.");
        }
        catch (TimeoutException exception)
        {
            throw new DomainValidationException(
                $"{exception.Message} It was reaching GitHub from {workingDirectory}. A call that stops here "
                + "is usually gh, or something gh started, waiting on input it cannot ask for — run "
                + "'gh auth status' by hand from that directory, then try again.");
        }
    }
}
