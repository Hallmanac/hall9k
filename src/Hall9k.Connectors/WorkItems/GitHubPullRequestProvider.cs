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
public sealed record PullRequestFacts(
    string Repository, int Number, string Title, string? Body, string State, string BaseRefName, Uri? Url);

/// <summary>
/// GitHub pull requests through the <c>gh</c> CLI, exactly the same already-authenticated seam
/// <see cref="GitHubWorkItemProvider"/> uses for issues (PLAN.md §10). A pull request is a
/// distinct <see cref="WorkItemProvider"/> from an issue, never the same one: a pr-review task
/// adopts the PR itself as the thing it reviews, and <see cref="GitHubWorkItemProvider"/>
/// deliberately refuses a pull-request reference for exactly that reason.
/// </summary>
public sealed class GitHubPullRequestProvider(ProcessRunner? runner = null, TimeProvider? clock = null) : IWorkItemProvider
{
    private const string RequestedFields = "number,title,body,state,url,baseRefName";

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
        (string? repository, int number) = ParseReference(reference);

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

    public Uri? WebUrl(ExternalReference reference) =>
        reference.Provider == WorkItemProvider.GitHubPullRequest
        && TryParseCanonical(reference.Reference, out string repository, out int number)
            ? new Uri($"https://github.com/{repository}/pull/{number}")
            : null;

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

    private static (string? Repository, int Number) ParseReference(string reference)
    {
        string trimmed = reference?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "--from-pr needs a pull request to import. Pass the number (42), the owner/repo#42 "
                + "shorthand, or the pull request URL.");
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
        if (segments is [{ } owner, { } repository, "pull", { } number, ..]
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            return ($"{owner}/{repository}", parsed);
        }

        throw segments is [_, _, "issues", ..]
            ? new DomainValidationException(
                $"{RelayedText.OneLine(reference)} is an issue, not a pull request. --from-pr adopts pull "
                + "requests; an issue has no diff for a pr-review task to read.")
            : Unreadable(reference);
    }

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

    private static bool IsGitHubDotCom(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

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

            return new PullRequestFacts(
                RepositoryFrom(url),
                number,
                ReadString(root, "title") ?? string.Empty,
                ReadString(root, "body") is { } body && body.IsNotBlank() ? body : null,
                ReadString(root, "state") ?? string.Empty,
                ReadString(root, "baseRefName") ?? string.Empty,
                url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed : null);
        }
    }

    private static string RepositoryFrom(string? url)
    {
        if (url is null
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || parsed.AbsolutePath.Trim('/').Split('/') is not [{ } owner, { } repository, ..])
        {
            throw new DomainValidationException(
                "gh returned a pull request with no URL, so the repository it belongs to cannot be "
                + "named. Re-run with the full pull request URL so the reference records a repository.");
        }

        if (!IsGitHubDotCom(parsed.Host))
        {
            throw ForeignHost(url, parsed.Host);
        }

        return $"{owner}/{repository}";
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

    private static DomainValidationException ForeignHost(string url, string host) => new(
        $"{RelayedText.OneLine(url)} is on {RelayedText.OneLine(host)}, and Hall9k reviews pull requests "
        + "from github.com only. Write the task with --objective and --context instead.");

    private static DomainValidationException Unreadable(string reference) => new(
        $"'{RelayedText.OneLine(reference)}' does not name a GitHub pull request. Use the number (42), "
        + "the owner/repo#42 shorthand, or the pull request URL "
        + "(https://github.com/owner/repo/pull/42).");
}
