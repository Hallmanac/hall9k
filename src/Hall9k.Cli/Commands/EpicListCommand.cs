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

        [CommandOption("--solution <SOLUTION>")]
        [Description(
            "Only the epics of this solution's member projects known on this node: its name, or a prefix of "
            + "its key (h9k solution list shows them). The ladder is solution, project, epic, task")]
        public string? Solution { get; init; }

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

        IReadOnlySet<Guid>? solutionProjectIds = settings.Solution.IsNotBlank()
            ? await SolutionScope.ProjectIdsAsync(session, settings.Solution, cancellationToken)
            : null;

        IReadOnlyList<EpicDetails> epics = await session.Query<EpicDetails>().ToListAsync(cancellationToken);
        if (epics.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No epics yet. Name one:[/] h9k epic add --project <name> --title \"<name>\"");
            return ExitCodes.Ok;
        }

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);

        // A solution epic is one row, so the filters and the state count read whole epics: --project keeps a
        // solution epic when the project holds a copy or is a member of its solution, which includes the
        // epics created before it was linked and whose copy has not been made on this node.
        List<EpicGroup> scoped = [.. EpicGroup.Of(epics, context.Projects)
            .Where(group => project is null || InProject(group, project))
            .Where(group => solutionProjectIds is null || group.Copies.Any(copy => solutionProjectIds.Contains(copy.ProjectId)))];
        List<EpicGroup> matched = [.. scoped
            .Where(group => state is null || group.State == state)
            .OrderByDescending(group => group.AddedAt)];
        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No epics match {Filters(project, state, settings.Solution)}. Drop a filter, or see everything:[/] "
                + "h9k epic list --state all");
            return ExitCodes.Ok;
        }

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(
            session, DateTimeOffset.UtcNow, cancellationToken);
        EpicBoard[] boards = [.. matched.Select(group => EpicBoard.Compose(group, context, rows))];

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

        // An epic can hold another owner's task. Those are counted in no group above, so the row
        // would otherwise stop adding up to its tasks without saying why; the column appears only
        // where some listed epic has one, which leaves a one-owner install's table as it always was.
        bool anyTeammates = boards.Any(board => board.Rollup.Teammates > 0);
        if (anyTeammates)
        {
            table.AddColumn(new TableColumn(TaskRollup.TeammatesColumn).RightAligned());
        }

        foreach (EpicBoard board in boards)
        {
            EpicGroup group = board.Group;
            TaskRollup rollup = board.Rollup;
            table.AddRow([
                $"[dim]{Reference(group)}[/]",
                StateMarkup(group.State),
                .. project is null ? (string[])[ProjectCell(board)] : [],
                TitleCell(board),
                .. rollup.Cells,
                .. anyTeammates ? (string[])[rollup.TeammatesCell] : [],
            ]);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(Footer(matched, scoped, project, state, settings.Solution));
        return ExitCodes.Ok;
    }

    /// <summary>What a person types for the epic: the copy's short id, or the short form of a solution epic's shared key.</summary>
    internal static string Reference(EpicGroup group) =>
        EpicIdResolver.ReferenceOf(group.First.Id, group.SolutionEpicKey);

    private static bool InProject(EpicGroup group, ProjectDetails project) =>
        group.Copies.Any(copy => copy.ProjectId == project.Id)
        || (group.SolutionKey is { } key && project.Solution is { Holds: true } membership
            && membership.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string ProjectCell(EpicBoard board) =>
        board.Projects.Count == 0
            ? "?"
            : string.Join(", ", board.Projects.Select(project => project.ProjectName.EscapeMarkup()));

    /// <summary>The title, with the solution named beside a solution epic so its single row says what it spans.</summary>
    private static string TitleCell(EpicBoard board) =>
        board.Group.IsSolutionEpic
            ? $"{board.Group.Title.EscapeMarkup()} [dim](solution {(board.SolutionName ?? "not recorded here").EscapeMarkup()})[/]"
            : board.Group.Title.EscapeMarkup();

    /// <summary>
    /// Names the state scope actually shown, and what the default open-only filter left out — the
    /// same discipline <c>TaskListCommand.Footer</c> and <c>IdeaListCommand.Footer</c> already hold,
    /// so a bare count here never reads as a claim about every epic on the install.
    /// </summary>
    internal static string Footer(
        IReadOnlyList<EpicGroup> matched, IReadOnlyList<EpicGroup> scoped, ProjectDetails? project,
        EpicState? state, string? solution = null)
    {
        string scope = $"{matched.Count} epic{(matched.Count == 1 ? string.Empty : "s")}{Scope(project, state, solution)}";
        return $"[dim]{scope}{Elsewhere(scoped, project, state, solution)} — see one's tasks:[/] "
            + $"h9k epic show {Reference(matched[0])} [dim]· filter tasks by epic:[/] "
            + $"h9k task list --epic {Reference(matched[0])}";
    }

    private static string Scope(ProjectDetails? project, EpicState? state, string? solution)
    {
        string scope = project is null ? string.Empty : $" in {project.Name.EscapeMarkup()}";
        scope += solution.IsNotBlank() ? $" in solution {solution.EscapeMarkup()}" : string.Empty;
        return state is null ? $"{scope}, every state" : $"{scope} {state.Value.ToLowerInvariant()}";
    }

    /// <summary>What the state filter is hiding, counted and named by the state it is actually in.</summary>
    private static string Elsewhere(
        IReadOnlyList<EpicGroup> scoped, ProjectDetails? project, EpicState? state, string? solution)
    {
        if (state is null)
        {
            return string.Empty;
        }

        string[] named = [.. scoped
            .Where(group => group.State != state)
            .GroupBy(group => group.State)
            .OrderBy(group => group.Key == EpicState.Open ? 0 : 1)
            .Select(group => $"{group.Count()} {group.Key.Value.ToLowerInvariant()}")];

        return named.Length == 0
            ? string.Empty
            : $" · {string.Join(", ", named)} — see them with: h9k epic list --state all{Repeat(project, solution)}";
    }

    /// <summary>The active filters, echoed so --state all keeps the view the reader is looking at.</summary>
    private static string Repeat(ProjectDetails? project, string? solution) =>
        (project is null ? string.Empty : $" --project {project.Name.EscapeMarkup()}")
        + (solution.IsNotBlank() ? $" --solution {solution.EscapeMarkup()}" : string.Empty);

    /// <summary>The narrowing filters, for messages that suggest dropping one to widen the result.</summary>
    private static string Filters(ProjectDetails? project, EpicState? state, string? solution)
    {
        List<string> filters = [];
        if (project is not null)
        {
            filters.Add($"--project {project.Name.EscapeMarkup()}");
        }

        if (solution.IsNotBlank())
        {
            filters.Add($"--solution {solution.EscapeMarkup()}");
        }

        if (state is not null)
        {
            filters.Add($"--state {state.Value.ToLowerInvariant()}");
        }

        return filters.Count == 0 ? "those filters" : string.Join(" ", filters);
    }

    internal static string StateMarkup(EpicState state) => state.Value switch
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
