using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.Credentials;
using Hall9k.Connectors.WorkItems;
using Hall9k.Domain.Features.Connection;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Hall9k.Domain.Infrastructure.Ids;
using Hall9k.Domain.Shared.Exceptions;
using Hall9k.Domain.Shared.ValueObjects;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Register the Jira Cloud account Hall9k reads cards through (PLAN.md §10, backlog 18).
/// <para>
/// The credential never reaches an event payload, which is the whole discipline: the stream
/// records a <see cref="CredentialReference"/> — the variable, the keychain entry, or the file
/// name — and the token itself lives wherever that points. Registering twice replaces the
/// existing connection rather than adding a second, so rotating a token is this command again.
/// </para>
/// </summary>
public sealed class ConnectionAddJiraCommand : Hall9kAsyncCommand<ConnectionAddJiraCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--site <URL>")]
        [Description(
            "The Jira Cloud site, for example https://your-org.atlassian.net — the address in the browser "
            + "when a board is open. Must be https: every request carries the API token in an "
            + "Authorization header, so an unencrypted site would put that token on the wire")]
        public string? Site { get; init; }

        [CommandOption("--email <ADDRESS>")]
        [Description(
            "The Atlassian account the token belongs to. Jira Cloud authenticates as email plus API "
            + "token, so the pair has to match — a token is not usable with a different account")]
        public string? Email { get; init; }

        [CommandOption("--token <TOKEN>")]
        [Description(
            "The API token itself, which Hall9k then stores under ~/.hall9k/credentials readable by you "
            + "alone and records only by file name. Convenient and the least private of the three: it "
            + "lands in your shell history. Omit every token option and you are prompted for it instead, "
            + "which does not. Create tokens at https://id.atlassian.com/manage-profile/security/api-tokens "
            + "— an account password is not one and Jira Cloud rejects it")]
        public string? Token { get; init; }

        [CommandOption("--token-env <VARIABLE>")]
        [Description(
            "Name an environment variable that holds the token; Hall9k records the variable name and "
            + "never copies the value. The most private option and the one with a catch: h9kd inherits "
            + "the environment it was started in, so a variable exported after 'h9k daemon start' is "
            + "invisible to the daemon until it restarts")]
        public string? TokenEnvironmentVariable { get; init; }

        [CommandOption("--keychain <SERVICE>")]
        [Description(
            "Name a macOS keychain item you already created (security add-generic-password -s <SERVICE> "
            + "-a <account> -w); Hall9k records the service name and reads the secret through the "
            + "'security' tool at each call. macOS only — the reference names a store no other platform has")]
        public string? Keychain { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        Uri site = JiraAccount.ParseSite(settings.Site);
        string email = (settings.Email ?? string.Empty).Trim();
        if (email.IsBlank())
        {
            throw new DomainValidationException(
                "A Jira connection needs the account the token belongs to: --email you@example.com. "
                + "Jira Cloud authenticates as email plus API token, so the token alone names nobody.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ConnectionDetails? existing = await WorkItemConnections.FindJiraConnectionAsync(session, cancellationToken);

        ChosenCredential chosen = await ChooseCredentialAsync(settings, site, email, cancellationToken);

        // Prove the account works before recording it, and — for a token Hall9k stores itself —
        // before writing it. A connection registered from an unchecked token is a record of an
        // intention rather than an observation, and the failure it hides surfaces later inside a
        // dispatched run, where the person who mistyped it is not watching (AGENTS.md, never
        // guess at unobserved facts).
        JiraWorkItemProvider provider = new(chosen.Account);
        string displayName = await provider.VerifyAccessAsync(cancellationToken);

        // Only now does anything change on disk. The stored file is named from the site and the
        // account, so this is the same file the existing connection reads through: writing it
        // before the check above would mean a rejected token had replaced a working one, from a
        // command that exited non-zero and appeared to have done nothing.
        CredentialReference credential = await chosen.RecordAsync(site, email, cancellationToken);

        if (existing is null)
        {
            Guid connectionId = DomainId.New();
            session.Events.StartStream<ConnectionAggregate>(connectionId, ConnectionDecider.Register(
                connectionId, context.OwnerId, WorkItemProvider.Jira, email, credential,
                DateTimeOffset.UtcNow, site));
        }
        else
        {
            ConnectionAggregate aggregate = (await session.Events
                .AggregateStreamAsync<ConnectionAggregate>(existing.Id, token: cancellationToken))!;
            session.Events.Append(existing.Id, ConnectionDecider.Reregister(
                aggregate, email, credential, DateTimeOffset.UtcNow, site));
        }

        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(existing is null
            ? $"[green]Jira connection registered[/] as {ExternalText.OneLineMarkup(displayName)} "
              + $"[dim]({email.EscapeMarkup()}) at {site.Host.EscapeMarkup()}[/]"
            : $"[green]Jira connection updated[/] to {ExternalText.OneLineMarkup(displayName)} "
              + $"[dim]({email.EscapeMarkup()}) at {site.Host.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"[dim]  credential: {credential.ToString().EscapeMarkup()} (the token is not on the event stream)[/]");
        if (credential.Kind == CredentialKind.File && OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine(
                "[yellow]  On Windows the token file carries no explicit permissions[/] [dim]— it is protected "
                + "by your user profile's own access control, the same as everything else under ~/.hall9k. "
                + "Use --token-env or a credential manager if that is not enough.[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]Next:[/] h9k project set <project> --jira <KEY> "
            + "[dim](bind the board, then h9k task add --from-jira <KEY>)[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// A credential this command has settled on but not necessarily written down: an account that
    /// can authenticate right now, so verification happens first, and the token still in hand when
    /// Hall9k is the one that will store it (null when it already lives somewhere Hall9k only
    /// points at). <see cref="RecordAsync"/> is the step that touches the disk.
    /// </summary>
    private sealed record ChosenCredential(JiraAccount Account, CredentialReference? Stored, string? TokenToStore)
    {
        public async Task<CredentialReference> RecordAsync(
            Uri site, string email, CancellationToken cancellationToken) => this switch
        {
            { Stored: { } reference } => reference,
            { TokenToStore: { } token } => await CredentialVault.StoreAsync(
                FileName(site, email), token, cancellationToken),
            _ => throw new DomainValidationException(
                "No credential was settled on for this connection, so there is nothing to record."),
        };
    }

    /// <summary>
    /// Where the token will be read from, decided once and refused rather than guessed at when
    /// more than one source is named. Each branch verifies the source can actually be read now:
    /// a reference to an unset variable or an absent keychain item is a connection that will fail
    /// on its first real use, and the moment to find that out is while the human is here.
    /// <para>
    /// Nothing here writes. The two reference kinds point at a secret somebody else put there, so
    /// reading them is harmless; the third is a token Hall9k would store itself, and that one is
    /// carried out of here unwritten so the account can be proven before the file is replaced.
    /// </para>
    /// </summary>
    private static async Task<ChosenCredential> ChooseCredentialAsync(
        Settings settings, Uri site, string email, CancellationToken cancellationToken)
    {
        string[] named =
        [
            .. new (string Option, string? Value)[]
            {
                ("--token", settings.Token),
                ("--token-env", settings.TokenEnvironmentVariable),
                ("--keychain", settings.Keychain),
            }.Where(option => option.Value.IsNotBlank()).Select(option => option.Option),
        ];

        if (named.Length > 1)
        {
            throw new DomainValidationException(
                $"{string.Join(" and ", named)} each name a different place to get the token from; pass one. "
                + "The connection records exactly one credential reference.");
        }

        if (settings.TokenEnvironmentVariable is { } variable && variable.IsNotBlank())
        {
            CredentialReference reference = CredentialReference.EnvironmentVariable(variable.Trim());
            await CredentialVault.Default.ResolveAsync(reference, $"read Jira at {site.Host}", cancellationToken);
            return new ChosenCredential(new JiraAccount(site, email, reference), reference, null);
        }

        if (settings.Keychain is { } service && service.IsNotBlank())
        {
            CredentialReference reference = CredentialReference.Keychain(service.Trim());
            await CredentialVault.Default.ResolveAsync(reference, $"read Jira at {site.Host}", cancellationToken);
            return new ChosenCredential(new JiraAccount(site, email, reference), reference, null);
        }

        string token = settings.Token.IsNotBlank() ? settings.Token.Trim() : Prompt(site);
        return new ChosenCredential(JiraAccount.WithTokenInHand(site, email, token), null, token);
    }

    /// <summary>
    /// The token asked for rather than typed on a command line, which is the difference between a
    /// secret and a secret in your shell history. Spectre's secret prompt is what makes it not
    /// echo; a script with no terminal is refused and told the two options that work unattended,
    /// because a prompt nobody can answer is a hang rather than an error.
    /// </summary>
    private static string Prompt(Uri site)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new DomainValidationException(
                $"No token was given for {site.Host} and there is no terminal here to ask on. Pass "
                + "--token-env <VARIABLE> (nothing is copied), --keychain <SERVICE> on macOS, or --token "
                + "<TOKEN> if a token in this script's history is acceptable.");
        }

        AnsiConsole.MarkupLine(
            $"[dim]Create an API token at https://id.atlassian.com/manage-profile/security/api-tokens "
            + $"— an account password is not one, and {site.Host.EscapeMarkup()} will reject it.[/]");
        return AnsiConsole.Prompt(new TextPrompt<string>("[bold]API token[/]:").Secret());
    }

    /// <summary>
    /// What the stored token is filed under: the site and the account, so two connections could
    /// never share a file, reduced to the characters a file name is safe to be made of. It is
    /// derived rather than random because re-registering the same account should overwrite the
    /// same file — a rotated token that left the old one on disk would be a secret nobody meant
    /// to keep.
    /// </summary>
    internal static string FileName(Uri site, string email) =>
        "jira-" + Slug($"{site.Host}-{email}");

    private static string Slug(string value)
    {
        char[] characters =
        [
            .. value.ToLowerInvariant()
                .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'),
        ];
        return string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
