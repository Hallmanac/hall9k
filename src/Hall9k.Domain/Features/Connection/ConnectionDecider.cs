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
        // A Claude Code config directory is the other kind with no identifier: the directory is the
        // external account id, so recording it again here would record it twice. The pair is exclusive
        // both ways, because a claude-code connection holds no secret any other kind could point at.
        bool isClaudeCode = provider == WorkItemProvider.ClaudeCode;
        if (isClaudeCode != (credentialReference.Kind == CredentialKind.ClaudeConfigDirectory))
        {
            throw new DomainValidationException(
                isClaudeCode
                    ? "A claude-code connection records its Claude Code config directory and no secret, so its "
                      + "credential reference is 'claude-config-dir'. Register it with: h9k connection add claude-code --config-dir ~/.claude-alt"
                    : $"A '{provider.Value}' connection cannot use a Claude Code config directory as its credential; "
                      + "'claude-config-dir' belongs to claude-code connections alone.");
        }

        if (isClaudeCode)
        {
            if (credentialReference.Identifier.IsNotBlank())
            {
                throw new DomainValidationException(
                    "A claude-code connection records its directory once, as its external account id, and its "
                    + "credential reference names nothing.");
            }

            if (!Path.IsPathFullyQualified(externalAccountId)
                || externalAccountId.EndsWith(Path.DirectorySeparatorChar)
                || externalAccountId.EndsWith(Path.AltDirectorySeparatorChar))
            {
                throw new DomainValidationException(
                    $"A claude-code connection records an absolute directory with no trailing separator, and "
                    + $"'{externalAccountId}' is not one (ClaudeConfigDirectory.Normalize is the rule).");
            }
        }

        if (credentialReference.Kind != CredentialKind.GhCli
            && credentialReference.Kind != CredentialKind.ClaudeConfigDirectory
            && credentialReference.Identifier.IsBlank())
        {
            throw new DomainValidationException(
                $"A '{credentialReference.Kind.Value}' credential reference names where the secret lives "
                + "(the variable, the keychain entry, or the file), and this one names nothing.");
        }

        // gh-cli points at whoever the machine's gh is logged in as, and that login is a GitHub
        // session holding no token any other provider can present. A Jira connection registered
        // against it is unusable the moment anything tries to read a card, so it is refused where
        // it would be written rather than at the first import — the same argument as the rule
        // above, applied to a pointer that names something real for the wrong provider. Origin:
        // the pull-request review of the Jira branch (2026-08-22).
        if (provider != WorkItemProvider.GitHub && credentialReference.Kind == CredentialKind.GhCli)
        {
            throw new DomainValidationException(
                $"A '{provider.Value}' connection cannot borrow the gh CLI's login: it is a GitHub "
                + "session, and it holds no token Hall9k can present anywhere else (PLAN.md §10). "
                + "Register it against a token instead, with an env, keychain, or file reference: "
                + "h9k connection add jira --help");
        }

        // Jira accounts live at a tenant of their own and nothing can be read without knowing
        // which; Linear's live in a workspace whose address (https://linear.app/<urlKey>) is the
        // one thing a reference cannot be turned into a link without. GitHub has exactly one home,
        // so the field stays null there rather than being filled in with the obvious answer. The
        // rule is stated per provider deliberately: a site required of every connection would put
        // "https://github.com" on record as an observation nobody made.
        if (HasSite(provider) && siteUrl is null)
        {
            throw new DomainValidationException(
                provider == WorkItemProvider.Linear
                    ? "A Linear connection requires the workspace address it authenticates against, "
                      + "for example https://linear.app/your-workspace."
                    : "A Jira connection requires the site it authenticates against, "
                      + "for example https://your-org.atlassian.net.");
        }

        // The rule runs both ways, because half of it would let the stream say what the other
        // half forbids: a GitHub connection carrying https://github.com records a tenant nobody
        // chose and makes a null SiteUrl mean "not filled in" instead of "there is one home".
        // Written as "every provider but the ones that have a site", so a provider that genuinely
        // has tenants of its own is refused here the first time somebody adds one, and the decision
        // to let it carry a site gets made in this method rather than discovered on the stream
        // afterwards.
        if (!HasSite(provider) && siteUrl is not null)
        {
            throw new DomainValidationException(
                $"A '{provider.Value}' connection has one home, so it records no site, and this one was "
                + $"given {siteUrl}. Register it without a site; the field is Jira's and Linear's, where "
                + "an account lives at a tenant or workspace of its own.");
        }

        return new ConnectionRegistered(
            id, ownerId, provider, externalAccountId, credentialReference, registeredAt, siteUrl);
    }

    /// <summary>Whether a provider's accounts live somewhere of their own that the connection must record.</summary>
    private static bool HasSite(WorkItemProvider provider) =>
        provider == WorkItemProvider.Jira || provider == WorkItemProvider.Linear;

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

    /// <summary>
    /// What GitHub just reported about this connection's own account, or null when nothing about
    /// it changed since the last observation — the same "harmless to observe twice, so only record
    /// a change" idiom <c>ProjectDecider</c>'s own GitHub access mirror uses (idea 202383dc, A2b).
    /// Refused on a non-GitHub connection: nothing else in this platform ever asks GitHub who a
    /// Jira account is.
    /// </summary>
    public static ConnectionGitHubIdentityObserved? ObserveGitHubIdentity(
        ConnectionAggregate connection, long gitHubAccountId, string login, DateTimeOffset observedAt)
    {
        if (connection.Provider != WorkItemProvider.GitHub)
        {
            throw new DomainValidationException(
                $"Connection {connection.Id} is a '{connection.Provider}' connection, and GitHub's own "
                + "identity read only ever applies to a GitHub one.");
        }

        if (login.IsBlank())
        {
            throw new DomainValidationException("A GitHub identity observation requires the login GitHub reported.");
        }

        // The numeric id is what makes this an identity rather than a label — a login can be
        // renamed by its own holder, but a different id names a genuinely different account.
        // Treating a changed id the same as a renamed login would let an owner's unrelated `gh
        // auth switch` (for other work, with the connection's own account still logged in)
        // silently rebind this install's one GitHub connection, and every project routed through
        // it, to whichever account gh's active session happens to be the next time anything
        // refreshes it. Account switching as a first-class feature is deliberately parked (idea
        // 202383dc, A2b's own reshape); until it exists, a genuine account change is refused
        // rather than silently accepted (independent pre-PR review, cycle 3, adversarial lens,
        // medium).
        if (connection.GitHubAccountId is { } confirmedId && confirmedId != gitHubAccountId)
        {
            throw new DomainValidationException(
                $"Connection {connection.Id}'s confirmed GitHub account is #{confirmedId} "
                + $"({connection.GitHubLogin}); gh just reported a different account, #{gitHubAccountId} "
                + $"({login}). Switching this connection to a different GitHub account is not supported "
                + $"yet — run 'gh auth switch' back to {connection.GitHubLogin}, then retry.");
        }

        return connection.GitHubAccountId == gitHubAccountId && connection.GitHubLogin == login
            ? null
            : new ConnectionGitHubIdentityObserved(connection.Id, gitHubAccountId, login, observedAt);
    }

    /// <summary>
    /// What <c>claude auth status</c> reported for a claude-code connection's directory, or null when it
    /// is the identity already recorded. Refused on any other provider, and refused without an email and
    /// an org id: that is the shape an override credential in the environment produces, and recording it
    /// would bind the connection to nobody.
    /// </summary>
    public static ConnectionClaudeCodeAccountObserved? ObserveClaudeCodeAccount(
        ConnectionAggregate connection, string email, string organizationId, string? organizationName,
        string? subscriptionType, DateTimeOffset observedAt)
    {
        if (connection.Provider != WorkItemProvider.ClaudeCode)
        {
            throw new DomainValidationException(
                $"Connection {connection.Id} is a '{connection.Provider}' connection, and a Claude Code account "
                + "observation only ever applies to a claude-code one.");
        }

        if (email.IsBlank() || organizationId.IsBlank())
        {
            throw new DomainValidationException(
                "A Claude Code account observation requires the email and org id claude auth status reported.");
        }

        return connection.ClaudeCodeEmail == email
            && connection.ClaudeCodeOrganizationId == organizationId
            && connection.ClaudeCodeOrganizationName == organizationName
            && connection.ClaudeCodeSubscriptionType == subscriptionType
                ? null
                : new ConnectionClaudeCodeAccountObserved(
                    connection.Id, email, organizationId, organizationName, subscriptionType, observedAt);
    }
}
