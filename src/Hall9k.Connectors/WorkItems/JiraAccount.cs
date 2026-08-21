using System.Text;
using Hall9k.Connectors.Credentials;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// A registered Jira Cloud account: the site and the email, which live on the connection's event
/// stream, and a <see cref="CredentialReference"/>, which is all that ever does (PLAN.md §10).
/// The token itself is fetched from the vault at the moment of each call and held no longer than
/// the request that carries it, so a rotated token takes effect on the next command rather than
/// on the next daemon restart.
/// <para>
/// That is also what lets this type be built for free. Turning <c>jira:PROJ-123</c> into a link
/// needs the site and nothing else, so <c>h9k task show</c> can construct an account and ask for
/// a URL without a keychain prompt or a file read ever happening.
/// </para>
/// <para>
/// Auth scope is Jira Cloud API tokens (backlog 18): email plus token as HTTP Basic, which is
/// what Atlassian documents for Cloud. Server and Data Center use a different scheme and are out
/// of scope until somebody has one, rather than being half-supported by a header that happens to
/// be shaped the same.
/// </para>
/// </summary>
public sealed class JiraAccount(
    Uri siteUrl, string accountEmail, CredentialReference credential, CredentialVault? vault = null)
{
    private readonly CredentialVault vault = vault ?? CredentialVault.Default;

    public Uri SiteUrl { get; } = siteUrl;

    public string AccountEmail { get; } = accountEmail;

    /// <summary>Where the token lives; never the token.</summary>
    public CredentialReference Credential { get; } = credential;

    /// <summary>
    /// The site as it is written in messages and built into URLs: scheme and host, no trailing
    /// slash and no path. A site registered as "https://org.atlassian.net/jira/software" would
    /// otherwise produce request URLs with the console's own path buried in them, which fails as
    /// a 404 that reads like a missing card.
    /// </summary>
    public string Site => SiteUrl.GetLeftPart(UriPartial.Authority);

    /// <summary>A URL under this site's REST API, built from the one canonical site string.</summary>
    public Uri Endpoint(string relativePath) => new($"{Site}{relativePath}");

    /// <summary>
    /// The Basic header for this account, with the token read now. <paramref name="purpose"/> is
    /// what the call was for, so a vault refusal names the thing that failed rather than only
    /// that something did. The result is handed straight to a request and never stored.
    /// </summary>
    public async ValueTask<string> AuthorizationAsync(string purpose, CancellationToken cancellationToken)
    {
        string token = await vault.ResolveAsync(Credential, purpose, cancellationToken);
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AccountEmail}:{token}"));
    }

    /// <summary>
    /// The site as a human types it, made into the absolute https URL the rest of this type
    /// assumes. A bare host is accepted because that is how people say it out loud, and http is
    /// refused outright: a Basic header is the credential itself in every request, and sending
    /// one unencrypted would put the token on the wire in exchange for saving a typo.
    /// </summary>
    public static Uri ParseSite(string? value)
    {
        string trimmed = value?.Trim().TrimEnd('/') ?? string.Empty;
        if (trimmed.IsBlank())
        {
            throw new DomainValidationException(
                "A Jira connection needs its site, for example https://your-org.atlassian.net.");
        }

        string candidate = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? site)
            || site.HostNameType is UriHostNameType.Unknown
            || site.Host.IsBlank())
        {
            throw new DomainValidationException(
                $"'{Text.RelayedText.OneLine(trimmed)}' is not a Jira site URL. It looks like "
                + "https://your-org.atlassian.net — the address you see in the browser when the board "
                + "is open.");
        }

        return site.Scheme == Uri.UriSchemeHttps
            ? site
            : throw new DomainValidationException(
                $"'{Text.RelayedText.OneLine(trimmed)}' is not https. Every request Hall9k makes to Jira "
                + "carries the API token in an Authorization header, so an unencrypted site would put "
                + "that token on the wire. Use the https address.");
    }
}
