using Hall9k.Connectors.Processes;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Posting a reply a human directed onto a pull request, through the same already-authenticated
/// <c>gh</c> seam every other GitHub read here uses (task: a changes-requested pull-request review
/// from a human becomes a fix lap).
/// <para>
/// It lives in Connectors rather than in the daemon's closeout inspector because the caller is
/// <c>h9k review resolve</c>, in the CLI process: the human is standing in front of it, so the
/// post either succeeds where they can see it or fails where they can answer for it — which is
/// exactly the property a queued, daemon-side post would lose.
/// </para>
/// <para>
/// Two reply verbs, and they are not interchangeable. <see cref="ReplyInThreadAsync"/> answers an inline
/// comment inside its own thread, which is the only place this platform may write in a review
/// (AGENTS.md: nothing here ever STARTS a review thread, because a thread's first comment is what
/// tells a reviewer's words from an agent's). <see cref="CommentAsync"/> answers a review's own
/// body, which GitHub makes unthreadable — there is nothing to reply inside, so a top-level
/// comment is the only answer that exists. <see cref="ResolveThreadAsync"/> closes a thread after
/// the reply that answered it, which the daemon does for a fix reply once its push has carried
/// the fix (task: a review-feedback lap's fix reply posts only after the platform's push has
/// moved the pull request's head).
/// </para>
/// </summary>
public sealed class GitHubReviewReplies(ProcessRunner? runner = null)
{
    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    private const string ReplyMutation =
        """
        mutation($threadId: ID!, $body: String!) {
          addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $threadId, body: $body}) {
            comment { url }
          }
        }
        """;

    private const string ResolveMutation =
        """
        mutation($threadId: ID!) {
          resolveReviewThread(input: {threadId: $threadId}) {
            thread { id isResolved }
          }
        }
        """;

    /// <summary>
    /// The words a top-level comment answering a review body carries: the review's own url first,
    /// so the reviewer reads one shape whichever route the words took.
    /// </summary>
    public static string ReviewBodyComment(string reviewUrl, string body) => $"On {reviewUrl}:\n\n{body}";

    /// <summary>
    /// Replies inside an existing review thread. Throws on any failure, the convention every
    /// other provider write in this codebase uses for a refused call — the caller must be able to
    /// tell "the reviewer was told" from "nobody was told", and a swallowed failure would record
    /// the first while the second is true.
    /// </summary>
    public async Task ReplyInThreadAsync(
        string workingDirectory, string threadId, string body, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            ["api", "graphql", "-f", $"query={ReplyMutation}", "-f", $"threadId={threadId}", "-f", $"body={body}"],
            workingDirectory,
            cancellationToken);
        Ensure(result, $"reply in review thread {threadId}");

        // A GraphQL mutation can answer HTTP 200 with an `errors` array and nothing mutated. gh
        // ordinarily surfaces that as a non-zero exit, which Ensure above already catches; this is
        // the belt to that braces, and it is warranted here and nowhere else in this codebase's gh
        // reads: everything else re-reads on the next sweep, while a silent failure here would let
        // the run record that a person was answered when nothing reached them.
        EnsureNoGraphQlErrors(result, $"the reply in review thread {threadId}");
    }

    /// <summary>
    /// Resolves a review thread. Throws on any failure on the same terms as
    /// <see cref="ReplyInThreadAsync"/>, including the errors-array check: a thread reported
    /// resolved that GitHub left open would read as an answered point to the next closeout read.
    /// </summary>
    public async Task ResolveThreadAsync(
        string workingDirectory, string threadId, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            ["api", "graphql", "-f", $"query={ResolveMutation}", "-f", $"threadId={threadId}"],
            workingDirectory,
            cancellationToken);
        Ensure(result, $"resolve review thread {threadId}");
        EnsureNoGraphQlErrors(result, $"resolving review thread {threadId}");
    }

    private static void EnsureNoGraphQlErrors(ProcessResult result, string what)
    {
        if (result.StandardOutput.Contains("\"errors\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GitHub answered {what} with errors and mutated nothing: {result.StandardOutput.Trim()}");
        }
    }

    /// <summary>
    /// Posts a top-level comment on the pull request — the only answer a review BODY can have.
    /// Throws on failure, on the same terms as <see cref="ReplyInThreadAsync"/>.
    /// </summary>
    public async Task CommentAsync(
        string workingDirectory, int pullRequestNumber, string body, CancellationToken cancellationToken)
    {
        ProcessResult result = await runner(
            "gh",
            ["pr", "comment", pullRequestNumber.ToString(), "--body", body],
            workingDirectory,
            cancellationToken);
        Ensure(result, $"comment on pull request #{pullRequestNumber}");
    }

    /// <summary>
    /// gh's own stderr, quoted rather than summarized: an operator whose post was refused needs to
    /// read what GitHub said (an expired token, a thread that no longer exists, a rate limit) to
    /// know whether to retry, re-authenticate, or reply by hand.
    /// </summary>
    private static void Ensure(ProcessResult result, string what)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh could not {what}: {result.StandardError.Trim()}".Trim());
        }
    }
}
