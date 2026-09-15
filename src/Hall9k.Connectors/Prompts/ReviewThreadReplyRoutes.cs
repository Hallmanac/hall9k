using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Which shell commands put text inside somebody's pull-request review thread (task: a
/// review-feedback follow-up never answers a human reviewer in the owner's name on its own).
/// The knowledge behind <see cref="ClaudeSettingsFile.ReviewThreadReplyGuardHook"/>, kept here so
/// it is a unit-testable function rather than a regex buried in a hook command.
/// <para>
/// Matched on the command text, because that is all a <c>PreToolUse</c> hook is given — so the
/// routes are recognized by the API surface they name rather than by the program invoked, which
/// is what makes the recognition survive quoting, variables, and <c>bash -c</c> wrapping.
/// </para>
/// <para>
/// <b>Naming a surface is not using it</b>, though, so a refusal takes two halves: the command
/// has to reach GitHub's API at all (<see cref="ReachesTheGitHubApi"/>), AND it has to name a
/// route that writes into a thread. Without the first half, this repository's own source text
/// turned every <c>git grep addPullRequestReviewThreadReply</c>, every
/// <c>rg "comments/.*/replies"</c> and every commit message quoting one of those identifiers into
/// a refusal — a follow-up touching this guard or its own connector hit it every lap (independent
/// pre-PR review, cycle 1, adversarial lens).
/// </para>
/// <para>The write routes:</para>
/// <list type="bullet">
/// <item>the REST reply endpoint (<c>…/pulls/&lt;n&gt;/comments/&lt;id&gt;/replies</c>), which is
/// the line the resolve-review-threads skill teaches, and which has no read form at all;</item>
/// <item>the REST inline-comment endpoint (<c>…/pulls/&lt;n&gt;/comments</c>) carrying
/// <c>in_reply_to</c>, which replies inside an existing thread, or <c>commit_id</c>, which starts
/// a new one — the form <c>walk-pr-review-findings</c> teaches an operator-walked post with. The
/// same path answers a GET, so the path alone decides nothing and the write parameter is what
/// makes it a write;</item>
/// <item>the REST review endpoint (<c>…/pulls/&lt;n&gt;/reviews</c>), which is how this platform's
/// own poster submits a review with inline comments (<c>gh pr review</c> takes none), read the
/// same way — path plus a parameter only a write carries;</item>
/// <item>every GraphQL mutation in the <c>addPullRequestReview…</c> family, matched on the shared
/// prefix: the reply mutation this platform's own poster uses and a session reaches for when the
/// REST route fails, the two that START a thread or submit a review with inline comments (which
/// agents are forbidden from either way, by AGENTS.md's never-start-a-review-thread rule), and
/// the deprecated <c>addPullRequestReviewComment</c>, which an exact-name list missed;</item>
/// <item><c>gh pr review</c>, which submits a review under the caller's login without going near
/// <c>gh api</c> and would otherwise be the way around a reply guard.</item>
/// </list>
/// <para>
/// <b>Deliberately not matched:</b> <c>gh pr comment</c> and the issue-comment endpoint under it.
/// A review BODY is unthreadable, so a top-level comment is the only answer that exists to one,
/// and the follow-up prompt has told sessions to write it since Decisions Log #62. Refusing it
/// here would break an instruction this task was told not to touch. It is a hole in the fence
/// and it is named as one: a session can still address a person at the top level of the pull
/// request under the owner's login.
/// </para>
/// </summary>
public static class ReviewThreadReplyRoutes
{
    /// <summary>
    /// The command can make an HTTP request to GitHub at all. <c>gh api</c> carries the host in
    /// its own configuration; anything else — <c>curl</c>, <c>wget</c>, a <c>python</c> or
    /// <c>node</c> one-liner — has to spell <c>api.github.com</c>, because it has no credentials
    /// or base url of its own to inherit. So this is the surface every poster shares, and a
    /// command matching none of it is naming a route rather than calling one.
    /// </summary>
    private static readonly Regex ReachesTheGitHubApi = new(
        string.Join(
            '|',
            @"\bgh\s+api\b",
            @"\bcurl\b",
            @"\bwget\b",
            @"api\.github\.com"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// <c>gh pr review</c>, which is both the surface and the route: it submits a review under the
    /// caller's login without going near <c>gh api</c>, so it is tested on its own rather than
    /// behind <see cref="ReachesTheGitHubApi"/>.
    /// </summary>
    private static readonly Regex SubmitsAReviewDirectly = new(
        @"\bgh\s+pr\s+review\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Routes = new(
        string.Join(
            '|',
            // The path segment, not the whole url: the command routinely spells the host, the
            // slug, and the numbers through shell variables, and an anchored full-url pattern
            // would match none of those. `\S*` rather than `\d+` for the ids for the same reason.
            @"comments/\S+/replies",
            // The shared prefix, not the three mutation names one at a time: every write mutation
            // in this family begins here — `…ThreadReply`, `…Thread`, the bare review submit, and
            // `addPullRequestReviewComment`, the deprecated reply-into-a-pending-review one an
            // exact-name list missed — and no read query is named `addAnything`.
            "addPullRequestReview"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The two REST paths under a pull request that a GET also answers — the inline-comment
    /// endpoint and the review endpoint. Matching either one alone would refuse a lap's own reads
    /// (listing a pull request's review comments is how a session finds a thread's numeric reply
    /// id), so each is paired with <see cref="WriteParameters"/>.
    /// </summary>
    private static readonly Regex ReadableWritePaths = new(
        @"pulls/\S+/(comments|reviews)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// What a write to one of <see cref="ReadableWritePaths"/> carries and a read never does:
    /// the thread it replies into, the commit a new thread hangs off, the verdict a review
    /// submits, the text itself, or an explicit POST.
    /// </summary>
    private static readonly Regex WriteParameters = new(
        string.Join(
            '|',
            "in_reply_to",
            "commit_id",
            @"\bevent=",
            @"\bbody=",
            @"--method\s+POST",
            @"-X\s+POST"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Whether this command writes into a pull request's review threads. False for everything
    /// else, including every read — a follow-up fetches its threads with
    /// <c>gh api graphql</c> and resolves a bot's with <c>resolveReviewThread</c>, and neither
    /// says anything to anybody — and false for a command that merely quotes one of these routes
    /// in a search pattern or a commit message, which reaches no API at all.
    /// </summary>
    public static bool WritesIntoAReviewThread(string? command) =>
        command.IsNotBlank()
        && (SubmitsAReviewDirectly.IsMatch(command)
            || (ReachesTheGitHubApi.IsMatch(command) && NamesAThreadWrite(command)));

    private static bool NamesAThreadWrite(string command) =>
        Routes.IsMatch(command)
        || (ReadableWritePaths.IsMatch(command) && WriteParameters.IsMatch(command));

    /// <summary>
    /// What the session is told when the guard refuses, in the shape a refusal here has to take:
    /// name the rule, name the route that is allowed instead, and say what happens to a reply it
    /// is not allowed to send. An agent that cannot self-correct from the message will simply
    /// retry the same command.
    /// </summary>
    public const string RefusalReason =
        "This platform posts in-thread replies through " + ClaudeSettingsFile.ReviewThreadReplyCommand
        + " <task> --thread <id> --disposition <fix|decline|route> --body \"<text>\", never through gh "
        + "directly, because only that command can tell a bot's thread from a person's. A decline or a "
        + "route on a thread a PERSON opened is not yours to post at all: draft it, close with the "
        + "DISAGREEMENT block and RESOLUTION: disputed, and the run parks so the owner sends it, edits "
        + "it, or drops it.";
}
