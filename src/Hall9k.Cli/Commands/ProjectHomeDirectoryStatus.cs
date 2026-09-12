using Hall9k.Domain.Features.Project.Projections;
using Spectre.Console;

namespace Hall9k.Cli.Commands;

/// <summary>
/// Whether a project's recorded home directory is actually there on this machine, printed the same
/// way after either <c>h9k project add --reactivate-archived</c> or <c>h9k project reactivate</c>
/// reactivates a project — the acceptance criteria's own "reports whether the home directory is
/// intact, pointing at h9k project init if not" (task: a project can be archived, listed as
/// archived, reactivated, and renamed).
/// </summary>
internal static class ProjectHomeDirectoryStatus
{
    public static void Report(ProjectDetails project)
    {
        if (!project.HomeDirectory.HasValue)
        {
            AnsiConsole.MarkupLine(
                $"[dim]No home was ever recorded for it. Give it one:[/] h9k project init {project.Name.EscapeMarkup()}");
            return;
        }

        AnsiConsole.MarkupLine(Directory.Exists(project.HomeDirectory.Value)
            ? $"[dim]Home directory intact:[/] {project.HomeDirectory.Value.EscapeMarkup()}"
            : $"[yellow]Home directory is missing on this machine.[/] Recreate it: "
              + $"h9k project init {project.Name.EscapeMarkup()}");
    }
}
