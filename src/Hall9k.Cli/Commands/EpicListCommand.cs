using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Shared.Exceptions;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

public sealed class EpicListCommand : Hall9kAsyncCommand<EpicListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <PROJECT>")]
        [Description("Only this project's epics: its name, an unambiguous fragment of it, or its id")]
        public string? Project { get; init; }

        [CommandOption("--state <STATE>")]
        [Description("open | closed | all — defaults to open")]
        public string? State { get; init; }
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        EpicState? state = ParseState(settings.State);

        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        ProjectDetails? project = settings.Project.IsNotBlank()
            ? await ProjectResolver.ResolveAsync(session, settings.Project, cancellationToken)
            : null;

        IReadOnlyList<EpicDetails> epics = await session.Query<EpicDetails>().ToListAsync(cancellationToken);
        if (epics.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No epics yet. Name one:[/] h9k epic add --project <name> --title \"<name>\"");
            return ExitCodes.Ok;
        }

        List<EpicDetails> scoped = [.. epics.Where(epic => project is null || epic.ProjectId == project.Id)];
        List<EpicDetails> matched = [.. scoped
            .Where(epic => state is null || epic.State == state)
            .OrderByDescending(epic => epic.AddedAt)];
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No epics match those filters. Drop a filter, or see everything:[/] h9k epic list --state all");
            return ExitCodes.Ok;
        }

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        Dictionary<Guid, TaskRollup> rollups = rows
            .Where(row => row.EpicId is not null)
            .GroupBy(row => row.EpicId is { } epicId ? epicId : default)
            .ToDictionary(group => group.Key, TaskRollup.From);
        Dictionary<Guid, string> projects = (await session.Query<ProjectDetails>()
            .ToListAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id");
        table.AddColumn("State");
        if (project is null)
        {
            table.AddColumn("Project");
        }

        table.AddColumn("Title");
        foreach (string column in TaskRollup.Columns)
        {
            table.AddColumn(new TableColumn(column).RightAligned());
        }

        foreach (EpicDetails epic in matched)
        {
            TaskRollup rollup = rollups.GetValueOrDefault(epic.Id) ?? TaskRollup.Empty;
            table.AddRow([
                $"[dim]{TaskListCommand.ShortId(epic.Id)}[/]",
                StateMarkup(epic.State),
                .. project is null ? (string[])[projects.GetValueOrDefault(epic.ProjectId, "?").EscapeMarkup()] : [],
                epic.Title.EscapeMarkup(),
                .. rollup.Cells,
            ]);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(Footer(matched, scoped, project, state));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Names the state scope actually shown, and what the default open-only filter left out — the
    /// same discipline <c>TaskListCommand.Footer</c> and <c>IdeaListCommand.Footer</c> already hold,
    /// so a bare count here never reads as a claim about every epic on the install.
    /// </summary>
    internal static string Footer(
        IReadOnlyList<EpicDetails> matched, IReadOnlyList<EpicDetails> scoped, ProjectDetails? project,
        EpicState? state)
    {
        string scope = $"{matched.Count} epic{(matched.Count == 1 ? string.Empty : "s")}{Scope(project, state)}";
        return $"[dim]{scope}{Elsewhere(scoped, project, state)} — see one's tasks:[/] "
            + $"h9k epic show {TaskListCommand.ShortId(matched[0].Id)} [dim]· filter tasks by epic:[/] "
            + $"h9k task list --epic {TaskListCommand.ShortId(matched[0].Id)}";
    }

    private static string Scope(ProjectDetails? project, EpicState? state)
    {
        string scope = project is null ? string.Empty : $" in {project.Name.EscapeMarkup()}";
        return state is null ? $"{scope}, every state" : $"{scope} {state.Value.ToLowerInvariant()}";
    }

    /// <summary>What the state filter is hiding, counted and named by the state it is actually in.</summary>
    private static string Elsewhere(IReadOnlyList<EpicDetails> scoped, ProjectDetails? project, EpicState? state)
    {
        if (state is null)
        {
            return string.Empty;
        }

        string[] named = [.. scoped
            .Where(epic => epic.State != state)
            .GroupBy(epic => epic.State)
            .OrderBy(group => group.Key == EpicState.Open ? 0 : 1)
            .Select(group => $"{group.Count()} {group.Key.Value.ToLowerInvariant()}")];

        return named.Length == 0
            ? string.Empty
            : $" · {string.Join(", ", named)} — see them with: h9k epic list --state all{Repeat(project)}";
    }

    /// <summary>The active filters, echoed so --state all keeps the view the reader is looking at.</summary>
    private static string Repeat(ProjectDetails? project) =>
        project is null ? string.Empty : $" --project {project.Name.EscapeMarkup()}";

    private static string StateMarkup(EpicState state) => state.Value switch
    {
        "Open" => "[green]Open[/]",
        "Closed" => "[dim]Closed[/]",
        _ => "[dim]?[/]",
    };

    internal static EpicState? ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "open" => EpicState.Open,
        "closed" => EpicState.Closed,
        "all" => null,
        _ => throw new DomainValidationException($"Unknown epic state '{value}'. Use one of: open, closed, all."),
    };
}
