using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Message;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Extensions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>Lists this node's own received messages — unread (received, not yet handled) by
/// default; <c>--all</c> includes ones already handled too (idea 202383dc, M1b); <c>--project</c>
/// narrows to one project's own copy (idea 202383dc, M2) — otherwise every eligible project's
/// received messages show together.</summary>
public sealed class MessagesCommand : Hall9kAsyncCommand<MessagesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--all")]
        [Description("Include already-handled messages alongside unread ones. Unread only by default.")]
        public bool All { get; init; }

        [CommandOption("--project <PROJECT>")]
        [Description(
            "Only this project's own received messages: its name, an unambiguous fragment of it, "
            + "or its id (h9k project list shows them all). Every project's messages show together "
            + "by default.")]
        public string? Project { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid? projectId = null;
        if (settings.Project.IsNotBlank())
        {
            ProjectDetails project = await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken);
            projectId = project.Id;
        }

        IQueryable<MessageDetails> receivedQuery = session.Query<MessageDetails>().Where(message => message.ReceivedAt != null);
        if (projectId is { } filterProjectId)
        {
            receivedQuery = receivedQuery.Where(message => message.ProjectId == filterProjectId);
        }

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
        table.AddColumn("Project");
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
                message.ProjectId == Guid.Empty ? "[dim]—[/]" : TaskListCommand.ShortId(message.ProjectId),
                message.SentAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                message.About is { Length: > 0 } about ? about.EscapeMarkup() : "[dim]—[/]",
                TaskListCommand.Truncate(message.Body ?? string.Empty, 60).EscapeMarkup(),
                message.HandledAt is not null ? "[dim]handled[/]" : $"h9k message handle {shortId}");
        }

        AnsiConsole.Write(table);
        return ExitCodes.Ok;
    }
}
