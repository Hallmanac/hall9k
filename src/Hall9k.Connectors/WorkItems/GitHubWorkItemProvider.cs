using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// GitHub issues through the <c>gh</c> CLI, which is the platform's already-authenticated seam
/// (PLAN.md §10 — GitHub access piggybacks the machine's <c>gh</c> login, so Hall9k stores no
/// GitHub token of its own). The daemon already shells out to <c>gh</c> for pull requests; this
/// is the same credential reached for a different noun.
/// </summary>
public sealed class GitHubWorkItemProvider(ProcessRunner? runner = null, TimeProvider? clock = null) : IWorkItemProvider
{
    /// <summary>Exactly the fields the import maps. Asking for more would be storing what we do not use.</summary>
    private const string RequestedFields = "number,title,body,state,url";

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public WorkItemProvider Provider => WorkItemProvider.GitHub;

    /// <summary>
    /// Whether a project's registered repository is one this provider can create and later find
    /// an issue in. <c>gh issue create</c> takes any host <c>gh</c> is configured against,
    /// including a GitHub Enterprise one, and succeeds there — but <see cref="ExternalReference"/>
    /// records <c>owner/repo</c> with no host (the same reasoning <see cref="IsGitHubDotCom"/>
    /// documents for import), so an issue created on another host could never be read back by
    /// <see cref="CreateReadBackAsync"/> or linked by hand afterwards. Checked by
    /// <c>TaskPublishCommand.TrackInBacklogAsync</c> before <see cref="CreateAsync"/> is ever
    /// called, so an enterprise-remoted project is told once, rather than filing an orphan issue
    /// on every publish that this provider can never confirm or link.
    /// </summary>
    public static bool SupportsRepository(Uri repositoryUrl) => IsGitHubDotCom(repositoryUrl.Host);

    /// <summary>
    /// The host <c>gh</c> would actually create against, for a project that carries no recorded
    /// <c>RepositoryUrl</c> at all (registered with <c>--repo</c> and no <c>--repo-url</c>) — the
    /// case <see cref="SupportsRepository"/> has nothing to check, because the never-guess rule
    /// (AGENTS.md) means the platform never assumed one. One cheap <c>gh repo view</c> round trip
    /// observes the same host <see cref="CreateReadBackAsync"/> would otherwise only discover
    /// after an issue already exists there, so a caller can refuse before creating rather than
    /// only explain after. Best-effort: <c>gh</c> failing to resolve a remote at all (not
    /// installed, no network, no origin configured) leaves nothing observed, so the caller falls
    /// back to letting <see cref="CreateAsync"/>'s own read-back guard catch a foreign host after
    /// the fact, exactly as it always has.
    /// </summary>
    public async Task<Uri?> TryObserveRepositoryHostAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner("gh", ["repo", "view", "--json", "url"], workingDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.ValueKind is JsonValueKind.Object
                && ReadString(document.RootElement, "url") is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ImportedWorkItem> ImportAsync(
        WorkItemImportRequest request, CancellationToken cancellationToken)
    {
        (string? repository, int number) = ParseReference(request.Reference);

        List<string> arguments =
            ["issue", "view", number.ToString(CultureInfo.InvariantCulture), "--json", RequestedFields];
        if (repository is not null)
        {
            arguments.AddRange(["--repo", repository]);
        }

        ProcessResult result = await RunGhAsync(arguments, request.WorkingDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Explain(result.StandardError, repository, number, request.WorkingDirectory);
        }

        return Map(result.StandardOutput, number, clock.GetUtcNow());
    }

    /// <summary>
    /// Author one issue deterministically (backlog: every published task is tracked
    /// automatically). This is the platform writing to GitHub with its own words rather than an
    /// agent's, which is honest only because an issue's shape — a title, a body, labels — is
    /// uniform across repositories in a way a Jira card's issue type and required fields are not
    /// (<see cref="JiraWorkItemProvider"/>'s own doc comment is the fuller version of this
    /// argument).
    /// <para>
    /// <c>gh issue create</c> prints the new issue's URL on success, and that printed claim is
    /// never what gets recorded: it is read straight back through <see cref="ImportAsync"/>, the
    /// same call <c>--from-issue</c> makes, so the observation gate this whole feature is built
    /// on applies to a card the platform authored itself exactly as much as one an agent reports
    /// having made.
    /// </para>
    /// </summary>
    public async Task<ImportedWorkItem> CreateAsync(
        GitHubIssueCreateRequest request, CancellationToken cancellationToken)
    {
        // A temp file rather than --body on the command line, the same idiom PullRequestOpener
        // uses for the same call shape (gh's own body argument): a long issue body over Windows'
        // roughly 32K command-line limit fails the spawn outright, and CouldNotStartGh would then
        // misdiagnose that as a missing gh install, since .NET reports both the same way.
        string bodyFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(bodyFile, request.Body ?? string.Empty, cancellationToken);

            List<string> arguments = ["issue", "create", "--title", request.Title, "--body-file", bodyFile];
            foreach (string label in request.Labels)
            {
                arguments.Add("--label");
                arguments.Add(label);
            }

            ProcessResult result = await RunGhAsync(
                arguments, request.WorkingDirectory, cancellationToken,
                onOutputStuckAfterSuccess: exception => new DomainConflictException(
                    $"gh issue create for '{RelayedText.OneLine(request.Title)}' exited successfully, but "
                    + "something it started was still holding its output open when Hall9k stopped "
                    + "waiting, so the new issue's URL was never printed to read back. The issue was "
                    + "very likely created — check what exists with 'gh issue list' and link it by hand "
                    + $"with h9k task link-issue rather than creating another. {exception.Message}"),
                onStoppedAnswering: exception =>
                    GhStoppedAnsweringOnCreate(exception, request.WorkingDirectory, request.Title));
            if (result.ExitCode != 0)
            {
                throw ExplainCreate(result.StandardError, request);
            }

            return await CreateReadBackAsync(result.StandardOutput, request, cancellationToken);
        }
        finally
        {
            File.Delete(bodyFile);
        }
    }

    private async Task<ImportedWorkItem> CreateReadBackAsync(
        string standardOutput, GitHubIssueCreateRequest request, CancellationToken cancellationToken)
    {
        string url = standardOutput.Trim();
        if (url.IsBlank())
        {
            // Distinct from a create failure for the same reason CreateReadBackAsync's own catch
            // below is: gh already exited 0, so the issue was very likely created — a caller that
            // reacted the way it reacts to a real create failure ("create one by hand") would file
            // a duplicate for whatever exists but printed nothing.
            throw new DomainConflictException(
                "gh issue create exited successfully but printed no URL, so whatever it created (if "
                + "anything) cannot be read back and verified. Check what exists with 'gh issue list' "
                + "and link it by hand with h9k task link-issue if it is there.");
        }

        // Distinct from a create failure on purpose: gh already reported success and a real issue
        // exists at this URL, so a caller that reacted the way it reacts to ExplainCreate's
        // failures — "create one by hand" — would file a second issue for the same task. A
        // DomainConflictException (unused elsewhere in this method) lets the caller tell the two
        // apart and give advice that does not risk a duplicate.
        try
        {
            return await ImportAsync(new WorkItemImportRequest(Provider, url, request.WorkingDirectory), cancellationToken);
        }
        catch (DomainException exception)
        {
            throw new DomainConflictException(
                $"gh issue create succeeded and reported {Head(url)}, but reading it back to verify "
                + $"failed: {exception.Message} The issue was not recorded, but it likely exists at "
                + "that URL — link it by hand with h9k task link-issue rather than creating another.");
        }
    }

    /// <summary>
    /// Post a comment on an issue — closeout's own write, with no card semantics behind it,
    /// unlike Jira's own merge comment, which goes through <see cref="JiraWriteExecutor"/> and
    /// <see cref="JiraWriteCoordinator"/> instead of a provider method like this one.
    /// Never a close or a transition: which label or state a merge should move an issue to is the
    /// project's workflow, not a fact this platform gets to have an opinion on.
    /// </summary>
    public async Task CommentAsync(
        ExternalReference reference, string comment, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!TryParseCanonical(reference.Reference, out string repository, out int number))
        {
            throw new DomainValidationException(
                $"'{RelayedText.OneLine(reference.ToString())}' does not read as a github owner/repo#number "
                + "reference, so there is no issue to comment on.");
        }

        List<string> arguments =
        [
            "issue", "comment", number.ToString(CultureInfo.InvariantCulture), "--repo", repository, "--body", comment,
        ];
        ProcessResult result = await RunGhAsync(
            arguments, workingDirectory, cancellationToken,
            onStoppedAnswering: exception => GhStoppedAnsweringOnComment(exception, workingDirectory, repository, number));
        if (result.ExitCode != 0)
        {
            throw new DomainValidationException(
                $"gh could not comment on {repository}#{number}: {RelayedText.OneLine(result.StandardError).Trim()}");
        }
    }

    /// <summary>
    /// <c>github:owner/repo#42</c> points at
    /// <c>https://github.com/owner/repo/issues/42</c>. A format rule rather than a lookup, so it
    /// is safe to apply without asking GitHub; a reference that does not carry an owner and a
    /// repository yields null rather than a plausible-looking guess.
    /// </summary>
    public Uri? WebUrl(ExternalReference reference) =>
        reference.Provider == WorkItemProvider.GitHub && TryParseCanonical(reference.Reference, out string repository, out int number)
            ? new Uri($"https://github.com/{repository}/issues/{number}")
            : null;

    /// <summary>The parse <see cref="WebUrl"/> and <see cref="CommentAsync"/> both need, factored once.</summary>
    private static bool TryParseCanonical(string reference, out string repository, out int number)
    {
        string[] parts = reference.Split('#');
        if (parts is [{ } candidateRepository, { } candidateNumber]
            && candidateRepository.Split('/') is [{ Length: > 0 }, { Length: > 0 }]
            && int.TryParse(candidateNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            repository = candidateRepository;
            number = parsed;
            return true;
        }

        repository = string.Empty;
        number = 0;
        return false;
    }

    /// <summary>
    /// gh's stderr, turned into the one sentence that says what to do next, for the creation path
    /// specifically: <see cref="Explain"/> is keyed to a number that already exists, and creation
    /// fails on different grounds — a bad label, above all, since the routing guidance a human
    /// wrote as a comma list is not checked against the repository's actual labels before gh is asked.
    /// </summary>
    private static DomainException ExplainCreate(string standardError, GitHubIssueCreateRequest request)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        string title = RelayedText.OneLine(request.Title);

        if (reported.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainValidationException(
                $"gh is not authenticated, so the issue for '{title}' could not be created. Run "
                + $"'gh auth login' and try again. gh reported: {reported}");
        }

        if (reported.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainNotFoundException(
                $"gh could not resolve the repository to create '{title}' in, read from "
                + $"{request.WorkingDirectory}. gh reported: {reported}");
        }

        if (reported.Contains("label", StringComparison.OrdinalIgnoreCase)
            && reported.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainValidationException(
                $"gh could not create '{title}' because a label from this project's backlog routing "
                + $"guidance does not exist in the repository: {reported}. Create the label first, or "
                + "drop it with h9k project set --backlog-routing.");
        }

        return new DomainValidationException($"gh could not create an issue for '{title}': {reported}");
    }

    /// <summary>
    /// The forms a human actually has to hand: the number they are reading on the board, the
    /// <c>owner/repo#42</c> shorthand GitHub itself prints, and the URL in the address bar.
    /// A bare number means the project's own repository, which is what <c>gh</c> resolves from
    /// the working directory.
    /// </summary>
    private static (string? Repository, int Number) ParseReference(string reference)
    {
        string trimmed = reference?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "--from-issue needs an issue to import. Pass the number (42), the owner/repo#42 "
                + "shorthand, or the issue URL.");
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? ParseUrl(trimmed)
                : ParseShorthand(trimmed);
    }

    private static (string? Repository, int Number) ParseUrl(string reference)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out Uri? url))
        {
            throw Unreadable(reference);
        }

        if (!IsGitHubDotCom(url.Host))
        {
            throw ForeignHost(reference, url.Host);
        }

        string[] segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments is [{ } owner, { } repository, "issues", { } number]
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return ($"{owner}/{repository}", parsed);
        }

        // A pull request is a different noun with the same URL shape, and gh's own error for
        // it ("not found") would send the reader hunting for a deleted issue.
        throw IsPullRequestPath(segments)
            ? new DomainValidationException(
                $"{RelayedText.OneLine(reference)} is a pull request, not an issue. --from-issue "
                + "adopts issues; a pull request is work already under way, so it has no task to "
                + "seed.")
            : Unreadable(reference);
    }

    /// <summary>
    /// The two hash-shaped forms: <c>owner/repo#42</c>, and the bare issue in the project's own
    /// repository, which a human is as likely to type the way GitHub prints it (<c>#42</c>) as
    /// plainly (<c>42</c>). The leading hash comes off before the split, because splitting first
    /// leaves <c>#42</c> as an empty repository and a number, which reads as a malformed
    /// <c>owner/repo#42</c> rather than as the form it is.
    /// </summary>
    private static (string? Repository, int Number) ParseShorthand(string reference)
    {
        string bare = reference.StartsWith('#') ? reference[1..] : reference;
        if (int.TryParse(bare, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return (null, number);
        }

        return reference.Split('#') is [{ } repository, { } suffix]
            && repository.Split('/') is [{ Length: > 0 }, { Length: > 0 }]
            && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int qualified)
                ? (repository, qualified)
                : throw Unreadable(reference);
    }

    /// <summary>
    /// The noun is the third path segment (<c>/owner/repo/pull/12</c>), never any occurrence of
    /// "pull" in the URL. <c>wei/pull</c> is a real and widely used repository, so an issue in it
    /// reads <c>/wei/pull/issues/100</c> — a substring test refuses that as a pull request, and no
    /// project whose repository is named <c>pull</c> could adopt an issue at all.
    /// </summary>
    private static bool IsPullRequestPath(string[] segments) => segments is [_, _, "pull", ..];

    /// <summary>
    /// github.com is the only GitHub this provider can honestly adopt from. <c>gh</c> itself would
    /// take an enterprise host (<c>--repo [HOST/]OWNER/REPO</c>), but an
    /// <see cref="ExternalReference"/> records <c>owner/repo</c> with no host at all, so an
    /// enterprise issue would be stored, and later linked back to by <see cref="WebUrl"/>, as the
    /// github.com repository of the same name. That is a guessed identity, which the never-guess
    /// rule (AGENTS.md) forbids, so a host we cannot record is refused rather than dropped.
    /// </summary>
    private static bool IsGitHubDotCom(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every refusal below quotes something this class did not write — the reference the human
    /// typed, the URL gh answered with, a host read out of it — into a message Program.cs prints
    /// to a terminal, so each quoted value goes through <see cref="RelayedText"/> on the way, the
    /// same as <see cref="Explain"/> and <see cref="Head"/> already do with gh's own output.
    /// <para>
    /// A reference is not obviously hostile text, which is exactly why it was missed: an
    /// unparseable one is refused, and refusing it is what puts it on screen. A host is no safer,
    /// because an internationalised host name carries whatever Unicode its registry allows,
    /// including the overrides that reverse the line it sits in.
    /// </para>
    /// </summary>
    private static DomainValidationException ForeignHost(string url, string host) => new(
        $"{RelayedText.OneLine(url)} is on {RelayedText.OneLine(host)}, and Hall9k adopts issues "
        + "from github.com. An adopted reference records owner/repo with no host, so an issue from "
        + "another GitHub would be filed (and linked back to) as the github.com repository of the "
        + "same name. Write the task with --objective and --context instead, quoting what the "
        + "issue says.");

    private static DomainValidationException Unreadable(string reference) => new(
        $"'{RelayedText.OneLine(reference)}' does not name a GitHub issue. Use the number (42), "
        + "the owner/repo#42 shorthand, or the issue URL "
        + "(https://github.com/owner/repo/issues/42).");

    /// <summary>
    /// <paramref name="onOutputStuckAfterSuccess"/> is the create path's escape hatch: gh exiting
    /// 0 and then losing the drain race is a real success whose answer never arrived, not a
    /// failure, and only a caller whose call can create something external — <see cref="CreateAsync"/>
    /// — needs to say what that means instead of getting the import-flavoured
    /// <see cref="GhStoppedAnswering"/> text. Every other caller leaves it null and keeps that text,
    /// which is the right read when nothing was created (an import).
    /// <paramref name="onStoppedAnswering"/> is the same idea for a call that never went quiet
    /// before answering at all: <see cref="GhStoppedAnswering"/>'s wording is written for a read
    /// (<c>ImportAsync</c>) and tells the reader to "import again", which is wrong for
    /// <see cref="CommentAsync"/> — a write that is never retried automatically — and wrong in a
    /// second way for <see cref="CreateAsync"/>, whose outcome is genuinely unknown (gh may have
    /// created the issue before it went quiet), so its own override returns a
    /// <see cref="DomainConflictException"/> rather than the <see cref="DomainValidationException"/>
    /// the type used to require, the same distinction <see cref="CreateReadBackAsync"/> draws for
    /// the same reason: a caller must be able to tell "nothing happened, create one" apart from
    /// "something might have happened, check and link". Left null, a caller keeps the import
    /// wording, which is the right read for a call that is, in fact, a read.
    /// <para>
    /// A <see cref="ProcessOutputStuckException"/> whose <see cref="ProcessOutputStuckException.ExitCode"/>
    /// is non-zero carries none of that ambiguity, for every caller: gh already reported failure
    /// before its output got stuck draining, so this is a definite failure with an unreadable
    /// reason, never an unknown success. It is handled ahead of, and the same way regardless of,
    /// <paramref name="onOutputStuckAfterSuccess"/> and <paramref name="onStoppedAnswering"/>
    /// (see <see cref="GhFailedWithUnreadableError"/>) rather than falling through to the
    /// <see cref="TimeoutException"/> handler below, which it would otherwise reach because
    /// <see cref="ProcessOutputStuckException"/> is one and would be misread as the "did this even
    /// run" uncertainty that handler exists for.
    /// </para>
    /// </summary>
    private async Task<ProcessResult> RunGhAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        Func<ProcessOutputStuckException, DomainException>? onOutputStuckAfterSuccess = null,
        Func<TimeoutException, DomainException>? onStoppedAnswering = null)
    {
        try
        {
            return await runner("gh", arguments, workingDirectory, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw CouldNotStartGh(exception, workingDirectory);
        }
        catch (ProcessOutputStuckException exception) when (exception.ExitCode == 0 && onOutputStuckAfterSuccess is not null)
        {
            throw onOutputStuckAfterSuccess(exception);
        }
        catch (ProcessOutputStuckException exception) when (exception.ExitCode != 0)
        {
            throw GhFailedWithUnreadableError(exception, workingDirectory);
        }
        catch (TimeoutException exception)
        {
            throw onStoppedAnswering?.Invoke(exception) ?? GhStoppedAnswering(exception, workingDirectory);
        }
    }

    /// <summary>
    /// gh started and then either went quiet for longer than <see cref="ExternalProcess.Deadline"/>
    /// or left its output held open past <see cref="ExternalProcess.DrainGrace"/> after exiting, so
    /// the runner ended it. Which of the two happened is the runner's sentence to write, since only
    /// it watched; this adds what only this class knows. Translated here rather than left as a
    /// <see cref="TimeoutException"/> because Program.cs maps <see cref="DomainException"/> and
    /// nothing else, and because only this class knows which tool was asked what: the runner can
    /// say a process stopped answering, but not that the way to check it is 'gh auth status'.
    /// <para>
    /// The likeliest cause is named rather than guessed at as the cause, and it is the same one
    /// either way: gh waits for input it has no terminal to ask for when a credential helper needs
    /// unlocking, and a credential helper is also the thing most likely to be left holding gh's
    /// output after gh itself is gone. Both are why an import can hang on a machine where the
    /// same command works by hand.
    /// </para>
    /// </summary>
    private static DomainValidationException GhStoppedAnswering(
        TimeoutException exception, string workingDirectory) => new(
        $"{exception.Message} It was reading the issue from {workingDirectory}. An import that "
        + "stops here is usually gh, or something gh started, waiting on input it cannot ask for "
        + "— an unlocked keychain or a credential helper — so run 'gh auth status' and then "
        + "'gh issue view 42' by hand from that directory to see what it is waiting on, and "
        + "import again.");

    /// <summary>
    /// gh exited with a failure code and then something it started kept holding its output open
    /// past <see cref="ExternalProcess.DrainGrace"/>, so the exit code arrived but the stderr that
    /// would explain it never drained. Unlike <see cref="GhStoppedAnswering"/> and its siblings,
    /// this carries no "did this even run" uncertainty — an exit code was actually observed, and
    /// it says failure — so every caller (a read, a create, a comment) gets the same shape of
    /// answer: a definite failure whose reason has to be read by hand. Naming the exit code rather
    /// than guessing at gh's reason keeps this honest about what was, and was not, observed
    /// (AGENTS.md — never guess at unobserved facts).
    /// </summary>
    private static DomainValidationException GhFailedWithUnreadableError(
        ProcessOutputStuckException exception, string workingDirectory) => new(
        $"{exception.Message} Run the same gh command by hand from {workingDirectory} to read the "
        + "error it could not print here, then try again.");

    /// <summary>
    /// The comment-flavoured sibling of <see cref="GhStoppedAnswering"/>: a comment is a write,
    /// never retried automatically, so telling the reader to "import again" points at a command
    /// that has nothing to do with what stalled and never runs on its own. Names the issue the
    /// comment was headed for and says to re-post it, rather than to re-read something.
    /// </summary>
    private static DomainValidationException GhStoppedAnsweringOnComment(
        TimeoutException exception, string workingDirectory, string repository, int number) => new(
        $"{exception.Message} It was posting a comment on {repository}#{number} from {workingDirectory}. "
        + "A comment that stops here is usually gh, or something gh started, waiting on input it "
        + "cannot ask for — an unlocked keychain or a credential helper — so run 'gh auth status' "
        + "by hand from that directory to see what it is waiting on. The comment is not retried "
        + $"automatically; check 'gh issue view {number} --repo {repository}' to see whether it "
        + "posted, and add it by hand if it did not.");

    /// <summary>
    /// The create-flavoured sibling of <see cref="GhStoppedAnswering"/>, and a
    /// <see cref="DomainConflictException"/> rather than a <see cref="DomainValidationException"/>
    /// for the same reason <see cref="CreateReadBackAsync"/>'s own two branches are: gh going quiet
    /// here says nothing about whether the issue was created before it stopped answering, so the
    /// generic "reading the issue... import again" wording is doubly wrong — there is no import
    /// under way, and a caller reacting to it as an ordinary failure ("create one by hand") risks
    /// filing a duplicate for an issue that already exists.
    /// </summary>
    private static DomainException GhStoppedAnsweringOnCreate(
        TimeoutException exception, string workingDirectory, string title) => new DomainConflictException(
        $"{exception.Message} It was creating an issue for '{RelayedText.OneLine(title)}' from "
        + $"{workingDirectory}. gh stopping here is usually gh, or something gh started, waiting on "
        + "input it cannot ask for — an unlocked keychain or a credential helper — and whether the "
        + "issue was created before that happened is unknown. Run 'gh auth status' by hand from that "
        + "directory to see what it is waiting on, then check what exists with 'gh issue list' and "
        + "link it by hand with h9k task link-issue rather than creating another.");

    /// <summary>
    /// A missing gh and a missing working directory arrive as the same exception: .NET reports
    /// both as <see cref="Win32Exception"/> ("No such file or directory"), so the directory itself
    /// is the only thing that tells the two apart, and it is read only once starting gh has
    /// already failed. Origin observation (2026-08-21): this was written as a
    /// <c>catch (DirectoryNotFoundException)</c>, which <see cref="System.Diagnostics.Process.Start()"/>
    /// never throws for a bad <c>WorkingDirectory</c>, so a project whose registered repository
    /// path had moved was told to install the GitHub CLI and run 'gh auth login'.
    /// </summary>
    private static DomainException CouldNotStartGh(Win32Exception exception, string workingDirectory) =>
        Directory.Exists(workingDirectory)
            ? new DomainValidationException(
                "Could not run gh, the GitHub CLI Hall9k imports issues through: "
                + $"{exception.Message}. Install it (https://cli.github.com) and sign in with "
                + "'gh auth login', then run the import again.")
            : new DomainNotFoundException(
                $"The project's repository path does not exist: {workingDirectory}. gh reads the "
                + "repository from the directory it runs in, so fix the path with "
                + "'h9k project show <name>' as your reference before importing. gh reported: "
                + $"{exception.Message}");

    /// <summary>
    /// Turn gh's stderr into the one sentence that says what to do next. The strings are
    /// matched rather than parsed because gh reports these as prose; anything unmatched is
    /// passed through verbatim rather than relabelled as something we recognise.
    /// <para>
    /// Verbatim in wording, not in bytes: what is quoted here is another program's output on its
    /// way to a human's terminal, so it goes through <see cref="RelayedText"/> first. Whether the
    /// stderr of a tool answering about someone else's repository can carry an escape sequence is
    /// not a question worth leaving to the tool.
    /// </para>
    /// </summary>
    private static DomainException Explain(
        string standardError, string? repository, int number, string workingDirectory)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        string named = repository is null ? $"#{number}" : $"{repository}#{number}";

        // The repository failing to resolve is a different problem from the issue failing to
        // resolve, and it is worth saying so: GitHub answers "could not resolve" for a
        // repository that does not exist and for a private one the signed-in account cannot
        // see, deliberately, so that the API does not leak which. Hall9k cannot tell them apart
        // either, so it names both rather than picking one — the number is not the thing to
        // check here, and telling an authenticated user to check it sends them somewhere the
        // answer is not.
        if (reported.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainNotFoundException(
                $"gh could not resolve the repository for {named}"
                + (repository is null ? $", read from the project's path at {workingDirectory}" : string.Empty)
                + $". gh reported: {reported}. GitHub answers the same way for a repository that "
                + "does not exist and for one your account cannot see, so check the owner/repo "
                + "spelling, and check that the account 'gh auth status' reports has access to it "
                + "(a private repository needs it, and an organisation behind SSO needs the token "
                + "authorised for that organisation).");
        }

        if (reported.Contains("Could not resolve to an Issue", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("no issues found", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainNotFoundException(
                $"GitHub has no issue {named}"
                + (repository is null ? $" in the repository at {workingDirectory}" : string.Empty)
                + $". gh reported: {reported}. Check the number, or pass the full issue URL if the "
                + "issue lives in another repository.");
        }

        // Matched on what gh actually says when the account is the problem, and deliberately not
        // on the bare word "authentication". That word appears in answers this remedy is wrong
        // for — "HTTP 407 Proxy Authentication Required" is a proxy asking for credentials, and
        // sending that reader to 'gh auth login' relabels someone else's failure as gh's, which
        // is the one thing this method promises not to do. An answer with no precise match falls
        // through to the branch that quotes gh and claims nothing.
        if (reported.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
            || reported.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainValidationException(
                $"gh is not authenticated for {named}. Run 'gh auth login' (Hall9k holds no GitHub "
                + $"token of its own — it uses yours) and import again. gh reported: {reported}");
        }

        return new DomainValidationException(
            $"gh could not read issue {named}: {reported}");
    }

    private static ImportedWorkItem Map(string json, int requestedNumber, DateTimeOffset observedAt)
    {
        using JsonDocument document = ReadIssueJson(json);
        JsonElement root = document.RootElement;

        string? url = ReadString(root, "url");
        // The kind is checked before the value is read, the way ReadString checks it: TryGetInt32
        // only reports whether a number fits, and throws outright on anything that is not one, so
        // a "number" arriving as the string "42" from something on PATH pretending to be gh would
        // leave as an InvalidOperationException stack trace. The number asked for is the honest
        // fallback, since it is what the fetch was for.
        int number = root.TryGetProperty("number", out JsonElement element)
            && element.ValueKind is JsonValueKind.Number
            && element.TryGetInt32(out int reported)
                ? reported
                : requestedNumber;

        // gh issue view resolves pull requests too — issues and pull requests share one number
        // sequence, so "42" may be either and only the URL gh returns says which. Caught here
        // rather than at parse time because a bare number cannot be judged before the fetch.
        // Origin observation (2026-08-21): `gh issue view 1 --repo cli/cli` returns a merged
        // pull request, which would otherwise have been adopted as an issue.
        if (url is not null
            && Uri.TryCreate(url, UriKind.Absolute, out Uri? resolved)
            && IsPullRequestPath(resolved.AbsolutePath.Trim('/').Split('/')))
        {
            throw new DomainValidationException(
                $"#{number} is a pull request, not an issue ({RelayedText.OneLine(url)}). Issues "
                + "and pull requests share one number sequence on GitHub. --from-issue adopts "
                + "issues; a pull request is work already under way, so it has no task to seed.");
        }

        return new ImportedWorkItem(
            new ExternalReference(WorkItemProvider.GitHub, $"{RepositoryFrom(url)}#{number}"),
            ReadString(root, "title") ?? string.Empty,
            // An issue with no body has no body. Blank collapses to null so the agent context
            // says the body was empty rather than printing an empty section under a heading —
            // but that is the only judgement made here. A body that is present is carried
            // character for character, because the context contract promises the agent the
            // issue as written, and in Markdown leading spaces are content: four of them open a
            // code block, and trimming them turns a code sample into a paragraph.
            ReadString(root, "body") is { } body && body.IsNotBlank() ? body : null,
            WorkItemStatus.Parse(ReadString(root, "state")),
            url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed : null,
            observedAt);
    }

    /// <summary>
    /// gh's answer, checked for being the thing it was asked for before anything is read out of
    /// it. Exit code zero is not on its own a promise of shape: something else on PATH named gh
    /// (a wrapper, a shim, a corporate proxy for the real thing) succeeds and prints its own
    /// prose, and an extension can put a notice on stdout ahead of the JSON.
    /// <para>
    /// Left unguarded, that reaches the human as a <see cref="JsonException"/> stack trace, since
    /// Program.cs maps <see cref="DomainException"/> and nothing else — and a stack trace is the
    /// one error shape an agent cannot self-correct from (AGENTS.md, failures print why). So it
    /// becomes a refusal that quotes what actually arrived, because what arrived is the only
    /// evidence of which gh ran.
    /// </para>
    /// </summary>
    private static JsonDocument ReadIssueJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw NotAnIssueDocument(json, exception.Message);
        }

        JsonValueKind kind = document.RootElement.ValueKind;
        if (kind is JsonValueKind.Object)
        {
            return document;
        }

        document.Dispose();
        throw NotAnIssueDocument(json, $"the JSON it printed is {kind}, not an object");
    }

    private static DomainValidationException NotAnIssueDocument(string json, string reported) => new(
        "gh exited successfully but did not answer with an issue in JSON, so there is nothing to "
        + $"adopt: {reported}. It printed: {Head(json)}. Check that the 'gh' on PATH is the GitHub "
        + "CLI itself rather than a wrapper of the same name, and that "
        + "'gh issue view 42 --json number,title' prints JSON from the project's repository.");

    /// <summary>
    /// Enough of the output to recognise what answered, and no more: the whole of it could be a
    /// page of an unrelated tool's help text, and an error nobody reads to the end teaches
    /// nothing. Blank output says so outright rather than leaving an empty pair of quotes.
    /// <para>
    /// It is quoting a program that has just proved it is not the one we asked for, into a
    /// message bound for a terminal, so it is sanitised and cut on a text-element boundary rather
    /// than at a raw char index (<see cref="RelayedText"/>).
    /// </para>
    /// </summary>
    private static string Head(string output)
    {
        string trimmed = RelayedText.OneLine(output).Trim();
        return trimmed.IsBlank()
            ? "nothing at all"
            : RelayedText.Truncate(trimmed, 200);
    }

    /// <summary>
    /// owner/repo out of the URL gh returned, which is the observed answer rather than one
    /// reconstructed from what the caller typed — a bare number carries no repository at all,
    /// and the canonical reference must name one.
    /// </summary>
    private static string RepositoryFrom(string? url)
    {
        if (url is null
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || parsed.AbsolutePath.Trim('/').Split('/') is not [{ } owner, { } repository, ..])
        {
            throw new DomainValidationException(
                "gh returned an issue with no URL, so the repository it belongs to cannot be "
                + "named. Re-run with the full issue URL so the reference records a repository.");
        }

        // The host is checked again on what came back, not only on what was typed: a bare number
        // is resolved by gh against its own default host, which on an enterprise-configured
        // machine is not github.com, and the reference cannot say so.
        if (!IsGitHubDotCom(parsed.Host))
        {
            throw ForeignHost(url, parsed.Host);
        }

        return IsGitHubName(owner) && IsGitHubName(repository)
            ? $"{owner}/{repository}"
            : throw NotARepositoryPath(url, $"{owner}/{repository}");
    }

    /// <summary>
    /// Whether a path segment can be a GitHub owner or repository name: letters, digits, hyphen,
    /// underscore and dot, and at least one character. It is asked of the URL that came back for
    /// the same reason the host is (<see cref="IsGitHubDotCom"/>) — exit code zero is not a promise
    /// of shape, and this reference is stored, then rendered on every surface that shows the task.
    /// <para>
    /// The concrete failure is a bracket. <c>Uri.AbsolutePath</c> escapes a space but leaves
    /// <c>[</c> and <c>]</c> alone, so an owner containing one survives into the canonical
    /// reference and then into <c>h9k task show</c>, whose external row is Spectre markup of the
    /// form <c>[link=url]label[/]</c>: the <c>]</c> ends the tag early and every later reading of
    /// that task throws an unmapped stack trace instead of printing. Refusing the import is the
    /// honest place to stop, because there is nothing recoverable to store — the alternative is a
    /// reference that names a repository which cannot exist.
    /// </para>
    /// </summary>
    private static bool IsGitHubName(string segment) =>
        segment.Length > 0 && segment.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static DomainValidationException NotARepositoryPath(string url, string path) => new(
        $"{RelayedText.OneLine(url)} does not name a github.com repository: "
        + $"'{RelayedText.OneLine(path)}' is not an owner and repository (GitHub names are letters, "
        + "digits, hyphens, underscores and dots). Nothing here can be adopted, because the "
        + "reference Hall9k would store names no repository. Re-run with the issue URL as GitHub "
        + "prints it (https://github.com/owner/repo/issues/42).");

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
