using System.Globalization;
using System.Text.Json;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Everything a pr-review task needs to know about the pull request it targets, as gh
/// reported it just now. Title/Body seed the task's objective/agent context at adoption
/// (<see cref="GitHubPullRequestProvider.ImportAsync"/>); BaseRefName is read again, fresh,
/// at every dispatch (never cached on the task) so the diff a review reads is always against
/// the PR's current base, not a snapshot from whenever it was adopted.
/// </summary>
/// <param name="IsCrossRepository">
/// Whether this pull request's head sits on a fork of <see cref="Repository"/> rather than a
/// branch of it (security review idea 6be68ee2, process-injection finding 1) — gh's own
/// <c>isCrossRepository</c> field. A head repository can never change once a pull request is
/// opened (only its base can move, on a retarget), so a caller resolves this once at dispatch and
/// never needs to re-read it later. False when the field was absent or unparseable, never a guess
/// standing in for an observation this read genuinely could not make.
/// </param>
/// <param name="HeadRefOid">
/// The pull request's own head commit at the moment this was read (idea 6be68ee2, finding 1,
/// phase two): what a pre-flight verdict is bound to, and what the checkout that follows a safe
/// verdict re-checks after its own fetch of <c>refs/pull/&lt;n&gt;/head</c>, since GitHub's own
/// read here and the checkout's own fetch are two separate observations that can disagree if the
/// head moved in between. Blank when the field was absent or unparseable, never a guess standing
/// in for an observation this read genuinely could not make.
/// </param>
public sealed record PullRequestFacts(
    string Repository, int Number, string Title, string? Body, string State, string BaseRefName, Uri? Url,
    bool IsCrossRepository = false, string HeadRefOid = "");

/// <summary>
/// GitHub pull requests through the <c>gh</c> CLI, exactly the same already-authenticated seam
/// <see cref="GitHubWorkItemProvider"/> uses for issues (PLAN.md §10). A pull request is a
/// distinct <see cref="WorkItemProvider"/> from an issue, never the same one: a pr-review task
/// adopts the PR itself as the thing it reviews, and <see cref="GitHubWorkItemProvider"/>
/// deliberately refuses a pull-request reference for exactly that reason.
/// </summary>
public sealed class GitHubPullRequestProvider(ProcessRunner? runner = null, TimeProvider? clock = null) : IWorkItemProvider
{
    private const string RequestedFields = "number,title,body,state,url,baseRefName,isCrossRepository,headRefOid";

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public WorkItemProvider Provider => WorkItemProvider.GitHubPullRequest;

    public async Task<ImportedWorkItem> ImportAsync(WorkItemImportRequest request, CancellationToken cancellationToken)
    {
        PullRequestFacts facts = await FetchFactsAsync(request.Reference, request.WorkingDirectory, cancellationToken);
        return new ImportedWorkItem(
            new ExternalReference(WorkItemProvider.GitHubPullRequest, $"{facts.Repository}#{facts.Number}"),
            facts.Title,
            facts.Body,
            WorkItemStatus.Parse(facts.State),
            facts.Url,
            clock.GetUtcNow());
    }

    /// <summary>
    /// The live read a dispatch reads BaseRefName from (RunLauncher, before the worktree is
    /// cut): a task's own adoption-time snapshot is never trusted for this, because the PR's
    /// base can move after adoption and the diff must always read against the PR's actual,
    /// current base.
    /// </summary>
    public async Task<PullRequestFacts> FetchFactsAsync(
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
    /// The pull-request review pre-flight's own first read (idea 6be68ee2, finding 1, phase one):
    /// every path the pull request's diff touches, through <c>gh pr diff --name-only</c>. Run from
    /// the pre-flight's own run directory, never a git repository — <paramref name="reference"/>
    /// always carries an explicit repository for a pr-review task's own adopted reference, so
    /// <c>--repo</c> is always passed and <c>gh</c> never needs to infer one from a git remote at
    /// <paramref name="workingDirectory"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchChangedFileNamesAsync(
        string reference, string workingDirectory, CancellationToken cancellationToken)
    {
        (string? repository, int number) = GitHubPullRequestReference.Parse(reference);

        List<string> arguments =
            ["pr", "diff", number.ToString(CultureInfo.InvariantCulture), "--name-only"];
        if (repository is not null)
        {
            arguments.AddRange(["--repo", repository]);
        }

        ProcessResult result = await RunGhAsync(arguments, workingDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Explain(result.StandardError, repository, number, workingDirectory);
        }

        return [.. result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.IsNotBlank())];
    }

    /// <summary>
    /// The pull-request review pre-flight's own second read: the full unified diff, through
    /// <c>gh pr diff</c> — the pre-flight prompt then fences only the hunks for the files
    /// <see cref="Hall9k.Domain.Features.PrReviewPreflight.PrReviewPreflightSurfaceMatcher"/>
    /// matched, capped, never the whole thing. Same "always carries an explicit repository"
    /// reasoning as <see cref="FetchChangedFileNamesAsync"/>.
    /// </summary>
    public async Task<string> FetchDiffAsync(string reference, string workingDirectory, CancellationToken cancellationToken)
    {
        (string? repository, int number) = GitHubPullRequestReference.Parse(reference);

        List<string> arguments = ["pr", "diff", number.ToString(CultureInfo.InvariantCulture)];
        if (repository is not null)
        {
            arguments.AddRange(["--repo", repository]);
        }

        ProcessResult result = await RunGhAsync(arguments, workingDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Explain(result.StandardError, repository, number, workingDirectory);
        }

        return result.StandardOutput;
    }

    public Uri? WebUrl(ExternalReference reference) =>
        reference.Provider == WorkItemProvider.GitHubPullRequest
        && GitHubPullRequestReference.TryParseCanonical(reference.Reference, out string repository, out int number)
            ? new Uri($"https://github.com/{repository}/pull/{number}")
            : null;

    private static PullRequestFacts Map(string json, int requestedNumber)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw NotAPullRequestDocument(json, exception.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw NotAPullRequestDocument(json, $"the JSON it printed is {document.RootElement.ValueKind}, not an object");
            }

            JsonElement root = document.RootElement;
            string? url = ReadString(root, "url");
            int number = root.TryGetProperty("number", out JsonElement element)
                && element.ValueKind is JsonValueKind.Number
                && element.TryGetInt32(out int reported)
                    ? reported
                    : requestedNumber;

            bool isCrossRepository = root.TryGetProperty("isCrossRepository", out JsonElement crossRepository)
                && crossRepository.ValueKind is JsonValueKind.True or JsonValueKind.False
                && crossRepository.GetBoolean();

            return new PullRequestFacts(
                GitHubPullRequestReference.RepositoryFromUrl(url),
                number,
                ReadString(root, "title") ?? string.Empty,
                ReadString(root, "body") is { } body && body.IsNotBlank() ? body : null,
                ReadString(root, "state") ?? string.Empty,
                ReadString(root, "baseRefName") ?? string.Empty,
                url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed : null,
                isCrossRepository,
                ReadString(root, "headRefOid") ?? string.Empty);
        }
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
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
                    "Could not run gh, the GitHub CLI Hall9k reads pull requests through: "
                    + $"{exception.Message}. Install it (https://cli.github.com) and sign in with "
                    + "'gh auth login', then try again.")
                : new DomainNotFoundException(
                    $"The project's repository path does not exist: {workingDirectory}. gh reads the "
                    + $"pull request from the directory it runs in. gh reported: {exception.Message}");
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
                $"{exception.Message} It was reading the pull request from {workingDirectory}. A read that "
                + "stops here is usually gh, or something gh started, waiting on input it cannot ask for — "
                + "run 'gh auth status' by hand from that directory, then try again.");
        }
    }

    private static DomainException Explain(string standardError, string? repository, int number, string workingDirectory)
    {
        string reported = RelayedText.OneLine(standardError).Trim();
        string named = repository is null ? $"#{number}" : $"{repository}#{number}";

        if (reported.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase))
        {
            return new DomainNotFoundException(
                $"gh could not resolve the repository for {named}"
                + (repository is null ? $", read from the project's path at {workingDirectory}" : string.Empty)
                + $". gh reported: {reported}.");
        }

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
                $"gh is not authenticated for {named}. Run 'gh auth login' and try again. "
                + $"gh reported: {reported}");
        }

        return new DomainValidationException($"gh could not read pull request {named}: {reported}");
    }

    private static DomainValidationException NotAPullRequestDocument(string json, string reported) => new(
        "gh exited successfully but did not answer with a pull request in JSON, so there is nothing to "
        + $"adopt: {reported}. gh printed: {RelayedText.OneLine(RelayedText.Truncate(json, 200))}. Check "
        + "that the 'gh' on PATH is the GitHub CLI itself.");
}
