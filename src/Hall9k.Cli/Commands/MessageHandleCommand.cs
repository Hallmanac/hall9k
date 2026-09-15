using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Shared.Exceptions;
using JasperFx.Events;
using Marten;
using Marten.Events;
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
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();
        return await RunAsync(session, settings, cancellationToken);
    }

    /// <summary>
    /// The whole command body, with its session handed in rather than opened through
    /// <see cref="CliStore.Open()"/> — this codebase's CLI commands have no other test seam (the
    /// same shape <c>PullRequestReviewCommand.RunAsync</c> and
    /// <c>ReviewResolveCommand.ResolvePrReviewAsync</c> already take), and a round trip test that
    /// only re-implements this body inline never actually exercises it.
    /// </summary>
    internal static async Task<int> RunAsync(IDocumentSession session, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Id.IsBlank())
        {
            throw new DomainValidationException("An id is required — h9k messages prints one for every unread message.");
        }

        MessageDetails details = await MessageIdResolver.ResolveReceivedAsync(session, settings.Id, cancellationToken);
        if (details.HandledAt is not null)
        {
            AnsiConsole.MarkupLineInterpolated($"[dim]Already handled at {details.HandledAt:u}.[/]");
            return ExitCodes.Ok;
        }

        StreamState? fence = await session.Events.FetchStreamStateAsync(details.Id, cancellationToken)
            ?? throw new DomainNotFoundException($"No message stream for '{settings.Id}'.");
        MessageAggregate message = await session.Events.AggregateStreamAsync<MessageAggregate>(
                details.Id, version: fence.Version, token: cancellationToken)
            ?? throw new DomainNotFoundException($"No message stream for '{settings.Id}'.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Events.Append(details.Id, expectedVersion: fence.Version + 1, MessageDecider.Handle(message, now));
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (EventStreamUnexpectedMaxEventIdException)
        {
            throw new DomainConflictException(
                $"Message {settings.Id} changed while handling — check h9k messages; re-run this command " +
                "if it is still unhandled.");
        }

        AnsiConsole.MarkupLineInterpolated($"[blue]Handled[/] message from {details.FromOwnerFingerprint}.");
        return ExitCodes.Ok;
    }
}
