using Hall9k.Cli.DaemonControl;
using Hall9k.Cli.Infrastructure;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class StatusCommand : Hall9kAsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        // A quiet queue must never be a mystery (Decisions Log #31): say up front when
        // nothing is dispatching and what to do about it.
        if (DaemonProcess.Probe() is null)
        {
            AnsiConsole.MarkupLine(
                "[red]daemon not running[/] — tasks queue but do not dispatch; start it with [bold]h9k daemon start[/]");
        }

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(session, now, cancellationToken);
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing tracked yet. Queue work with h9k task add.[/]");
            return ExitCodes.Ok;
        }

        AnsiConsole.MarkupLine($"[bold]h9k status[/] · {TaskRollup.From(rows).Summary()}");

        List<TaskStatusRow> ordered = [.. rows.OrderBy(row => row.Priority).ThenByDescending(row => row.AddedAt)];
        int objective = TaskStatusRow.ObjectiveWidth(AnsiConsole.Profile.Width, bordered: true,
        [
            ["Id", .. ordered.Select(row => row.IdMarkup)],
            ["Status", .. ordered.Select(row => row.StatusMarkup)],
            ["Project", .. ordered.Select(row => row.ProjectMarkup)],
            ["Activity", .. ordered.Select(row => row.Activity)],
            ["PR", .. ordered.Select(row => row.PullRequestMarkup)],
        ]);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Id", "Status", "Project", "Objective", "Activity", "PR");
        foreach (TaskStatusRow row in ordered)
        {
            table.AddRow(
                row.IdMarkup,
                row.StatusMarkup,
                row.ProjectMarkup,
                row.ObjectiveMarkup(objective),
                row.Activity,
                row.PullRequestMarkup);
        }

        AnsiConsole.Write(table);
        return ExitCodes.Ok;
    }
}
