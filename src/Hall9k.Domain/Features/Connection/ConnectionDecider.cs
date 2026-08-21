using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Connection;

public static class ConnectionDecider
{
    public static ConnectionRegistered Register(
        Guid id,
        Guid ownerId,
        WorkItemProvider provider,
        string externalAccountId,
        CredentialReference credentialReference,
        DateTimeOffset registeredAt,
        Uri? siteUrl = null)
    {
        if (provider == WorkItemProvider.Unknown)
        {
            throw new DomainValidationException("A connection requires a known provider (e.g. github).");
        }

        if (externalAccountId.IsBlank())
        {
            throw new DomainValidationException("A connection requires the external account id it authenticates as.");
        }

        // The credential is a pointer, and a pointer that names nothing points nowhere. gh-cli is
        // the one kind that needs no identifier, because the thing it points at is "whoever the
        // machine's gh is logged in as" — every other kind names a variable, a keychain entry, or
        // a file, and a connection registered without that name would be a credential reference
        // that cannot be resolved, discovered at the first import rather than here.
        if (credentialReference.Kind != CredentialKind.GhCli && credentialReference.Identifier.IsBlank())
        {
            throw new DomainValidationException(
                $"A '{credentialReference.Kind.Value}' credential reference names where the secret lives "
                + "(the variable, the keychain entry, or the file), and this one names nothing.");
        }

        // Jira accounts live at a tenant of their own and nothing can be read without knowing
        // which; GitHub has exactly one home, so the field stays null there rather than being
        // filled in with the obvious answer. The rule is stated per provider deliberately: a
        // site required of every connection would put "https://github.com" on record as an
        // observation nobody made.
        if (provider == WorkItemProvider.Jira && siteUrl is null)
        {
            throw new DomainValidationException(
                "A Jira connection requires the site it authenticates against, "
                + "for example https://your-org.atlassian.net.");
        }

        // The rule runs both ways, because half of it would let the stream say what the other
        // half forbids: a GitHub connection carrying https://github.com records a tenant nobody
        // chose and makes a null SiteUrl mean "not filled in" instead of "there is one home".
        // Written as "every provider but Jira", so a provider that genuinely has tenants of its
        // own is refused here the first time somebody adds one, and the decision to let it carry
        // a site gets made in this method rather than discovered on the stream afterwards.
        if (provider != WorkItemProvider.Jira && siteUrl is not null)
        {
            throw new DomainValidationException(
                $"A '{provider.Value}' connection has one home, so it records no site, and this one was "
                + $"given {siteUrl}. Register it without a site; the field is Jira's, where an account "
                + "lives at a tenant of its own.");
        }

        return new ConnectionRegistered(
            id, ownerId, provider, externalAccountId, credentialReference, registeredAt, siteUrl);
    }

    /// <summary>
    /// The same connection with new details. It runs the identical rules rather than a relaxed
    /// set, because a re-registration that could record a Jira connection with no site would make
    /// registering twice a way around the check that registering once enforces.
    /// </summary>
    public static ConnectionReregistered Reregister(
        ConnectionAggregate connection,
        string externalAccountId,
        CredentialReference credentialReference,
        DateTimeOffset reregisteredAt,
        Uri? siteUrl = null)
    {
        ConnectionRegistered vetted = Register(
            connection.Id,
            connection.OwnerId,
            connection.Provider,
            externalAccountId,
            credentialReference,
            reregisteredAt,
            siteUrl);

        return new ConnectionReregistered(
            vetted.Id, vetted.Provider, vetted.ExternalAccountId,
            vetted.CredentialReference, reregisteredAt, vetted.SiteUrl);
    }
}
