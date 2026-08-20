using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class TaskListCommand : Hall9kAsyncCommand<TaskListCommand.Settings>
{
    /// <summary>Rows shown when nothing else is asked for. Task volume grows; the browse surface should not.</summary>
    internal const int DefaultLimit = 20;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description("Only this project's tasks: its name, an unambiguous fragment of it, or its id (h9k project list shows them all)")]
        public string? Project { get; init; }

        [CommandOption("--state <STATE>")]
        [Description(
            "Only tasks in this state, matched against the Status column. An attention group selects all of "
            + "it (needs-you, stalled, active, in-review, queued, done, closed); an exact state selects "
            + "just that one (Running, Verifying, ChecksFailing, NeedsHuman, …). Hyphens and case are optional.")]
        public string? State { get; init; }

        [CommandOption("--limit <N>")]
        [Description("How many rows to show, newest first (default 20). The footer says how many were held back.")]
        public int? Limit { get; init; }

        [CommandOption("--all")]
        [Description("Show every matching task, unbounded — this wins over --limit")]
        public bool All { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Limit is { } requested && requested < 1)
        {
            throw new DomainValidationException($"--limit must be at least 1, got {requested}.");
        }

        if (settings.State.IsNotBlank())
        {
            TaskStateFilter.Validate(settings.State);
        }

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        // Resolve the project first: a misspelled name must say so even when the filtered
        // result would have been empty anyway.
        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : null;

        IReadOnlyList<TaskStatusRow> all = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        if (all.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No tasks. Add one:[/] h9k task add --project <name> --objective \"…\" --criteria \"…\"");
            return ExitCodes.Ok;
        }

        List<TaskStatusRow> matched = [.. all
            .Where(row => project is null || row.ProjectId == project.Id)
            .Where(row => settings.State.IsBlank() || TaskStateFilter.Matches(row, settings.State))
            .OrderByDescending(row => row.AddedAt)];
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No tasks match {Filters(settings, project)}. Drop a filter, or browse everything:[/] h9k task list --all");
            return ExitCodes.Ok;
        }

        int limit = settings.All ? matched.Count : settings.Limit ?? DefaultLimit;
        List<TaskStatusRow> shown = [.. matched.Take(limit)];

        AnsiConsole.Write(Rows(shown, scoped: project is not null, AnsiConsole.Profile.Width, DateTimeOffset.UtcNow));
        AnsiConsole.MarkupLine(Footer(matched.Count, shown.Count, settings, project));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The browse table. The fixed-width columns never wrap, so a narrow terminal spends its
    /// width on the objective instead of stacking every id and state down the page; the
    /// objective is truncated to exactly the width those columns leave it. Built apart from
    /// the query so the layout can be rendered and measured without a database.
    /// </summary>
    internal static Table Rows(
        IReadOnlyList<TaskStatusRow> rows, bool scoped, int consoleWidth, DateTimeOffset now)
    {
        string[] ages = [.. rows.Select(row => $"[dim]{row.AgeMarkup(now)}[/]")];
        int objective = TaskStatusRow.ObjectiveWidth(consoleWidth, bordered: true,
        [
            ["Id", .. rows.Select(row => row.IdMarkup)],
            ["Status", .. rows.Select(row => row.StatusMarkup)],
            ["Type", .. rows.Select(row => row.TypeMarkup)],
            .. scoped ? (string[][])[] : [["Project", .. rows.Select(row => row.ProjectMarkup)]],
            ["Added", .. ages],
            ["PR", .. rows.Select(row => row.PullRequestMarkup)],
        ]);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Id").NoWrap());
        table.AddColumn(new TableColumn("Status").NoWrap());
        table.AddColumn(new TableColumn("Type").NoWrap());
        if (!scoped)
        {
            table.AddColumn(new TableColumn("Project").NoWrap());
        }

        table.AddColumn("Objective");
        table.AddColumn(new TableColumn("Added").NoWrap());
        table.AddColumn(new TableColumn("PR").NoWrap());

        for (int index = 0; index < rows.Count; index++)
        {
            TaskStatusRow row = rows[index];
            table.AddRow([
                row.IdMarkup,
                row.StatusMarkup,
                row.TypeMarkup,
                .. scoped ? (string[])[] : [row.ProjectMarkup],
                row.ObjectiveMarkup(objective),
                ages[index],
                row.PullRequestMarkup,
            ]);
        }

        return table;
    }

    /// <summary>
    /// The bounded-list contract: never let a truncated view read as the whole truth. Says
    /// what was held back and the exact flag that shows it.
    /// </summary>
    internal static string Footer(int matched, int shown, Settings settings, ProjectDetails? project)
    {
        string scope = $"{shown} of {matched}{Scope(settings, project)}, newest first";
        int held = matched - shown;
        return held > 0
            ? $"[dim]{scope} · {held} held back — see them with:[/] h9k task list --all"
              + $"{Repeat(settings, project)} [dim]or[/] --limit {matched}"
            : $"[dim]{scope} · filter with:[/] h9k task list --project <name> --state <state>";
    }

    private static string Scope(Settings settings, ProjectDetails? project)
    {
        string scope = project is null ? string.Empty : $" in {project.Name.EscapeMarkup()}";
        return settings.State.IsNotBlank() ? $"{scope} matching --state {settings.State.EscapeMarkup()}" : scope;
    }

    /// <summary>The active filters, echoed so --all keeps the view the reader is looking at.</summary>
    private static string Repeat(Settings settings, ProjectDetails? project) =>
        (project is null ? string.Empty : $" --project {project.Name.EscapeMarkup()}")
        + (settings.State.IsBlank() ? string.Empty : $" --state {settings.State.EscapeMarkup()}");

    private static string Filters(Settings settings, ProjectDetails? project)
    {
        List<string> filters = [];
        if (project is not null)
        {
            filters.Add($"--project {project.Name.EscapeMarkup()}");
        }

        if (settings.State.IsNotBlank())
        {
            filters.Add($"--state {settings.State.EscapeMarkup()}");
        }

        return filters.Count > 0 ? string.Join(" ", filters) : "those filters";
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
