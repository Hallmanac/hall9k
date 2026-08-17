using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Features.Tasks.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskListCommand : Hall9kAsyncCommand<TaskListCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<TaskListItem> tasks = await session.Query<TaskListItem>()
            .OrderBy(t => t.AddedAt)
            .ToListAsync(cancellationToken);

        if (tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No tasks. Add one: h9k task add --project <name> --objective \"...\" --criteria \"...\"[/]");
            return ExitCodes.Ok;
        }

        Dictionary<Guid, string> projectNames = (await session.Query<ProjectDetails>()
            .ToListAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Id", "State", "Type", "Project", "Objective");
        foreach (TaskListItem task in tasks)
        {
            table.AddRow(
                $"[dim]{ShortId(task.Id)}[/]",
                StateMarkup(task.State),
                task.Type.Value.EscapeMarkup(),
                (projectNames.GetValueOrDefault(task.ProjectId) ?? "?").EscapeMarkup(),
                Truncate(task.Objective, 60).EscapeMarkup());
        }

        AnsiConsole.Write(table);
        return ExitCodes.Ok;
    }

    internal static string StateMarkup(TaskState state) => state.Value switch
    {
        "Queued" => "[blue]Queued[/]",
        "Claimed" => "[yellow]Claimed[/]",
        "NeedsHuman" => "[red bold]NeedsHuman[/]",
        "Done" => "[green]Done[/]",
        "Failed" => "[red]Failed[/]",
        "Abandoned" => "[dim]Abandoned[/]",
        _ => state.Value.EscapeMarkup(),
    };

    // UUIDv7 front-loads the timestamp, so same-batch ids share their first chars;
    // the random tail is what tells them apart.
    internal static string ShortId(Guid id) => id.ToString("N")[^8..];

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
