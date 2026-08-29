using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Infrastructure.Bootstrap;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Record the Jira epic this epic points at, identity only (Decisions Log #100):
/// no data is read from or written to Jira through this command, and none ever will be. It is
/// a link for a human to click, never a sync.
/// </summary>
public sealed class EpicLinkJiraCommand : Hall9kAsyncCommand<EpicLinkJiraCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<EPIC>")]
        [Description("Epic id (full, or an unambiguous fragment)")]
        public string Epic { get; init; } = string.Empty;

        [CommandArgument(1, "<KEY-OR-URL>")]
        [Description("The Jira epic's key (PROJ-123) or its URL — recorded exactly as typed, never verified")]
        public string Reference { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IDocumentSession session = store.LightweightSession();

        EpicAggregate epic = await EpicIdResolver.LoadAsync(session, settings.Epic, cancellationToken);
        if (EpicDecider.AlreadyLinkedTo(epic, settings.Reference))
        {
            AnsiConsole.MarkupLine(
                $"[green]Already linked[/] to {settings.Reference.EscapeMarkup()}. [dim]Nothing to do.[/]");
            return ExitCodes.Ok;
        }

        BootstrapContext context = await NodeBootstrap.EnsureAsync(session, cancellationToken);
        EpicLinkedToJira linked = EpicDecider.LinkJira(
            epic, settings.Reference, DateTimeOffset.UtcNow, context.OwnerId);
        session.Events.Append(epic.Id, linked);
        await session.SaveChangesAsync(cancellationToken);

        string shortId = TaskListCommand.ShortId(epic.Id);
        AnsiConsole.MarkupLine($"[green]Linked[/] epic {shortId} to {linked.Reference.EscapeMarkup()}");
        return ExitCodes.Ok;
    }
}
