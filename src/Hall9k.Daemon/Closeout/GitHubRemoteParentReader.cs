using System.Text.Json;
using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Daemon.Closeout;

/// <summary>
/// Reads a stacked parent's pull request through the <c>gh</c> CLI, which is how every other
/// GitHub read in this daemon reaches the provider — the operator's own login, no token of
/// Hall9k's. One call per look, deliberately: this is a poll on the closeout watcher's cadence
/// against a pull request nothing here owns, and there is nothing on it to gather beyond where
/// its branch is and whether it is still open.
/// <para>
/// The <see cref="ProcessRunner"/> is injected rather than spawned inline (unlike
/// <see cref="GitHubPullRequestInspector"/>, which predates that discipline) so the sweep's own
/// tests exercise the parsing and the missing-versus-unreadable split against recorded <c>gh</c>
/// output rather than a live account.
/// </para>
/// </summary>
public sealed partial class GitHubRemoteParentReader(ProcessRunner processRunner) : IRemoteParentReader
{
    /// <summary>
    /// What <c>gh</c> says when the repository simply has no pull request with that number, as
    /// distinct from every way a look can fail — an unreachable host, an expired credential, a
    /// repository this login cannot see. The two are different facts carrying different outcomes
    /// (a child waits on the first and is told nothing about the second), and this message is the
    /// one observation that separates them. Anything gh says that this does not recognise counts as
    /// the read failing rather than the pull request missing, which is the direction that keeps
    /// looking instead of asserting an absence nobody observed.
    /// </summary>
    private const string MissingPullRequestMarker = "Could not resolve to a PullRequest";

    public async Task<RemoteParentRead> ReadAsync(
        string repositoryPath, int pullRequestNumber, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner(
                "gh",
                ["pr", "view", pullRequestNumber.ToString(),
                    "--json", "state,headRefName,headRefOid,baseRefName,url,closingIssuesReferences"],
                repositoryPath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return RemoteParentRead.Unobserved(
                $"pull request #{pullRequestNumber} could not be read: {FirstLine(exception.Message)}");
        }

        if (result.ExitCode != 0)
        {
            return result.StandardError.IsNotBlank()
                && result.StandardError.Contains(MissingPullRequestMarker, StringComparison.OrdinalIgnoreCase)
                ? new RemoteParentRead(
                    RemoteParentState.Absent, string.Empty, string.Empty, string.Empty, string.Empty, null,
                    $"this repository has no pull request #{pullRequestNumber}")
                : RemoteParentRead.Unobserved(
                    $"gh could not read pull request #{pullRequestNumber}: {FirstLine(result.StandardError)}");
        }

        try
        {
            return Parse(pullRequestNumber, result.StandardOutput);
        }
        catch (JsonException exception)
        {
            return RemoteParentRead.Unobserved(
                $"gh's answer for pull request #{pullRequestNumber} could not be read as JSON: "
                + FirstLine(exception.Message));
        }
    }

    /// <summary>
    /// Internal so the sweep's tests can drive the mapping off recorded <c>gh</c> output directly.
    /// A state word this build does not recognise reads as <see cref="RemoteParentState.Unknown"/>
    /// rather than being forced into one of the three: an unrecognised answer is not an observation.
    /// </summary>
    internal static RemoteParentRead Parse(int pullRequestNumber, string viewJson)
    {
        using JsonDocument view = JsonDocument.Parse(viewJson);
        JsonElement root = view.RootElement;
        string state = ReadString(root, "state");
        string headBranch = ReadString(root, "headRefName");
        string headCommit = ReadString(root, "headRefOid");
        string baseBranch = ReadString(root, "baseRefName");
        string url = ReadString(root, "url");

        RemoteParentState observed = state switch
        {
            "OPEN" => RemoteParentState.Open,
            "MERGED" => RemoteParentState.Merged,
            "CLOSED" => RemoteParentState.ClosedUnmerged,
            _ => RemoteParentState.Unknown,
        };

        string detail = observed == RemoteParentState.Unknown
            ? $"gh reported pull request #{pullRequestNumber} in a state this build does not recognise "
              + $"('{state}'), so nothing is claimed about it"
            : $"pull request #{pullRequestNumber} is {observed.Describe()}"
              + (headBranch.IsNotBlank() ? $", head branch {headBranch}" : string.Empty)
              + (baseBranch.IsNotBlank() ? $", targeting {baseBranch}" : string.Empty);

        return new RemoteParentRead(
            observed, headBranch, headCommit, baseBranch, url, ReadLinkedWorkItem(root, url), detail);
    }

    /// <summary>
    /// The issue the pull request itself says it closes, read from GitHub's own
    /// <c>closingIssuesReferences</c> rather than from a keyword scraped out of the body — the
    /// provider already resolved the closing keywords, so this is an observation rather than a
    /// reparse. Recorded in the canonical <c>github:owner/repo#42</c> form every
    /// <see cref="ExternalReference"/> uses, so a local task that adopted the same issue matches on
    /// equal terms. Null when the pull request names none, which is the ordinary case for a
    /// repository whose backlog lives in Jira or nowhere.
    /// </summary>
    private static ExternalReference? ReadLinkedWorkItem(JsonElement root, string pullRequestUrl)
    {
        if (!root.TryGetProperty("closingIssuesReferences", out JsonElement references)
            || references.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement reference in references.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // The issue's own URL is the only field that carries the repository, and the
            // repository is half of a canonical reference — a bare number would match an issue of
            // the same number in any repository at all.
            if (OwnerRepositoryAndNumber(ReadString(reference, "url")) is { } fromUrl)
            {
                return new ExternalReference(WorkItemProvider.GitHub, fromUrl);
            }

            // gh has reported the reference without a usable URL. The number alone is real and the
            // repository is not, so it is completed from the PULL REQUEST's own URL — the same
            // repository by construction, since closingIssuesReferences on a pull request in one
            // repository can only name issues gh resolved through it.
            if (reference.TryGetProperty("number", out JsonElement number)
                && number.TryGetInt32(out int issueNumber)
                && OwnerAndRepository(pullRequestUrl) is { } owningRepository)
            {
                return new ExternalReference(WorkItemProvider.GitHub, $"{owningRepository}#{issueNumber}");
            }
        }

        return null;
    }

    /// <summary>
    /// <c>owner/repo#42</c> out of an issue URL, or null when the URL is not one — never a
    /// half-formed reference, which would match nothing and read as though it might.
    /// </summary>
    private static string? OwnerRepositoryAndNumber(string issueUrl) =>
        IssueUrl().Match(issueUrl) is { Success: true } match
            ? $"{match.Groups[1].Value}/{match.Groups[2].Value}#{match.Groups[3].Value}"
            : null;

    /// <summary><c>owner/repo</c> out of any github.com URL, or null when it is not one.</summary>
    private static string? OwnerAndRepository(string url) =>
        RepositoryUrl().Match(url) is { Success: true } match
            ? $"{match.Groups[1].Value}/{match.Groups[2].Value}"
            : null;

    [GeneratedRegex(@"github\.com/([\w.-]+)/([\w.-]+)/issues/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IssueUrl();

    [GeneratedRegex(@"github\.com/([\w.-]+)/([\w.-]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex RepositoryUrl();

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstLine(string? text) =>
        text.IsBlank() ? "no output" : text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
}
