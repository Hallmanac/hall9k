using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Message;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Lists this node's own received messages — unread (received, not yet handled) by
/// default; <c>--all</c> includes ones already handled too (idea 202383dc, M1b).</summary>
public sealed class MessagesCommand : Hall9kAsyncCommand<MessagesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--all")]
        [Description("Include already-handled messages alongside unread ones. Unread only by default.")]
        public bool All { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IQueryable<MessageDetails> receivedQuery = session.Query<MessageDetails>().Where(message => message.ReceivedAt != null);
        IReadOnlyList<MessageDetails> shown = await (settings.All ? receivedQuery : receivedQuery.Where(message => message.HandledAt == null))
            .OrderBy(message => message.ReceivedAt)
            .ToListAsync(cancellationToken);

        if (shown.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.All
                ? "[dim]No messages yet.[/]"
                : "[dim]No unread messages.[/] h9k messages --all shows every handled one too.");
            return ExitCodes.Ok;
        }

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id");
        table.AddColumn("Sender");
        table.AddColumn("At");
        table.AddColumn("About");
        table.AddColumn("Body");
        table.AddColumn("");

        foreach (MessageDetails message in shown)
        {
            string shortId = TaskListCommand.ShortId(message.Id);
            table.AddRow(
                shortId,
                TaskListCommand.ShortId(message.FromNodeId),
                message.SentAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                message.About is { Length: > 0 } about ? about.EscapeMarkup() : "[dim]—[/]",
                TaskListCommand.Truncate(message.Body ?? string.Empty, 60).EscapeMarkup(),
                message.HandledAt is not null ? "[dim]handled[/]" : $"h9k message handle {shortId}");
        }

        AnsiConsole.Write(table);
        return ExitCodes.Ok;
    }
}
