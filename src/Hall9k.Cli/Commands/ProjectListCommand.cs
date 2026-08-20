using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class ProjectListCommand : Hall9kAsyncCommand<ProjectListCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ProjectDetails> projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No projects registered. Register one:[/] "
                + "h9k project add --name <name> --repo <path> [dim][[--base-branch <branch>]][/]");
            return ExitCodes.Ok;
        }

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        Dictionary<Guid, TaskRollup> rollups = rows
            .GroupBy(row => row.ProjectId)
            .ToDictionary(group => group.Key, TaskRollup.From);
        List<(ProjectDetails Project, TaskRollup Rollup)> listed = [.. projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => (project, rollups.GetValueOrDefault(project.Id) ?? TaskRollup.Empty))];

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Project");
        foreach (string column in TaskRollup.Columns)
        {
            table.AddColumn(new TableColumn(column).RightAligned());
        }

        foreach ((ProjectDetails project, TaskRollup rollup) in listed)
        {
            table.AddRow([project.Name.EscapeMarkup(), .. rollup.Cells]);
        }

        AnsiConsole.Write(table);

        // The help that teaches (AGENTS.md CLI standards): the rollup says where the work
        // is, and the footer says how to go look at it. The task count is summed from the
        // rows shown, so it never claims tasks the table does not account for.
        string first = listed[0].Project.Name.EscapeMarkup();
        int counted = listed.Sum(entry => entry.Rollup.Total);
        AnsiConsole.MarkupLine(
            $"[dim]{listed.Count} project{(listed.Count == 1 ? string.Empty : "s")}, "
            + $"{counted} task{(counted == 1 ? string.Empty : "s")} — the columns are single-assignment, "
            + "so a row sums to that project's tasks.[/]");
        AnsiConsole.MarkupLine(
            $"[dim]Settings and recent tasks:[/] h9k project show {first} [dim]· "
            + $"browse its tasks:[/] h9k task list --project {first}");

        if (rows.Any(row => row.Attention is AttentionBucket.NeedsYou or AttentionBucket.Stalled))
        {
            AnsiConsole.MarkupLine("[dim]Something is waiting on you — see it with:[/] h9k status");
        }

        return ExitCodes.Ok;
    }
}
