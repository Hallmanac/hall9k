using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Features.Project.Projections;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// One solution in one pane: its state and members, and its board across the member projects it can
/// read here, grouped by project. The ladder is solution, project, epic, task, so each project's
/// tasks are the same rows <c>h9k project show</c> composes, under the same rollup.
/// </summary>
public sealed class SolutionShowCommand : Hall9kAsyncCommand<SolutionShowCommand.Settings>
{
    /// <summary>How many of a project's tasks the board lists before pointing at h9k task list.</summary>
    private const int TasksPerProject = 10;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOLUTION>")]
        [Description("The solution: its name, or a prefix of its key (h9k solution list shows them)")]
        public string Solution { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        SolutionContext context = await SolutionContext.LoadAsync(session, cancellationToken);
        SolutionView view = SolutionRegistry.Match(context.Solutions, settings.Solution);

        IReadOnlyList<TaskStatusRow> rows = await TaskStatusComposer.ComposeAllAsync(session, DateTimeOffset.UtcNow, cancellationToken);
        SolutionBoard board = SolutionBoard.Compose(view, context.Projects, rows, context.RootFingerprintOf);

        AnsiConsole.Write(Header(view.Membership));
        WriteBoard(board, AnsiConsole.Profile.Width, DateTimeOffset.UtcNow);
        return ExitCodes.Ok;
    }

    internal static Table Header(SolutionMembership membership)
    {
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumns("k", "v");
        table.AddRow("[bold]Solution[/]", $"[bold]{membership.Name.EscapeMarkup()}[/]");
        table.AddRow("Key", $"[dim]{membership.Key.EscapeMarkup()}[/]");
        table.AddRow("State", membership.State == SolutionState.Closed
            ? $"[yellow]closed[/] [dim]({membership.CloseReason.EscapeMarkup()})[/]"
            : "[green]open[/]");
        table.AddRow("Members", $"{membership.Members.Count} [dim]one-repo projects, named by project key[/]");
        return table;
    }

    internal static void WriteBoard(SolutionBoard board, int consoleWidth, DateTimeOffset now)
    {
        AnsiConsole.MarkupLine($"\n[bold]Board[/] {board.Rollup.Summary()}");
        foreach (SolutionBoardProject project in board.Projects)
        {
            string name = project.Project.Name.EscapeMarkup();
            AnsiConsole.MarkupLine($"\n[bold]{name}[/] [dim]{project.Member.RepositoryUrl.ToString().EscapeMarkup()}[/] {project.Rollup.Summary()}");
            if (project.Rows.Count == 0)
            {
                AnsiConsole.MarkupLine($"[dim]No tasks yet. Add one: h9k task add --project {name} --objective \"…\"[/]");
                continue;
            }

            TaskStatusRow[] shown = [.. project.Rows.Take(TasksPerProject)];
            AnsiConsole.Write(ProjectShowCommand.TaskTable(shown, consoleWidth, now));
            if (project.Rows.Count > shown.Length)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]Showing the {shown.Length} newest of {project.Rows.Count}; all of them:[/] h9k task list --project {name} --all");
            }
        }

        if (board.NotJoined.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Not joined on this node[/] [dim](no tasks to show; register and join one to see it)[/]");
            foreach (SolutionMember member in board.NotJoined)
            {
                AnsiConsole.MarkupLine($"{member.Name.EscapeMarkup()} [dim]{member.RepositoryUrl.ToString().EscapeMarkup()}[/]");
            }
        }
    }
}
