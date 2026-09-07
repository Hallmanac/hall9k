using System.Globalization;
using Hall9k.Connectors.Text;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// How a human names a pull request, in one place: the bare number (<c>42</c>, <c>#42</c>), the
/// <c>owner/repo#42</c> shorthand, or the browser URL. Extracted out of
/// <see cref="GitHubPullRequestProvider"/> when a second surface needed the identical parse
/// (<see cref="GitHubPullRequestSurface"/>, the reviewer's own review lap, Decisions Log #149):
/// two copies of this would be two answers to "which pull request did the reviewer mean", and
/// the whole point of the lap attaching to an existing pr-review task is that both surfaces
/// resolve one reviewer's typing to the same one.
/// <para>
/// A null repository means "the repository gh is run in decides" — the bare-number case — and is
/// carried as null rather than filled in from anywhere, so <c>gh</c> answers that question with
/// its own working directory instead of this platform guessing at it.
/// </para>
/// </summary>
public static class GitHubPullRequestReference
{
    public static (string? Repository, int Number) Parse(string? reference)
    {
        string trimmed = reference?.Trim() ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "No pull request was named. Pass the number (42), the owner/repo#42 shorthand, or the "
                + "pull request URL.");
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? ParseUrl(trimmed)
                : ParseShorthand(trimmed);
    }

    /// <summary>
    /// The canonical <c>owner/repo#42</c> form a stored <c>ExternalReference</c> carries, split
    /// back apart. False rather than throwing: a caller reading a reference off a task is asking
    /// whether it is one, not asserting that it is.
    /// </summary>
    public static bool TryParseCanonical(string reference, out string repository, out int number)
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
    /// The <c>owner/repo</c> a pull request's own URL belongs to. Throws rather than returning
    /// empty: every caller records this as the canonical half of a reference, and a reference
    /// naming no repository is one nothing can ever dedup against or post back to.
    /// </summary>
    public static string RepositoryFromUrl(string? url)
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

    public static bool IsGitHubDotCom(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    public static DomainValidationException ForeignHost(string url, string host) => new(
        $"{RelayedText.OneLine(url)} is on {RelayedText.OneLine(host)}, and Hall9k reviews pull requests "
        + "from github.com only. Write the task with --objective and --context instead.");

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
                $"{RelayedText.OneLine(reference)} is an issue, not a pull request. A pr-review task adopts "
                + "pull requests; an issue has no diff to read.")
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

    private static DomainValidationException Unreadable(string reference) => new(
        $"'{RelayedText.OneLine(reference)}' does not name a GitHub pull request. Use the number (42), "
        + "the owner/repo#42 shorthand, or the pull request URL "
        + "(https://github.com/owner/repo/pull/42).");
}
