using System.ComponentModel;
using Hall9k.Cli.ClaudeCode;
using Hall9k.Cli.Infrastructure;
using Hall9k.Connectors.ClaudeCode;
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
/// Register a Claude Code config directory as a node-local claude-code connection, so a project bound to
/// it (<c>h9k project set --claude-code-account</c>) runs every claude process under that directory's login.
/// <para>
/// The connection records the directory and the account <c>claude auth status</c> reports for it, read with
/// <c>CLAUDE_CONFIG_DIR</c> set before anything is recorded. Hall9k never logs in, never reads the login's
/// Keychain item or credentials file, and never handles a token: the login is the human's to make, and this
/// command only confirms whose it is. Registering a directory that is already recorded re-observes the same
/// connection, which is how a deliberately changed login is accepted.
/// </para>
/// </summary>
public sealed class ConnectionAddClaudeCodeCommand : Hall9kAsyncCommand<ConnectionAddClaudeCodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--config-dir <DIR>")]
        [Description(
            "The Claude Code config directory to register, for example ~/.claude-alt. It is recorded as an "
            + "absolute path with ~ expanded and no trailing separator, and is never resolved through a symlink: "
            + "on macOS Claude Code keys the directory's login to that exact string, so every process Hall9k starts "
            + "under it is handed the same one. Relative paths and filesystem roots are refused. Log in first with "
            + "CLAUDE_CONFIG_DIR='<dir>' claude, then /login inside it")]
        public string? ConfigDirectory { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        string directory = ClaudeConfigDirectory.Normalize(
            settings.ConfigDirectory ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Read before anything is recorded, and before the database is opened: a directory that is missing,
        // logged out, or overridden by a credential in the environment is refused with its fix line.
        ClaudeAccountCheckResult result = await CliClaudeAccount.Checker.CheckNewDirectoryAsync(directory, cancellationToken);
        ClaudeAuthStatus status = AcceptedStatus(result);

        using DocumentStore store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);

        ConnectionDetails? existing = RecordedFor(await ClaudeCodeAccounts.ListAsync(session, cancellationToken), directory);

        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        Guid connectionId;
        string? previousEmail = null;
        if (existing is null)
        {
            connectionId = DomainId.New();
            session.Events.StartStream<ConnectionAggregate>(
                connectionId, [.. RegistrationEvents(connectionId, context.OwnerId, directory, status, observedAt)]);
        }
        else
        {
            connectionId = existing.Id;
            previousEmail = existing.ClaudeCodeEmail;
            ConnectionAggregate aggregate = await session.Events.AggregateStreamAsync<ConnectionAggregate>(
                    existing.Id, token: cancellationToken)
                ?? throw new DomainNotFoundException($"Connection {existing.Id} has no stream on this node.");
            IReadOnlyList<object> events = ReobservationEvents(aggregate, status, observedAt);
            if (events.Count > 0)
            {
                session.Events.Append(existing.Id, [.. events]);
            }
        }

        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLine(
            (existing is null ? "[green]Claude Code connection registered[/] " : "[green]Claude Code connection re-observed[/] ")
            + $"{DomainId.Short(connectionId)} [dim]for {directory.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            $"[dim]  account: {ExternalText.OneLineMarkup(status.Email ?? string.Empty)}, "
            + $"{ExternalText.OneLineMarkup(status.OrganizationName ?? status.OrganizationId ?? string.Empty)}"
            + (status.SubscriptionType is { } subscription ? $" ({ExternalText.OneLineMarkup(subscription)})" : string.Empty)
            + " (read from claude auth status)[/]");
        if (previousEmail is not null && !string.Equals(previousEmail, status.Email, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  This directory was recorded as {ExternalText.OneLineMarkup(previousEmail)}[/] [dim]and is now "
                + "recorded as the account above; every project bound to it runs under that account from now on.[/]");
        }

        AnsiConsole.MarkupLine("[dim]  Recorded on this node only: the directory and its login never leave this machine.[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Next:[/] h9k project set <project> --claude-code-account {DomainId.Short(connectionId)} "
            + "[dim](run that project's Claude Code sessions under this account)[/]");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The claude-code connection already recorded for <paramref name="directory"/>, which a registration re-observes
    /// rather than adding a second connection beside it, or null when the directory is new to this node.
    /// </summary>
    internal static ConnectionDetails? RecordedFor(IReadOnlyList<ConnectionDetails> recorded, string directory) =>
        recorded.FirstOrDefault(connection => connection.ExternalAccountId == directory);

    /// <summary>The events that start a new claude-code connection for a directory this node has not recorded.</summary>
    internal static IReadOnlyList<object> RegistrationEvents(
        Guid connectionId, Guid ownerId, string directory, ClaudeAuthStatus status, DateTimeOffset observedAt) =>
    [
        ConnectionDecider.Register(
            connectionId, ownerId, WorkItemProvider.ClaudeCode, directory,
            CredentialReference.ClaudeConfigDirectory, observedAt),
        new ConnectionClaudeCodeAccountObserved(
            connectionId, status.Email ?? string.Empty, status.OrganizationId ?? string.Empty,
            status.OrganizationName, status.SubscriptionType, observedAt),
    ];

    /// <summary>
    /// The events that re-observe an already recorded connection: the account its check read, when that differs
    /// from what was recorded, and the clear of a refusal the passing check just disproved, which lets the projects
    /// bound to it take new work without waiting for the daemon's own re-check. Nothing when neither applies.
    /// </summary>
    internal static IReadOnlyList<object> ReobservationEvents(
        ConnectionAggregate aggregate, ClaudeAuthStatus status, DateTimeOffset observedAt)
    {
        List<object> events = [];
        if (ConnectionDecider.ObserveClaudeCodeAccount(
                aggregate, status.Email ?? string.Empty, status.OrganizationId ?? string.Empty,
                status.OrganizationName, status.SubscriptionType, observedAt) is { } observed)
        {
            events.Add(observed);
        }

        if (aggregate.ClaudeCodeRefused)
        {
            events.Add(new ConnectionClaudeCodeAccountCleared(aggregate.Id, observedAt));
        }

        return events;
    }

    /// <summary>
    /// The status a passing registration check read, or the check's own refusal: a logged-out directory names
    /// the login line, and a status with no email or org id names the override credential that hid the account.
    /// </summary>
    internal static ClaudeAuthStatus AcceptedStatus(ClaudeAccountCheckResult result) =>
        result is { Passed: true, Status: { } status }
            ? status
            : throw new DomainValidationException($"Not registering this Claude Code connection. {result.Message}");
}
