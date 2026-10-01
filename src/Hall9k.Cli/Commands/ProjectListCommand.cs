using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class ProjectListCommand : Hall9kAsyncCommand<ProjectListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--everyone")]
        [Description(TeammateRows.EveryoneDescription)]
        public bool Everyone { get; init; }

        [CommandOption("--include-archived")]
        [Description("Show archived projects (h9k project remove) alongside live ones, marked archived with the date")]
        public bool IncludeArchived { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ProjectDetails> all = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
        int archivedHidden = settings.IncludeArchived ? 0 : all.Count(project => project.IsArchived);
        IReadOnlyList<ProjectDetails> projects = settings.IncludeArchived
            ? all
            : [.. all.Where(project => !project.IsArchived)];
        if (projects.Count == 0)
        {
            if (archivedHidden > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]No live projects registered — {archivedHidden} archived project"
                    + $"{(archivedHidden == 1 ? string.Empty : "s")} hidden. See "
                    + "them:[/] h9k project list --include-archived [dim]· reactivate one:[/] "
                    + "h9k project reactivate <project>");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[dim]No projects registered. Register one:[/] "
                    + "h9k project add --name <name> --repo <path> [dim][[--base-branch <branch>]][/]");
            }

            return ExitCodes.Ok;
        }

        // The counts are the viewer's own work: a teammate's task is composed into its own group, so
        // dropping the rows here keeps it out of every column, and the footer says how many it held
        // back. Only the projects the table lists are counted, so a hidden task in an archived
        // project is not reported as hidden from a table that does not show that project either.
        IReadOnlyList<TaskStatusRow> everyoneRows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        HashSet<Guid> shownProjectIds = [.. projects.Select(project => project.Id)];
        (IReadOnlyList<TaskStatusRow> rows, int hiddenTeammates) = TeammateRows.Apply(
            [.. everyoneRows.Where(row => shownProjectIds.Contains(row.ProjectId))], settings.Everyone);
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

        if (settings.Everyone)
        {
            table.AddColumn(new TableColumn(TaskRollup.TeammatesColumn).RightAligned());
        }

        foreach ((ProjectDetails project, TaskRollup rollup) in listed)
        {
            string name = project.IsArchived
                ? $"{project.Name.EscapeMarkup()} [yellow](archived {project.ArchivedAt?.ToLocalTime():yyyy-MM-dd})[/]"
                    + (project.PurgeAt is { } purgeAt
                        ? $" [red](purge {purgeAt.ToLocalTime():yyyy-MM-dd HH:mm} — "
                          + $"h9k project cancel-purge {project.Name.EscapeMarkup()})[/]"
                        : string.Empty)
                : project.Name.EscapeMarkup();
            table.AddRow([name, .. rollup.Cells, .. settings.Everyone ? (string[])[rollup.TeammatesCell] : []]);
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
            + $"browse its tasks:[/] h9k task list --project {first} --include-archived");
        if (hiddenTeammates > 0)
        {
            // The suggested command repeats --include-archived when it was given, so the rows it
            // brings back are counted over the same projects the note's number was.
            string command = settings.IncludeArchived ? "h9k project list --include-archived" : "h9k project list";
            AnsiConsole.MarkupLine($"[dim]{TeammateRows.HiddenNote(hiddenTeammates, command)}[/]");
        }

        // rows is already narrowed to the projects the table shows: without --include-archived, an
        // archived project's own needs-you or stalled task must not trigger this footer — the table
        // just told the operator that project is hidden, and h9k status would show the identical row
        // with nothing to act on until it is reactivated.
        if (rows.Any(row => row.Group is AttentionBucket.NeedsYou or AttentionBucket.Stalled))
        {
            AnsiConsole.MarkupLine("[dim]Something is waiting on you — see it with:[/] h9k status");
        }

        if (archivedHidden > 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]{archivedHidden} archived project{(archivedHidden == 1 ? string.Empty : "s")} hidden — "
                + "see them:[/] h9k project list --include-archived");
        }

        return ExitCodes.Ok;
    }
}
