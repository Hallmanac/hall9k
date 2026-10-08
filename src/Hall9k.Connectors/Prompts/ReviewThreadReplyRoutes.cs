using System.Text.RegularExpressions;

namespace Hall9k.Connectors.Prompts;

/// <summary>
/// Which shell commands put text onto somebody's pull request by a route other than
/// <c>h9k pr reply</c>: inside a review thread, or at the top level of the pull request or one of
/// its issue-style comments (task: a review-feedback follow-up never answers a human reviewer in
/// the owner's name on its own; and a dispatched session never speaks to a person at the top level
/// of a pull request on its own). The knowledge behind
/// <see cref="ClaudeSettingsFile.ReviewThreadReplyGuardHook"/>, kept here so it is a unit-testable
/// function rather than a regex buried in a hook command.
/// <para>
/// Matched on the command text, because that is all a <c>PreToolUse</c> hook is given — so the
/// API routes are recognized by the surface they name rather than by the program invoked, which
/// is what makes the recognition survive quoting, variables, and <c>bash -c</c> wrapping. The
/// <c>gh</c> subcommands that reach GitHub without <c>gh api</c> cannot be told from text that way,
/// so they are matched as programs (<see cref="ShellInvocations"/>): invoked, not merely mentioned.
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
/// <item>the REST comment endpoint without a pull-request number
/// (<c>…/pulls/comments/&lt;id&gt;</c>), which is where a single review comment is edited and
/// deleted: rewriting what a thread already says, or removing a reviewer's words, is writing into
/// it, and a POST-only test for the write let every <c>PATCH</c> and <c>DELETE</c> spelling of it
/// through (Copilot, PR #397);</item>
/// <item>the REST review endpoint (<c>…/pulls/&lt;n&gt;/reviews</c>), which is how this platform's
/// own poster submits a review with inline comments (<c>gh pr review</c> takes none), read the
/// same way — path plus a parameter only a write carries;</item>
/// <item>every GraphQL mutation in the <c>addPullRequestReview…</c> family, matched on the shared
/// prefix: the reply mutation this platform's own poster uses and a session reaches for when the
/// REST route fails, the two that START a thread or submit a review with inline comments (which
/// agents are forbidden from either way, by AGENTS.md's never-start-a-review-thread rule), and
/// the deprecated <c>addPullRequestReviewComment</c>, which an exact-name list missed;</item>
/// <item><c>gh pr review</c>, which submits a review under the caller's login without going near
/// <c>gh api</c> and would otherwise be the way around a reply guard;</item>
/// <item>the top-level routes, which answer a review BODY or speak to a person at the top of the
/// pull request (task: a dispatched session never speaks to a person at the top level of a pull
/// request on its own): <c>gh pr comment</c> and <c>gh issue comment</c>, which share one number
/// space and one resource so the second posts on a pull request as readily as the first; the REST
/// issue-comment endpoint (<c>…/issues/&lt;n&gt;/comments</c>, which a GET also answers, so it needs
/// a write parameter, a mutating method, or an <c>--input</c> body to count) and its edit and
/// delete path (<c>…/issues/comments/&lt;id&gt;</c>); and the GraphQL <c>addComment</c>,
/// <c>updateIssueComment</c> and <c>deleteIssueComment</c> mutations. A review BODY is
/// unthreadable, so its answer is a top-level comment, and that answer goes through
/// <c>h9k pr reply --review</c>, which can tell a bot's review from a person's. Reads stay open:
/// <c>gh pr view --comments</c> and a GET of the comments endpoint refuse nothing.</item>
/// </list>
/// <para>
/// <b>What this still does not stop, stated plainly.</b> A session that reaches GitHub's API
/// through a client library of its own, spelling neither a route this recognizes nor a host, or
/// that hides the program behind an indirection the command text does not show, is outside what
/// any of this sees. It refuses the routes a session actually reaches for.
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
    /// The <c>gh</c> subcommands that put text on a pull request without going near <c>gh api</c>,
    /// so each is both the surface and the route and is tested on its own rather than behind
    /// <see cref="ReachesTheGitHubApi"/>: <c>gh pr review</c> submits a review under the caller's
    /// login, and <c>gh pr comment</c> and <c>gh issue comment</c> post at the top level (the issue
    /// form reaches a pull request too, since the two share a number space).
    /// </summary>
    private static readonly (string Group, string Verb)[] DirectPostingSubcommands =
    [
        ("pr", "review"),
        ("pr", "comment"),
        ("issue", "comment"),
    ];

    private static readonly Regex Routes = new(
        string.Join(
            '|',
            // The path segment, not the whole url: the command routinely spells the host, the
            // slug, and the numbers through shell variables, and an anchored full-url pattern
            // would match none of those. `\S*` rather than `\d+` for the ids for the same reason.
            @"comments/\S+/replies",
            // The shared prefix, not the mutation names one at a time: every write mutation in
            // this family begins here — `…ThreadReply`, `…Thread`, the bare review submit, and
            // `addPullRequestReviewComment`, the deprecated reply-into-a-pending-review one an
            // exact-name list missed — and no read query is named `addAnything`. The edit and
            // delete verbs ride the same prefix for the same reason (Copilot, PR #397): rewriting
            // the words already in a thread, or removing a reviewer's, is writing into it, and
            // GraphQL spells those `updatePullRequestReview…` and `deletePullRequestReview…`.
            "(add|update|delete)PullRequestReview",
            // The top-level comment mutations (task: a dispatched session never speaks to a person
            // at the top level of a pull request on its own): `addComment` posts on any
            // subject, a pull request included, and the other two rewrite or remove what is
            // already there. Word-bounded so `addCommentSomethingElse` is not this one.
            @"\b(addComment|updateIssueComment|deleteIssueComment)\b"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The REST paths under a pull request that a GET also answers — the inline-comment endpoint,
    /// the review endpoint, and the issue-comment endpoint a pull request's top-level comments
    /// live at (<c>…/issues/&lt;n&gt;/comments</c>, and <c>…/issues/comments/&lt;id&gt;</c> for an edit or
    /// a delete). Matching any one alone would refuse a lap's own reads (listing
    /// a pull request's review comments is how a session finds a thread's numeric reply id), so
    /// each is paired with <see cref="WriteParameters"/>.
    /// <para>
    /// The pull request's number is optional because one route does not carry it: a single review
    /// comment is edited and deleted at <c>…/pulls/comments/&lt;id&gt;</c>, with no number in it
    /// at all, and the number-bearing pattern matched none of that path (Copilot, PR #397).
    /// </para>
    /// </summary>
    private static readonly Regex ReadableWritePaths = new(
        @"(pulls/(\S+/)?(comments|reviews)|issues/(\S+/)?comments)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// What a write to one of <see cref="ReadableWritePaths"/> carries and a read never does:
    /// the thread it replies into, the commit a new thread hangs off, the verdict a review
    /// submits, the text itself, or an explicitly named mutating method.
    /// <para>
    /// Every mutating method, not <c>POST</c> alone, and in every spelling the two clients accept
    /// (Copilot, PR #397): a POST-only test left <c>gh api --method=PATCH …/pulls/comments/&lt;id&gt;</c>
    /// and <c>curl -XDELETE</c> matching nothing, and those rewrite and remove the comments
    /// already in a thread. <c>--request</c> is curl's own long spelling of the same flag, and the
    /// separator class takes the attached (<c>-XPOST</c>), equals (<c>--method=PATCH</c>) and
    /// quoted forms, all of which are one flag to the program and were three holes here.
    /// </para>
    /// <para>
    /// <b>The field has to be SET, not merely named.</b> <c>in_reply_to</c> and <c>commit_id</c>
    /// are response fields as well as request ones, so matching them as bare substrings refused a
    /// read that merely filtered on one:
    /// <c>gh api …/pulls/7/comments --jq 'map(select(.in_reply_to_id == null))'</c> is how a
    /// session finds a thread's numeric reply id, and it was blocked with a message telling it to
    /// use <c>h9k pr reply</c>, which cannot list anything (independent pre-PR review, cycle 1,
    /// adversarial lens). So each is matched only where an assignment follows it — <c>-f
    /// in_reply_to=123</c>, or the JSON <c>"in_reply_to":</c> of an <c>--input</c> body — with a
    /// word-start guard ahead of it so a longer field name is not this one, and a negative
    /// lookahead behind the separator so jq's own <c>==</c> comparison is a read rather than a
    /// write.
    /// </para>
    /// </summary>
    private static readonly Regex WriteParameters = new(
        string.Join(
            '|',
            @"(?<!\w)in_reply_to[\s""']*[=:](?!=)",
            @"(?<!\w)commit_id[\s""']*[=:](?!=)",
            @"\bevent=",
            @"\bbody=",
            @"(--method|--request|-X)[\s='""]*(POST|PUT|PATCH|DELETE)\b"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The flags that supply a request body without naming a method: <c>gh api --input</c> and
    /// curl's data flags, which is how a top-level comment is posted from a prepared payload.
    /// <para>
    /// Unlike <see cref="WriteParameters"/> these are tested against ONE simple command that also
    /// reaches the API (<see cref="SendsABodyFlag"/>), not the whole text, and the data flags are
    /// case-sensitive. <c>-d</c> and <c>--json</c> mean a body to <c>curl</c> and something else to
    /// the other programs a command line strings together: <c>gh pr view --json reviews</c> and
    /// <c>cut -d' '</c> are reads, and curl's <c>-D</c> dumps response headers. Matched over the
    /// whole text they refused a read of the comments endpoint whenever any program beside it used
    /// one (independent pre-PR review, cycle 1, adversarial lens). The attached forms
    /// (<c>-d@file</c>, <c>-d{…}</c>, which is what <c>-d'{…}'</c> reads as once its quotes are
    /// gone) count, and so does curl's short-flag cluster ending in <c>d</c> (<c>-sd</c>, <c>-sSd</c>),
    /// restricted to curl's own no-argument letters so an unrelated word ending in <c>d</c> is not a body.
    /// </para>
    /// </summary>
    private static readonly Regex BodyFlags = new(
        @"(?<![\w-])(?:--input\b|(?:-[sSkLvfiIgGOJZq#]*d|--data|--data-raw|--data-binary|--data-urlencode|--json)(?=[\s='""@{\[]|$))",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The message argument of a <c>git commit</c> (<c>-m "…"</c>, <c>--message='…'</c>), whose
    /// text names routes without calling them: this repository writes commit messages like
    /// <c>refuse addComment sent through gh api graphql</c>, and a whole-text match refused those
    /// (independent pre-PR review, cycle 2, conformance lens). Only a quoted message that cannot
    /// run anything is masked: a double-quoted one holding <c>$(</c> or a backtick is left in
    /// place, because a substitution inside it executes. So is one holding a backslash: bash reads
    /// <c>\"</c> as an escaped quote but PowerShell, which this same function guards, ends the string
    /// there, so a <c>-m "x\"; gh api …; echo \""</c> would hide a real call inside what looks like
    /// one message (independent pre-PR review, cycle 3, adversarial lens). Everything outside the message stays,
    /// so a <c>gh api</c> call chained after the commit, or fed through a variable or heredoc, is
    /// still seen; a message this does not recognize is left alone and merely refused as before.
    /// </summary>
    private static readonly Regex CommitMessages = new(
        @"(?<=^" + QuoteBalancedText(@"[^'""`\\#]") + @"\bgit\s+commit\b" + QuoteBalancedText(@"[^\n;&|'""`\\#]") + @"\s)"
        + @"(?:-m|--message)(?:=|\s+)(?:'[^']*'|""(?:[^""\\`$]|\$(?!\())*"")",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A run of text in which every quote that opens a string also closes it, with
    /// <paramref name="plainCharacter"/> standing for one character outside any quote. Text that
    /// holds an UNBALANCED quote is exactly text the shell reads differently from the regex:
    /// <c>echo 'git commit -m "x'; gh api …; echo 'y"'</c> has a <c>git commit -m "</c> that is
    /// only characters inside an echo's string, and masking from that quote to the next one hid
    /// the real call between them (independent pre-PR review, cycle 4, adversarial lens). So the
    /// message is masked only when everything ahead of it, from the start of the command, is plain
    /// text or whole quoted strings; the plain class also leaves out <c>#</c> (a comment can hide
    /// a <c>git commit</c> from the shell) and a backslash (which can escape a quote).
    /// </summary>
    private static string QuoteBalancedText(string plainCharacter) =>
        "(?:" + plainCharacter + @"|'[^']*'|""(?:[^""\\`$]|\$(?!\())*"")*?";

    /// <summary>
    /// Whether this command puts text onto a pull request by a route other than
    /// <c>h9k pr reply</c>: inside a review thread, or at the top level of the pull request. (The
    /// name predates the top-level routes joining the list.) False for everything else, including
    /// every read — a follow-up fetches its threads with <c>gh api graphql</c>, lists comments
    /// with a GET, and resolves a bot's thread with <c>resolveReviewThread</c>, and none of those
    /// says anything to anybody — and false for a command that merely quotes one of these routes
    /// in a search pattern or a commit message, which invokes no such program and reaches no API.
    /// </summary>
    public static bool WritesIntoAReviewThread(string? command) =>
        command.IsNotBlank()
        && (ShellInvocations.InvokesGh(command, DirectPostingSubcommands)
            || ReachesAndWrites(CommitMessages.Replace(command, "-m \"\"")));

    private static bool ReachesAndWrites(string command) =>
        ReachesTheGitHubApi.IsMatch(command) && NamesAThreadWrite(command);

    private static bool NamesAThreadWrite(string command) =>
        Routes.IsMatch(command)
        || (ReadableWritePaths.IsMatch(command)
            && (WriteParameters.IsMatch(command) || SendsABodyFlag(command)));

    /// <summary>
    /// Whether a body flag sits in the same simple command as the API call. A backtick is the
    /// PowerShell line continuation, but the shell reader reads it as the start of a command
    /// substitution, which splits <c>gh api …/comments `</c> and <c>--input payload.json</c> into
    /// two commands that each look harmless (independent pre-PR review, cycle 2, adversarial
    /// lens). So text holding a backtick is judged whole rather than command by command: that can
    /// only refuse more, never less.
    /// </summary>
    private static bool SendsABodyFlag(string command) =>
        command.Contains('`')
            ? BodyFlags.IsMatch(command)
            : ShellInvocations.CommandLines(command).Any(line =>
                ReachesTheGitHubApi.IsMatch(line) && BodyFlags.IsMatch(line));

    /// <summary>
    /// What the session is told when the guard refuses, in the shape a refusal here has to take:
    /// name the rule, name the route that is allowed instead, and say what happens to a reply it
    /// is not allowed to send. An agent that cannot self-correct from the message will simply
    /// retry the same command.
    /// </summary>
    public const string RefusalReason =
        "This platform posts replies on a pull request through " + ClaudeSettingsFile.ReviewThreadReplyCommand
        + ", never through gh directly, because only that command can tell a bot's words from a person's. "
        + "Inside a review thread: " + ClaudeSettingsFile.ReviewThreadReplyCommand
        + " <task> --thread <id> --disposition <fix|decline|route> --body \"<text>\". "
        + "To answer a review's own body, which has no thread (this replaces gh pr comment and gh issue "
        + "comment): " + ClaudeSettingsFile.ReviewThreadReplyCommand
        + " <task> --review <review url> --disposition <fix|decline|route> --body \"<text>\". "
        + "A decline or a route on a thread or a review body a PERSON wrote is not yours to post at all: "
        + "draft it, close with the DISAGREEMENT block and RESOLUTION: disputed, and the run parks so the "
        + "owner sends it, edits it, or drops it.";
}
