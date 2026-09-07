using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.WorkItems;

/// <summary>One file the pull request touches, as gh reported it.</summary>
public sealed record PullRequestFileChange(string Path, int Additions, int Deletions);

/// <summary>
/// One check GitHub's status rollup reported for the pull request's head. <see cref="Conclusion"/>
/// is honestly null while a check is still running — an unfinished check has not concluded
/// anything, and reporting one as though it had is the plausible-but-unobserved fill-in
/// AGENTS.md forbids.
/// </summary>
public sealed record PullRequestCheck(string Name, string? Workflow, string Status, string? Conclusion);

/// <summary>
/// Everything a reviewer's own review lap needs to read off a pull request in one gh call
/// (Decisions Log #149): the stated shape of the change, the files it touches, the checks that
/// ran, and the head the review will be submitted against.
/// <para>
/// Deliberately a superset of <see cref="PullRequestFacts"/> rather than an extension of it:
/// that record is what a pr-review task's own adoption and dispatch read, on a hot path that
/// runs on every dispatch, and widening it would make every one of those calls pay for a file
/// list and a status rollup neither of them looks at.
/// </para>
/// <para>
/// <see cref="ChecksObserved"/> tells "GitHub reported no checks at all" apart from "the rollup
/// came back empty because this repository runs none" — the briefing says which, rather than
/// printing a silent empty section a reviewer would read as "CI passed".
/// </para>
/// </summary>
public sealed record PullRequestSurface(
    string Repository,
    int Number,
    string Title,
    string? Body,
    string State,
    string BaseRefName,
    string HeadRefName,
    string HeadSha,
    Uri? Url,
    string? AuthorLogin,
    int Additions,
    int Deletions,
    /// <summary>
    /// How many files GitHub says the pull request touches, which is not always
    /// <c>Files.Count</c>: the files list the API serves is paginated, so a very large pull
    /// request comes back with this number honest and the list short. Carried so a reader can
    /// tell the two apart instead of taking a truncated list for the whole change.
    /// </summary>
    int ChangedFiles,
    IReadOnlyList<PullRequestFileChange> Files,
    IReadOnlyList<PullRequestCheck> Checks,
    bool ChecksObserved);

/// <summary>
/// One line comment going out with a changes-requested review. <see cref="Line"/> is a line in
/// the pull request's own diff, which is GitHub's rule and not this platform's: a comment on a
/// line the diff does not contain is rejected by the API, and the whole review with it.
/// </summary>
public sealed record PullRequestReviewLineComment(string Path, int Line, string Body)
{
    // Lazy path, so the LEFTMOST ":<digits>:" is the separator and everything after it is the
    // reviewer's own text. Written greedy first, on the reasoning that a path could itself carry
    // a colon-number sequence; the reverse is true where it counts (independent pre-PR review,
    // cycle 1, adversarial lens). A colon-number-colon inside free prose is ordinary
    // ("…see RFC 3986 section 3:2: wrong scheme"), while a repo-relative path carrying one is
    // exotic and on Windows impossible — so the greedy form stole the split from a reviewer's
    // sentence and named a path that does not exist. It failed loudly rather than posting
    // anything wrong (GitHub 422s the whole review), which is why this is graded low; the lazy
    // form simply parses what the reviewer meant.
    // [0-9] rather than \d, because .NET's \d matches every Unicode decimal digit — Arabic-Indic
    // digits pasted out of a document matched the shape and then failed int.Parse with a raw
    // FormatException (independent pre-PR review, cycle 1, adversarial lens). GitHub's line
    // number is ASCII or it is nothing, so the class that parses is the class that matches.
    private static readonly Regex Shape = new(
        @"^(?<path>.+?):(?<line>[0-9]+):\s*(?<text>.*\S.*)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Parses the <c>--finding</c> form a reviewer types: <c>path:line: text</c>. Refuses rather
    /// than guessing, because every plausible repair is wrong in a way that reaches GitHub: a
    /// missing line number has no diff position to attach to, and dropping the comment to the
    /// review body instead would silently move a finding the reviewer aimed at one line.
    /// </summary>
    public static PullRequestReviewLineComment Parse(string finding)
    {
        Match match = Shape.Match(finding?.Trim() ?? string.Empty);
        // The parse is part of the match check rather than a step after it: the digit run the
        // shape accepts is unbounded in length, so a fat-fingered or pasted line number past
        // int.MaxValue threw an OverflowException that Program.cs's mapping (DomainException,
        // CommandAppException, Npgsql) never sees — a raw stack trace and an unmapped exit code
        // where this command's own teaching refusal was designed (independent pre-PR review,
        // cycle 1, both lenses). A number that cannot be an int cannot be a line either, so it
        // belongs in the same refusal as a missing one.
        if (!match.Success
            || !int.TryParse(match.Groups["line"].Value, CultureInfo.InvariantCulture, out int line))
        {
            throw new DomainValidationException(
                $"'{RelayedText.OneLine(finding ?? string.Empty)}' is not a finding this command can post. "
                + "Write it as path:line: text — for example "
                + "--finding \"src/Hall9k.Cli/Program.cs:42: this swallows the cancellation\". The line "
                + "number has to be a line the pull request's own diff contains; GitHub rejects a review "
                + "whose comment points anywhere else.");
        }

        return new PullRequestReviewLineComment(
            match.Groups["path"].Value.Trim(), line, match.Groups["text"].Value.Trim());
    }

    /// <summary>The <c>path:line: text</c> form again, for the record kept on the task of what was actually posted.</summary>
    public override string ToString() => $"{Path}:{Line}: {Body}";
}

/// <summary>What GitHub answered with once the review was submitted. <see cref="ReviewUrl"/> is honestly null when the response carried none.</summary>
public sealed record PostedPullRequestReview(string HeadSha, string? ReviewUrl);

/// <summary>
/// The reviewer's own read-and-post seam onto a pull request, through the same already
/// authenticated <c>gh</c> CLI every other GitHub read in this codebase uses (PLAN.md §10) — so
/// the review that gets submitted is submitted under the reviewer's own login, which is the only
/// thing that makes their verdict theirs.
/// <para>
/// The post goes through <c>gh api</c> rather than <c>gh pr review</c>: only the REST endpoint
/// takes line comments alongside the review body, and a changes-requested verdict whose findings
/// arrived as prose in the body instead of on their lines is a materially worse review.
/// </para>
/// </summary>
public sealed class GitHubPullRequestSurface(ProcessRunner? runner = null)
{
    private const string RequestedFields =
        "number,title,body,state,url,baseRefName,headRefName,headRefOid,author,additions,deletions,changedFiles,files,statusCheckRollup";

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>
    /// One <c>gh pr view</c> for everything the briefing states. The reference is whatever the
    /// reviewer typed — a number, <c>owner/repo#42</c>, or the URL — parsed the same way
    /// <see cref="GitHubPullRequestProvider"/> parses it, so the two surfaces can never disagree
    /// about which pull request a reviewer named.
    /// </summary>
    public async Task<PullRequestSurface> ReadAsync(
        string reference, string workingDirectory, CancellationToken cancellationToken)
    {
        (string? repository, int number) = GitHubPullRequestReference.Parse(reference);

        List<string> arguments =
            ["pr", "view", number.ToString(CultureInfo.InvariantCulture), "--json", RequestedFields];
        if (repository is not null)
        {
            arguments.AddRange(["--repo", repository]);
        }

        ProcessResult result = await RunGhAsync(arguments, workingDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Explain(result.StandardError, repository, number, workingDirectory);
        }

        return Map(result.StandardOutput, number);
    }

    /// <summary>
    /// Submits the review. <paramref name="headSha"/> is pinned by the caller from a read it
    /// just took, and travels as the request's own <c>commit_id</c>: a review is an opinion about
    /// a specific tree, and letting GitHub default it to whatever the head is at post time would
    /// silently attach the reviewer's verdict to a push they never read.
    /// <para>
    /// The request body goes through a temporary file and <c>gh api --input</c>, not through
    /// arguments: a note or a finding is free text a human just typed, and threading it onto a
    /// command line means quoting it correctly on every platform this runs on. A file has no
    /// quoting. It is deleted in a finally, and it lands under the OS temp directory rather than
    /// in the worktree so a failure never leaves a stray file in a checkout a reviewer is reading.
    /// </para>
    /// </summary>
    public async Task<PostedPullRequestReview> PostReviewAsync(
        string repository,
        int number,
        string headSha,
        ReviewerVerdict verdict,
        string note,
        IReadOnlyList<PullRequestReviewLineComment> comments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (verdict.GitHubEvent.IsBlank())
        {
            throw new DomainValidationException(
                $"'{verdict.Value}' is not a review GitHub can be asked to submit. Only an approval or a "
                + "changes-requested verdict is ever posted from here.");
        }

        if (headSha.IsBlank())
        {
            throw new DomainValidationException(
                "The pull request's current head could not be read, so there is no commit to submit this "
                + "review against. GitHub would attach it to whatever the head is at post time instead, "
                + "which may be a push nobody has read. Re-run once gh can read the pull request.");
        }

        string payload = BuildPayload(headSha, verdict, note, comments);
        string payloadFile = Path.Combine(Path.GetTempPath(), $"hall9k-review-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(payloadFile, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        try
        {
            ProcessResult result = await RunGhAsync(
                [
                    "api", $"repos/{repository}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/reviews",
                    "--method", "POST", "--input", payloadFile,
                ],
                workingDirectory,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw ExplainPost(result.StandardError, repository, number, comments);
            }

            return new PostedPullRequestReview(headSha, ReadReviewUrl(result.StandardOutput));
        }
        finally
        {
            try
            {
                File.Delete(payloadFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The review either posted or it did not; a temp file that outlives this call
                // changes neither, and throwing from a finally would replace the real outcome.
            }
        }
    }

    /// <summary>
    /// The REST body, built with <see cref="Utf8JsonWriter"/> rather than string interpolation so
    /// a note containing a quote, a backslash, or a newline reaches GitHub as the reviewer wrote
    /// it instead of breaking the request.
    /// </summary>
    internal static string BuildPayload(
        string headSha, ReviewerVerdict verdict, string note, IReadOnlyList<PullRequestReviewLineComment> comments)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("commit_id", headSha);
            writer.WriteString("event", verdict.GitHubEvent);
            writer.WriteString("body", note);
            if (comments.Count > 0)
            {
                writer.WriteStartArray("comments");
                foreach (PullRequestReviewLineComment comment in comments)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", comment.Path);
                    writer.WriteNumber("line", comment.Line);
                    // RIGHT, not LEFT: a review lap reads the pull request's own head, so a
                    // finding is about the line as it now stands rather than the line it
                    // replaced. Omitting this defaults to RIGHT too, but stating it is what
                    // keeps that from being an accident of the API's defaults.
                    writer.WriteString("side", "RIGHT");
                    writer.WriteString("body", comment.Body);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? ReadReviewUrl(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("html_url", out JsonElement url)
                && url.ValueKind == JsonValueKind.String
                    ? url.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // gh exited 0, so the review is posted. An unreadable response body is a gap in
            // what this platform can record about it, not a reason to claim it failed — the
            // verdict is recorded with a null URL, which says exactly that.
            return null;
        }
    }

    private static PullRequestSurface Map(string json, int requestedNumber)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException(
                "gh exited successfully but did not answer with a pull request in JSON, so there is nothing "
                + $"to review: {exception.Message}. gh printed: "
                + $"{RelayedText.OneLine(RelayedText.Truncate(json, 200))}. Check that the 'gh' on PATH is "
                + "the GitHub CLI itself.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new DomainValidationException(
                    $"gh answered with {root.ValueKind}, not a pull request object — nothing here can be reviewed.");
            }

            string? url = ReadString(root, "url");
            IReadOnlyList<PullRequestFileChange> files = ReadFiles(root);
            (IReadOnlyList<PullRequestCheck> checks, bool checksObserved) = ReadChecks(root);
            return new PullRequestSurface(
                GitHubPullRequestReference.RepositoryFromUrl(url),
                ReadInt(root, "number") ?? requestedNumber,
                ReadString(root, "title") ?? string.Empty,
                ReadString(root, "body") is { } body && body.IsNotBlank() ? body : null,
                ReadString(root, "state") ?? string.Empty,
                ReadString(root, "baseRefName") ?? string.Empty,
                ReadString(root, "headRefName") ?? string.Empty,
                ReadString(root, "headRefOid") ?? string.Empty,
                url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed : null,
                root.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object
                    ? ReadString(author, "login")
                    : null,
                ReadInt(root, "additions") ?? 0,
                ReadInt(root, "deletions") ?? 0,
                // Falls back to the list's own length when gh reported no count, which reads as
                // "nothing was truncated" — the honest reading of an absent count paired with a
                // present list, and never a number larger than what was actually observed.
                ReadInt(root, "changedFiles") ?? files.Count,
                files,
                checks,
                checksObserved);
        }
    }

    private static IReadOnlyList<PullRequestFileChange> ReadFiles(JsonElement root)
    {
        if (!root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PullRequestFileChange> changes = [];
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || ReadString(file, "path") is not { } path || path.IsBlank())
            {
                continue;
            }

            changes.Add(new PullRequestFileChange(path, ReadInt(file, "additions") ?? 0, ReadInt(file, "deletions") ?? 0));
        }

        return changes;
    }

    /// <summary>
    /// The status rollup, flattened. The second half of the answer is whether GitHub reported a
    /// rollup at all: an absent property means nothing was observed, while a present-but-empty
    /// array means observed-and-there-are-none. Those are different sentences in a briefing and
    /// must not collapse into one.
    /// </summary>
    private static (IReadOnlyList<PullRequestCheck> Checks, bool Observed) ReadChecks(JsonElement root)
    {
        if (!root.TryGetProperty("statusCheckRollup", out JsonElement rollup) || rollup.ValueKind != JsonValueKind.Array)
        {
            return ([], false);
        }

        List<PullRequestCheck> checks = [];
        foreach (JsonElement check in rollup.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A check run carries name/status/conclusion; a plain commit status carries
            // context/state instead. Both appear in the same rollup, so both are read here
            // rather than only the shape this repository's own CI happens to produce.
            string name = ReadString(check, "name") ?? ReadString(check, "context") ?? "(unnamed check)";
            string status = ReadString(check, "status") ?? ReadString(check, "state") ?? string.Empty;
            string? conclusion = ReadString(check, "conclusion");
            checks.Add(new PullRequestCheck(
                name,
                ReadString(check, "workflowName"),
                status,
                conclusion.IsNotBlank() ? conclusion : null));
        }

        return (checks, true);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
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
                    "Could not run gh, the GitHub CLI a review lap reads and posts through: "
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

    private static DomainException Explain(string standardError, string? repository, int number, string workingDirectory)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        string named = repository is null ? $"#{number}" : $"{repository}#{number}";

        if (reported.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("Could not resolve to a PullRequest", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainNotFoundException(
                $"GitHub has no pull request {named}"
                + (repository is null ? $" in the repository at {workingDirectory}" : string.Empty)
                + $". gh reported: {reported}. Check the number, or pass the full pull request URL.");
        }

        if (reported.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainValidationException(
                $"gh is not authenticated for {named}. A review lap posts under YOUR login, so there is no "
                + $"fallback credential here. Run 'gh auth login' and try again. gh reported: {reported}");
        }

        return new DomainValidationException($"gh could not read pull request {named}: {reported}");
    }

    /// <summary>
    /// The post's own refusals. The 422 case is called out by name because it is the one a
    /// reviewer causes and can fix: GitHub rejects the entire review — body and every other
    /// comment with it — when one comment names a line the diff does not contain, so nothing was
    /// posted and re-running with the line corrected is the whole remedy.
    /// <para>
    /// Reviewing one's OWN pull request is a 422 as well, not the 403 it reads like, and it is
    /// checked for first (independent pre-PR review, cycle 1, adversarial lens): a single-login
    /// install is the ordinary one, so the daemon opened the pull request under the very login
    /// the reviewer posts with, and the generic 422 explanation blamed a line comment or a moved
    /// head for it. GitHub answers "Can not approve your own pull request" (and the
    /// changes-requested equivalent), so the phrase this matches on is the shared tail of both
    /// rather than either verb.
    /// </para>
    /// </summary>
    private static DomainException ExplainPost(
        string standardError, string repository, int number, IReadOnlyList<PullRequestReviewLineComment> comments)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        if (reported.Contains("your own pull request", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainValidationException(
                $"GitHub refused the review on {repository}#{number} and posted nothing: the gh login this "
                + "posted under is that pull request's own author, and GitHub takes no review from an author "
                + "on their own pull request. On a single-login install this is the ordinary shape of "
                + "reviewing a pull request this same account opened — the review has to come from another "
                + "account, and closing the task out without posting is "
                + "h9k review resolve <task> --merge-ready. GitHub reported: "
                + $"{reported}");
        }

        if (reported.Contains("HTTP 422", StringComparison.OrdinalIgnoreCase))
        {
            string locations = comments.Count == 0
                ? string.Empty
                : " The lines this review asked for were: "
                  + string.Join(", ", comments.Select(comment => $"{comment.Path}:{comment.Line}")) + ".";
            return new DomainValidationException(
                $"GitHub refused the review on {repository}#{number} and posted nothing — not the body, not "
                + $"any comment. This is what it answers when a comment names a line the pull request's diff "
                + $"does not contain, or when the head moved under the review.{locations} GitHub reported: "
                + $"{reported}");
        }

        if (reported.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately no longer naming self-review here: that case is a 422 and is handled
            // above. What a 403 or 401 actually means is a login that cannot review this
            // repository at all — unauthenticated, or without the access requesting changes
            // takes.
            return new DomainValidationException(
                $"gh's login is not allowed to review {repository}#{number} — it is either not authenticated "
                + "for this repository (run 'gh auth status' from the project's clone) or lacks the access a "
                + $"review takes on it. GitHub reported: {reported}");
        }

        return new DomainValidationException(
            $"The review could not be posted on {repository}#{number}, so no verdict was recorded: {reported}");
    }
}
