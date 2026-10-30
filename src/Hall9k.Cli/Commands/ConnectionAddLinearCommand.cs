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
/// Register the Linear workspace Hall9k reads issues through (PLAN.md §10), the sibling of
/// <see cref="ConnectionAddJiraCommand"/> and shaped like it.
/// <para>
/// The key never reaches an event payload: the stream records a <see cref="CredentialReference"/> and
/// the key lives wherever that points. The viewer's id goes on <see cref="ConnectionTrackerIdentityObserved"/>,
/// its name on the connection's external account id, and the workspace address
/// (<c>https://linear.app/&lt;urlKey&gt;</c>) on the connection's site, all read from Linear before
/// anything is recorded. An install holds one Linear connection, so registering again replaces it,
/// which is how a rotated key is applied.
/// </para>
/// </summary>
public sealed class ConnectionAddLinearCommand : Hall9kAsyncCommand<ConnectionAddLinearCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--token <KEY>")]
        [Description(
            "The Linear personal API key itself, which Hall9k then stores under ~/.hall9k/credentials "
            + "readable by you alone and records only by file name. Convenient and the least private of the "
            + "three: it lands in your shell history. Omit every key option and you are prompted for it "
            + "instead, which does not. Create keys in Linear under Settings, Security & access, Personal "
            + "API keys. A key can be limited by permission or by team in Linear, and Hall9k cannot see those "
            + "limits when it registers the key")]
        public string? Token { get; init; }

        [CommandOption("--token-env <VARIABLE>")]
        [Description(
            "Name an environment variable that holds the API key; Hall9k records the variable name and "
            + "never copies the value. The most private option and the one with a catch: h9kd inherits "
            + "the environment it was started in, so a variable exported after 'h9k daemon start' is "
            + "invisible to the daemon until it restarts")]
        public string? TokenEnvironmentVariable { get; init; }

        [CommandOption("--keychain <SERVICE>")]
        [Description(
            "Name a macOS keychain item you already created (security add-generic-password -s <SERVICE> "
            + "-a <account> -w); Hall9k records the service name and reads the secret through the "
            + "'security' tool at each call. macOS only: the reference names a store no other platform has")]
        public string? Keychain { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        ConnectionDetails? existing = await WorkItemConnections.FindLinearConnectionAsync(session, cancellationToken);

        ChosenKey chosen = await ChooseKeyAsync(settings, cancellationToken);

        // Prove Linear accepts the key before recording it, and, for a key Hall9k stores itself, before
        // writing it. The workspace is learned from the same read, which is why the account being
        // proven carries none yet. This proves the key is accepted and nothing about what it may do.
        LinearViewer viewer = await new LinearWorkItemProvider(chosen.Account).VerifyAccessAsync(cancellationToken);
        Uri workspace = LinearAccount.WorkspaceFor(viewer.WorkspaceKey);
        string accountName = viewer.Name.IsNotBlank() ? viewer.Name : viewer.Id;

        CredentialReference credential = chosen.Reference(workspace, viewer.Id);

        // Decided while the session and token are both still good: there is something to undo only
        // when Hall9k is the one storing the key and no connection already reads that same file.
        bool discardOnFailure = chosen.KeyToStore is not null
            && !await ConnectionAddJiraCommand.PointedAtAsync(session, credential, cancellationToken);

        try
        {
            await chosen.WriteAsync(workspace, viewer.Id, cancellationToken);

            DateTimeOffset registeredAt = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                Guid connectionId = DomainId.New();
                session.Events.StartStream<ConnectionAggregate>(
                    connectionId,
                    ConnectionDecider.Register(
                        connectionId, context.OwnerId, WorkItemProvider.Linear, accountName, credential,
                        registeredAt, workspace),
                    new ConnectionTrackerIdentityObserved(connectionId, viewer.Id, registeredAt));
            }
            else
            {
                ConnectionAggregate aggregate = (await session.Events
                    .AggregateStreamAsync<ConnectionAggregate>(existing.Id, token: cancellationToken))!;
                session.Events.Append(
                    existing.Id,
                    ConnectionDecider.Reregister(aggregate, accountName, credential, registeredAt, workspace),
                    // Re-recorded on every re-registration, never assumed to carry over: the new key may
                    // belong to a different user, and a stale id would gate against whoever held the old one.
                    new ConnectionTrackerIdentityObserved(existing.Id, viewer.Id, registeredAt));
            }

            await session.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (discardOnFailure)
            {
                await ConnectionAddJiraCommand.UnwindStoredTokenAsync(credential);
            }

            throw;
        }

        CredentialReference? superseded = await ConnectionAddJiraCommand.SupersededCredentialAsync(
            session, existing, credential, cancellationToken);

        AnsiConsole.MarkupLine(existing is null
            ? $"[green]Linear connection registered[/] as {ExternalText.OneLineMarkup(accountName)} "
              + $"[dim]at {workspace.ToString().EscapeMarkup()}[/]"
            : $"[green]Linear connection updated[/] to {ExternalText.OneLineMarkup(accountName)} "
              + $"[dim]at {workspace.ToString().EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            $"[dim]  Linear user id: {ExternalText.OneLineMarkup(viewer.Id)} (read from Linear's viewer)[/]");
        AnsiConsole.MarkupLine(
            $"[dim]  credential: {credential.ToString().EscapeMarkup()} (the key is not on the event stream)[/]");
        AnsiConsole.MarkupLine(
            "[dim]  Linear accepted the key. That is all this checked: a key limited by permission or by team "
            + "can still be refused on a later read or write.[/]");
        if (superseded is not null)
        {
            ConnectionAddJiraCommand.DiscardAndReport(superseded);
        }

        if (credential.Kind == CredentialKind.File && OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine(
                "[yellow]  On Windows the key file carries no explicit permissions[/] [dim]: it is protected "
                + "by your user profile's own access control, the same as everything else under ~/.hall9k. "
                + "Use --token-env or a credential manager if that is not enough.[/]");
        }

        AnsiConsole.MarkupLine(
            "[dim]Next:[/] h9k task add --project <name> --from-linear <IDENTIFIER> "
            + "[dim](adopt an open issue), or h9k task link-linear <task> <IDENTIFIER>[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// A key this command has settled on but not necessarily written down: an account that can
    /// authenticate right now, so verification happens first, and the key still in hand when Hall9k is
    /// the one that will store it (null when it already lives somewhere Hall9k only points at).
    /// </summary>
    private sealed record ChosenKey(LinearAccount Account, CredentialReference? Stored, string? KeyToStore)
    {
        /// <summary>Where the connection will say the key lives, knowable before anything is written.</summary>
        public CredentialReference Reference(Uri workspace, string viewerId) => this switch
        {
            { Stored: { } reference } => reference,
            { KeyToStore: not null } => CredentialReference.File(FileName(workspace, viewerId)),
            _ => throw new DomainValidationException(
                "No credential was settled on for this connection, so there is nothing to record."),
        };

        /// <summary>Put the key where <see cref="Reference"/> says it is; the only write this command makes.</summary>
        public async ValueTask WriteAsync(Uri workspace, string viewerId, CancellationToken cancellationToken)
        {
            if (KeyToStore is { } key)
            {
                await CredentialVault.StoreAsync(FileName(workspace, viewerId), key, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Where the key will be read from, decided once and refused rather than guessed at when more than
    /// one source is named. Each reference kind is resolved now: a reference to an unset variable or an
    /// absent keychain item is a connection that fails on its first real use, and the moment to find
    /// that out is while the human is here.
    /// </summary>
    private static async Task<ChosenKey> ChooseKeyAsync(Settings settings, CancellationToken cancellationToken)
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
                $"{string.Join(" and ", named)} each name a different place to get the key from; pass one. "
                + "The connection records exactly one credential reference.");
        }

        if (settings.TokenEnvironmentVariable is { } variable && variable.IsNotBlank())
        {
            CredentialReference reference = CredentialReference.EnvironmentVariable(variable.Trim());
            await CredentialVault.Default.ResolveAsync(reference, "read Linear", cancellationToken);
            return new ChosenKey(LinearAccount.ForReference(reference), reference, null);
        }

        if (settings.Keychain is { } service && service.IsNotBlank())
        {
            CredentialReference reference = CredentialReference.Keychain(service.Trim());
            await CredentialVault.Default.ResolveAsync(reference, "read Linear", cancellationToken);
            return new ChosenKey(LinearAccount.ForReference(reference), reference, null);
        }

        string key = (settings.Token.IsNotBlank() ? settings.Token : Prompt()).Trim();
        return new ChosenKey(LinearAccount.WithKeyInHand(key), null, key);
    }

    /// <summary>
    /// The key asked for rather than typed on a command line, which is the difference between a secret
    /// and a secret in your shell history. A script with no terminal is refused and told the options
    /// that work unattended, because a prompt nobody can answer is a hang rather than an error.
    /// </summary>
    private static string Prompt()
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            throw new DomainValidationException(
                "No API key was given for Linear and there is no terminal here to ask on. Pass "
                + "--token-env <VARIABLE> (nothing is copied), --keychain <SERVICE> on macOS, or --token "
                + "<KEY> if a key in this script's history is acceptable.");
        }

        AnsiConsole.MarkupLine(
            "[dim]Create a personal API key in Linear under Settings, Security & access, Personal API keys.[/]");
        return AnsiConsole.Prompt(new TextPrompt<string>("[bold]API key[/]:").Secret());
    }

    /// <summary>
    /// What the stored key is filed under: the workspace and the viewer, so two connections could never
    /// share a file, reduced to the characters a file name is safe to be made of. Derived rather than
    /// random because re-registering the same user should overwrite the same file.
    /// </summary>
    internal static string FileName(Uri workspace, string viewerId) =>
        "linear-" + ConnectionAddJiraCommand.Slug($"{workspace.AbsolutePath.Trim('/')}-{viewerId}");
}
