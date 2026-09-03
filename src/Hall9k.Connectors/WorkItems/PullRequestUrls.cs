using System.Diagnostics.CodeAnalysis;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// Reads the number out of a pull-request URL (https://github.com/&lt;owner&gt;/&lt;repo&gt;/pull/&lt;number&gt;).
/// URLs are not always canonical gh output — h9k task resolve --pr accepts a human-pasted
/// one — so the number survives trailing slashes and query/fragment noise. Anything
/// unparsable yields 0, never a guess (an honest "no number" the callers treat as absent).
/// The path's second-to-last segment must literally be "pull" — an otherwise well-formed
/// GitHub URL ending in a number, such as an issue (.../issues/24), does not name a pull
/// request and must not be read as one (adversarial review, cycle 1: a human-pasted --pr
/// naming an issue silently enrolled that issue's number as a run's merge signal).
/// </summary>
public static class PullRequestUrls
{
    public static int ParseNumber(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return 0;
        }

        string[] segments = parsed.AbsolutePath.Trim('/').Split('/');
        return segments is [.., "pull", string numberSegment] && int.TryParse(numberSegment, out int number)
            ? number
            : 0;
    }

    /// <summary>
    /// "owner/repo" out of a repository or pull-request URL's first two path segments — the
    /// shared home <c>TaskResolveCommand</c>'s own repository-match guard uses (independent
    /// pre-PR review, cycle 3, low: an earlier copy in <c>TaskResolveCommand</c> itself claimed
    /// there was no shared home across the Cli/Daemon boundary, when this method's own sibling,
    /// <see cref="ParseNumber"/>, already lived here and was already called from both). Both
    /// projects reference <c>Hall9k.Connectors</c>, so a future caller on either side of that
    /// boundary can take this instead of writing its own copy. <c>RunLauncher.OwnerRepoFrom</c>
    /// (<c>Hall9k.Daemon</c>) is the same shape and predates this method; it is not switched
    /// over here, since that file is untouched by this branch's own changes.
    /// </summary>
    public static string? RepositoryFrom(Uri? url) =>
        url is not null && url.AbsolutePath.Trim('/').Split('/') is [{ Length: > 0 } owner, { Length: > 0 } repository, ..]
            ? $"{owner}/{TrimGitSuffix(repository)}"
            : null;

    private static string TrimGitSuffix(string repository) =>
        repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repository[..^4] : repository;

    /// <summary>
    /// Whether <paramref name="pullRequestUrl"/> is safe to treat as a task's own pull request
    /// anywhere it can be watched for a merge — <c>TaskResolveCommand</c>'s run-stream and
    /// task-stream paths, and <c>CloseoutEngine.InspectMissingRunAsync</c>, all share this one
    /// check rather than each keeping its own copy (independent pre-PR review, cycle 1, medium:
    /// the sweep read the pull request straight off the task stream with neither this guard nor
    /// the pr-review type check its two siblings already enforced, so a foreign URL recorded
    /// through <c>h9k task resolve --pr</c> with no run stream to protect it could reach the
    /// sweep unchecked). False for a blank URL, one that does not parse to a real pull request
    /// number (never guess a number, AGENTS.md's never-guess rule), or one naming a repository
    /// other than <paramref name="projectRepositoryUrl"/>'s own — checked by host as well as
    /// owner/repo, since <see cref="RepositoryFrom"/> alone reads path segments only and would
    /// otherwise treat <c>https://gitlab.com/x/y/pull/24</c> as the same repository as a project
    /// recorded at <c>https://github.com/x/y</c>. A mistyped, copy-pasted, or foreign URL must
    /// never become a run's merge signal: a false match lets an unrelated pull request's merge
    /// complete this task's closeout and delete this run's own branch out from under it
    /// (adversarial review, cycle 1). The repository check is a courtesy: a project whose
    /// repository cannot be resolved at all proceeds rather than blocking on information the
    /// caller does not have.
    /// </summary>
    public static bool IsSafePullRequestUrl(
        [NotNullWhen(true)] string? pullRequestUrl, Uri? projectRepositoryUrl)
    {
        if (pullRequestUrl.IsBlank() || ParseNumber(pullRequestUrl) <= 0)
        {
            return false;
        }

        if (projectRepositoryUrl is not null
            && Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out Uri? parsedPullRequestUrl)
            && RepositoryFrom(projectRepositoryUrl) is { } projectRepository
            && RepositoryFrom(parsedPullRequestUrl) is { } pullRequestRepository
            && (!string.Equals(projectRepositoryUrl.Host, parsedPullRequestUrl.Host, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(projectRepository, pullRequestRepository, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }
}
