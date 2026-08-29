using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class EpicShowCommand : Hall9kAsyncCommand<EpicShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Epic id (full, or an unambiguous fragment)")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        Guid epicId = await EpicIdResolver.ResolveAsync(session, settings.Id, cancellationToken);
        EpicDetails epic = await session.LoadAsync<EpicDetails>(epicId, cancellationToken)
            ?? throw new DomainNotFoundException($"No epic {epicId}.");
        ProjectDetails? project = await session.LoadAsync<ProjectDetails>(epic.ProjectId, cancellationToken);

        Table header = new Table().Border(TableBorder.None).HideHeaders();
        header.AddColumns("k", "v");
        header.AddRow("[bold]Epic[/]", $"[bold]{epic.Title.EscapeMarkup()}[/]");
        header.AddRow("State", epic.State == EpicState.Open ? "[green]Open[/]" : "[dim]Closed[/]");
        header.AddRow("Id", $"[dim]{epic.Id}[/]");
        header.AddRow("Project", project?.Name.EscapeMarkup() ?? $"[dim]id {epic.ProjectId}[/]");
        if (epic.JiraReference.IsNotBlank())
        {
            header.AddRow("Jira", JiraMarkup(epic.JiraReference));
        }

        header.AddRow("Added", $"[dim]{epic.AddedAt.ToLocalTime():g}[/]");
        if (epic.State == EpicState.Closed)
        {
            header.AddRow("Closed", $"[dim]{epic.ClosedAt?.ToLocalTime().ToString("g") ?? "an unrecorded time"}"
                + $" — {epic.CloseReason?.EscapeMarkup() ?? "no reason recorded"}[/]");
        }

        AnsiConsole.Write(header);

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        List<TaskStatusRow> members = [.. rows.Where(row => row.EpicId == epic.Id)];

        if (members.Count == 0)
        {
            AnsiConsole.MarkupLine(epic.State == EpicState.Open
                ? $"\n[bold]Tasks[/] [dim]none yet. Join one:[/] h9k task add --project "
                  + $"{(project?.Name ?? epic.ProjectId.ToString()).EscapeMarkup()} --objective \"…\" --epic {TaskListCommand.ShortId(epic.Id)} "
                  + $"[dim]for a new task, or for an existing draft:[/] h9k task revise <id> --epic {TaskListCommand.ShortId(epic.Id)} "
                  + $"[dim](revision is Draft-only — a Published task returns with[/] h9k task draft <id> "
                  + $"[dim]alone; an assigned task (Queued or Blocked) needs[/] h9k task unassign <id> && h9k task draft <id> "
                  + $"[dim]first)[/]"
                : "\n[bold]Tasks[/] [dim]none — and it's closed, so nothing can join it now.[/]");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine($"\n[bold]Tasks[/] {TaskRollup.From(members).Summary()}");
        AnsiConsole.Write(ProjectShowCommand.TaskTable(
            [.. members.OrderByDescending(row => row.AddedAt)], AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));

        if (epic.State == EpicState.Open)
        {
            AnsiConsole.MarkupLine(
                $"\n[dim]Nothing closes an epic automatically — not even its last task finishing. "
                + $"Close it deliberately when it's done:[/] h9k epic close {TaskListCommand.ShortId(epic.Id)} --reason \"<why>\"");
        }

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The identity-only pointer as something a human can click when it is a URL, or plain text
    /// when it is a bare key with no site to build a link from (no read against Jira ever
    /// happens to fill that gap — Decisions Log #99).
    /// </summary>
    internal static string JiraMarkup(string reference) =>
        Uri.TryCreate(reference, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? $"[link={reference.EscapeMarkup()}]{reference.EscapeMarkup()}[/]"
            : reference.EscapeMarkup();
}
