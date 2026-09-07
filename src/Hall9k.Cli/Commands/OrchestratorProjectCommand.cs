using System.ComponentModel;
using Hall9k.Cli.Infrastructure;
using Hall9k.Cli.Orchestrator;
using Hall9k.Domain.Features.Orchestrator;
using Hall9k.Domain.Features.Project.Projections;
using Hall9k.Domain.Infrastructure.Storage;
using Marten;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Hall9k.Cli.Commands;

/// <summary>
/// A project orchestrator's own view (task: an operator starts a lean node or project
/// orchestrator window): daemon liveness, the launch text, and where its recipe and journal are.
/// With no project named and exactly one registered, that one is used; with several, one block
/// prints per project rather than prompting — an interactive prompt is not something a headless
/// caller of this command could ever answer. Never launches a session.
/// </summary>
public sealed class OrchestratorProjectCommand : Hall9kAsyncCommand<OrchestratorProjectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT]")]
        [Description(
            "Project to report on: its name, an unambiguous fragment, or its full id. Omit it "
            + "when exactly one project is registered; with several, every one is printed.")]
        public string? Project { get; init; }

        [CommandOption("--cli <NAME>")]
        [Description("Which agent CLI's launch text to print. Defaults to claude-code.")]
        public string Cli { get; init; } = LaunchText.DefaultCli;
    }

    protected override async Task<int> ExecuteAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var store = CliStore.Open();
        await using IQuerySession session = store.QuerySession();

        IReadOnlyList<ProjectDetails> projects;
        if (settings.Project is { } projectArg)
        {
            projects = [await ProjectResolver.ResolveAsync(session, projectArg, cancellationToken)];
        }
        else
        {
            projects = await session.Query<ProjectDetails>().ToListAsync(cancellationToken);
            if (projects.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "[dim]No projects registered.[/] Register one: h9k project add --name <name> --repo <path>");
                return ExitCodes.Ok;
            }
        }

        AnsiConsole.MarkupLineInterpolated($"[dim]{OrchestratorDaemonLiveness.Describe()}[/]");

        foreach (ProjectDetails project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (projects.Count > 1)
            {
                AnsiConsole.MarkupLineInterpolated($"[bold]{project.Name.EscapeMarkup()}[/]");
            }

            LaunchText? resolved = OrchestratorLaunchTextResolution.Resolve(
                project.LaunchTexts,
                settings.Cli,
                OrchestratorRecipeContext.ProjectWorkingDirectory(project),
                OrchestratorRecipeContext.ProjectOpeningMessage(project.Name));

            string recipePath = project.HomeDirectory.HasValue
                ? ProjectHomePaths.OrchestratorRecipeFile(project.HomeDirectory.Value)
                : $"<no home recorded yet - run h9k project init {project.Name}>";
            string journalPath = project.HomeDirectory.HasValue
                ? ProjectHomePaths.RecipeJournalFile(project.HomeDirectory.Value)
                : $"<no home recorded yet - run h9k project init {project.Name}>";

            OrchestratorReport.Print(settings.Cli, resolved, recipePath, journalPath);
        }

        return ExitCodes.Ok;
    }
}
