using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class EpicShowCommand : Hall9kAsyncCommand<EpicShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description(
            "Epic id (full, or an unambiguous fragment). A solution epic is one epic in every member project: name it "
            + "by its solution epic key or any copy's id")]
        public string Id { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        EpicGroup group = await EpicIdResolver.ResolveGroupAsync(session, settings.Id, cancellationToken);
        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        IReadOnlyList<TaskStatusRow> allRows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        EpicBoard board = EpicBoard.Compose(group, context, allRows);

        AnsiConsole.Write(Header(board));
        WriteTasks(board, DateTimeOffset.UtcNow);
        return ExitCodes.Ok;
    }

    internal static Table Header(EpicBoard board)
    {
        EpicGroup group = board.Group;
        EpicDetails epic = group.First;
        Table header = new Table().Border(TableBorder.None).HideHeaders();
        header.AddColumns("k", "v");
        header.AddRow("[bold]Epic[/]", $"[bold]{group.Title.EscapeMarkup()}[/]");
        header.AddRow("State", EpicListCommand.StateMarkup(group.State));
        if (group.IsSolutionEpic)
        {
            header.AddRow("Solution", board.SolutionName?.EscapeMarkup() ?? "[dim]not recorded on this node[/]");
            header.AddRow("Key", $"[dim]{group.SolutionEpicKey}[/]");
            header.AddRow("Projects", string.Join(", ", board.Projects.Select(project =>
                $"{project.ProjectName.EscapeMarkup()} [dim]({TaskListCommand.ShortId(project.Copy.Id)}"
                + $"{(project.Copy.State == EpicState.Closed ? ", closed" : string.Empty)})[/]")));
        }
        else
        {
            header.AddRow("Id", $"[dim]{epic.Id}[/]");
            header.AddRow("Project", board.Projects.FirstOrDefault()?.ProjectName.EscapeMarkup() ?? $"[dim]id {epic.ProjectId}[/]");
        }

        foreach (string reference in group.Copies
                     .Select(copy => copy.JiraReference)
                     .Where(reference => reference.IsNotBlank())
                     .Distinct()
                     .OfType<string>())
        {
            header.AddRow("Jira", JiraMarkup(reference));
        }

        header.AddRow("Added", $"[dim]{group.AddedAt.ToLocalTime():g}[/]");
        if (group.Copies.Where(copy => copy.State == EpicState.Closed).OrderBy(copy => copy.ClosedAt).FirstOrDefault() is { } closed)
        {
            header.AddRow("Closed", $"[dim]{closed.ClosedAt?.ToLocalTime().ToString("g") ?? "an unrecorded time"}"
                + $" — {closed.CloseReason?.EscapeMarkup() ?? "no reason recorded"}[/]");
        }

        return header;
    }

    private static void WriteTasks(EpicBoard board, DateTimeOffset now)
    {
        EpicGroup group = board.Group;
        string reference = EpicIdResolver.ReferenceOf(group.First.Id, group.SolutionEpicKey);
        string projectHint = group.IsSolutionEpic
            ? "<member project>"
            : (board.Projects.FirstOrDefault()?.ProjectName ?? group.First.ProjectId.ToString()).EscapeMarkup();
        List<TaskStatusRow> members = [.. board.Projects.SelectMany(project => project.Rows)];

        if (members.Count == 0)
        {
            AnsiConsole.MarkupLine(group.State == EpicState.Open
                ? $"\n[bold]Tasks[/] [dim]none yet. Join one:[/] h9k task add --project "
                  + $"{projectHint} --objective \"…\" --epic {reference} "
                  + $"[dim]for a new task, or for an existing draft:[/] h9k task revise <id> --epic {reference} "
                  + $"[dim](revision is Draft-only — a Published task returns with[/] h9k task draft <id> "
                  + $"[dim]alone; an assigned task (Queued or Blocked) needs[/] h9k task dequeue <id> && h9k task draft <id> "
                  + $"[dim]first)[/]"
                : "\n[bold]Tasks[/] [dim]none — and it's closed, so nothing can join it now.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"\n[bold]Tasks[/] {board.Rollup.Summary()}");
        if (group.IsSolutionEpic)
        {
            foreach (EpicBoardProject project in board.Projects)
            {
                AnsiConsole.MarkupLine($"\n[bold]{project.ProjectName.EscapeMarkup()}[/] {project.Rollup.Summary()}");
                if (project.Rows.Count > 0)
                {
                    AnsiConsole.Write(ProjectShowCommand.TaskTable(project.Rows, AnsiConsole.Profile.Width, now));
                }
            }
        }
        else
        {
            AnsiConsole.Write(ProjectShowCommand.TaskTable(
                [.. members.OrderByDescending(row => row.AddedAt)], AnsiConsole.Profile.Width, now));
        }

        if (group.State == EpicState.Open)
        {
            AnsiConsole.MarkupLine(
                $"\n[dim]Nothing closes an epic automatically — not even its last task finishing. "
                + $"Close it deliberately when it's done:[/] h9k epic close {reference} --reason \"<why>\"");
        }
    }

    /// <summary>
    /// The identity-only pointer as something a human can click when it is a URL, or plain text
    /// when it is a bare key with no site to build a link from (no read against Jira ever
    /// happens to fill that gap — Decisions Log #100).
    /// </summary>
    internal static string JiraMarkup(string reference) =>
        Uri.TryCreate(reference, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? $"[link={reference.EscapeMarkup()}]{reference.EscapeMarkup()}[/]"
            : reference.EscapeMarkup();
}
