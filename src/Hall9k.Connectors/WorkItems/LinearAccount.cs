using Hall9k.Connectors.Credentials;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Connectors.WorkItems;

/// <summary>
/// A registered Linear workspace: the workspace address and the viewer's name, which live on the
/// connection's event stream, and a <see cref="CredentialReference"/>, which is all that ever does
/// (PLAN.md §10). The API key is fetched from the vault at the moment of each call and held no
/// longer than the request that carries it, so a rotated key takes effect on the next command.
/// <para>
/// Turning <c>linear:ENG-123</c> into a link needs the workspace address and nothing else, so
/// <c>h9k task show</c> can build an account and ask for a URL without a keychain prompt or a file
/// read ever happening.
/// </para>
/// <para>
/// A key limited by permission or by team is a real Linear feature, and nothing about the account
/// can prove what the key is allowed to do: <see cref="LinearWorkItemProvider.VerifyAccessAsync"/>
/// proves only that Linear accepts the key.
/// </para>
/// </summary>
public sealed class LinearAccount
{
    private readonly CredentialVault vault;

    /// <summary>The key itself, and only for an account that has not been written down yet.</summary>
    private readonly string? unstoredKey;

    private LinearAccount(Uri? workspaceUrl, string? viewerName, CredentialReference? credential, string? unstoredKey, CredentialVault? vault)
    {
        WorkspaceUrl = workspaceUrl;
        ViewerName = viewerName;
        Credential = credential;
        this.unstoredKey = unstoredKey;
        this.vault = vault ?? CredentialVault.Default;
    }

    /// <summary>
    /// The workspace address, <c>https://linear.app/&lt;urlKey&gt;</c>, or null for an account that is
    /// being proven and so does not know its workspace yet: the viewer read is what learns it.
    /// </summary>
    public Uri? WorkspaceUrl { get; }

    /// <summary>The name Linear reported for the key's user, for messages; null before registration.</summary>
    public string? ViewerName { get; }

    /// <summary>Where the key lives; never the key. Null only for <see cref="WithKeyInHand"/>.</summary>
    public CredentialReference? Credential { get; }

    /// <summary>An account as a registered connection records it.</summary>
    public static LinearAccount Registered(
        Uri workspaceUrl, string viewerName, CredentialReference credential, CredentialVault? vault = null) =>
        new(workspaceUrl, viewerName, credential, null, vault);

    /// <summary>
    /// A key that already lives somewhere Hall9k only points at (an environment variable, a
    /// keychain item), proven before the reference is recorded.
    /// </summary>
    public static LinearAccount ForReference(CredentialReference credential, CredentialVault? vault = null) =>
        new(null, null, credential, null, vault);

    /// <summary>
    /// An account authenticated by a key held for the length of one command, so the key can be
    /// proven before anything writes it down. The file a stored key lands in is named from the
    /// workspace and the viewer, so re-registering the same user writes over the file the live
    /// connection already reads, and a mistyped or revoked key would otherwise have destroyed the
    /// working one from a command that reported failure (the same order Jira's registration keeps).
    /// </summary>
    public static LinearAccount WithKeyInHand(string key) => new(null, null, null, key, null);

    /// <summary>
    /// The workspace as it is written in messages: <c>https://linear.app/&lt;urlKey&gt;</c> with no
    /// trailing slash, or a statement that none is recorded.
    /// </summary>
    public string Workspace => WorkspaceUrl?.GetLeftPart(UriPartial.Path).TrimEnd('/') ?? "the Linear workspace";

    /// <summary>
    /// The Authorization header value for this account: the key, with no scheme. <paramref name="purpose"/>
    /// is what the call was for, so a vault refusal names the thing that failed. The result is handed
    /// straight to a request and never stored.
    /// </summary>
    public async ValueTask<string> AuthorizationAsync(string purpose, CancellationToken cancellationToken) =>
        (unstoredKey, Credential) switch
        {
            ({ } inHand, _) => inHand,
            (_, { } credential) => await vault.ResolveAsync(credential, purpose, cancellationToken),
            _ => throw new DomainValidationException(
                $"The account used to {purpose} carries neither a stored credential reference nor a "
                + "key, so there is nothing to authenticate with. Register the connection again: "
                + "h9k connection add linear --help"),
        };

    /// <summary>
    /// The workspace address Linear's <c>organization.urlKey</c> names. The key is refused unless it
    /// is the plain slug Linear documents, because it is built into URLs that are printed, stored,
    /// and compared.
    /// </summary>
    public static Uri WorkspaceFor(string urlKey) =>
        urlKey.Length > 0 && urlKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? new Uri($"https://linear.app/{urlKey}")
            : throw new DomainValidationException(
                $"Linear answered with a workspace key, '{Text.RelayedText.OneLine(urlKey)}', that does not read "
                + "as the slug in a Linear address (https://linear.app/<workspace>), so no workspace address "
                + "was recorded.");
}
