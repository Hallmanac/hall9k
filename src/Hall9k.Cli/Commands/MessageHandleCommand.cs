using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Marks a received message handled — an explicit human or CLI act (idea 202383dc, M1b),
/// never implied by storage or by <c>h9k messages</c> having merely printed it.</summary>
public sealed class MessageHandleCommand : Hall9kAsyncCommand<MessageHandleCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("The message's own id, or an unambiguous fragment of one, as h9k messages prints it.")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Id.IsBlank())
        {
            throw new DomainValidationException("An id is required — h9k messages prints one for every unread message.");
        }

        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        MessageDetails details = await MessageIdResolver.ResolveReceivedAsync(session, settings.Id, cancellationToken);
        if (details.HandledAt is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Already handled at {details.HandledAt:u}.[/]");
            return ExitCodes.Ok;
        }

        MessageAggregate message = await session.Events.AggregateStreamAsync<MessageAggregate>(details.Id, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No message stream for '{settings.Id}'.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Events.Append(details.Id, MessageDecider.Handle(message, now));
        await session.SaveChangesAsync(cancellationToken);

        AnsiConsole.MarkupLineInterpolated($"[blue]Handled[/] message from {details.FromOwnerFingerprint}.");
        return ExitCodes.Ok;
    }
}
