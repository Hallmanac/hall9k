using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hall9k.Connectors.Processes;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// One submitted review as GitHub itself recorded it, read for the single purpose of deciding
/// whose words a session is about to answer (task: a dispatched session never speaks to a person
/// at the top level of a pull request on its own).
/// <para>
/// <see cref="Url"/> is the address GitHub reported for the review, not the one the caller asked
/// with. <see cref="AuthorLogin"/> is honestly null when GitHub returned no readable author (a
/// deleted account), and such a review is a person's: unattributable authorship is never read as a
/// bot's (AGENTS.md: never guess at unobserved facts), because erring toward a bot would let a
/// session answer a person unseen.
/// </para>
/// </summary>
public sealed record PullRequestReview(string Url, string? AuthorLogin, bool AuthoredByBot);

/// <summary>
/// Reads one review off a pull request through the already-authenticated <c>gh</c> seam every
/// other GitHub read here uses. It exists because <c>h9k pr reply --review</c> must decide whose
/// review a body answer is aimed at from GitHub's record at the time it runs, never from the
/// session's word, and the CLI cannot reference the daemon's own inspector. The bot-versus-person
/// rule is <see cref="GitHubActors.Classify"/>, the same one the inspector applies.
/// <para>
/// Throws <see cref="DomainValidationException"/> for a review it cannot vouch for: one that is
/// not on the named pull request, or one GitHub would not return. The caller posts nothing and
/// records nothing on that, so a refused read can never be mistaken later for GitHub's report.
/// </para>
/// </summary>
public sealed class GitHubPullRequestReviews(ProcessRunner? runner = null)
{
    private static readonly Regex ReviewFragment = new(
        @"^pullrequestreview-(?<id>\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly ProcessRunner runner = runner ?? ExternalProcess.Runner;

    /// <summary>
    /// Reads the review <paramref name="reviewUrl"/> names, which must sit on
    /// <paramref name="pullRequestUrl"/> (the task's own pull request): same host, same
    /// repository, same number. Both comparisons are case-insensitive, the way GitHub treats them.
    /// </summary>
    public async Task<PullRequestReview> ReadAsync(
        string pullRequestUrl, string reviewUrl, string workingDirectory, CancellationToken cancellationToken)
    {
        (string repository, int number, long reviewId) = ParseReviewTarget(pullRequestUrl, reviewUrl);

        ProcessResult result;
        try
        {
            result = await runner(
                "gh",
                ["api", $"repos/{repository}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/reviews/{reviewId.ToString(CultureInfo.InvariantCulture)}"],
                workingDirectory,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Unreadable(reviewUrl, RelayedText.OneLine(exception.Message));
        }

        if (result.ExitCode != 0)
        {
            throw Unreadable(reviewUrl, RelayedText.OneLine(result.StandardError).Trim());
        }

        return Parse(result.StandardOutput, repository, number, reviewUrl);
    }

    /// <summary>
    /// The reading half, split from the <c>gh</c> call so the classification and the
    /// pull-request match are testable against payloads. Internal for exactly that.
    /// </summary>
    internal static PullRequestReview Parse(string json, string repository, int number, string reviewUrl)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Unreadable(reviewUrl, "GitHub's answer was not JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Unreadable(reviewUrl, "GitHub's answer was not a review.");
            }

            // The path already names the pull request, so GitHub answering 200 for a review that
            // belongs elsewhere should not happen; the reported pull request is checked anyway
            // because this is the one fact the posted text will vouch for to a real reviewer.
            string? pullRequestApiUrl = ReadString(root, "pull_request_url");
            if (pullRequestApiUrl.IsBlank()
                || !pullRequestApiUrl.EndsWith(
                    $"/repos/{repository}/pulls/{number.ToString(CultureInfo.InvariantCulture)}",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainValidationException(
                    $"GitHub did not place review {RelayedText.OneLine(reviewUrl)} on pull request "
                    + $"{repository}#{number}. Nothing was posted. Pass the url of a review on this task's "
                    + "own pull request.");
            }

            string? htmlUrl = ReadString(root, "html_url");
            if (htmlUrl.IsBlank())
            {
                throw Unreadable(reviewUrl, "GitHub's answer carried no address for the review.");
            }

            string? login = null;
            string typeName = string.Empty;
            if (root.TryGetProperty("user", out JsonElement user) && user.ValueKind == JsonValueKind.Object)
            {
                login = ReadString(user, "login");
                typeName = ReadString(user, "type") ?? string.Empty;
            }

            // No readable author is a person's review. A login-less user object is the same.
            bool bot = login.IsNotBlank()
                && GitHubActors.Classify(typeName, login) == GitHubActorKind.Bot;
            return new PullRequestReview(htmlUrl, login.IsNotBlank() ? login : null, bot);
        }
    }

    /// <summary>
    /// The repository, pull request number and numeric review id out of a review url, after
    /// proving the url sits on the task's own pull request. Public-facing text is quoted through
    /// <see cref="RelayedText.OneLine"/> because the url is the session's word.
    /// </summary>
    internal static (string Repository, int Number, long ReviewId) ParseReviewTarget(
        string pullRequestUrl, string reviewUrl)
    {
        int number = PullRequestUrls.ParseNumber(pullRequestUrl);
        if (number <= 0
            || !Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out Uri? pullRequest)
            || PullRequestUrls.RepositoryFrom(pullRequest) is not { } repository)
        {
            throw new DomainConflictException(
                $"The task's pull request '{RelayedText.OneLine(pullRequestUrl)}' does not name a pull request, "
                + "so there is no review of its own to answer.");
        }

        Match fragment = Uri.TryCreate(reviewUrl.Trim(), UriKind.Absolute, out Uri? review)
            ? ReviewFragment.Match(review.Fragment.TrimStart('#'))
            : Match.Empty;
        if (review is null
            || !fragment.Success
            || !string.Equals(review.Host, pullRequest.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                PullRequestUrls.RepositoryFrom(review), repository, StringComparison.OrdinalIgnoreCase)
            || PullRequestUrls.ParseNumber(review.GetLeftPart(UriPartial.Path)) != number
            || !long.TryParse(fragment.Groups["id"].Value, CultureInfo.InvariantCulture, out long reviewId))
        {
            throw new DomainValidationException(
                $"'{RelayedText.OneLine(reviewUrl)}' is not a review on this task's own pull request "
                + $"({pullRequestUrl}). Nothing was posted. A review url reads "
                + $"{pullRequestUrl}#pullrequestreview-<id>, and is the one shown on the review itself.");
        }

        return (repository, number, reviewId);
    }

    private static DomainValidationException Unreadable(string reviewUrl, string detail) =>
        new(
            $"Could not read review {RelayedText.OneLine(reviewUrl)} from GitHub, so whose review it is "
            + $"cannot be told. Nothing was posted and nothing was recorded. {detail}".TrimEnd());

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
